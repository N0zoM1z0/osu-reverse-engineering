using System;
using System.Collections.Generic;
using System.Globalization;

namespace LocalManiaAuto.Plugin
{
    internal sealed class HumanizedPlanResult
    {
        public HumanizedPlanResult(
            LiveManiaPlan plan,
            int seed,
            int jammedPresses,
            int stickyReleases,
            int rushedNotes,
            int frameHitches,
            int minimumOffset,
            int maximumOffset,
            double meanOffset,
            double standardDeviation,
            double earlyPercent,
            int grade320,
            int grade300,
            int grade200,
            int grade100,
            int grade50,
            int miss,
            int safeHitWindow,
            int denseNotes,
            int denseGrade100,
            int sparseNotes,
            int sparseGrade100)
        {
            Plan = plan;
            Seed = seed;
            JammedPresses = jammedPresses;
            StickyReleases = stickyReleases;
            RushedNotes = rushedNotes;
            FrameHitches = frameHitches;
            MinimumOffset = minimumOffset;
            MaximumOffset = maximumOffset;
            MeanOffset = meanOffset;
            StandardDeviation = standardDeviation;
            EarlyPercent = earlyPercent;
            Grade320 = grade320;
            Grade300 = grade300;
            Grade200 = grade200;
            Grade100 = grade100;
            Grade50 = grade50;
            Miss = miss;
            SafeHitWindow = safeHitWindow;
            DenseNotes = denseNotes;
            DenseGrade100 = denseGrade100;
            SparseNotes = sparseNotes;
            SparseGrade100 = sparseGrade100;
        }

        public readonly LiveManiaPlan Plan;
        public readonly int Seed;
        public readonly int JammedPresses;
        public readonly int StickyReleases;
        public readonly int RushedNotes;
        public readonly int FrameHitches;
        public readonly int MinimumOffset;
        public readonly int MaximumOffset;
        public readonly double MeanOffset;
        public readonly double StandardDeviation;
        public readonly double EarlyPercent;
        public readonly int Grade320;
        public readonly int Grade300;
        public readonly int Grade200;
        public readonly int Grade100;
        public readonly int Grade50;
        public readonly int Miss;
        public readonly int SafeHitWindow;
        public readonly int DenseNotes;
        public readonly int DenseGrade100;
        public readonly int SparseNotes;
        public readonly int SparseGrade100;

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
                + ", grades=320:" + Grade320
                + "/300:" + Grade300
                + "/200:" + Grade200
                + "/100:" + Grade100
                + "/50:" + Grade50
                + "/miss:" + Miss
                + ", density-100=dense:" + DenseGrade100 + "/" + DenseNotes
                + " sparse:" + SparseGrade100 + "/" + SparseNotes
                + ", rush-notes=" + RushedNotes
                + ", jams=" + JammedPresses
                + ", sticky=" + StickyReleases
                + ", frame-hitches=" + FrameHitches
                + ", realized-offset=" + MinimumOffset + ".." + MaximumOffset + "ms";
        }
    }

    internal static class Humanizer
    {
        private const int EasyBit = 0x2;
        private const int HardRockBit = 0x10;
        private const int DoubleTimeBit = 0x40;
        private const int HalfTimeBit = 0x100;

        private sealed class StyleParameters
        {
            public double CorrelationMilliseconds;
            public double RushMinimumSigma;
            public double RushMaximumSigma;
            public double MaximumFatigueSigma;
            public double FrameHitchProbability;
            public int JamMinimum;
            public int JamMaximum;
            public int StickyMinimum;
            public int StickyMaximum;
        }

        private sealed class HitWindows
        {
            public int Grade320;
            public int Grade300;
            public int Grade200;
            public int Grade100;
            public int Grade50;
        }

        private sealed class NotePair
        {
            public int Lane;
            public int SourceLine;
            public int OriginalDown;
            public int OriginalUp;
            public bool HasDown;
            public bool HasUp;
            public int DesiredDown;
            public int DesiredUp;
            public double Density;
            public double RawError;
            public bool RushActive;
            public double FrameWander;
            public bool FrameHitch;
            public bool IsHold;
        }

        public static HumanizedPlanResult Apply(
            LiveManiaPlan source,
            AgentOptionsSnapshot options,
            int? seedOverride)
        {
            return Apply(source, options, 0, seedOverride);
        }

        public static HumanizedPlanResult Apply(
            LiveManiaPlan source,
            AgentOptionsSnapshot options,
            int selectedMods,
            int? seedOverride)
        {
            if (source == null)
                throw new ArgumentNullException("source");
            if (options == null)
                throw new ArgumentNullException("options");
            ValidateOptions(options);

            StyleParameters parameters = ParametersFor(options.Style);
            HitWindows windows = CalculateWindows(source.OverallDifficulty, selectedMods);
            int seed = seedOverride.HasValue
                ? seedOverride.Value
                : CreateSeed(source.Path, options, selectedMods);
            Random random = new Random(seed == Int32.MinValue ? 0 : Math.Abs(seed));
            List<NotePair> notes = PairNotes(source);
            CalculateDensity(notes, source.KeyCount);

            double framePeriod = AgentOptionsSnapshot.FramePeriod(options.FrameCadence);
            double framePhase = framePeriod > 0.0 ? random.NextDouble() * framePeriod : 0.0;
            double[] laneBias = new double[source.KeyCount];
            for (int lane = 0; lane < laneBias.Length; lane++)
                laneBias[lane] = NextGaussian(random) * 0.16;

            double arState = NextGaussian(random);
            double frameWander = 0.0;
            int previousGroupTime = notes[0].OriginalDown;
            int rushRemaining = 0;
            double rushAmplitude = 0.0;
            int rushedNotes = 0;
            int frameHitches = 0;
            int duration = Math.Max(1, source.LastObjectTime - source.FirstObjectTime);
            int groupStart = 0;
            while (groupStart < notes.Count)
            {
                int groupEnd = groupStart + 1;
                int groupTime = notes[groupStart].OriginalDown;
                while (groupEnd < notes.Count && notes[groupEnd].OriginalDown == groupTime)
                    groupEnd++;

                int delta = Math.Max(1, groupTime - previousGroupTime);
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
                    rushRemaining--;

                bool hitch = framePeriod > 0.0
                    && random.NextDouble() < parameters.FrameHitchProbability;
                if (hitch)
                    frameHitches++;
                if (framePeriod > 0.0)
                {
                    frameWander = frameWander * 0.88
                        + NextGaussian(random) * framePeriod * 0.055;
                }

                double groupError = arState * 0.78 + NextGaussian(random) * 0.24 + rushShift;
                int groupSize = groupEnd - groupStart;
                double rollDirection = random.Next(0, 2) == 0 ? -1.0 : 1.0;
                for (int index = groupStart; index < groupEnd; index++)
                {
                    NotePair note = notes[index];
                    double progress = Clamp01(
                        (note.OriginalDown - source.FirstObjectTime) / (double)duration);
                    double fatigueVariance = options.FatigueEnabled
                        ? 1.0 + progress * 0.55
                        : 1.0;
                    double independent = NextGaussian(random)
                        * 0.54
                        * (1.0 + note.Density * 0.34)
                        * fatigueVariance;
                    double roll = groupSize > 1
                        ? rollDirection * (index - groupStart - (groupSize - 1) / 2.0) * 0.10
                        : 0.0;
                    double fatigue = options.FatigueEnabled
                        ? parameters.MaximumFatigueSigma * progress * progress
                        : 0.0;
                    note.RawError = groupError
                        + independent
                        + laneBias[note.Lane]
                        + roll
                        + fatigue;
                    note.RushActive = rushing;
                    note.FrameWander = frameWander;
                    note.FrameHitch = hitch;
                    if (rushing)
                        rushedNotes++;
                }

                previousGroupTime = groupTime;
                groupStart = groupEnd;
            }

            double rawMean;
            double rawDeviation;
            MeanAndDeviation(notes, out rawMean, out rawDeviation);
            double targetSigma = options.BaseUnstableRate / 10.0;
            int jammedPresses = 0;
            int stickyReleases = 0;
            for (int index = 0; index < notes.Count; index++)
            {
                NotePair note = notes[index];
                double normalized = rawDeviation > 0.000001
                    ? (note.RawError - rawMean) / rawDeviation
                    : 0.0;
                int baseOffset = checked((int)Math.Round(
                    options.TimingBiasMilliseconds + targetSigma * normalized,
                    MidpointRounding.AwayFromZero));

                int jamDelay = 0;
                if (Roll(random, options.FingerTroublePercent))
                {
                    jamDelay = NextInclusive(random, parameters.JamMinimum, parameters.JamMaximum);
                    jammedPresses++;
                }

                int stickyDelay = 0;
                if (Roll(random, options.FingerTroublePercent))
                {
                    stickyDelay = NextInclusive(random, parameters.StickyMinimum, parameters.StickyMaximum);
                    stickyReleases++;
                }

                double desiredDown = note.OriginalDown + baseOffset + jamDelay;
                desiredDown = QuantizeFrame(
                    desiredDown,
                    framePeriod,
                    framePhase,
                    note.FrameWander,
                    note.FrameHitch);

                int forcedGrade = SelectLowGrade(random, note, options);
                if (forcedGrade != 0)
                {
                    int lower = forcedGrade == 100 ? windows.Grade200 : windows.Grade300;
                    int upper = forcedGrade == 100 ? windows.Grade100 : windows.Grade200;
                    bool early = ChooseEarly(random, note, options);
                    int magnitude = SampleSafeBand(random, lower, upper);
                    desiredDown = note.OriginalDown + (early ? -magnitude : magnitude);
                    desiredDown = QuantizeFrame(
                        desiredDown,
                        framePeriod,
                        framePhase,
                        note.FrameWander,
                        false);
                    desiredDown = note.OriginalDown + SnapToBand(
                        checked((int)Math.Round(desiredDown - note.OriginalDown)),
                        lower,
                        upper,
                        early);
                }

                note.DesiredDown = checked((int)Math.Round(
                    desiredDown,
                    MidpointRounding.AwayFromZero));
                int releaseNoise = checked((int)Math.Round(
                    NextGaussian(random) * targetSigma * 0.20,
                    MidpointRounding.AwayFromZero));
                double desiredUp = note.OriginalUp + baseOffset + releaseNoise + stickyDelay;
                desiredUp = QuantizeFrame(
                    desiredUp,
                    framePeriod,
                    framePhase,
                    note.FrameWander,
                    false);
                note.DesiredUp = checked((int)Math.Round(
                    desiredUp,
                    MidpointRounding.AwayFromZero));
            }

            int safetyGuard = Math.Max(4, checked((int)Math.Ceiling(framePeriod * 0.55)));
            int safeHitWindow = Math.Max(1, windows.Grade100 - safetyGuard);
            EnforcePhysicalLaneOrder(notes, source.KeyCount, safeHitWindow);

            List<LiveLaneTransition> realized = new List<LiveLaneTransition>(notes.Count * 2);
            int minimumOffset = Int32.MaxValue;
            int maximumOffset = Int32.MinValue;
            int firstDown = Int32.MaxValue;
            int lastUp = Int32.MinValue;
            double sum = 0.0;
            int earlyCount = 0;
            int grade320 = 0;
            int grade300 = 0;
            int grade200 = 0;
            int grade100 = 0;
            int grade50 = 0;
            int miss = 0;
            int denseNotes = 0;
            int denseGrade100 = 0;
            int sparseNotes = 0;
            int sparseGrade100 = 0;
            for (int index = 0; index < notes.Count; index++)
            {
                NotePair note = notes[index];
                realized.Add(new LiveLaneTransition(
                    note.DesiredDown,
                    note.Lane,
                    true,
                    note.SourceLine,
                    note.OriginalDown,
                    note.IsHold));
                realized.Add(new LiveLaneTransition(
                    note.DesiredUp,
                    note.Lane,
                    false,
                    note.SourceLine,
                    note.OriginalUp,
                    note.IsHold));
                int downOffset = note.DesiredDown - note.OriginalDown;
                int upOffset = note.DesiredUp - note.OriginalUp;
                minimumOffset = Math.Min(minimumOffset, Math.Min(downOffset, upOffset));
                maximumOffset = Math.Max(maximumOffset, Math.Max(downOffset, upOffset));
                firstDown = Math.Min(firstDown, note.DesiredDown);
                lastUp = Math.Max(lastUp, note.DesiredUp);
                sum += downOffset;
                if (downOffset < 0)
                    earlyCount++;
                int predictedGrade = GradeFor(Math.Abs(downOffset), windows);
                switch (predictedGrade)
                {
                    case 320: grade320++; break;
                    case 300: grade300++; break;
                    case 200: grade200++; break;
                    case 100: grade100++; break;
                    case 50: grade50++; break;
                    default: miss++; break;
                }
                if (note.Density >= 0.65)
                {
                    denseNotes++;
                    if (predictedGrade == 100)
                        denseGrade100++;
                }
                else if (note.Density <= 0.20)
                {
                    sparseNotes++;
                    if (predictedGrade == 100)
                        sparseGrade100++;
                }
            }

            double meanOffset = sum / notes.Count;
            double variance = 0.0;
            for (int index = 0; index < notes.Count; index++)
            {
                double offset = notes[index].DesiredDown - notes[index].OriginalDown - meanOffset;
                variance += offset * offset;
            }
            double standardDeviation = Math.Sqrt(variance / notes.Count);
            double earlyPercent = earlyCount * 100.0 / notes.Count;

            realized.Sort(CompareTransitions);
            List<LiveTransitionBatch> batches = Batch(realized);
            List<string> warnings = new List<string>(source.Warnings);
            LiveManiaPlan plan = new LiveManiaPlan(
                source.Path,
                source.KeyCount,
                source.OverallDifficulty,
                source.ObjectCount,
                firstDown,
                lastUp,
                batches,
                warnings);
            return new HumanizedPlanResult(
                plan,
                seed,
                jammedPresses,
                stickyReleases,
                rushedNotes,
                frameHitches,
                minimumOffset,
                maximumOffset,
                meanOffset,
                standardDeviation,
                earlyPercent,
                grade320,
                grade300,
                grade200,
                grade100,
                grade50,
                miss,
                safeHitWindow,
                denseNotes,
                denseGrade100,
                sparseNotes,
                sparseGrade100);
        }

        private static void ValidateOptions(AgentOptionsSnapshot options)
        {
            if (options.BaseUnstableRate < 0 || options.BaseUnstableRate > 200)
                throw new ArgumentOutOfRangeException("options", "base UR must be from 0 through 200");
            if (options.TimingBiasMilliseconds < -30 || options.TimingBiasMilliseconds > 30)
                throw new ArgumentOutOfRangeException("options", "timing bias must be from -30 through 30 ms");
            if (options.RushPercent < 0 || options.RushPercent > 50)
                throw new ArgumentOutOfRangeException("options", "rush mix must be from 0 through 50 percent");
            if (options.Grade200Permille < 0 || options.Grade200Permille > 150)
                throw new ArgumentOutOfRangeException("options", "200 mix must be from 0 through 15 percent");
            if (options.Grade100Permille < 0 || options.Grade100Permille > 50)
                throw new ArgumentOutOfRangeException("options", "100 mix must be from 0 through 5 percent");
            if (options.DenseBoostPercent < 0 || options.DenseBoostPercent > 250)
                throw new ArgumentOutOfRangeException("options", "dense boost must be from 0 through 250 percent");
            if (options.FingerTroublePercent < 0 || options.FingerTroublePercent > 10)
                throw new ArgumentOutOfRangeException("options", "finger trouble must be from 0 through 10 percent");
        }

        private static List<NotePair> PairNotes(LiveManiaPlan source)
        {
            Dictionary<long, NotePair> byObject = new Dictionary<long, NotePair>();
            for (int batchIndex = 0; batchIndex < source.Batches.Count; batchIndex++)
            {
                List<LiveLaneTransition> transitions = source.Batches[batchIndex].Transitions;
                for (int transitionIndex = 0; transitionIndex < transitions.Count; transitionIndex++)
                {
                    LiveLaneTransition transition = transitions[transitionIndex];
                    long key = ((long)transition.Lane << 32) | (uint)transition.SourceLine;
                    NotePair pair;
                    if (!byObject.TryGetValue(key, out pair))
                    {
                        pair = new NotePair
                        {
                            Lane = transition.Lane,
                            SourceLine = transition.SourceLine,
                            IsHold = transition.IsHold
                        };
                        byObject.Add(key, pair);
                    }
                    else if (pair.IsHold != transition.IsHold)
                    {
                        throw new InvalidOperationException(
                            "humanizer saw inconsistent object type for source line "
                                + pair.SourceLine);
                    }

                    if (transition.IsDown)
                    {
                        if (pair.HasDown)
                            throw new InvalidOperationException("humanizer saw two downs for source line " + pair.SourceLine);
                        pair.HasDown = true;
                        pair.OriginalDown = transition.ReferenceTime;
                    }
                    else
                    {
                        if (pair.HasUp)
                            throw new InvalidOperationException("humanizer saw two ups for source line " + pair.SourceLine);
                        pair.HasUp = true;
                        pair.OriginalUp = transition.ReferenceTime;
                    }
                }
            }

            List<NotePair> notes = new List<NotePair>(byObject.Count);
            foreach (KeyValuePair<long, NotePair> entry in byObject)
            {
                if (!entry.Value.HasDown || !entry.Value.HasUp)
                    throw new InvalidOperationException(
                        "humanizer could not pair source line " + entry.Value.SourceLine);
                notes.Add(entry.Value);
            }
            notes.Sort(delegate(NotePair left, NotePair right)
            {
                int byTime = left.OriginalDown.CompareTo(right.OriginalDown);
                if (byTime != 0)
                    return byTime;
                int byLane = left.Lane.CompareTo(right.Lane);
                if (byLane != 0)
                    return byLane;
                return left.SourceLine.CompareTo(right.SourceLine);
            });
            if (notes.Count == 0)
                throw new InvalidOperationException("humanizer received an empty plan");
            return notes;
        }

        private static void CalculateDensity(List<NotePair> notes, int keyCount)
        {
            int[] previousLaneTime = new int[keyCount];
            for (int lane = 0; lane < keyCount; lane++)
                previousLaneTime[lane] = Int32.MinValue;

            int left = 0;
            int right = 0;
            for (int index = 0; index < notes.Count; index++)
            {
                int time = notes[index].OriginalDown;
                while (left < notes.Count && notes[left].OriginalDown < time - 500)
                    left++;
                if (right < index)
                    right = index;
                while (right < notes.Count && notes[right].OriginalDown <= time + 500)
                    right++;

                double localNotesPerSecond = right - left;
                double streamDensity = Clamp01((localNotesPerSecond - 5.0) / 15.0);
                int previous = previousLaneTime[notes[index].Lane];
                double jackDensity = 0.0;
                if (previous != Int32.MinValue)
                    jackDensity = Clamp01((140.0 - (time - previous)) / 110.0);
                notes[index].Density = Math.Max(streamDensity, jackDensity);
                previousLaneTime[notes[index].Lane] = time;
            }
        }

        private static void MeanAndDeviation(
            List<NotePair> notes,
            out double mean,
            out double deviation)
        {
            double sum = 0.0;
            for (int index = 0; index < notes.Count; index++)
                sum += notes[index].RawError;
            mean = sum / notes.Count;
            double variance = 0.0;
            for (int index = 0; index < notes.Count; index++)
            {
                double delta = notes[index].RawError - mean;
                variance += delta * delta;
            }
            deviation = Math.Sqrt(variance / notes.Count);
        }

        private static int SelectLowGrade(
            Random random,
            NotePair note,
            AgentOptionsSnapshot options)
        {
            double dense = options.DenseBoostPercent / 100.0 * note.Density;
            double probability100 = options.Grade100Permille / 1000.0
                * (1.0 + dense * 2.0);
            double probability200 = options.Grade200Permille / 1000.0
                * (1.0 + dense);
            probability100 = Math.Min(0.35, probability100);
            probability200 = Math.Min(0.45, probability200);
            double total = probability100 + probability200;
            if (total > 0.65)
            {
                probability100 *= 0.65 / total;
                probability200 *= 0.65 / total;
            }

            double roll = random.NextDouble();
            if (roll < probability100)
                return 100;
            if (roll < probability100 + probability200)
                return 200;
            return 0;
        }

        private static bool ChooseEarly(
            Random random,
            NotePair note,
            AgentOptionsSnapshot options)
        {
            double probability = 0.50;
            if (note.RushActive)
                probability += 0.34;
            probability += Math.Max(-0.12, Math.Min(0.12, -options.TimingBiasMilliseconds / 100.0));
            return random.NextDouble() < Math.Max(0.08, Math.Min(0.92, probability));
        }

        private static int SampleSafeBand(Random random, int lowerWindow, int upperWindow)
        {
            int width = Math.Max(1, upperWindow - lowerWindow);
            int margin = Math.Max(2, Math.Min(6, width / 5));
            int minimum = Math.Min(upperWindow, lowerWindow + margin);
            int maximum = Math.Max(minimum, upperWindow - margin);
            double inwardShape = Math.Min(random.NextDouble(), random.NextDouble());
            return checked((int)Math.Round(
                minimum + inwardShape * (maximum - minimum),
                MidpointRounding.AwayFromZero));
        }

        private static int SnapToBand(
            int offset,
            int lowerWindow,
            int upperWindow,
            bool early)
        {
            int width = Math.Max(1, upperWindow - lowerWindow);
            int margin = Math.Max(2, Math.Min(6, width / 5));
            int minimum = Math.Min(upperWindow, lowerWindow + margin);
            int maximum = Math.Max(minimum, upperWindow - margin);
            int magnitude = Math.Max(minimum, Math.Min(maximum, Math.Abs(offset)));
            return early ? -magnitude : magnitude;
        }

        private static double QuantizeFrame(
            double desired,
            double period,
            double phase,
            double wander,
            bool hitch)
        {
            if (period <= 0.0)
                return desired;
            double movingPhase = phase + wander;
            double frame = movingPhase
                + Math.Ceiling((desired - movingPhase - period * 0.5) / period) * period;
            if (hitch)
                frame += period;
            return frame;
        }

        private static void EnforcePhysicalLaneOrder(
            List<NotePair> notes,
            int keyCount,
            int maximumAbsolute)
        {
            for (int lane = 0; lane < keyCount; lane++)
            {
                List<NotePair> laneNotes = new List<NotePair>();
                for (int index = 0; index < notes.Count; index++)
                {
                    if (notes[index].Lane == lane)
                        laneNotes.Add(notes[index]);
                }
                laneNotes.Sort(delegate(NotePair left, NotePair right)
                {
                    int byTime = left.OriginalDown.CompareTo(right.OriginalDown);
                    return byTime != 0 ? byTime : left.SourceLine.CompareTo(right.SourceLine);
                });
                if (laneNotes.Count == 0)
                    continue;

                int[] latest = new int[laneNotes.Count];
                latest[laneNotes.Count - 1] = checked(
                    laneNotes[laneNotes.Count - 1].OriginalDown + maximumAbsolute);
                for (int index = laneNotes.Count - 2; index >= 0; index--)
                {
                    int high = checked(laneNotes[index].OriginalDown + maximumAbsolute);
                    latest[index] = Math.Min(high, latest[index + 1] - 2);
                }

                int previousDown = Int32.MinValue;
                for (int index = 0; index < laneNotes.Count; index++)
                {
                    NotePair note = laneNotes[index];
                    int low = checked(note.OriginalDown - maximumAbsolute);
                    if (previousDown != Int32.MinValue)
                        low = Math.Max(low, previousDown + 2);
                    if (low > latest[index])
                    {
                        throw new InvalidOperationException(
                            "no miss-safe physical timing exists for lane " + lane);
                    }
                    note.DesiredDown = Math.Max(low, Math.Min(latest[index], note.DesiredDown));
                    previousDown = note.DesiredDown;
                }

                for (int index = 0; index < laneNotes.Count; index++)
                {
                    NotePair note = laneNotes[index];
                    int releaseLow = Math.Max(
                        checked(note.OriginalUp - maximumAbsolute),
                        note.DesiredDown + 1);
                    int releaseHigh = checked(note.OriginalUp + maximumAbsolute);
                    if (index + 1 < laneNotes.Count)
                        releaseHigh = Math.Min(releaseHigh, laneNotes[index + 1].DesiredDown - 1);
                    if (releaseLow > releaseHigh)
                        releaseLow = releaseHigh;
                    note.DesiredUp = Math.Max(releaseLow, Math.Min(releaseHigh, note.DesiredUp));
                }
            }
        }

        private static HitWindows CalculateWindows(double overallDifficulty, int selectedMods)
        {
            double odDeficit = Math.Min(10.0, Math.Max(0.0, 10.0 - overallDifficulty));
            return new HitWindows
            {
                Grade320 = AdjustWindow(16.0, selectedMods),
                Grade300 = AdjustWindow(34.0 + 3.0 * odDeficit, selectedMods),
                Grade200 = AdjustWindow(67.0 + 3.0 * odDeficit, selectedMods),
                Grade100 = AdjustWindow(97.0 + 3.0 * odDeficit, selectedMods),
                Grade50 = AdjustWindow(121.0 + 3.0 * odDeficit, selectedMods)
            };
        }

        private static int AdjustWindow(double value, int selectedMods)
        {
            if ((selectedMods & HardRockBit) != 0)
                value /= 1.4;
            else if ((selectedMods & EasyBit) != 0)
                value *= 1.4;
            if ((selectedMods & DoubleTimeBit) != 0)
                value *= 1.5;
            else if ((selectedMods & HalfTimeBit) != 0)
                value *= 0.75;
            return (int)value;
        }

        private static int GradeFor(int absoluteOffset, HitWindows windows)
        {
            if (absoluteOffset <= windows.Grade320) return 320;
            if (absoluteOffset <= windows.Grade300) return 300;
            if (absoluteOffset <= windows.Grade200) return 200;
            if (absoluteOffset <= windows.Grade100) return 100;
            if (absoluteOffset <= windows.Grade50) return 50;
            return 0;
        }

        private static int CompareTransitions(LiveLaneTransition left, LiveLaneTransition right)
        {
            int byTime = left.Time.CompareTo(right.Time);
            if (byTime != 0)
                return byTime;
            if (left.IsDown != right.IsDown)
                return left.IsDown ? 1 : -1;
            int byLane = left.Lane.CompareTo(right.Lane);
            if (byLane != 0)
                return byLane;
            return left.SourceLine.CompareTo(right.SourceLine);
        }

        private static List<LiveTransitionBatch> Batch(List<LiveLaneTransition> transitions)
        {
            List<LiveTransitionBatch> result = new List<LiveTransitionBatch>();
            int index = 0;
            while (index < transitions.Count)
            {
                int time = transitions[index].Time;
                List<LiveLaneTransition> batch = new List<LiveLaneTransition>();
                while (index < transitions.Count && transitions[index].Time == time)
                {
                    batch.Add(transitions[index]);
                    index++;
                }
                result.Add(new LiveTransitionBatch(time, batch));
            }
            return result;
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
                        RushMaximumSigma = 1.2,
                        MaximumFatigueSigma = 0.35,
                        FrameHitchProbability = 0.0,
                        JamMinimum = 8,
                        JamMaximum = 18,
                        StickyMinimum = 12,
                        StickyMaximum = 28
                    };

                case HumanStyle.Human:
                    return new StyleParameters
                    {
                        CorrelationMilliseconds = 950.0,
                        RushMinimumSigma = 0.9,
                        RushMaximumSigma = 1.7,
                        MaximumFatigueSigma = 0.55,
                        FrameHitchProbability = 0.0007,
                        JamMinimum = 12,
                        JamMaximum = 32,
                        StickyMinimum = 18,
                        StickyMaximum = 45
                    };

                case HumanStyle.Tired:
                    return new StyleParameters
                    {
                        CorrelationMilliseconds = 1250.0,
                        RushMinimumSigma = 0.8,
                        RushMaximumSigma = 1.6,
                        MaximumFatigueSigma = 0.95,
                        FrameHitchProbability = 0.0018,
                        JamMinimum = 16,
                        JamMaximum = 45,
                        StickyMinimum = 24,
                        StickyMaximum = 65
                    };

                case HumanStyle.Chaos:
                    return new StyleParameters
                    {
                        CorrelationMilliseconds = 700.0,
                        RushMinimumSigma = 1.1,
                        RushMaximumSigma = 2.2,
                        MaximumFatigueSigma = 1.25,
                        FrameHitchProbability = 0.0035,
                        JamMinimum = 20,
                        JamMaximum = 65,
                        StickyMinimum = 30,
                        StickyMaximum = 90
                    };

                default:
                    throw new ArgumentOutOfRangeException("style");
            }
        }

        private static int CreateSeed(
            string path,
            AgentOptionsSnapshot options,
            int selectedMods)
        {
            if (!options.RepeatableVariation)
            {
                int randomSeed = unchecked(
                    Environment.TickCount
                        ^ Guid.NewGuid().GetHashCode()
                        ^ path.GetHashCode());
                return randomSeed == Int32.MinValue ? 0 : Math.Abs(randomSeed);
            }

            string material = path.ToUpperInvariant()
                + "|" + (int)options.Style
                + "|" + options.BaseUnstableRate
                + "|" + options.TimingBiasMilliseconds
                + "|" + options.RushPercent
                + "|" + options.Grade200Permille
                + "|" + options.Grade100Permille
                + "|" + options.DenseBoostPercent
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
