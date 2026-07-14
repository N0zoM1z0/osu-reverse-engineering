using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LocalManiaAuto;

internal enum PlaybackStopReason
{
    Completed,
    UserAborted,
    FocusLost,
}

internal sealed record PlaybackResult(
    PlaybackStopReason StopReason,
    int BatchesSent,
    int TransitionsInjected,
    double MaximumLatenessMilliseconds,
    string Message);

internal static class WindowsPlayback
{
    private const int VkEscape = 0x1B;
    private const int VkF6 = 0x75;
    private const int VkF7 = 0x76;
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventExtendedKey = 0x0001;
    private const uint KeyEventScanCode = 0x0008;
    private const uint MapVkToScanCode = 0;
    private const uint MapVkToScanCodeEx = 4;
    private static readonly UIntPtr InjectionMarker = new(0x4D414E49u); // "MANI"

    public static PlaybackResult Run(
        LiveTimeline timeline,
        IReadOnlyList<VirtualKeySpec> keys,
        int anchorTime,
        double rate,
        double offsetMilliseconds)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("play requires Windows; inspect, frames, and events can run under WSL.");
        }
        if (timeline.Batches.Count == 0)
        {
            throw new ArgumentException("The timeline contains no playable events.", nameof(timeline));
        }
        if (anchorTime > timeline.Batches[0].Time)
        {
            throw new ArgumentOutOfRangeException(nameof(anchorTime), "anchor-ms cannot be later than the first object; this prototype cannot take over mid-map.");
        }

        var pressed = new bool[keys.Count];
        int cancelFlag = 0;
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            Interlocked.Exchange(ref cancelFlag, 1);
        };
        Console.CancelKeyPress += cancelHandler;

        try
        {
            TargetWindow? target = WaitForStart(() => Volatile.Read(ref cancelFlag) != 0);
            if (target is null)
            {
                return new PlaybackResult(PlaybackStopReason.UserAborted, 0, 0, 0, "Stopped before playback started.");
            }

            Console.WriteLine($"Locked to foreground osu! PID {target.ProcessId}; timing started. F7, Esc, or Ctrl+C stops playback.");
            _ = TimeBeginPeriod(1);
            var stopwatch = Stopwatch.StartNew();
            int batchesSent = 0;
            int transitionsInjected = 0;
            double maximumLateness = 0;

            try
            {
                foreach (TransitionBatch batch in timeline.Batches)
                {
                    double dueMilliseconds = ((batch.Time - anchorTime) / rate) + offsetMilliseconds;
                    PlaybackStopReason? stopReason = WaitUntil(
                        stopwatch,
                        dueMilliseconds,
                        target.Handle,
                        () => Volatile.Read(ref cancelFlag) != 0);

                    if (stopReason is PlaybackStopReason.UserAborted)
                    {
                        return new PlaybackResult(stopReason.Value, batchesSent, transitionsInjected, maximumLateness, "Stopped by the user.");
                    }
                    if (stopReason is PlaybackStopReason.FocusLost)
                    {
                        return new PlaybackResult(stopReason.Value, batchesSent, transitionsInjected, maximumLateness, "osu! lost foreground focus; playback stopped.");
                    }

                    int injected = InjectBatch(batch, keys, pressed);
                    batchesSent++;
                    transitionsInjected += injected;
                    maximumLateness = Math.Max(
                        maximumLateness,
                        Math.Max(0, stopwatch.Elapsed.TotalMilliseconds - dueMilliseconds));
                }

                return new PlaybackResult(
                    PlaybackStopReason.Completed,
                    batchesSent,
                    transitionsInjected,
                    maximumLateness,
                    "Timeline playback completed.");
            }
            finally
            {
                stopwatch.Stop();
                TryReleaseAll(keys, pressed);
                _ = TimeEndPeriod(1);
            }
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static TargetWindow? WaitForStart(Func<bool> cancellationRequested)
    {
        Console.WriteLine("Waiting for F6: focus osu! and press F6 at the anchor time. F7 or Esc cancels.");
        WaitForKeyRelease(VkF6, cancellationRequested);

        while (true)
        {
            if (cancellationRequested() || IsKeyDown(VkF7) || IsKeyDown(VkEscape))
            {
                return null;
            }

            if (!IsKeyDown(VkF6))
            {
                Thread.Sleep(5);
                continue;
            }

            IntPtr handle = GetForegroundWindow();
            if (TryGetOsuProcess(handle, out int processId))
            {
                return new TargetWindow(handle, processId);
            }

            Console.WriteLine("Ignored F6 because the foreground window is not osu!. Release F6 and try again.");
            WaitForKeyRelease(VkF6, cancellationRequested);
        }
    }

    private static void WaitForKeyRelease(int virtualKey, Func<bool> cancellationRequested)
    {
        while (IsKeyDown(virtualKey) && !cancellationRequested())
        {
            Thread.Sleep(5);
        }
    }

    private static PlaybackStopReason? WaitUntil(
        Stopwatch stopwatch,
        double dueMilliseconds,
        IntPtr targetWindow,
        Func<bool> cancellationRequested)
    {
        while (true)
        {
            if (cancellationRequested() || IsKeyDown(VkF7) || IsKeyDown(VkEscape))
            {
                return PlaybackStopReason.UserAborted;
            }
            if (GetForegroundWindow() != targetWindow)
            {
                return PlaybackStopReason.FocusLost;
            }

            double remaining = dueMilliseconds - stopwatch.Elapsed.TotalMilliseconds;
            if (remaining <= 0)
            {
                return null;
            }

            if (remaining > 3)
            {
                Thread.Sleep(Math.Clamp((int)(remaining - 2), 1, 8));
            }
            else
            {
                Thread.SpinWait(64);
                Thread.Yield();
            }
        }
    }

    private static int InjectBatch(
        TransitionBatch batch,
        IReadOnlyList<VirtualKeySpec> keys,
        bool[] pressed)
    {
        var nextState = (bool[])pressed.Clone();
        var inputs = new List<NativeInput>(batch.Transitions.Count);

        foreach (LaneTransition transition in batch.Transitions)
        {
            if (transition.Lane < 0 || transition.Lane >= keys.Count)
            {
                throw new InvalidOperationException($"The timeline contains invalid lane {transition.Lane}.");
            }
            if (nextState[transition.Lane] == transition.IsDown)
            {
                continue;
            }

            inputs.Add(CreateKeyboardInput(keys[transition.Lane], keyUp: !transition.IsDown));
            nextState[transition.Lane] = transition.IsDown;
        }

        if (inputs.Count == 0)
        {
            return 0;
        }

        Send(inputs);
        Array.Copy(nextState, pressed, pressed.Length);
        return inputs.Count;
    }

    private static void TryReleaseAll(IReadOnlyList<VirtualKeySpec> keys, bool[] pressed)
    {
        var releases = new List<NativeInput>(keys.Count);
        for (int lane = 0; lane < pressed.Length; lane++)
        {
            if (pressed[lane])
            {
                releases.Add(CreateKeyboardInput(keys[lane], keyUp: true));
                pressed[lane] = false;
            }
        }

        if (releases.Count == 0)
        {
            return;
        }

        try
        {
            Send(releases);
        }
        catch (Win32Exception exception)
        {
            Console.Error.WriteLine($"Warning: failed to release injected keys: {exception.Message}");
        }
    }

    private static NativeInput CreateKeyboardInput(VirtualKeySpec key, bool keyUp)
    {
        uint mappedScanCode = MapVirtualKeyW(key.VirtualKey, MapVkToScanCodeEx);
        if (mappedScanCode == 0)
        {
            mappedScanCode = MapVirtualKeyW(key.VirtualKey, MapVkToScanCode);
        }
        if (mappedScanCode == 0)
        {
            throw new Win32Exception($"Could not map {key.Name} (VK 0x{key.VirtualKey:X2}) to a scan code.");
        }

        byte prefix = (byte)((mappedScanCode >> 8) & 0xFF);
        uint flags = KeyEventScanCode;
        if (keyUp)
        {
            flags |= KeyEventKeyUp;
        }
        if (prefix is 0xE0 or 0xE1)
        {
            flags |= KeyEventExtendedKey;
        }

        return new NativeInput
        {
            Type = InputKeyboard,
            Union = new InputUnion
            {
                Keyboard = new KeyboardInput
                {
                    VirtualKey = 0,
                    ScanCode = (ushort)(mappedScanCode & 0xFF),
                    Flags = flags,
                    Time = 0,
                    ExtraInfo = InjectionMarker,
                },
            },
        };
    }

    private static void Send(List<NativeInput> inputs)
    {
        NativeInput[] array = inputs.ToArray();
        uint sent = SendInput((uint)array.Length, array, Marshal.SizeOf<NativeInput>());
        if (sent != array.Length)
        {
            int error = Marshal.GetLastWin32Error();
            throw new Win32Exception(
                error,
                $"SendInput emitted only {sent}/{array.Length} events. If osu! runs elevated, launch this tool at the same integrity level.");
        }
    }

    private static bool TryGetOsuProcess(IntPtr window, out int processId)
    {
        processId = 0;
        if (window == IntPtr.Zero)
        {
            return false;
        }

        _ = GetWindowThreadProcessId(window, out uint rawProcessId);
        if (rawProcessId == 0 || rawProcessId > int.MaxValue)
        {
            return false;
        }

        try
        {
            using Process process = Process.GetProcessById((int)rawProcessId);
            if (!process.ProcessName.Equals("osu!", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            processId = process.Id;
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsKeyDown(int virtualKey)
        => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private sealed record TargetWindow(IntPtr Handle, int ProcessId);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInput
    {
        public uint Type;
        public InputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MouseInput Mouse;

        [FieldOffset(0)]
        public KeyboardInput Keyboard;

        [FieldOffset(0)]
        public HardwareInput Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HardwareInput
    {
        public uint Message;
        public ushort ParameterLow;
        public ushort ParameterHigh;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, NativeInput[] inputs, int inputSize);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyW(uint code, uint mapType);

    [DllImport("winmm.dll")]
    private static extern uint TimeBeginPeriod(uint period);

    [DllImport("winmm.dll")]
    private static extern uint TimeEndPeriod(uint period);
}
