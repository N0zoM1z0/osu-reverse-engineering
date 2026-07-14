param(
    [Parameter(Mandatory = $true)]
    [int] $TargetProcessId
)

$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class LocalManiaWindowProbe
{
    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    public static string[] List(int targetProcessId)
    {
        List<string> result = new List<string>();
        EnumWindows(delegate(IntPtr window, IntPtr parameter)
        {
            uint processId;
            GetWindowThreadProcessId(window, out processId);
            if (processId != (uint)targetProcessId)
                return true;

            Rect rectangle;
            GetWindowRect(window, out rectangle);
            StringBuilder title = new StringBuilder(512);
            GetWindowText(window, title, title.Capacity);
            IntPtr owner = GetWindow(window, 4);
            int extendedStyle = GetWindowLong(window, -20);
            result.Add(String.Format(
                "hwnd=0x{0:X8} owner=0x{1:X8} visible={2} rect={3},{4},{5}x{6} ex=0x{7:X8} title={8}",
                window.ToInt32(),
                owner.ToInt32(),
                IsWindowVisible(window),
                rectangle.Left,
                rectangle.Top,
                rectangle.Right - rectangle.Left,
                rectangle.Bottom - rectangle.Top,
                extendedStyle,
                title));
            return true;
        }, IntPtr.Zero);
        return result.ToArray();
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr window, int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int maximumCount);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out Rect rectangle);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);
}
'@

[LocalManiaWindowProbe]::List($TargetProcessId)
