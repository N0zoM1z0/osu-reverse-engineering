using System;
using System.Globalization;

namespace LocalManiaAuto.Plugin
{
    internal enum HumanStyle
    {
        Clean = 0,
        Human = 1,
        Tired = 2,
        Chaos = 3
    }

    internal enum FrameCadence
    {
        Native = 0,
        Hz240 = 1,
        Hz120 = 2,
        Hz60 = 3
    }

    internal sealed class AgentOptionsSnapshot
    {
        public AgentOptionsSnapshot(
            bool enabled,
            HumanStyle style,
            int baseUnstableRate,
            int timingBiasMilliseconds,
            int rushPercent,
            int grade200Permille,
            int grade100Permille,
            int denseBoostPercent,
            FrameCadence frameCadence,
            bool fatigueEnabled,
            int fingerTroublePercent,
            bool repeatableVariation)
        {
            Enabled = enabled;
            Style = style;
            BaseUnstableRate = baseUnstableRate;
            TimingBiasMilliseconds = timingBiasMilliseconds;
            RushPercent = rushPercent;
            Grade200Permille = grade200Permille;
            Grade100Permille = grade100Permille;
            DenseBoostPercent = denseBoostPercent;
            FrameCadence = frameCadence;
            FatigueEnabled = fatigueEnabled;
            FingerTroublePercent = fingerTroublePercent;
            RepeatableVariation = repeatableVariation;
        }

        public readonly bool Enabled;
        public readonly HumanStyle Style;
        public readonly int BaseUnstableRate;
        public readonly int TimingBiasMilliseconds;
        public readonly int RushPercent;
        public readonly int Grade200Permille;
        public readonly int Grade100Permille;
        public readonly int DenseBoostPercent;
        public readonly FrameCadence FrameCadence;
        public readonly bool FatigueEnabled;
        public readonly int FingerTroublePercent;
        public readonly bool RepeatableVariation;

        public string Describe()
        {
            return StyleName(Style)
                + ", base-UR=" + BaseUnstableRate
                + ", bias=" + SignedMilliseconds(TimingBiasMilliseconds)
                + ", rush=" + RushPercent + "%"
                + ", 200=" + GradePercent(Grade200Permille)
                + ", 100=" + GradePercent(Grade100Permille)
                + ", dense-boost=" + DenseBoostPercent + "%"
                + ", frame=" + FrameName(FrameCadence)
                + ", fatigue=" + (FatigueEnabled ? "on" : "off")
                + ", finger-trouble=" + FingerTroublePercent + "%"
                + ", variation=" + (RepeatableVariation ? "repeatable" : "new-each-play");
        }

        public static string StyleName(HumanStyle style)
        {
            switch (style)
            {
                case HumanStyle.Clean: return "CLEAN";
                case HumanStyle.Human: return "HUMAN";
                case HumanStyle.Tired: return "TIRED";
                case HumanStyle.Chaos: return "CHAOS";
                default: return style.ToString().ToUpperInvariant();
            }
        }

        public static string FrameName(FrameCadence cadence)
        {
            switch (cadence)
            {
                case FrameCadence.Native: return "NATIVE";
                case FrameCadence.Hz240: return "240 Hz";
                case FrameCadence.Hz120: return "120 Hz";
                case FrameCadence.Hz60: return "60 Hz";
                default: return cadence.ToString().ToUpperInvariant();
            }
        }

        public static double FramePeriod(FrameCadence cadence)
        {
            switch (cadence)
            {
                case FrameCadence.Hz240: return 1000.0 / 240.0;
                case FrameCadence.Hz120: return 1000.0 / 120.0;
                case FrameCadence.Hz60: return 1000.0 / 60.0;
                default: return 0.0;
            }
        }

        public static string GradePercent(int permille)
        {
            return (permille / 10.0).ToString("0.0", CultureInfo.InvariantCulture) + "%";
        }

        public static string SignedMilliseconds(int value)
        {
            return (value > 0 ? "+" : String.Empty) + value + "ms";
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
        public const int RowCount = 12;

        private readonly object sync = new object();
        private bool enabled;
        private HumanStyle style;
        private int baseUnstableRate;
        private int timingBiasMilliseconds;
        private int rushPercent;
        private int grade200Permille;
        private int grade100Permille;
        private int denseBoostPercent;
        private FrameCadence frameCadence;
        private bool fatigueEnabled;
        private int fingerTroublePercent;
        private bool repeatableVariation;
        private bool menuVisible;
        private int selectedRow;
        private AgentRuntimeStatus runtime;

        public AgentControlState(bool initiallyEnabled)
        {
            enabled = initiallyEnabled;
            menuVisible = true;
            selectedRow = 0;
            runtime = AgentRuntimeStatus.Idle(
                initiallyEnabled ? "waiting for mania Player mode" : "Player mode: you are in control");
            ApplyStyleDefaults(HumanStyle.Human);
        }

        public AgentOptionsSnapshot GetOptions()
        {
            lock (sync)
                return CreateOptions();
        }

        public AgentOverlaySnapshot GetOverlaySnapshot()
        {
            lock (sync)
                return new AgentOverlaySnapshot(CreateOptions(), menuVisible, selectedRow, runtime);
        }

        public bool IsMenuVisible
        {
            get
            {
                lock (sync)
                    return menuVisible;
            }
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
            lock (sync)
                enabled = false;
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
                if (selectedRow < 0)
                    selectedRow += RowCount;
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
                        int styleValue = ((int)style + delta) % 4;
                        if (styleValue < 0)
                            styleValue += 4;
                        ApplyStyleDefaults((HumanStyle)styleValue);
                        return "style defaults loaded: " + CreateOptions().Describe();

                    case 2:
                        baseUnstableRate = Clamp(baseUnstableRate + (delta < 0 ? -5 : 5), 0, 200);
                        return "base-UR=" + baseUnstableRate;

                    case 3:
                        timingBiasMilliseconds = Clamp(
                            timingBiasMilliseconds + (delta < 0 ? -2 : 2),
                            -30,
                            30);
                        return "timing bias=" + AgentOptionsSnapshot.SignedMilliseconds(timingBiasMilliseconds);

                    case 4:
                        rushPercent = Clamp(rushPercent + (delta < 0 ? -5 : 5), 0, 50);
                        return "rush mix=" + rushPercent + "%";

                    case 5:
                        grade200Permille = Clamp(
                            grade200Permille + (delta < 0 ? -5 : 5),
                            0,
                            150);
                        return "200 mix=" + AgentOptionsSnapshot.GradePercent(grade200Permille);

                    case 6:
                        grade100Permille = Clamp(
                            grade100Permille + (delta < 0 ? -1 : 1),
                            0,
                            50);
                        return "100 mix=" + AgentOptionsSnapshot.GradePercent(grade100Permille);

                    case 7:
                        denseBoostPercent = Clamp(
                            denseBoostPercent + (delta < 0 ? -25 : 25),
                            0,
                            250);
                        return "dense boost=" + denseBoostPercent + "%";

                    case 8:
                        int frameValue = ((int)frameCadence + delta) % 4;
                        if (frameValue < 0)
                            frameValue += 4;
                        frameCadence = (FrameCadence)frameValue;
                        return "frame cadence=" + AgentOptionsSnapshot.FrameName(frameCadence);

                    case 9:
                        fatigueEnabled = !fatigueEnabled;
                        return "fatigue=" + (fatigueEnabled ? "on" : "off");

                    case 10:
                        fingerTroublePercent = Clamp(
                            fingerTroublePercent + (delta < 0 ? -1 : 1),
                            0,
                            10);
                        return "finger-trouble=" + fingerTroublePercent + "%";

                    case 11:
                        repeatableVariation = !repeatableVariation;
                        return "variation=" + (repeatableVariation ? "repeatable" : "new each play");

                    default:
                        return "settings unchanged";
                }
            }
        }

        public void UpdateRuntime(AgentRuntimeStatus status)
        {
            if (status == null)
                return;
            lock (sync)
                runtime = status;
        }

        private AgentOptionsSnapshot CreateOptions()
        {
            return new AgentOptionsSnapshot(
                enabled,
                style,
                baseUnstableRate,
                timingBiasMilliseconds,
                rushPercent,
                grade200Permille,
                grade100Permille,
                denseBoostPercent,
                frameCadence,
                fatigueEnabled,
                fingerTroublePercent,
                repeatableVariation);
        }

        private void ApplyStyleDefaults(HumanStyle value)
        {
            style = value;
            switch (style)
            {
                case HumanStyle.Clean:
                    baseUnstableRate = 0;
                    timingBiasMilliseconds = 0;
                    rushPercent = 0;
                    grade200Permille = 0;
                    grade100Permille = 0;
                    denseBoostPercent = 0;
                    frameCadence = FrameCadence.Native;
                    fatigueEnabled = false;
                    fingerTroublePercent = 0;
                    repeatableVariation = true;
                    break;

                case HumanStyle.Human:
                    baseUnstableRate = 65;
                    timingBiasMilliseconds = -4;
                    rushPercent = 20;
                    grade200Permille = 10;
                    grade100Permille = 2;
                    denseBoostPercent = 125;
                    frameCadence = FrameCadence.Hz240;
                    fatigueEnabled = false;
                    fingerTroublePercent = 1;
                    repeatableVariation = false;
                    break;

                case HumanStyle.Tired:
                    baseUnstableRate = 90;
                    timingBiasMilliseconds = -2;
                    rushPercent = 15;
                    grade200Permille = 25;
                    grade100Permille = 7;
                    denseBoostPercent = 150;
                    frameCadence = FrameCadence.Hz120;
                    fatigueEnabled = true;
                    fingerTroublePercent = 3;
                    repeatableVariation = false;
                    break;

                case HumanStyle.Chaos:
                    baseUnstableRate = 120;
                    timingBiasMilliseconds = -5;
                    rushPercent = 30;
                    grade200Permille = 60;
                    grade100Permille = 20;
                    denseBoostPercent = 200;
                    frameCadence = FrameCadence.Hz60;
                    fatigueEnabled = true;
                    fingerTroublePercent = 7;
                    repeatableVariation = false;
                    break;
            }
        }

        private static int Clamp(int value, int minimum, int maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }
    }
}
