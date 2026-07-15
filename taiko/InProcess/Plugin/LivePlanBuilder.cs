using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace LocalTaikoAgent.Plugin
{
    internal enum LiveTaikoObjectKind
    {
        Circle,
        DrumRoll,
        Spinner
    }

    internal enum LiveTaikoKey
    {
        InnerLeft = 0,
        InnerRight = 1,
        OuterLeft = 2,
        OuterRight = 3
    }

    internal sealed class LiveTaikoStrike
    {
        public int Time;
        public int ReferenceTime;
        public int ObjectStart;
        public int ObjectEnd;
        public int SourceLine;
        public LiveTaikoObjectKind Kind;
        public bool RequiredForCombo;
        public bool IsStrong;
        public int[] Keys;
        public int[] KeyDelays;

        public LiveTaikoStrike Clone()
        {
            return new LiveTaikoStrike
            {
                Time = Time,
                ReferenceTime = ReferenceTime,
                ObjectStart = ObjectStart,
                ObjectEnd = ObjectEnd,
                SourceLine = SourceLine,
                Kind = Kind,
                RequiredForCombo = RequiredForCombo,
                IsStrong = IsStrong,
                Keys = (int[])Keys.Clone(),
                KeyDelays = (int[])KeyDelays.Clone()
            };
        }
    }

    internal sealed class LiveTaikoTransition
    {
        public LiveTaikoTransition(
            int time,
            int key,
            bool isDown,
            int sourceLine,
            int referenceTime,
            LiveTaikoObjectKind kind,
            bool requiredForCombo)
        {
            Time = time;
            Key = key;
            IsDown = isDown;
            SourceLine = sourceLine;
            ReferenceTime = referenceTime;
            Kind = kind;
            RequiredForCombo = requiredForCombo;
        }

        public readonly int Time;
        public readonly int Key;
        public readonly bool IsDown;
        public readonly int SourceLine;
        public readonly int ReferenceTime;
        public readonly LiveTaikoObjectKind Kind;
        public readonly bool RequiredForCombo;
    }

    internal sealed class LiveTaikoTransitionBatch
    {
        public LiveTaikoTransitionBatch(int time, List<LiveTaikoTransition> transitions)
        {
            Time = time;
            Transitions = transitions;
        }

        public readonly int Time;
        public readonly List<LiveTaikoTransition> Transitions;
    }

    internal sealed class LiveTaikoKeySpec
    {
        public LiveTaikoKeySpec(string name, ushort virtualKey)
        {
            Name = name;
            VirtualKey = virtualKey;
        }

        public readonly string Name;
        public readonly ushort VirtualKey;
    }

    internal sealed class LiveTaikoPlan
    {
        public LiveTaikoPlan(
            string path,
            int formatVersion,
            double overallDifficulty,
            int objectCount,
            int circleCount,
            int drumRollCount,
            int spinnerCount,
            int firstObjectTime,
            int lastObjectTime,
            List<LiveTaikoStrike> strikes,
            List<LiveTaikoTransitionBatch> batches,
            List<string> warnings)
        {
            Path = path;
            FormatVersion = formatVersion;
            OverallDifficulty = overallDifficulty;
            ObjectCount = objectCount;
            CircleCount = circleCount;
            DrumRollCount = drumRollCount;
            SpinnerCount = spinnerCount;
            FirstObjectTime = firstObjectTime;
            LastObjectTime = lastObjectTime;
            Strikes = strikes;
            Batches = batches;
            Warnings = warnings;
        }

        public readonly string Path;
        public readonly int FormatVersion;
        public readonly double OverallDifficulty;
        public readonly int ObjectCount;
        public readonly int CircleCount;
        public readonly int DrumRollCount;
        public readonly int SpinnerCount;
        public readonly int FirstObjectTime;
        public readonly int LastObjectTime;
        public readonly List<LiveTaikoStrike> Strikes;
        public readonly List<LiveTaikoTransitionBatch> Batches;
        public readonly List<string> Warnings;
    }

    internal static class LivePlanBuilder
    {
        private const int HitSoundWhistle = 2;
        private const int HitSoundFinish = 4;
        private const int HitSoundClap = 8;
        private const int EasyBit = 0x2;
        private const int HardRockBit = 0x10;
        private const int DoubleTimeBit = 0x40;
        private const int HalfTimeBit = 0x100;
        private const double TaikoDrumRollLengthMultiplier = 1.4;

        private sealed class TimingPoint
        {
            public double Time;
            public double BeatLength;
            public bool Uninherited;
            public int SourceLine;
        }

        private sealed class RawObject
        {
            public int SourceLine;
            public string Text;
        }

        private sealed class ParsedObject
        {
            public int Start;
            public int End;
            public int SourceLine;
            public LiveTaikoObjectKind Kind;
            public bool IsKat;
            public bool IsStrong;
            public double BeatLength;
            public double VelocityMultiplier;
        }

        private sealed class TimingState
        {
            public double BeatLength;
            public double VelocityMultiplier;
        }

        private sealed class DownEvent
        {
            public int Time;
            public int Key;
            public int SourceLine;
            public int ReferenceTime;
            public LiveTaikoObjectKind Kind;
            public bool RequiredForCombo;
        }

        public static LiveTaikoPlan ParseAndBuild(string path, int tapMilliseconds, int selectedMods)
        {
            if (String.IsNullOrEmpty(path) || !File.Exists(path))
                throw new FileNotFoundException("Current beatmap .osu file was not found.", path);
            if (tapMilliseconds < 1 || tapMilliseconds > 100)
                throw new ArgumentOutOfRangeException("tapMilliseconds");

            int formatVersion = -1;
            int mode = -1;
            double overallDifficulty = Double.NaN;
            double sliderMultiplier = Double.NaN;
            double sliderTickRate = 1.0;
            string section = String.Empty;
            int lineNumber = 0;
            List<TimingPoint> timingPoints = new List<TimingPoint>();
            List<RawObject> rawObjects = new List<RawObject>();

            using (StreamReader reader = new StreamReader(path, Encoding.UTF8, true))
            {
                string raw;
                while ((raw = reader.ReadLine()) != null)
                {
                    lineNumber++;
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal))
                        continue;
                    if (lineNumber == 1
                        && line.StartsWith("osu file format v", StringComparison.OrdinalIgnoreCase))
                    {
                        formatVersion = ParseInt(path, lineNumber, line.Substring(17), "format version");
                        continue;
                    }
                    if (line[0] == '[' && line[line.Length - 1] == ']')
                    {
                        section = line.Substring(1, line.Length - 2).Trim();
                        continue;
                    }
                    if (String.Equals(section, "TimingPoints", StringComparison.OrdinalIgnoreCase))
                    {
                        timingPoints.Add(ParseTimingPoint(path, lineNumber, line));
                        continue;
                    }
                    if (String.Equals(section, "HitObjects", StringComparison.OrdinalIgnoreCase))
                    {
                        rawObjects.Add(new RawObject { SourceLine = lineNumber, Text = line });
                        continue;
                    }

                    int separator = line.IndexOf(':');
                    if (separator <= 0)
                        continue;
                    string key = line.Substring(0, separator).Trim();
                    string value = line.Substring(separator + 1).Trim();
                    if (String.Equals(section, "General", StringComparison.OrdinalIgnoreCase)
                        && String.Equals(key, "Mode", StringComparison.OrdinalIgnoreCase))
                    {
                        mode = ParseInt(path, lineNumber, value, "Mode");
                    }
                    else if (String.Equals(section, "Difficulty", StringComparison.OrdinalIgnoreCase))
                    {
                        if (String.Equals(key, "OverallDifficulty", StringComparison.OrdinalIgnoreCase))
                            overallDifficulty = ParseDouble(path, lineNumber, value, key);
                        else if (String.Equals(key, "SliderMultiplier", StringComparison.OrdinalIgnoreCase))
                            sliderMultiplier = ParseDouble(path, lineNumber, value, key);
                        else if (String.Equals(key, "SliderTickRate", StringComparison.OrdinalIgnoreCase))
                            sliderTickRate = ParseDouble(path, lineNumber, value, key);
                    }
                }
            }

            if (formatVersion < 1)
                throw new InvalidDataException(path + ": missing osu file format header.");
            if (mode != 1)
                throw new InvalidDataException(path + ": expected native Mode:1, got " + mode + ".");
            if (!IsFinite(overallDifficulty))
                throw new InvalidDataException(path + ": OverallDifficulty is missing or invalid.");
            if (!IsFinite(sliderMultiplier) || sliderMultiplier <= 0.0)
                throw new InvalidDataException(path + ": SliderMultiplier must be positive.");
            if (!IsFinite(sliderTickRate) || sliderTickRate <= 0.0)
                throw new InvalidDataException(path + ": SliderTickRate must be positive.");
            if (rawObjects.Count == 0)
                throw new InvalidDataException(path + ": [HitObjects] is empty.");

            timingPoints.Sort(delegate(TimingPoint left, TimingPoint right)
            {
                int byTime = left.Time.CompareTo(right.Time);
                return byTime != 0 ? byTime : left.SourceLine.CompareTo(right.SourceLine);
            });
            bool hasBaseTiming = false;
            for (int index = 0; index < timingPoints.Count; index++)
                hasBaseTiming |= timingPoints[index].Uninherited && timingPoints[index].BeatLength > 0.0;
            if (!hasBaseTiming)
                throw new InvalidDataException(path + ": no positive uninherited timing point was found.");

            List<ParsedObject> objects = new List<ParsedObject>(rawObjects.Count);
            for (int index = 0; index < rawObjects.Count; index++)
            {
                objects.Add(ParseHitObject(
                    path,
                    rawObjects[index],
                    timingPoints,
                    sliderMultiplier));
            }
            objects.Sort(delegate(ParsedObject left, ParsedObject right)
            {
                int byTime = left.Start.CompareTo(right.Start);
                return byTime != 0 ? byTime : left.SourceLine.CompareTo(right.SourceLine);
            });

            List<LiveTaikoStrike> strikes = new List<LiveTaikoStrike>();
            bool preferLeft = true;
            int circleCount = 0;
            int drumRollCount = 0;
            int spinnerCount = 0;
            for (int index = 0; index < objects.Count; index++)
            {
                ParsedObject hitObject = objects[index];
                if (hitObject.Kind == LiveTaikoObjectKind.Circle)
                {
                    AddCircle(strikes, hitObject, preferLeft);
                    circleCount++;
                }
                else if (hitObject.Kind == LiveTaikoObjectKind.DrumRoll)
                {
                    AddDrumRoll(
                        strikes,
                        hitObject,
                        formatVersion,
                        sliderTickRate,
                        ref preferLeft);
                    drumRollCount++;
                }
                else
                {
                    AddSpinner(strikes, hitObject, overallDifficulty, selectedMods);
                    spinnerCount++;
                }
                preferLeft = !preferLeft;
            }

            List<string> warnings = new List<string>();
            List<LiveTaikoTransitionBatch> batches = BuildBatches(strikes, tapMilliseconds, warnings);
            if (batches.Count == 0)
                throw new InvalidDataException(path + ": no live input transitions were generated.");
            return new LiveTaikoPlan(
                Path.GetFullPath(path),
                formatVersion,
                overallDifficulty,
                objects.Count,
                circleCount,
                drumRollCount,
                spinnerCount,
                objects[0].Start,
                objects[objects.Count - 1].End,
                strikes,
                batches,
                warnings);
        }

        public static LiveTaikoPlan Rebuild(
            LiveTaikoPlan source,
            List<LiveTaikoStrike> strikes,
            int tapMilliseconds)
        {
            List<string> warnings = new List<string>(source.Warnings);
            List<LiveTaikoTransitionBatch> batches = BuildBatches(strikes, tapMilliseconds, warnings);
            return new LiveTaikoPlan(
                source.Path,
                source.FormatVersion,
                source.OverallDifficulty,
                source.ObjectCount,
                source.CircleCount,
                source.DrumRollCount,
                source.SpinnerCount,
                batches[0].Time,
                batches[batches.Count - 1].Time,
                strikes,
                batches,
                warnings);
        }

        private static TimingPoint ParseTimingPoint(string path, int lineNumber, string line)
        {
            string[] fields = line.Split(',');
            if (fields.Length < 2)
                throw Error(path, lineNumber, "timing point has fewer than two fields");
            double beatLength = ParseDouble(path, lineNumber, fields[1], "beatLength");
            bool uninherited = fields.Length < 7
                || ParseInt(path, lineNumber, fields[6], "uninherited") != 0;
            if (uninherited && beatLength <= 0.0)
                throw Error(path, lineNumber, "uninherited timing point must have positive beatLength");
            if (!uninherited && beatLength >= 0.0)
                throw Error(path, lineNumber, "inherited timing point must have negative beatLength");
            return new TimingPoint
            {
                Time = ParseDouble(path, lineNumber, fields[0], "timing point time"),
                BeatLength = beatLength,
                Uninherited = uninherited,
                SourceLine = lineNumber
            };
        }

        private static ParsedObject ParseHitObject(
            string path,
            RawObject raw,
            List<TimingPoint> timingPoints,
            double sliderMultiplier)
        {
            string[] fields = raw.Text.Split(',');
            if (fields.Length < 5)
                throw Error(path, raw.SourceLine, "hit object has fewer than five fields");
            int start = ParseInt(path, raw.SourceLine, fields[2], "time");
            int type = ParseInt(path, raw.SourceLine, fields[3], "type");
            int hitSound = ParseInt(path, raw.SourceLine, fields[4], "hitSound");
            bool kat = (hitSound & (HitSoundWhistle | HitSoundClap)) != 0;
            bool strong = (hitSound & HitSoundFinish) != 0;

            if ((type & 1) != 0)
            {
                return new ParsedObject
                {
                    Start = start,
                    End = start,
                    SourceLine = raw.SourceLine,
                    Kind = LiveTaikoObjectKind.Circle,
                    IsKat = kat,
                    IsStrong = strong
                };
            }
            if ((type & 2) != 0)
            {
                if (fields.Length < 8)
                    throw Error(path, raw.SourceLine, "slider has fewer than eight fields");
                int repeat = ParseInt(path, raw.SourceLine, fields[6], "repeat count");
                double pixelLength = ParseDouble(path, raw.SourceLine, fields[7], "pixel length");
                if (repeat < 1 || pixelLength <= 0.0)
                    throw Error(path, raw.SourceLine, "slider repeat/length is invalid");
                TimingState timing = ResolveTiming(timingPoints, start);
                double duration = pixelLength * TaikoDrumRollLengthMultiplier * repeat
                    * timing.BeatLength
                    / (sliderMultiplier * 100.0 * timing.VelocityMultiplier);
                int end = checked(start + (int)duration);
                if (end <= start)
                    throw Error(path, raw.SourceLine, "computed Taiko drumroll duration is invalid");
                return new ParsedObject
                {
                    Start = start,
                    End = end,
                    SourceLine = raw.SourceLine,
                    Kind = LiveTaikoObjectKind.DrumRoll,
                    BeatLength = timing.BeatLength,
                    VelocityMultiplier = timing.VelocityMultiplier
                };
            }
            if ((type & 8) != 0)
            {
                if (fields.Length < 6)
                    throw Error(path, raw.SourceLine, "spinner has no end time");
                int end = ParseInt(path, raw.SourceLine, fields[5], "spinner end time");
                if (end <= start)
                    throw Error(path, raw.SourceLine, "spinner end must follow its start");
                return new ParsedObject
                {
                    Start = start,
                    End = end,
                    SourceLine = raw.SourceLine,
                    Kind = LiveTaikoObjectKind.Spinner
                };
            }
            throw Error(path, raw.SourceLine, "unsupported native Taiko hit object type=" + type);
        }

        private static TimingState ResolveTiming(List<TimingPoint> timingPoints, int time)
        {
            double beatLength = Double.NaN;
            double velocity = 1.0;
            for (int index = 0; index < timingPoints.Count; index++)
            {
                TimingPoint point = timingPoints[index];
                if (point.Time > time)
                    break;
                if (point.Uninherited)
                {
                    beatLength = point.BeatLength;
                    velocity = 1.0;
                }
                else
                {
                    velocity = Clamp(-100.0 / point.BeatLength, 0.1, 10.0);
                }
            }
            if (Double.IsNaN(beatLength))
            {
                for (int index = 0; index < timingPoints.Count; index++)
                {
                    if (timingPoints[index].Uninherited && timingPoints[index].BeatLength > 0.0)
                    {
                        beatLength = timingPoints[index].BeatLength;
                        break;
                    }
                }
            }
            return new TimingState { BeatLength = beatLength, VelocityMultiplier = velocity };
        }

        private static void AddCircle(
            List<LiveTaikoStrike> strikes,
            ParsedObject hitObject,
            bool preferLeft)
        {
            int left = hitObject.IsKat ? (int)LiveTaikoKey.OuterLeft : (int)LiveTaikoKey.InnerLeft;
            int right = hitObject.IsKat ? (int)LiveTaikoKey.OuterRight : (int)LiveTaikoKey.InnerRight;
            int[] keys = hitObject.IsStrong
                ? new int[] { left, right }
                : new int[] { preferLeft ? left : right };
            strikes.Add(CreateStrike(
                hitObject.Start,
                hitObject.Start,
                hitObject.Start,
                hitObject.SourceLine,
                LiveTaikoObjectKind.Circle,
                true,
                hitObject.IsStrong,
                keys));
        }

        private static void AddDrumRoll(
            List<LiveTaikoStrike> strikes,
            ParsedObject hitObject,
            int formatVersion,
            double sliderTickRate,
            ref bool preferLeft)
        {
            double interval = CalculateDrumRollInterval(
                formatVersion,
                sliderTickRate,
                hitObject.BeatLength,
                hitObject.VelocityMultiplier);

            int previous = Int32.MinValue;
            for (double exact = hitObject.Start; exact < hitObject.End; exact += interval)
            {
                int time = (int)exact;
                if (time == previous)
                    continue;
                int key = preferLeft
                    ? (int)LiveTaikoKey.InnerLeft
                    : (int)LiveTaikoKey.InnerRight;
                strikes.Add(CreateStrike(
                    time,
                    hitObject.Start,
                    hitObject.End,
                    hitObject.SourceLine,
                    LiveTaikoObjectKind.DrumRoll,
                    false,
                    false,
                    new int[] { key }));
                preferLeft = !preferLeft;
                previous = time;
            }
        }

        internal static double CalculateDrumRollInterval(
            int formatVersion,
            double sliderTickRate,
            double beatLength,
            double velocityMultiplier)
        {
            double interval;
            if (formatVersion < 8)
            {
                interval = (beatLength / velocityMultiplier) / 8.0;
            }
            else
            {
                bool specialRate = sliderTickRate == 1.5
                    || sliderTickRate == 3.0
                    || sliderTickRate == 6.0;
                interval = beatLength / (specialRate ? 6.0 : 8.0);
            }
            while (interval < 60.0)
                interval *= 2.0;
            while (interval > 120.0)
                interval /= 2.0;
            return interval;
        }

        private static void AddSpinner(
            List<LiveTaikoStrike> strikes,
            ParsedObject hitObject,
            double overallDifficulty,
            int selectedMods)
        {
            int duration = hitObject.End - hitObject.Start;
            int required = CalculateSpinnerRequiredHits(duration, overallDifficulty, selectedMods);
            int count = required + 1;
            int interval = Math.Max(1, duration / count);
            int[] cycle = new int[]
            {
                (int)LiveTaikoKey.InnerLeft,
                (int)LiveTaikoKey.OuterLeft,
                (int)LiveTaikoKey.InnerRight,
                (int)LiveTaikoKey.OuterRight
            };
            for (int index = 0; index < count; index++)
            {
                int time = checked(hitObject.Start + index * interval);
                if (time >= hitObject.End)
                    break;
                strikes.Add(CreateStrike(
                    time,
                    hitObject.Start,
                    hitObject.End,
                    hitObject.SourceLine,
                    LiveTaikoObjectKind.Spinner,
                    false,
                    false,
                    new int[] { cycle[index % cycle.Length] }));
            }
        }

        internal static int CalculateSpinnerRequiredHits(
            int durationMilliseconds,
            double overallDifficulty,
            int selectedMods)
        {
            double difficulty = overallDifficulty;
            if ((selectedMods & EasyBit) != 0)
                difficulty = Math.Max(0.0, difficulty / 2.0);
            if ((selectedMods & HardRockBit) != 0)
                difficulty = Math.Min(10.0, difficulty * 1.4);
            double rate = DifficultyRange(difficulty, 3.0, 5.0, 7.5);
            int baseRequired = (int)(((float)durationMilliseconds / 1000f) * rate);
            int required = (int)Math.Max(1f, baseRequired * 1.65f);
            if ((selectedMods & DoubleTimeBit) != 0)
                required = Math.Max(1, (int)(required * 0.75f));
            if ((selectedMods & HalfTimeBit) != 0)
                required = Math.Max(1, (int)(required * 1.5f));
            return required;
        }

        private static LiveTaikoStrike CreateStrike(
            int time,
            int objectStart,
            int objectEnd,
            int sourceLine,
            LiveTaikoObjectKind kind,
            bool required,
            bool strong,
            int[] keys)
        {
            return new LiveTaikoStrike
            {
                Time = time,
                ReferenceTime = time,
                ObjectStart = objectStart,
                ObjectEnd = objectEnd,
                SourceLine = sourceLine,
                Kind = kind,
                RequiredForCombo = required,
                IsStrong = strong,
                Keys = keys,
                KeyDelays = new int[keys.Length]
            };
        }

        private static List<LiveTaikoTransitionBatch> BuildBatches(
            List<LiveTaikoStrike> strikes,
            int tapMilliseconds,
            List<string> warnings)
        {
            List<DownEvent>[] byKey = new List<DownEvent>[4];
            for (int key = 0; key < byKey.Length; key++)
                byKey[key] = new List<DownEvent>();
            Dictionary<long, DownEvent> seen = new Dictionary<long, DownEvent>();
            for (int strikeIndex = 0; strikeIndex < strikes.Count; strikeIndex++)
            {
                LiveTaikoStrike strike = strikes[strikeIndex];
                for (int index = 0; index < strike.Keys.Length; index++)
                {
                    int key = strike.Keys[index];
                    int time = checked(strike.Time + strike.KeyDelays[index]);
                    long identity = ((long)time << 3) | (uint)key;
                    DownEvent existing;
                    if (seen.TryGetValue(identity, out existing))
                    {
                        bool promoted = strike.RequiredForCombo && !existing.RequiredForCombo;
                        if (promoted)
                        {
                            existing.SourceLine = strike.SourceLine;
                            existing.ReferenceTime = strike.ReferenceTime;
                            existing.Kind = strike.Kind;
                            existing.RequiredForCombo = true;
                        }
                        warnings.Add("coalesced " + (LiveTaikoKey)key + " down at " + time
                            + "ms; combo-relevant metadata "
                            + (promoted || existing.RequiredForCombo ? "retained" : "not required")
                            + " (source line " + strike.SourceLine + ")");
                        continue;
                    }
                    DownEvent down = new DownEvent
                    {
                        Time = time,
                        Key = key,
                        SourceLine = strike.SourceLine,
                        ReferenceTime = strike.ReferenceTime,
                        Kind = strike.Kind,
                        RequiredForCombo = strike.RequiredForCombo
                    };
                    seen.Add(identity, down);
                    byKey[key].Add(down);
                }
            }

            List<LiveTaikoTransition> transitions = new List<LiveTaikoTransition>(seen.Count * 2);
            for (int key = 0; key < byKey.Length; key++)
            {
                byKey[key].Sort(delegate(DownEvent left, DownEvent right)
                {
                    return left.Time.CompareTo(right.Time);
                });
                for (int index = 0; index < byKey[key].Count; index++)
                {
                    DownEvent down = byKey[key][index];
                    int release = checked(down.Time + tapMilliseconds);
                    if (index + 1 < byKey[key].Count && byKey[key][index + 1].Time <= release)
                        release = byKey[key][index + 1].Time - 1;
                    release = Math.Max(down.Time + 1, release);
                    transitions.Add(new LiveTaikoTransition(
                        down.Time,
                        key,
                        true,
                        down.SourceLine,
                        down.ReferenceTime,
                        down.Kind,
                        down.RequiredForCombo));
                    transitions.Add(new LiveTaikoTransition(
                        release,
                        key,
                        false,
                        down.SourceLine,
                        down.ReferenceTime,
                        down.Kind,
                        down.RequiredForCombo));
                }
            }

            transitions.Sort(delegate(LiveTaikoTransition left, LiveTaikoTransition right)
            {
                int byTime = left.Time.CompareTo(right.Time);
                if (byTime != 0)
                    return byTime;
                if (left.IsDown != right.IsDown)
                    return left.IsDown ? 1 : -1;
                return left.Key.CompareTo(right.Key);
            });
            List<LiveTaikoTransitionBatch> batches = new List<LiveTaikoTransitionBatch>();
            int transitionIndex = 0;
            while (transitionIndex < transitions.Count)
            {
                int time = transitions[transitionIndex].Time;
                List<LiveTaikoTransition> batch = new List<LiveTaikoTransition>();
                while (transitionIndex < transitions.Count && transitions[transitionIndex].Time == time)
                {
                    batch.Add(transitions[transitionIndex]);
                    transitionIndex++;
                }
                batches.Add(new LiveTaikoTransitionBatch(time, batch));
            }
            return batches;
        }

        private static double DifficultyRange(double difficulty, double minimum, double middle, double maximum)
        {
            return difficulty > 5.0
                ? middle + (maximum - middle) * (difficulty - 5.0) / 5.0
                : middle - (middle - minimum) * (5.0 - difficulty) / 5.0;
        }

        private static int ParseInt(string path, int line, string text, string field)
        {
            int value;
            if (!Int32.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                throw Error(path, line, field + " is not an integer: " + text);
            return value;
        }

        private static double ParseDouble(string path, int line, string text, string field)
        {
            double value;
            if (!Double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                || !IsFinite(value))
            {
                throw Error(path, line, field + " is not numeric: " + text);
            }
            return value;
        }

        private static bool IsFinite(double value)
        {
            return !Double.IsNaN(value) && !Double.IsInfinity(value);
        }

        private static double Clamp(double value, double minimum, double maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }

        private static InvalidDataException Error(string path, int line, string message)
        {
            return new InvalidDataException(path + ":" + line + ": " + message);
        }
    }
}
