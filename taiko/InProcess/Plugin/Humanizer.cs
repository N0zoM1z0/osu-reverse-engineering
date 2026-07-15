using System;
using System.Collections.Generic;
using System.Globalization;

namespace LocalTaikoAgent.Plugin
{
    internal sealed class HumanizedPlanResult
    {
        public HumanizedPlanResult(
            LiveTaikoPlan plan,
            int seed,
            int safeHitWindow,
            int hit300Window,
            int hit100Window,
            int grade300,
            int grade100,
            int miss,
            int rushedNotes,
            int jammedPresses,
            int splitStrongNotes,
            int minimumOffset,
            int maximumOffset,
            double meanOffset,
            double standardDeviation,
            double earlyPercent)
        {
            Plan = plan;
            Seed = seed;
            SafeHitWindow = safeHitWindow;
            Hit300Window = hit300Window;
            Hit100Window = hit100Window;
            Grade300 = grade300;
            Grade100 = grade100;
            Miss = miss;
            RushedNotes = rushedNotes;
            JammedPresses = jammedPresses;
            SplitStrongNotes = splitStrongNotes;
            MinimumOffset = minimumOffset;
            MaximumOffset = maximumOffset;
            MeanOffset = meanOffset;
            StandardDeviation = standardDeviation;
            EarlyPercent = earlyPercent;
        }

        public readonly LiveTaikoPlan Plan;
        public readonly int Seed;
        public readonly int SafeHitWindow;
        public readonly int Hit300Window;
        public readonly int Hit100Window;
        public readonly int Grade300;
        public readonly int Grade100;
        public readonly int Miss;
        public readonly int RushedNotes;
        public readonly int JammedPresses;
        public readonly int SplitStrongNotes;
        public readonly int MinimumOffset;
        public readonly int MaximumOffset;
        public readonly double MeanOffset;
        public readonly double StandardDeviation;
        public readonly double EarlyPercent;

        public double UnstableRate
        {
            get { return StandardDeviation * 10.0; }
        }

        public string Describe(AgentOptionsSnapshot options)
        {
            return options.Describe()
                + ", seed=" + Seed
                + ", actual-UR=" + UnstableRate.ToString("0.0", CultureInfo.InvariantCulture)
                + ", mean=" + MeanOffset.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) + "ms"
                + ", early=" + EarlyPercent.ToString("0.0", CultureInfo.InvariantCulture) + "%"
                + ", windows=<" + Hit300Window + "/<" + Hit100Window + "ms"
                + ", predicted=300:" + Grade300 + "/100:" + Grade100 + "/miss:" + Miss
                + ", rush-notes=" + RushedNotes
                + ", jams=" + JammedPresses
                + ", strong-splits=" + SplitStrongNotes
                + ", realized-offset=" + MinimumOffset + ".." + MaximumOffset + "ms";
        }
    }

    internal static class Humanizer
    {
        private const int EasyBit = 0x2;
        private const int HardRockBit = 0x10;

        private sealed class RequiredState
        {
            public int StrikeIndex;
            public int ReferenceTime;
            public double Density;
            public double RawError;
            public bool RushActive;
            public double FrameWander;
            public bool FrameHitch;
        }

        private sealed class StyleParameters
        {
            public double CorrelationMilliseconds;
            public double RushMinimumSigma;
            public double RushMaximumSigma;
            public double FatigueLateSigma;
            public double FrameHitchProbability;
            public int JamMinimum;
            public int JamMaximum;
        }

        public static HumanizedPlanResult Apply(
            LiveTaikoPlan source,
            AgentOptionsSnapshot options,
            int selectedMods,
            int tapMilliseconds,
            int? seedOverride)
        {
            if (source == null) throw new ArgumentNullException("source");
            if (options == null) throw new ArgumentNullException("options");
            ValidateOptions(options);

            int hit300Window;
            int hit100Window;
            CalculateWindows(source.OverallDifficulty, selectedMods, out hit300Window, out hit100Window);
            double framePeriod = AgentOptionsSnapshot.FramePeriod(options.FrameCadence);
            int safetyGuard = Math.Max(3, (int)Math.Ceiling(framePeriod * 0.60));
            int safeHitWindow = Math.Max(hit300Window, hit100Window - safetyGuard);
            int seed = seedOverride.HasValue
                ? seedOverride.Value
                : CreateSeed(source.Path, options, selectedMods);
            Random random = new Random(seed == Int32.MinValue ? 0 : Math.Abs(seed));
            StyleParameters parameters = ParametersFor(options.Style);

            List<LiveTaikoStrike> strikes = new List<LiveTaikoStrike>(source.Strikes.Count);
            List<RequiredState> required = new List<RequiredState>();
            for (int index = 0; index < source.Strikes.Count; index++)
            {
                LiveTaikoStrike clone = source.Strikes[index].Clone();
                strikes.Add(clone);
                if (clone.RequiredForCombo)
                {
                    required.Add(new RequiredState
                    {
                        StrikeIndex = index,
                        ReferenceTime = clone.ReferenceTime
                    });
                }
            }
            if (required.Count == 0)
                throw new InvalidOperationException("humanizer received no combo-relevant Taiko circles");
            CalculateDensity(required);

            double framePhase = framePeriod > 0.0 ? random.NextDouble() * framePeriod : 0.0;
            double arState = NextGaussian(random);
            double frameWander = 0.0;
            int previousTime = required[0].ReferenceTime;
            int rushRemaining = 0;
            double rushAmplitude = 0.0;
            int rushedNotes = 0;
            for (int index = 0; index < required.Count; index++)
            {
                RequiredState state = required[index];
                int delta = Math.Max(1, state.ReferenceTime - previousTime);
                double rho = Math.Exp(-delta / parameters.CorrelationMilliseconds);
                arState = rho * arState
                    + Math.Sqrt(Math.Max(0.0, 1.0 - rho * rho)) * NextGaussian(random);

                if (rushRemaining <= 0 && options.RushPercent > 0)
                {
                    double target = options.RushPercent / 100.0;
                    double startProbability = target / (5.0 * Math.Max(0.05, 1.0 - target));
                    if (random.NextDouble() < Math.Min(0.65, startProbability))
                    {
                        rushRemaining = NextInclusive(random, 3, 7);
                        rushAmplitude = parameters.RushMinimumSigma
                            + random.NextDouble()
                                * (parameters.RushMaximumSigma - parameters.RushMinimumSigma);
                    }
                }
                bool rushing = rushRemaining > 0;
                double rushShift = rushing
                    ? -rushAmplitude * (0.72 + 0.28 * rushRemaining / 7.0)
                    : 0.0;
                if (rushing)
                {
                    rushRemaining--;
                    rushedNotes++;
                }

                bool hitch = framePeriod > 0.0
                    && random.NextDouble() < parameters.FrameHitchProbability;
                if (framePeriod > 0.0)
                    frameWander = frameWander * 0.88 + NextGaussian(random) * framePeriod * 0.05;

                double progress = required.Count <= 1 ? 0.0 : index / (double)(required.Count - 1);
                double fatigue = options.FatigueEnabled
                    ? parameters.FatigueLateSigma * progress * progress
                    : 0.0;
                state.RawError = arState * 0.76
                    + NextGaussian(random) * (0.40 + state.Density * 0.24)
                    + rushShift
                    + fatigue;
                state.RushActive = rushing;
                state.FrameWander = frameWander;
                state.FrameHitch = hitch;
                previousTime = state.ReferenceTime;
            }

            double rawMean;
            double rawDeviation;
            MeanAndDeviation(required, out rawMean, out rawDeviation);
            double targetSigma = options.BaseUnstableRate / 10.0;
            int jammedPresses = 0;
            for (int index = 0; index < required.Count; index++)
            {
                RequiredState state = required[index];
                LiveTaikoStrike strike = strikes[state.StrikeIndex];
                double normalized = rawDeviation > 0.000001
                    ? (state.RawError - rawMean) / rawDeviation
                    : 0.0;
                int offset = (int)Math.Round(
                    options.TimingBiasMilliseconds + targetSigma * normalized,
                    MidpointRounding.AwayFromZero);

                if (Roll(random, options.FingerTroublePercent))
                {
                    offset += NextInclusive(random, parameters.JamMinimum, parameters.JamMaximum);
                    jammedPresses++;
                }

                double desired = strike.ReferenceTime + offset;
                desired = QuantizeFrame(
                    desired,
                    framePeriod,
                    framePhase,
                    state.FrameWander,
                    state.FrameHitch);

                double probability100 = options.Grade100Permille / 1000.0
                    * (1.0 + options.DenseBoostPercent / 100.0 * state.Density);
                probability100 = Math.Min(0.35, probability100);
                if (random.NextDouble() < probability100)
                {
                    bool early = ChooseEarly(random, state, options);
                    int magnitude = Sample100Band(random, hit300Window, hit100Window);
                    desired = strike.ReferenceTime + (early ? -magnitude : magnitude);
                    desired = QuantizeFrame(
                        desired,
                        framePeriod,
                        framePhase,
                        state.FrameWander,
                        false);
                    int quantizedOffset = (int)Math.Round(
                        desired - strike.ReferenceTime,
                        MidpointRounding.AwayFromZero);
                    desired = strike.ReferenceTime
                        + SnapTo100Band(quantizedOffset, hit300Window, hit100Window, early);
                }

                int realizedOffset = (int)Math.Round(
                    desired - strike.ReferenceTime,
                    MidpointRounding.AwayFromZero);
                realizedOffset = Math.Max(-safeHitWindow, Math.Min(safeHitWindow, realizedOffset));
                strike.Time = checked(strike.ReferenceTime + realizedOffset);
            }

            EnforceCircleOrder(required, strikes, safeHitWindow);
            HumanizeBonusStrikes(strikes, random, targetSigma, framePeriod, framePhase);

            int splitStrongNotes = 0;
            for (int index = 0; index < required.Count; index++)
            {
                LiveTaikoStrike strike = strikes[required[index].StrikeIndex];
                if (!strike.IsStrong || strike.Keys.Length != 2
                    || options.StrongSplitMaximumMilliseconds <= 0)
                {
                    continue;
                }
                int maximum = Math.Min(20, options.StrongSplitMaximumMilliseconds);
                if (index + 1 < required.Count)
                {
                    LiveTaikoStrike next = strikes[required[index + 1].StrikeIndex];
                    maximum = Math.Min(maximum, Math.Max(0, next.Time - strike.Time - 2));
                }
                if (maximum <= 0)
                    continue;
                int split = NextInclusive(random, 1, maximum);
                int delayedHand = random.Next(0, 2);
                strike.KeyDelays[delayedHand] = split;
                splitStrongNotes++;
            }

            LiveTaikoPlan plan = LivePlanBuilder.Rebuild(source, strikes, tapMilliseconds);
            int minimumOffset = Int32.MaxValue;
            int maximumOffset = Int32.MinValue;
            int earlyCount = 0;
            int grade300 = 0;
            int grade100 = 0;
            int miss = 0;
            double sum = 0.0;
            for (int index = 0; index < required.Count; index++)
            {
                LiveTaikoStrike strike = strikes[required[index].StrikeIndex];
                int offset = strike.Time - strike.ReferenceTime;
                minimumOffset = Math.Min(minimumOffset, offset);
                maximumOffset = Math.Max(maximumOffset, offset);
                sum += offset;
                if (offset < 0) earlyCount++;
                int absolute = Math.Abs(offset);
                if (absolute < hit300Window) grade300++;
                else if (absolute < hit100Window) grade100++;
                else miss++;
            }
            double mean = sum / required.Count;
            double variance = 0.0;
            for (int index = 0; index < required.Count; index++)
            {
                LiveTaikoStrike strike = strikes[required[index].StrikeIndex];
                double delta = strike.Time - strike.ReferenceTime - mean;
                variance += delta * delta;
            }
            double deviation = Math.Sqrt(variance / required.Count);
            return new HumanizedPlanResult(
                plan,
                seed,
                safeHitWindow,
                hit300Window,
                hit100Window,
                grade300,
                grade100,
                miss,
                rushedNotes,
                jammedPresses,
                splitStrongNotes,
                minimumOffset,
                maximumOffset,
                mean,
                deviation,
                earlyCount * 100.0 / required.Count);
        }

        private static void HumanizeBonusStrikes(
            List<LiveTaikoStrike> strikes,
            Random random,
            double targetSigma,
            double framePeriod,
            double framePhase)
        {
            for (int index = 0; index < strikes.Count; index++)
            {
                LiveTaikoStrike strike = strikes[index];
                if (strike.RequiredForCombo)
                    continue;
                double spread = Math.Min(5.0, Math.Max(0.8, targetSigma * 0.30));
                double desired = strike.ReferenceTime + NextGaussian(random) * spread;
                desired = QuantizeFrame(desired, framePeriod, framePhase, 0.0, false);
                int offset = (int)Math.Round(desired - strike.ReferenceTime);
                offset = Math.Max(-10, Math.Min(10, offset));
                int lower = strike.ObjectStart;
                int upper = Math.Max(lower, strike.ObjectEnd - 1);
                strike.Time = Math.Max(lower, Math.Min(upper, strike.ReferenceTime + offset));
            }
        }

        private static void EnforceCircleOrder(
            List<RequiredState> required,
            List<LiveTaikoStrike> strikes,
            int maximumAbsolute)
        {
            int[] latest = new int[required.Count];
            latest[required.Count - 1] = checked(
                required[required.Count - 1].ReferenceTime + maximumAbsolute);
            for (int index = required.Count - 2; index >= 0; index--)
            {
                int high = checked(required[index].ReferenceTime + maximumAbsolute);
                latest[index] = Math.Min(high, latest[index + 1] - 1);
            }
            int previous = Int32.MinValue;
            for (int index = 0; index < required.Count; index++)
            {
                LiveTaikoStrike strike = strikes[required[index].StrikeIndex];
                int low = checked(required[index].ReferenceTime - maximumAbsolute);
                if (previous != Int32.MinValue)
                    low = Math.Max(low, previous + 1);
                if (low > latest[index])
                    throw new InvalidOperationException("no miss-safe physical Taiko timing exists");
                strike.Time = Math.Max(low, Math.Min(latest[index], strike.Time));
                previous = strike.Time;
            }
        }

        private static void CalculateDensity(List<RequiredState> required)
        {
            int left = 0;
            int right = 0;
            for (int index = 0; index < required.Count; index++)
            {
                int time = required[index].ReferenceTime;
                while (left < required.Count && required[left].ReferenceTime < time - 500)
                    left++;
                if (right < index) right = index;
                while (right < required.Count && required[right].ReferenceTime <= time + 500)
                    right++;
                double notesPerSecond = right - left;
                required[index].Density = Clamp01((notesPerSecond - 4.0) / 14.0);
            }
        }

        private static void MeanAndDeviation(
            List<RequiredState> states,
            out double mean,
            out double deviation)
        {
            double sum = 0.0;
            for (int index = 0; index < states.Count; index++)
                sum += states[index].RawError;
            mean = sum / states.Count;
            double variance = 0.0;
            for (int index = 0; index < states.Count; index++)
            {
                double delta = states[index].RawError - mean;
                variance += delta * delta;
            }
            deviation = Math.Sqrt(variance / states.Count);
        }

        private static void CalculateWindows(
            double overallDifficulty,
            int selectedMods,
            out int hit300,
            out int hit100)
        {
            double difficulty = overallDifficulty;
            if ((selectedMods & EasyBit) != 0)
                difficulty = Math.Max(0.0, difficulty / 2.0);
            if ((selectedMods & HardRockBit) != 0)
                difficulty = Math.Min(10.0, difficulty * 1.4);
            hit300 = (int)DifficultyRange(difficulty, 80.0, 50.0, 20.0);
            hit100 = (int)DifficultyRange(difficulty, 140.0, 100.0, 60.0);
        }

        private static double DifficultyRange(double difficulty, double minimum, double middle, double maximum)
        {
            return difficulty > 5.0
                ? middle + (maximum - middle) * (difficulty - 5.0) / 5.0
                : middle - (middle - minimum) * (5.0 - difficulty) / 5.0;
        }

        private static int Sample100Band(Random random, int hit300, int hit100)
        {
            int minimum = Math.Min(hit100 - 1, hit300 + 3);
            int maximum = Math.Max(minimum, hit100 - 4);
            double inward = Math.Min(random.NextDouble(), random.NextDouble());
            return (int)Math.Round(
                minimum + inward * (maximum - minimum),
                MidpointRounding.AwayFromZero);
        }

        private static int SnapTo100Band(int offset, int hit300, int hit100, bool early)
        {
            int minimum = Math.Min(hit100 - 1, hit300 + 2);
            int maximum = Math.Max(minimum, hit100 - 2);
            int magnitude = Math.Max(minimum, Math.Min(maximum, Math.Abs(offset)));
            return early ? -magnitude : magnitude;
        }

        private static bool ChooseEarly(
            Random random,
            RequiredState state,
            AgentOptionsSnapshot options)
        {
            double probability = 0.50;
            if (state.RushActive) probability += 0.34;
            probability += Math.Max(-0.12, Math.Min(0.12, -options.TimingBiasMilliseconds / 100.0));
            return random.NextDouble() < Math.Max(0.08, Math.Min(0.92, probability));
        }

        private static double QuantizeFrame(
            double desired,
            double period,
            double phase,
            double wander,
            bool hitch)
        {
            if (period <= 0.0) return desired;
            double movingPhase = phase + wander;
            double frame = movingPhase
                + Math.Ceiling((desired - movingPhase - period * 0.5) / period) * period;
            if (hitch) frame += period;
            return frame;
        }

        private static StyleParameters ParametersFor(HumanStyle style)
        {
            switch (style)
            {
                case HumanStyle.Clean:
                    return new StyleParameters
                    {
                        CorrelationMilliseconds = 1100.0,
                        RushMinimumSigma = 0.7,
                        RushMaximumSigma = 1.1,
                        FatigueLateSigma = 0.2,
                        FrameHitchProbability = 0.0,
                        JamMinimum = 5,
                        JamMaximum = 12
                    };
                case HumanStyle.Human:
                    return new StyleParameters
                    {
                        CorrelationMilliseconds = 900.0,
                        RushMinimumSigma = 0.9,
                        RushMaximumSigma = 1.7,
                        FatigueLateSigma = 0.6,
                        FrameHitchProbability = 0.0007,
                        JamMinimum = 8,
                        JamMaximum = 22
                    };
                case HumanStyle.Tired:
                    return new StyleParameters
                    {
                        CorrelationMilliseconds = 1250.0,
                        RushMinimumSigma = 0.8,
                        RushMaximumSigma = 1.5,
                        FatigueLateSigma = 1.2,
                        FrameHitchProbability = 0.0018,
                        JamMinimum = 12,
                        JamMaximum = 30
                    };
                case HumanStyle.Chaos:
                    return new StyleParameters
                    {
                        CorrelationMilliseconds = 700.0,
                        RushMinimumSigma = 1.1,
                        RushMaximumSigma = 2.1,
                        FatigueLateSigma = 1.5,
                        FrameHitchProbability = 0.0035,
                        JamMinimum = 15,
                        JamMaximum = 38
                    };
                default:
                    throw new ArgumentOutOfRangeException("style");
            }
        }

        private static void ValidateOptions(AgentOptionsSnapshot options)
        {
            if (options.BaseUnstableRate < 0 || options.BaseUnstableRate > 180)
                throw new ArgumentOutOfRangeException("options", "base UR must be 0..180");
            if (options.TimingBiasMilliseconds < -30 || options.TimingBiasMilliseconds > 30)
                throw new ArgumentOutOfRangeException("options", "timing bias must be -30..30ms");
            if (options.RushPercent < 0 || options.RushPercent > 50)
                throw new ArgumentOutOfRangeException("options", "rush must be 0..50 percent");
            if (options.Grade100Permille < 0 || options.Grade100Permille > 100)
                throw new ArgumentOutOfRangeException("options", "100 mix must be 0..10 percent");
            if (options.DenseBoostPercent < 0 || options.DenseBoostPercent > 300)
                throw new ArgumentOutOfRangeException("options", "dense boost must be 0..300 percent");
            if (options.StrongSplitMaximumMilliseconds < 0
                || options.StrongSplitMaximumMilliseconds > 20)
            {
                throw new ArgumentOutOfRangeException("options", "strong split must be 0..20ms");
            }
            if (options.FingerTroublePercent < 0 || options.FingerTroublePercent > 10)
                throw new ArgumentOutOfRangeException("options", "finger trouble must be 0..10 percent");
        }

        private static int CreateSeed(string path, AgentOptionsSnapshot options, int selectedMods)
        {
            if (!options.RepeatableVariation)
            {
                int value = unchecked(Environment.TickCount ^ Guid.NewGuid().GetHashCode() ^ path.GetHashCode());
                return value == Int32.MinValue ? 0 : Math.Abs(value);
            }
            string material = path.ToUpperInvariant()
                + "|" + (int)options.Style
                + "|" + options.BaseUnstableRate
                + "|" + options.TimingBiasMilliseconds
                + "|" + options.RushPercent
                + "|" + options.Grade100Permille
                + "|" + options.DenseBoostPercent
                + "|" + options.StrongSplitMaximumMilliseconds
                + "|" + (int)options.FrameCadence
                + "|" + options.FatigueEnabled
                + "|" + options.FingerTroublePercent
                + "|" + selectedMods;
            uint hash = 2166136261u;
            for (int index = 0; index < material.Length; index++)
            {
                hash ^= material[index];
                hash *= 16777619u;
            }
            return (int)(hash & 0x7FFFFFFFu);
        }

        private static bool Roll(Random random, int percent)
        {
            return percent > 0 && random.Next(0, 100) < percent;
        }

        private static int NextInclusive(Random random, int minimum, int maximum)
        {
            return random.Next(minimum, checked(maximum + 1));
        }

        private static double NextGaussian(Random random)
        {
            double first = 1.0 - random.NextDouble();
            double second = 1.0 - random.NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(first)) * Math.Cos(2.0 * Math.PI * second);
        }

        private static double Clamp01(double value)
        {
            return Math.Max(0.0, Math.Min(1.0, value));
        }
    }
}
