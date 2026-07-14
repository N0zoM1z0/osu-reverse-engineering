using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace LocalManiaAuto.Plugin
{
    internal sealed class LiveLaneTransition
    {
        public LiveLaneTransition(int time, int lane, bool isDown, int sourceLine)
            : this(time, lane, isDown, sourceLine, time, false)
        {
        }

        public LiveLaneTransition(
            int time,
            int lane,
            bool isDown,
            int sourceLine,
            int referenceTime,
            bool isHold)
        {
            Time = time;
            Lane = lane;
            IsDown = isDown;
            SourceLine = sourceLine;
            ReferenceTime = referenceTime;
            IsHold = isHold;
        }

        public readonly int Time;
        public readonly int Lane;
        public readonly bool IsDown;
        public readonly int SourceLine;
        public readonly int ReferenceTime;
        public readonly bool IsHold;
    }

    internal sealed class LiveTransitionBatch
    {
        public LiveTransitionBatch(int time, List<LiveLaneTransition> transitions)
        {
            Time = time;
            Transitions = transitions;
        }

        public readonly int Time;
        public readonly List<LiveLaneTransition> Transitions;
    }

    internal sealed class LiveKeySpec
    {
        public LiveKeySpec(string name, ushort virtualKey)
        {
            Name = name;
            VirtualKey = virtualKey;
        }

        public readonly string Name;
        public readonly ushort VirtualKey;
    }

    internal sealed class LiveManiaPlan
    {
        public LiveManiaPlan(
            string path,
            int keyCount,
            double overallDifficulty,
            int objectCount,
            int firstObjectTime,
            int lastObjectTime,
            List<LiveTransitionBatch> batches,
            List<string> warnings)
        {
            Path = path;
            KeyCount = keyCount;
            OverallDifficulty = overallDifficulty;
            ObjectCount = objectCount;
            FirstObjectTime = firstObjectTime;
            LastObjectTime = lastObjectTime;
            Batches = batches;
            Warnings = warnings;
        }

        public readonly string Path;
        public readonly int KeyCount;
        public readonly double OverallDifficulty;
        public readonly int ObjectCount;
        public readonly int FirstObjectTime;
        public readonly int LastObjectTime;
        public readonly List<LiveTransitionBatch> Batches;
        public readonly List<string> Warnings;
    }

    internal static class LivePlanBuilder
    {
        private sealed class HitObject
        {
            public int X;
            public int Lane;
            public int StartTime;
            public int EndTime;
            public bool IsHold;
            public int SourceLine;
        }

        public static LiveManiaPlan ParseAndBuild(string path, int tapMilliseconds)
        {
            if (String.IsNullOrEmpty(path) || !File.Exists(path))
                throw new FileNotFoundException("Current beatmap .osu file was not found.", path);
            if (tapMilliseconds < 1 || tapMilliseconds > 100)
                throw new ArgumentOutOfRangeException("tapMilliseconds", "tap duration must be from 1 through 100 ms");

            int mode = -1;
            double circleSize = Double.NaN;
            double overallDifficulty = Double.NaN;
            string section = String.Empty;
            int lineNumber = 0;
            List<HitObject> objects = new List<HitObject>();

            using (StreamReader reader = new StreamReader(path, Encoding.UTF8, true))
            {
                string raw;
                while ((raw = reader.ReadLine()) != null)
                {
                    lineNumber++;
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal))
                        continue;

                    if (line[0] == '[' && line[line.Length - 1] == ']')
                    {
                        section = line.Substring(1, line.Length - 2).Trim();
                        continue;
                    }

                    if (String.Equals(section, "HitObjects", StringComparison.OrdinalIgnoreCase))
                    {
                        ParseHitObject(path, lineNumber, line, objects);
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
                    else if (String.Equals(section, "Difficulty", StringComparison.OrdinalIgnoreCase)
                        && String.Equals(key, "CircleSize", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!Double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out circleSize))
                            throw Error(path, lineNumber, "CircleSize is not numeric: " + value);
                    }
                    else if (String.Equals(section, "Difficulty", StringComparison.OrdinalIgnoreCase)
                        && String.Equals(key, "OverallDifficulty", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!Double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out overallDifficulty))
                            throw Error(path, lineNumber, "OverallDifficulty is not numeric: " + value);
                    }
                }
            }

            if (mode != 3)
                throw new InvalidDataException(path + ": expected native Mode:3, got " + mode + ".");
            if (Double.IsNaN(circleSize) || Double.IsInfinity(circleSize))
                throw new InvalidDataException(path + ": CircleSize must be an integer from 1 through 18.");
            if (Double.IsNaN(overallDifficulty) || Double.IsInfinity(overallDifficulty))
                throw new InvalidDataException(path + ": OverallDifficulty is missing or invalid.");

            int keyCount = checked((int)Math.Round(circleSize, MidpointRounding.AwayFromZero));
            if (Math.Abs(circleSize - keyCount) > 0.0001 || keyCount < 1 || keyCount > 18)
                throw new InvalidDataException(path + ": CircleSize must be an integer from 1 through 18.");
            if (objects.Count == 0)
                throw new InvalidDataException(path + ": [HitObjects] contains no supported mania objects.");

            int firstObjectTime = Int32.MaxValue;
            int lastObjectTime = Int32.MinValue;
            for (int index = 0; index < objects.Count; index++)
            {
                HitObject hitObject = objects[index];
                int lane = (int)Math.Floor(hitObject.X * keyCount / 512.0);
                hitObject.Lane = Math.Max(0, Math.Min(keyCount - 1, lane));
                firstObjectTime = Math.Min(firstObjectTime, hitObject.StartTime);
                lastObjectTime = Math.Max(lastObjectTime, hitObject.EndTime);
            }

            List<string> warnings = new List<string>();
            List<LiveLaneTransition> transitions = BuildTransitions(
                path,
                objects,
                keyCount,
                tapMilliseconds,
                warnings);
            List<LiveTransitionBatch> batches = Batch(transitions);
            if (batches.Count == 0)
                throw new InvalidDataException(path + ": no live input transitions were generated.");

            return new LiveManiaPlan(
                Path.GetFullPath(path),
                keyCount,
                overallDifficulty,
                objects.Count,
                firstObjectTime,
                lastObjectTime,
                batches,
                warnings);
        }

        public static List<LiveKeySpec> ResolveKeys(string configuredLayout, int keyCount)
        {
            string layout = configuredLayout;
            if (String.IsNullOrWhiteSpace(layout))
                layout = DefaultLayout(keyCount);
            if (String.IsNullOrEmpty(layout))
            {
                throw new InvalidOperationException(
                    keyCount + "K has no built-in key layout; set MANIA_AGENT_KEYS explicitly.");
            }

            string[] rawTokens = layout.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (rawTokens.Length != keyCount)
            {
                throw new InvalidOperationException(
                    "MANIA_AGENT_KEYS supplied " + rawTokens.Length + " keys for a " + keyCount + "K map.");
            }

            List<LiveKeySpec> result = new List<LiveKeySpec>(rawTokens.Length);
            HashSet<ushort> seen = new HashSet<ushort>();
            for (int index = 0; index < rawTokens.Length; index++)
            {
                LiveKeySpec key = ParseKey(rawTokens[index]);
                if (!seen.Add(key.VirtualKey))
                    throw new InvalidOperationException("MANIA_AGENT_KEYS repeats " + key.Name + ".");
                result.Add(key);
            }
            return result;
        }

        private static void ParseHitObject(
            string path,
            int lineNumber,
            string line,
            List<HitObject> objects)
        {
            string[] fields = line.Split(',');
            if (fields.Length < 5)
                throw Error(path, lineNumber, "HitObject has fewer than five fields.");

            int x = ParseInt(path, lineNumber, fields[0], "x");
            int start = ParseInt(path, lineNumber, fields[2], "time");
            int type = ParseInt(path, lineNumber, fields[3], "type");
            bool hold = (type & 128) != 0;
            bool tap = (type & 1) != 0;
            if (!hold && !tap)
                throw Error(path, lineNumber, "Unsupported mania HitObject type=" + type + ".");

            int end = start;
            if (hold)
            {
                if (fields.Length < 6)
                    throw Error(path, lineNumber, "Long note has no endTime field.");
                string endText = fields[5].Split(':')[0];
                end = ParseInt(path, lineNumber, endText, "endTime");
                if (end <= start)
                    throw Error(path, lineNumber, "Long note endTime must be greater than startTime.");
            }

            objects.Add(new HitObject
            {
                X = x,
                StartTime = start,
                EndTime = end,
                IsHold = hold,
                SourceLine = lineNumber
            });
        }

        private static List<LiveLaneTransition> BuildTransitions(
            string path,
            List<HitObject> objects,
            int keyCount,
            int tapMilliseconds,
            List<string> warnings)
        {
            List<LiveLaneTransition> transitions = new List<LiveLaneTransition>(objects.Count * 2);

            for (int lane = 0; lane < keyCount; lane++)
            {
                List<HitObject> laneObjects = new List<HitObject>();
                for (int objectIndex = 0; objectIndex < objects.Count; objectIndex++)
                {
                    if (objects[objectIndex].Lane == lane)
                        laneObjects.Add(objects[objectIndex]);
                }
                laneObjects.Sort(delegate(HitObject left, HitObject right)
                {
                    int byStart = left.StartTime.CompareTo(right.StartTime);
                    if (byStart != 0)
                        return byStart;
                    return left.EndTime.CompareTo(right.EndTime);
                });

                for (int index = 0; index < laneObjects.Count; index++)
                {
                    HitObject hitObject = laneObjects[index];
                    int nextStart = index + 1 < laneObjects.Count
                        ? laneObjects[index + 1].StartTime
                        : Int32.MaxValue;
                    if (nextStart == hitObject.StartTime)
                    {
                        throw Error(
                            path,
                            hitObject.SourceLine,
                            "lane " + (lane + 1) + " has two objects at " + hitObject.StartTime
                                + "ms; one physical key cannot express both.");
                    }

                    int releaseTime;
                    if (hitObject.IsHold)
                    {
                        releaseTime = hitObject.EndTime - 1;
                        if (nextStart <= releaseTime)
                        {
                            releaseTime = nextStart - 1;
                            warnings.Add(
                                "lane " + (lane + 1) + ": LN at source line " + hitObject.SourceLine
                                    + " overlaps the next object; release moved to " + releaseTime + "ms.");
                        }
                    }
                    else
                    {
                        releaseTime = hitObject.StartTime + tapMilliseconds;
                        if (nextStart <= releaseTime)
                            releaseTime = nextStart - 1;
                    }

                    releaseTime = Math.Max(hitObject.StartTime + 1, releaseTime);
                    transitions.Add(new LiveLaneTransition(
                        hitObject.StartTime,
                        lane,
                        true,
                        hitObject.SourceLine,
                        hitObject.StartTime,
                        hitObject.IsHold));
                    transitions.Add(new LiveLaneTransition(
                        releaseTime,
                        lane,
                        false,
                        hitObject.SourceLine,
                        releaseTime,
                        hitObject.IsHold));
                }
            }

            transitions.Sort(delegate(LiveLaneTransition left, LiveLaneTransition right)
            {
                int byTime = left.Time.CompareTo(right.Time);
                if (byTime != 0)
                    return byTime;
                if (left.IsDown != right.IsDown)
                    return left.IsDown ? 1 : -1;
                return left.Lane.CompareTo(right.Lane);
            });
            return transitions;
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

        private static string DefaultLayout(int keyCount)
        {
            switch (keyCount)
            {
                case 1: return "SPACE";
                case 2: return "F,J";
                case 3: return "F,SPACE,J";
                case 4: return "D,F,J,K";
                case 5: return "D,F,SPACE,J,K";
                case 6: return "S,D,F,J,K,L";
                case 7: return "S,D,F,SPACE,J,K,L";
                case 8: return "A,S,D,F,J,K,L,SEMICOLON";
                case 9: return "A,S,D,F,SPACE,J,K,L,SEMICOLON";
                default: return null;
            }
        }

        private static LiveKeySpec ParseKey(string text)
        {
            string normalized = text.Trim().ToUpperInvariant();
            if (normalized.Length == 1)
            {
                char character = normalized[0];
                if ((character >= 'A' && character <= 'Z') || (character >= '0' && character <= '9'))
                    return new LiveKeySpec(normalized, checked((ushort)character));

                ushort punctuation = PunctuationKey(character);
                if (punctuation != 0)
                    return new LiveKeySpec(normalized, punctuation);
            }

            if (normalized.StartsWith("NUMPAD", StringComparison.Ordinal)
                && normalized.Length == 7
                && normalized[6] >= '0'
                && normalized[6] <= '9')
            {
                return new LiveKeySpec(normalized, checked((ushort)(0x60 + normalized[6] - '0')));
            }

            if (normalized.Length >= 2 && normalized[0] == 'F')
            {
                int functionNumber;
                if (Int32.TryParse(
                        normalized.Substring(1),
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out functionNumber)
                    && functionNumber >= 1
                    && functionNumber <= 24)
                {
                    return new LiveKeySpec(
                        normalized,
                        checked((ushort)(0x70 + functionNumber - 1)));
                }
            }

            ushort named = NamedKey(normalized);
            if (named != 0)
                return new LiveKeySpec(normalized, named);

            if (normalized.StartsWith("0X", StringComparison.Ordinal) && normalized.Length > 2)
            {
                ushort raw;
                if (UInt16.TryParse(
                        normalized.Substring(2),
                        NumberStyles.AllowHexSpecifier,
                        CultureInfo.InvariantCulture,
                        out raw)
                    && raw > 0
                    && raw < 0xFF)
                {
                    return new LiveKeySpec("0x" + raw.ToString("X2"), raw);
                }
            }

            throw new InvalidOperationException(
                "Unknown key '" + text
                    + "'. Use A-Z, 0-9, F1-F24, NUMPAD0-9, a named key, or 0xNN.");
        }

        private static ushort NamedKey(string name)
        {
            switch (name)
            {
                case "BACKSPACE": return 0x08;
                case "TAB": return 0x09;
                case "ENTER":
                case "RETURN": return 0x0D;
                case "SPACE":
                case "SPACEBAR": return 0x20;
                case "PAGEUP": return 0x21;
                case "PAGEDOWN": return 0x22;
                case "END": return 0x23;
                case "HOME": return 0x24;
                case "LEFT": return 0x25;
                case "UP": return 0x26;
                case "RIGHT": return 0x27;
                case "DOWN": return 0x28;
                case "INSERT": return 0x2D;
                case "DELETE": return 0x2E;
                case "LSHIFT": return 0xA0;
                case "RSHIFT": return 0xA1;
                case "LCTRL": return 0xA2;
                case "RCTRL": return 0xA3;
                case "LALT": return 0xA4;
                case "RALT": return 0xA5;
                case "SEMICOLON":
                case "OEM1": return 0xBA;
                case "PLUS":
                case "EQUALS": return 0xBB;
                case "COMMA": return 0xBC;
                case "MINUS": return 0xBD;
                case "PERIOD":
                case "DOT": return 0xBE;
                case "SLASH": return 0xBF;
                case "BACKTICK": return 0xC0;
                case "LBRACKET": return 0xDB;
                case "BACKSLASH": return 0xDC;
                case "RBRACKET": return 0xDD;
                case "QUOTE": return 0xDE;
                default: return 0;
            }
        }

        private static ushort PunctuationKey(char character)
        {
            switch (character)
            {
                case ';': return 0xBA;
                case '=': return 0xBB;
                case ',': return 0xBC;
                case '-': return 0xBD;
                case '.': return 0xBE;
                case '/': return 0xBF;
                case '`': return 0xC0;
                case '[': return 0xDB;
                case '\\': return 0xDC;
                case ']': return 0xDD;
                case '\'': return 0xDE;
                default: return 0;
            }
        }

        private static int ParseInt(string path, int lineNumber, string text, string field)
        {
            int value;
            if (!Int32.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                throw Error(path, lineNumber, field + " is not an integer: " + text);
            return value;
        }

        private static InvalidDataException Error(string path, int lineNumber, string message)
        {
            return new InvalidDataException(path + ":" + lineNumber + ": " + message);
        }
    }
}
