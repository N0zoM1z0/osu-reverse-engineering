using System;
using System.Globalization;

namespace LocalCatchAgent.Plugin
{
    internal enum CatchPathStyle
    {
        Smooth,
        Centered,
        Lively,
        LastMoment
    }

    internal sealed class AgentOptionsSnapshot
    {
        public AgentOptionsSnapshot(
            bool enabled,
            CatchPathStyle style,
            double safetyMargin,
            int wanderPixels,
            double trackingDeadband,
            bool includeTinyDropletsAsHardConstraints,
            bool fatigueEnabled,
            bool repeatableVariation)
        {
            Enabled = enabled;
            Style = style;
            SafetyMargin = safetyMargin;
            WanderPixels = wanderPixels;
            TrackingDeadband = trackingDeadband;
            IncludeTinyDropletsAsHardConstraints = includeTinyDropletsAsHardConstraints;
            FatigueEnabled = fatigueEnabled;
            RepeatableVariation = repeatableVariation;
        }

        public readonly bool Enabled;
        public readonly CatchPathStyle Style;
        public readonly double SafetyMargin;
        public readonly int WanderPixels;
        public readonly double TrackingDeadband;
        public readonly bool IncludeTinyDropletsAsHardConstraints;
        public readonly bool FatigueEnabled;
        public readonly bool RepeatableVariation;

        public string Describe()
        {
            return StyleName(Style)
                + ", safety-floor=" + FormatPixels(SafetyMargin)
                + ", wander=" + WanderPixels + "px"
                + ", deadband=" + FormatPixels(TrackingDeadband)
                + ", tiny=" + (IncludeTinyDropletsAsHardConstraints ? "hard" : "soft")
                + ", fatigue=" + (FatigueEnabled ? "projected" : "off")
                + ", variation=" + (RepeatableVariation ? "repeatable" : "new each play");
        }

        public static string StyleName(CatchPathStyle style)
        {
            switch (style)
            {
                case CatchPathStyle.Centered: return "CENTERED";
                case CatchPathStyle.Lively: return "LIVELY";
                case CatchPathStyle.LastMoment: return "LAST MOMENT";
                default: return "SMOOTH";
            }
        }

        public static string FormatPixels(double value)
        {
            return value.ToString("0.##", CultureInfo.InvariantCulture) + "px";
        }
    }

    internal sealed class AgentRuntimeStatus
    {
        public AgentRuntimeStatus(string phase, string mapName, string detail)
        {
            Phase = String.IsNullOrEmpty(phase) ? "IDLE" : phase;
            MapName = mapName ?? String.Empty;
            Detail = detail ?? String.Empty;
        }

        public readonly string Phase;
        public readonly string MapName;
        public readonly string Detail;

        public static AgentRuntimeStatus Idle(string detail)
        {
            return new AgentRuntimeStatus("IDLE", String.Empty, detail);
        }
    }

    internal sealed class AgentOverlaySnapshot
    {
        public AgentOverlaySnapshot(
            AgentOptionsSnapshot options,
            bool menuVisible,
            int selectedRow,
            AgentRuntimeStatus runtime)
        {
            Options = options;
            MenuVisible = menuVisible;
            SelectedRow = selectedRow;
            Runtime = runtime;
        }

        public readonly AgentOptionsSnapshot Options;
        public readonly bool MenuVisible;
        public readonly int SelectedRow;
        public readonly AgentRuntimeStatus Runtime;
    }

    internal sealed class AgentControlState
    {
        public const int RowCount = 8;

        private readonly object sync = new object();
        private bool enabled;
        private CatchPathStyle style;
        private double safetyMargin;
        private int wanderPixels;
        private double trackingDeadband;
        private bool tinyHard;
        private bool fatigueEnabled;
        private bool repeatableVariation;
        private bool menuVisible;
        private int selectedRow;
        private AgentRuntimeStatus runtime;

        public AgentControlState(bool initiallyEnabled)
        {
            enabled = initiallyEnabled;
            style = CatchPathStyle.Smooth;
            ApplyStyleDefaults(style);
            menuVisible = true;
            selectedRow = 0;
            runtime = AgentRuntimeStatus.Idle(
                initiallyEnabled ? "waiting for Catch Player mode" : "Player mode: you are in control");
        }

        public AgentOptionsSnapshot GetOptions()
        {
            lock (sync) return CreateOptions();
        }

        public AgentOverlaySnapshot GetOverlaySnapshot()
        {
            lock (sync)
                return new AgentOverlaySnapshot(CreateOptions(), menuVisible, selectedRow, runtime);
        }

        public bool IsMenuVisible
        {
            get { lock (sync) return menuVisible; }
        }

        public string ToggleEnabled()
        {
            lock (sync)
            {
                enabled = !enabled;
                return "agent=" + (enabled ? "on" : "off (Player controls)");
            }
        }

        public void Disable()
        {
            lock (sync) enabled = false;
        }

        public string ToggleMenu()
        {
            lock (sync)
            {
                menuVisible = !menuVisible;
                return "overlay menu=" + (menuVisible ? "open" : "closed");
            }
        }

        public void MoveSelection(int delta)
        {
            lock (sync)
            {
                selectedRow = (selectedRow + delta) % RowCount;
                if (selectedRow < 0) selectedRow += RowCount;
            }
        }

        public string AdjustSelected(int delta)
        {
            lock (sync)
            {
                switch (selectedRow)
                {
                    case 0:
                        enabled = !enabled;
                        return "agent=" + (enabled ? "on" : "off (Player controls)");
                    case 1:
                        style = Cycle(style, delta);
                        ApplyStyleDefaults(style);
                        return "style=" + AgentOptionsSnapshot.StyleName(style) + " (style defaults applied)";
                    case 2:
                        safetyMargin = Clamp(safetyMargin + 0.25 * Math.Sign(delta), 0.5, 1.5);
                        return "safety floor=" + AgentOptionsSnapshot.FormatPixels(safetyMargin);
                    case 3:
                        wanderPixels = Clamp(wanderPixels + Math.Sign(delta), 0, 12);
                        return "wander=" + wanderPixels + "px";
                    case 4:
                        trackingDeadband = Clamp(trackingDeadband + Math.Sign(delta), 2.0, 12.0);
                        return "deadband=" + AgentOptionsSnapshot.FormatPixels(trackingDeadband);
                    case 5:
                        tinyHard = !tinyHard;
                        return "tiny droplets=" + (tinyHard ? "hard constraints" : "soft/optional");
                    case 6:
                        fatigueEnabled = !fatigueEnabled;
                        return "fatigue=" + (fatigueEnabled ? "projected" : "off");
                    case 7:
                        repeatableVariation = !repeatableVariation;
                        return "variation=" + (repeatableVariation ? "repeatable" : "new each play");
                    default:
                        return String.Empty;
                }
            }
        }

        public void UpdateRuntime(AgentRuntimeStatus value)
        {
            lock (sync) runtime = value ?? AgentRuntimeStatus.Idle(String.Empty);
        }

        private AgentOptionsSnapshot CreateOptions()
        {
            return new AgentOptionsSnapshot(
                enabled,
                style,
                safetyMargin,
                wanderPixels,
                trackingDeadband,
                tinyHard,
                fatigueEnabled,
                repeatableVariation);
        }

        private void ApplyStyleDefaults(CatchPathStyle value)
        {
            safetyMargin = 1.0;
            tinyHard = true;
            fatigueEnabled = false;
            repeatableVariation = true;
            if (value == CatchPathStyle.Centered)
            {
                wanderPixels = 0;
                trackingDeadband = 3.0;
            }
            else if (value == CatchPathStyle.Lively)
            {
                wanderPixels = 8;
                trackingDeadband = 5.0;
                fatigueEnabled = true;
            }
            else if (value == CatchPathStyle.LastMoment)
            {
                wanderPixels = 3;
                trackingDeadband = 4.0;
            }
            else
            {
                wanderPixels = 3;
                trackingDeadband = 4.0;
            }
        }

        private static CatchPathStyle Cycle(CatchPathStyle value, int delta)
        {
            int count = Enum.GetValues(typeof(CatchPathStyle)).Length;
            int next = ((int)value + Math.Sign(delta)) % count;
            if (next < 0) next += count;
            return (CatchPathStyle)next;
        }

        private static double Clamp(double value, double minimum, double maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }

        private static int Clamp(int value, int minimum, int maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }
    }
}
