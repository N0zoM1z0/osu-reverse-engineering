using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;

[assembly: AssemblyTitle("LocalManiaAuto.Plugin")]
[assembly: AssemblyDescription("In-process local osu!mania Player-input agent experiment")]
[assembly: AssemblyCompany("Local research")]
[assembly: AssemblyProduct("LocalManiaAgent")]
[assembly: AssemblyVersion("0.5.0.0")]
[assembly: AssemblyFileVersion("0.5.0.0")]

namespace LocalManiaAuto.Plugin
{
    public static class Entry
    {
        private const string SupportedSha256 = "6e182c10d1813209d12753dbc70b3a5bba00fef4ecf64bc42051870e6dfe4b7d";
        private const int SelectedModsFieldToken = 0x04000CC6;
        private const int CurrentPlayModeMethodToken = 0x06002232;
        private const int ManiaMode = 3;
        private const int VkControl = 0x11;
        private const int VkMenu = 0x12;
        private const int VkEnter = 0x0D;
        private const int VkLeft = 0x25;
        private const int VkUp = 0x26;
        private const int VkRight = 0x27;
        private const int VkDown = 0x28;
        private const int VkF7 = 0x76;
        private const int VkF8 = 0x77;

        private static readonly object LogLock = new object();
        private static int started;

        public static void Start()
        {
            if (Interlocked.Exchange(ref started, 1) != 0)
                return;

            Log("Entry.Start in AppDomain " + AppDomain.CurrentDomain.FriendlyName);
            Thread worker = new Thread(WorkerMain);
            worker.Name = "LocalManiaAgent.Plugin";
            worker.IsBackground = true;
            worker.Start();
        }

        private static void WorkerMain()
        {
            LiveAgent agent = null;
            AgentOverlay overlay = null;
            AgentControlState controls = null;

            try
            {
                Assembly game = WaitForEntryAssembly();
                if (game == null)
                {
                    Log("entry assembly did not become available within 30 seconds");
                    return;
                }
                if (!String.Equals(game.GetName().Name, "osu!", StringComparison.Ordinal))
                {
                    Log("diagnostic host only; entry assembly=" + game.FullName);
                    return;
                }

                string gamePath = game.Location;
                string sha256 = ComputeSha256(gamePath);
                Log("target=" + gamePath + ", sha256=" + sha256);
                if (!String.Equals(sha256, SupportedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    Log("unsupported osu! build; refusing to resolve or mutate game fields");
                    return;
                }

                Module module = game.ManifestModule;
                FieldInfo selectedMods = module.ResolveField(SelectedModsFieldToken);
                MethodInfo currentPlayMode = module.ResolveMethod(CurrentPlayModeMethodToken) as MethodInfo;
                ValidateTargets(selectedMods, currentPlayMode);
                agent = new LiveAgent(game, Log);

                string enabledText = Environment.GetEnvironmentVariable("MANIA_AGENT_ENABLED");
                if (String.IsNullOrEmpty(enabledText))
                    enabledText = Environment.GetEnvironmentVariable("MANIA_AUTO_ENABLED");
                controls = new AgentControlState(IsTrue(enabledText));
                overlay = AgentOverlay.Start(controls.GetOverlaySnapshot, Log);
                bool previousF7 = false;
                bool previousF8 = false;
                bool previousUp = false;
                bool previousDown = false;
                bool previousLeft = false;
                bool previousRight = false;
                bool previousEnter = false;
                Log("ready; " + controls.GetOptions().Describe()
                    + ", architecture=normal Player input (no Autoplay, no replay list)"
                    + ", settings=Ctrl+Alt+F7, toggle=Ctrl+Alt+F8");

                while (true)
                {
                    bool ownForeground = IsCurrentProcessForeground();
                    bool modifiers = ownForeground && IsKeyDown(VkControl) && IsKeyDown(VkMenu);
                    bool f7 = modifiers && IsKeyDown(VkF7);
                    if (RisingEdge(f7, ref previousF7))
                    {
                        Log("ui: " + controls.ToggleMenu());
                    }
                    bool f8 = modifiers && IsKeyDown(VkF8);
                    if (RisingEdge(f8, ref previousF8))
                        Log("ui: " + controls.ToggleEnabled());

                    bool menuControls = modifiers && controls.IsMenuVisible;
                    if (RisingEdge(menuControls && IsKeyDown(VkUp), ref previousUp))
                        controls.MoveSelection(-1);
                    if (RisingEdge(menuControls && IsKeyDown(VkDown), ref previousDown))
                        controls.MoveSelection(1);
                    if (RisingEdge(menuControls && IsKeyDown(VkLeft), ref previousLeft))
                        Log("ui: " + controls.AdjustSelected(-1));
                    if (RisingEdge(menuControls && IsKeyDown(VkRight), ref previousRight))
                        Log("ui: " + controls.AdjustSelected(1));
                    if (RisingEdge(menuControls && IsKeyDown(VkEnter), ref previousEnter))
                        Log("ui: " + controls.AdjustSelected(1));

                    int mode = Convert.ToInt32(currentPlayMode.Invoke(null, null));
                    int mods = Convert.ToInt32(selectedMods.GetValue(null));
                    try
                    {
                        agent.Tick(controls.GetOptions(), mode, mods);
                        controls.UpdateRuntime(agent.GetRuntimeStatus());
                    }
                    catch (Exception exception)
                    {
                        controls.Disable();
                        agent.EmergencyStop("tick failure");
                        controls.UpdateRuntime(AgentRuntimeStatus.Idle("disabled after tick failure"));
                        Log("agent disabled after tick failure: " + UsefulMessage(exception));
                    }

                    Thread.Sleep(agent.IsTimingCritical ? 1 : 20);
                }
            }
            catch (ThreadAbortException)
            {
                if (agent != null)
                    agent.EmergencyStop("worker thread abort");
                Thread.ResetAbort();
            }
            catch (Exception exception)
            {
                if (agent != null)
                    agent.EmergencyStop("worker failure");
                Log("worker failure: " + UsefulMessage(exception));
            }
            finally
            {
                if (agent != null)
                    agent.Shutdown();
                if (overlay != null)
                    overlay.Dispose();
            }
        }

        private static void ValidateTargets(FieldInfo selectedMods, MethodInfo currentPlayMode)
        {
            if (selectedMods == null
                || !selectedMods.IsStatic
                || !selectedMods.FieldType.IsEnum
                || !String.Equals(selectedMods.FieldType.FullName, "osu_common.Mods", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("selected-mods metadata token failed structural validation");
            }
            if (currentPlayMode == null
                || !currentPlayMode.IsStatic
                || currentPlayMode.GetParameters().Length != 0
                || !currentPlayMode.ReturnType.IsEnum
                || !String.Equals(currentPlayMode.ReturnType.FullName, "osu_common.PlayModes", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("play-mode metadata token failed structural validation");
            }
        }

        private static string ComputeSha256(string path)
        {
            using (FileStream stream = File.OpenRead(path))
            using (SHA256 algorithm = SHA256.Create())
            {
                byte[] hash = algorithm.ComputeHash(stream);
                return BitConverter.ToString(hash).Replace("-", String.Empty).ToLowerInvariant();
            }
        }

        private static bool IsTrue(string value)
        {
            return String.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
                || String.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                || String.Equals(value, "on", StringComparison.OrdinalIgnoreCase)
                || String.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
        }

        private static Assembly WaitForEntryAssembly()
        {
            for (int attempt = 0; attempt < 600; attempt++)
            {
                Assembly entry = Assembly.GetEntryAssembly();
                if (entry != null)
                    return entry;
                Thread.Sleep(50);
            }
            return null;
        }

        private static bool IsKeyDown(int virtualKey)
        {
            return (GetAsyncKeyState(virtualKey) & 0x8000) != 0;
        }

        private static bool RisingEdge(bool current, ref bool previous)
        {
            bool result = current && !previous;
            previous = current;
            return result;
        }

        private static bool IsCurrentProcessForeground()
        {
            IntPtr window = GetForegroundWindow();
            if (window == IntPtr.Zero)
                return false;
            uint processId;
            GetWindowThreadProcessId(window, out processId);
            return processId == (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
        }

        private static string UsefulMessage(Exception exception)
        {
            TargetInvocationException invocation = exception as TargetInvocationException;
            if (invocation != null && invocation.InnerException != null)
                return invocation.InnerException.ToString();
            return exception.ToString();
        }

        private static void Log(string message)
        {
            try
            {
                string path = Environment.GetEnvironmentVariable("MANIA_AUTO_LOG");
                if (String.IsNullOrEmpty(path))
                    path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LocalManiaAuto.log");

                string directory = Path.GetDirectoryName(path);
                if (!String.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                lock (LogLock)
                {
                    string line = DateTime.UtcNow.ToString("O") + " [plugin pid="
                        + System.Diagnostics.Process.GetCurrentProcess().Id + "] "
                        + message + Environment.NewLine;
                    for (int attempt = 0; attempt < 10; attempt++)
                    {
                        try
                        {
                            File.AppendAllText(path, line);
                            return;
                        }
                        catch (IOException)
                        {
                            Thread.Sleep(10);
                        }
                    }
                }
            }
            catch
            {
            }
        }

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int virtualKey);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    }
}
