using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace LocalManiaAuto.Plugin
{
    internal sealed class NativeFrameData
    {
        public NativeFrameData(int time, int keyMask)
        {
            Time = time;
            KeyMask = keyMask;
        }

        public int Time;
        public int KeyMask;
    }

    internal sealed class ParsedManiaMap
    {
        public ParsedManiaMap(int keyCount, int objectCount, int firstObjectTime, List<NativeFrameData> frames)
        {
            KeyCount = keyCount;
            ObjectCount = objectCount;
            FirstObjectTime = firstObjectTime;
            Frames = frames;
        }

        public readonly int KeyCount;
        public readonly int ObjectCount;
        public readonly int FirstObjectTime;
        public readonly List<NativeFrameData> Frames;
    }

    internal static class NativeFrameBuilder
    {
        private sealed class HitObject
        {
            public int Lane;
            public int StartTime;
            public int EndTime;
            public bool IsHold;
            public int SourceLine;
        }

        public static ParsedManiaMap ParseAndBuild(string path)
        {
            if (String.IsNullOrEmpty(path) || !File.Exists(path))
                throw new FileNotFoundException("Current beatmap .osu file was not found.", path);

            int mode = -1;
            double circleSize = Double.NaN;
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
                }
            }

            if (mode != 3)
                throw new InvalidDataException(path + ": expected native Mode:3, got " + mode + ".");
            if (Double.IsNaN(circleSize) || Double.IsInfinity(circleSize))
                throw new InvalidDataException(path + ": CircleSize must be an integer from 1 through 18.");
            int keyCount = checked((int)Math.Round(circleSize, MidpointRounding.AwayFromZero));
            if (Math.Abs(circleSize - keyCount) > 0.0001 || keyCount < 1 || keyCount > 18)
                throw new InvalidDataException(path + ": CircleSize must be an integer from 1 through 18.");
            if (objects.Count == 0)
                throw new InvalidDataException(path + ": [HitObjects] contains no supported mania objects.");

            for (int index = 0; index < objects.Count; index++)
            {
                HitObject hitObject = objects[index];
                int x = hitObject.Lane;
                int lane = (int)Math.Floor(x * keyCount / 512.0);
                hitObject.Lane = Math.Max(0, Math.Min(keyCount - 1, lane));
            }

            objects.Sort(delegate(HitObject left, HitObject right)
            {
                int byStart = left.StartTime.CompareTo(right.StartTime);
                if (byStart != 0)
                    return byStart;
                return left.Lane.CompareTo(right.Lane);
            });

            List<NativeFrameData> frames = Build(objects);
            return new ParsedManiaMap(keyCount, objects.Count, objects[0].StartTime, frames);
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
                // Preserve x temporarily; it is converted after CircleSize is known.
                Lane = x,
                StartTime = start,
                EndTime = end,
                IsHold = hold,
                SourceLine = lineNumber
            });
        }

        private static List<NativeFrameData> Build(List<HitObject> objects)
        {
            List<NativeFrameData> frames = new List<NativeFrameData>();
            frames.Add(new NativeFrameData(0, 0));

            for (int objectIndex = 0; objectIndex < objects.Count; objectIndex++)
            {
                HitObject hitObject = objects[objectIndex];
                int bit = 1 << hitObject.Lane;
                ToggleAt(frames, hitObject.StartTime, bit);

                if (hitObject.IsHold)
                {
                    for (int frameIndex = 0; frameIndex < frames.Count; frameIndex++)
                    {
                        NativeFrameData frame = frames[frameIndex];
                        if (frame.Time > hitObject.StartTime && frame.Time < hitObject.EndTime)
                            frame.KeyMask = Toggle(frame.KeyMask, bit);
                    }
                    ToggleAt(frames, hitObject.EndTime - 1, -bit);
                }
                else
                {
                    ToggleAt(frames, hitObject.StartTime + 1, -bit);
                }
            }
            return frames;
        }

        private static void ToggleAt(List<NativeFrameData> frames, int time, int signedBit)
        {
            int previousIndex = FindLastAtOrBefore(frames, time);
            if (previousIndex < 0)
            {
                frames.Add(new NativeFrameData(time, Toggle(0, signedBit)));
                frames.Sort(delegate(NativeFrameData left, NativeFrameData right)
                {
                    return left.Time.CompareTo(right.Time);
                });
                return;
            }

            if (time == 0)
            {
                frames.Insert(previousIndex + 1, new NativeFrameData(
                    0,
                    Toggle(frames[previousIndex].KeyMask, signedBit)));
            }
            else if (frames[previousIndex].Time == time)
            {
                frames[previousIndex].KeyMask = Toggle(frames[previousIndex].KeyMask, signedBit);
            }
            else
            {
                frames.Insert(previousIndex + 1, new NativeFrameData(
                    time,
                    Toggle(frames[previousIndex].KeyMask, signedBit)));
            }
        }

        private static int FindLastAtOrBefore(List<NativeFrameData> frames, int time)
        {
            int low = 0;
            int high = frames.Count - 1;
            int result = -1;
            while (low <= high)
            {
                int middle = low + ((high - low) / 2);
                if (frames[middle].Time <= time)
                {
                    result = middle;
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }
            return result;
        }

        private static int Toggle(int mask, int signedBit)
        {
            if (signedBit > 0)
                return (mask & signedBit) == 0 ? mask | signedBit : mask & ~signedBit;

            int bit = -signedBit;
            return (mask & bit) != 0 ? mask & ~bit : mask;
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
