using System;

namespace LocalTaikoAgent.Plugin
{
    internal static class InputTimingPolicy
    {
        private const int DoubleTimeBit = 0x40;
        private const int HalfTimeBit = 0x100;

        // This is a physical key-hold duration. The planner converts it to the
        // map clock so DT and HT preserve the same wall-clock pulse width.
        public const int DefaultPhysicalTapMilliseconds = 30;

        // A normally scheduled pulse already lasts DefaultPhysicalTapMilliseconds.
        // This guard only covers a late scheduler pass which sees both DOWN and UP
        // as overdue. Twenty milliseconds spans the observed p99 input-frame gap
        // without extending deliberately clipped dense-pattern pulses.
        public const int MaximumLateSamplingGuardMilliseconds = 20;

        public static double ClockRate(int selectedMods)
        {
            if ((selectedMods & DoubleTimeBit) != 0)
                return 1.5;
            if ((selectedMods & HalfTimeBit) != 0)
                return 0.75;
            return 1.0;
        }

        public static int ToMapPulseMilliseconds(int physicalMilliseconds, int selectedMods)
        {
            if (physicalMilliseconds < 1)
                throw new ArgumentOutOfRangeException("physicalMilliseconds");
            return Math.Max(
                1,
                checked((int)Math.Ceiling(physicalMilliseconds * ClockRate(selectedMods))));
        }

        public static int LateSamplingGuardMilliseconds(
            int physicalMilliseconds,
            int plannedMapMilliseconds,
            double clockRate)
        {
            if (physicalMilliseconds < 1)
                throw new ArgumentOutOfRangeException("physicalMilliseconds");
            if (plannedMapMilliseconds < 1)
                throw new ArgumentOutOfRangeException("plannedMapMilliseconds");
            if (clockRate <= 0.0 || Double.IsNaN(clockRate) || Double.IsInfinity(clockRate))
                throw new ArgumentOutOfRangeException("clockRate");

            int plannedPhysical = Math.Max(
                1,
                checked((int)Math.Ceiling(plannedMapMilliseconds / clockRate)));
            return Math.Min(
                plannedPhysical,
                Math.Min(physicalMilliseconds, MaximumLateSamplingGuardMilliseconds));
        }
    }
}
