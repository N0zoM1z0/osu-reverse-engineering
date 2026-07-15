using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace LocalTaikoAgent.Plugin
{
    internal sealed class AgentOverlay : IDisposable
    {
        private readonly Func<AgentOverlaySnapshot> snapshotProvider;
        private readonly Action<string> log;
        private Thread thread;
        private OverlayForm form;
        private bool disposed;

        private AgentOverlay(Func<AgentOverlaySnapshot> provider, Action<string> logger)
        {
            snapshotProvider = provider;
            log = logger;
        }

        public static AgentOverlay Start(
            Func<AgentOverlaySnapshot> provider,
            Action<string> logger)
        {
            if (provider == null) throw new ArgumentNullException("provider");
            if (logger == null) throw new ArgumentNullException("logger");
            AgentOverlay result = new AgentOverlay(provider, logger);
            result.thread = new Thread(result.ThreadMain);
            result.thread.Name = "LocalTaikoAgent.Overlay";
            result.thread.IsBackground = true;
            result.thread.SetApartmentState(ApartmentState.STA);
            result.thread.Start();
            return result;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            OverlayForm current = form;
            if (current == null || current.IsDisposed) return;
            try { current.BeginInvoke(new Action(current.Close)); }
            catch { }
        }

        private void ThreadMain()
        {
            try
            {
                form = new OverlayForm(snapshotProvider);
                Application.Run(form);
            }
            catch (Exception exception)
            {
                log("overlay failure: " + exception);
            }
        }

        private sealed class OverlayForm : Form
        {
            private const int ExtendedStyleToolWindow = 0x00000080;
            private const int ExtendedStyleTransparent = 0x00000020;
            private const int ExtendedStyleNoActivate = 0x08000000;
            private const int WindowOwnerIndex = -8;
            private const int ShowNoActivate = 4;

            private readonly Func<AgentOverlaySnapshot> snapshotProvider;
            private readonly int processId;
            private readonly System.Windows.Forms.Timer timer;
            private readonly Font titleFont;
            private readonly Font bodyFont;
            private readonly Font smallFont;
            private IntPtr gameWindow;
            private AgentOverlaySnapshot snapshot;

            public OverlayForm(Func<AgentOverlaySnapshot> provider)
            {
                snapshotProvider = provider;
                processId = Process.GetCurrentProcess().Id;
                AutoScaleMode = AutoScaleMode.None;
                BackColor = Color.FromArgb(18, 21, 29);
                ClientSize = new Size(420, 76);
                DoubleBuffered = true;
                FormBorderStyle = FormBorderStyle.None;
                Opacity = 0.94;
                ShowIcon = false;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                TopMost = true;
                titleFont = CreateFont(10.5f, FontStyle.Bold);
                bodyFont = CreateFont(9.3f, FontStyle.Regular);
                smallFont = CreateFont(8.0f, FontStyle.Regular);
                timer = new System.Windows.Forms.Timer();
                timer.Interval = 50;
                timer.Tick += OnTimerTick;
                timer.Start();
            }

            protected override bool ShowWithoutActivation { get { return true; } }

            protected override CreateParams CreateParams
            {
                get
                {
                    CreateParams value = base.CreateParams;
                    value.ExStyle |= ExtendedStyleToolWindow
                        | ExtendedStyleTransparent
                        | ExtendedStyleNoActivate;
                    return value;
                }
            }

            protected override void OnPaint(PaintEventArgs eventArgs)
            {
                base.OnPaint(eventArgs);
                AgentOverlaySnapshot current = snapshot;
                if (current == null) return;
                Graphics graphics = eventArgs.Graphics;
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                graphics.Clear(Color.FromArgb(18, 21, 29));
                Color accent = current.Options.Enabled
                    ? Color.FromArgb(235, 79, 103)
                    : Color.FromArgb(115, 124, 145);
                using (Brush accentBrush = new SolidBrush(accent))
                    graphics.FillRectangle(accentBrush, 0, 0, 5, ClientSize.Height);
                if (current.MenuVisible) DrawMenu(graphics, current, accent);
                else DrawCompact(graphics, current, accent);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    timer.Dispose();
                    titleFont.Dispose();
                    bodyFont.Dispose();
                    smallFont.Dispose();
                }
                base.Dispose(disposing);
            }

            private void OnTimerTick(object sender, EventArgs eventArgs)
            {
                try
                {
                    snapshot = snapshotProvider();
                    if (snapshot == null) return;
                    if (gameWindow == IntPtr.Zero || !IsWindow(gameWindow))
                    {
                        gameWindow = FindGameWindow();
                        if (gameWindow != IntPtr.Zero && IsHandleCreated)
                            SetWindowLong(Handle, WindowOwnerIndex, gameWindow.ToInt32());
                    }
                    if (gameWindow == IntPtr.Zero || IsIconic(gameWindow) || !IsOwnProcessForeground())
                    {
                        if (Visible) Hide();
                        return;
                    }
                    int wantedHeight = snapshot.MenuVisible ? 482 : 76;
                    if (ClientSize.Width != 420 || ClientSize.Height != wantedHeight)
                        ClientSize = new Size(420, wantedHeight);
                    NativeRect client;
                    NativePoint origin = new NativePoint();
                    if (!GetClientRect(gameWindow, out client)
                        || !ClientToScreen(gameWindow, ref origin)) return;
                    int clientWidth = client.Right - client.Left;
                    int x = origin.X + Math.Max(8, clientWidth - Width - 18);
                    int y = origin.Y + 18;
                    if (Location.X != x || Location.Y != y) Location = new Point(x, y);
                    if (!Visible) ShowWindow(Handle, ShowNoActivate);
                    Invalidate();
                }
                catch { }
            }

            private void DrawCompact(Graphics graphics, AgentOverlaySnapshot current, Color accent)
            {
                DrawText(graphics, "LOCAL TAIKO AGENT", titleFont, Color.White, 18, 12, 230, 20);
                DrawRightText(
                    graphics,
                    current.Options.Enabled ? "AGENT" : "PLAYER",
                    titleFont,
                    accent,
                    270,
                    12,
                    112,
                    20);
                DrawText(
                    graphics,
                    current.Runtime.Phase + "  " + current.Runtime.Detail,
                    smallFont,
                    Color.FromArgb(188, 194, 209),
                    18,
                    39,
                    364,
                    18);
                DrawText(graphics, "Ctrl+Alt+F7 settings   Ctrl+Alt+F8 toggle",
                    smallFont, Color.FromArgb(126, 134, 154), 18, 56, 364, 16);
            }

            private void DrawMenu(Graphics graphics, AgentOverlaySnapshot current, Color accent)
            {
                DrawText(graphics, "LOCAL TAIKO AGENT", titleFont, Color.White, 18, 12, 230, 20);
                DrawRightText(
                    graphics,
                    current.Options.Enabled ? "AGENT ON" : "YOU PLAY",
                    titleFont,
                    accent,
                    245,
                    12,
                    137,
                    20);
                DrawText(graphics, "Ctrl+Alt + Up/Down select, Left/Right change",
                    smallFont, Color.FromArgb(145, 153, 171), 18, 36, 364, 18);

                string timing = AgentOptionsSnapshot.SignedMilliseconds(
                    current.Options.TimingBiasMilliseconds);
                if (current.Options.TimingBiasMilliseconds < 0) timing += "  RUSH";
                else if (current.Options.TimingBiasMilliseconds > 0) timing += "  LATE";
                string[] labels = new string[]
                {
                    "CONTROL", "STYLE", "BASE UR", "TIMING", "RUSH BURSTS", "100 MIX",
                    "DENSE BOOST", "STRONG SPLIT", "FRAME CADENCE", "FATIGUE",
                    "FINGER TROUBLE", "VARIATION"
                };
                string[] values = new string[]
                {
                    current.Options.Enabled ? "AGENT" : "PLAYER / SELF",
                    AgentOptionsSnapshot.StyleName(current.Options.Style),
                    current.Options.BaseUnstableRate + "  (sigma "
                        + (current.Options.BaseUnstableRate / 10.0).ToString("0.0") + "ms)",
                    timing,
                    current.Options.RushPercent + "%  correlated early runs",
                    AgentOptionsSnapshot.GradePercent(current.Options.Grade100Permille),
                    "+" + current.Options.DenseBoostPercent + "%  high-NPS bias",
                    "0.." + current.Options.StrongSplitMaximumMilliseconds + "ms  second hand",
                    AgentOptionsSnapshot.FrameName(current.Options.FrameCadence),
                    current.Options.FatigueEnabled ? "ON" : "OFF",
                    current.Options.FingerTroublePercent + "%  delayed finger",
                    current.Options.RepeatableVariation ? "REPEATABLE" : "NEW EACH PLAY"
                };

                const int rowTop = 64;
                const int rowHeight = 27;
                for (int row = 0; row < labels.Length; row++)
                {
                    int y = rowTop + row * rowHeight;
                    if (row == current.SelectedRow)
                    {
                        using (Brush selected = new SolidBrush(Color.FromArgb(45, accent)))
                            graphics.FillRectangle(selected, 10, y, ClientSize.Width - 20, rowHeight - 2);
                        using (Brush marker = new SolidBrush(accent))
                            graphics.FillRectangle(marker, 10, y, 3, rowHeight - 2);
                    }
                    DrawText(graphics, labels[row], smallFont, Color.FromArgb(137, 145, 164),
                        22, y + 5, 135, 19);
                    DrawRightText(graphics, values[row], bodyFont,
                        row == current.SelectedRow ? Color.White : Color.FromArgb(214, 218, 228),
                        150, y + 3, 232, 21);
                }

                int statusTop = rowTop + labels.Length * rowHeight + 8;
                using (Pen line = new Pen(Color.FromArgb(54, 59, 72)))
                    graphics.DrawLine(line, 18, statusTop, ClientSize.Width - 18, statusTop);
                DrawText(graphics, current.Runtime.Phase, titleFont, accent,
                    18, statusTop + 9, 92, 20);
                DrawText(graphics, current.Runtime.Detail, smallFont, Color.FromArgb(188, 194, 209),
                    108, statusTop + 11, 274, 18);
                if (!String.IsNullOrEmpty(current.Runtime.MapName))
                    DrawText(graphics, current.Runtime.MapName, smallFont, Color.FromArgb(126, 134, 154),
                        18, statusTop + 32, 364, 18);
                DrawText(graphics, "Ctrl+Alt+F7 close   Ctrl+Alt+F8 quick toggle",
                    smallFont, Color.FromArgb(126, 134, 154),
                    18, ClientSize.Height - 22, 364, 16);
            }

            private IntPtr FindGameWindow()
            {
                Process process = Process.GetCurrentProcess();
                process.Refresh();
                IntPtr main = process.MainWindowHandle;
                if (main != IntPtr.Zero && main != Handle && IsWindow(main)) return main;
                IntPtr best = IntPtr.Zero;
                long bestArea = 0;
                EnumWindows(delegate(IntPtr window, IntPtr parameter)
                {
                    uint windowProcessId;
                    GetWindowThreadProcessId(window, out windowProcessId);
                    if (windowProcessId != (uint)processId || window == Handle || !IsWindowVisible(window))
                        return true;
                    NativeRect rectangle;
                    if (!GetWindowRect(window, out rectangle)) return true;
                    long area = (long)(rectangle.Right - rectangle.Left)
                        * (rectangle.Bottom - rectangle.Top);
                    if (area > bestArea) { bestArea = area; best = window; }
                    return true;
                }, IntPtr.Zero);
                return best;
            }

            private bool IsOwnProcessForeground()
            {
                IntPtr foreground = GetForegroundWindow();
                if (foreground == IntPtr.Zero) return false;
                uint foregroundProcessId;
                GetWindowThreadProcessId(foreground, out foregroundProcessId);
                return foregroundProcessId == (uint)processId;
            }

            private static Font CreateFont(float size, FontStyle style)
            {
                try { return new Font("Segoe UI", size, style, GraphicsUnit.Point); }
                catch { return new Font(FontFamily.GenericSansSerif, size, style, GraphicsUnit.Point); }
            }

            private static void DrawText(
                Graphics graphics, string text, Font font, Color color,
                int x, int y, int width, int height)
            {
                using (Brush brush = new SolidBrush(color))
                using (StringFormat format = new StringFormat(StringFormat.GenericTypographic))
                {
                    format.Trimming = StringTrimming.EllipsisCharacter;
                    format.FormatFlags |= StringFormatFlags.NoWrap;
                    graphics.DrawString(text ?? String.Empty, font, brush,
                        new RectangleF(x, y, width, height), format);
                }
            }

            private static void DrawRightText(
                Graphics graphics, string text, Font font, Color color,
                int x, int y, int width, int height)
            {
                using (Brush brush = new SolidBrush(color))
                using (StringFormat format = new StringFormat(StringFormat.GenericTypographic))
                {
                    format.Alignment = StringAlignment.Far;
                    format.Trimming = StringTrimming.EllipsisCharacter;
                    format.FormatFlags |= StringFormatFlags.NoWrap;
                    graphics.DrawString(text ?? String.Empty, font, brush,
                        new RectangleF(x, y, width, height), format);
                }
            }

            private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);
            [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X; public int Y; }
            [StructLayout(LayoutKind.Sequential)] private struct NativeRect
            { public int Left; public int Top; public int Right; public int Bottom; }

            [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref NativePoint point);
            [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);
            [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
            [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out NativeRect rectangle);
            [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);
            [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
            [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
            [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
            [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
            [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr window, int index, int value);
            [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
        }
    }
}
