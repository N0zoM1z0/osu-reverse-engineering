using System.Globalization;
using System.Text;

namespace LocalManiaAuto;

internal sealed record ManiaHitObject(
    int X,
    int Lane,
    int StartTime,
    int EndTime,
    bool IsHold,
    int Type,
    int SourceLine);

internal sealed record ManiaBeatmap(
    string Path,
    int FormatVersion,
    int Mode,
    int KeyCount,
    string Artist,
    string Title,
    string Difficulty,
    string AudioFilename,
    IReadOnlyList<ManiaHitObject> HitObjects)
{
    public int FirstObjectTime => HitObjects.Count == 0 ? 0 : HitObjects[0].StartTime;

    public int LastObjectTime => HitObjects.Count == 0 ? 0 : HitObjects.Max(static o => o.EndTime);
}

internal sealed class BeatmapFormatException : Exception
{
    public BeatmapFormatException(string message)
        : base(message)
    {
    }
}

internal static class BeatmapParser
{
    public static ManiaBeatmap Parse(string path)
    {
        string fullPath = System.IO.Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The .osu file was not found.", fullPath);
        }

        var values = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var rawHitObjects = new List<(int LineNumber, string Text)>();
        string section = string.Empty;
        int formatVersion = 0;
        int lineNumber = 0;

        using var reader = new StreamReader(fullPath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        while (reader.ReadLine() is { } rawLine)
        {
            lineNumber++;
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            if (formatVersion == 0 && line.StartsWith("osu file format v", StringComparison.OrdinalIgnoreCase))
            {
                string versionText = line["osu file format v".Length..].Trim();
                if (!int.TryParse(versionText, NumberStyles.Integer, CultureInfo.InvariantCulture, out formatVersion))
                {
                    throw Error(fullPath, lineNumber, $"Could not parse format version: {versionText}");
                }
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
                if (!values.ContainsKey(section))
                {
                    values[section] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }
                continue;
            }

            if (section.Equals("HitObjects", StringComparison.OrdinalIgnoreCase))
            {
                rawHitObjects.Add((lineNumber, line));
                continue;
            }

            int separator = line.IndexOf(':');
            if (separator > 0 && section.Length > 0)
            {
                string key = line[..separator].Trim();
                string value = line[(separator + 1)..].Trim();
                values[section][key] = value;
            }
        }

        if (formatVersion == 0)
        {
            throw new BeatmapFormatException($"{fullPath}: missing 'osu file format vN' header.");
        }

        int mode = ParseRequiredInt(values, fullPath, "General", "Mode");
        if (mode != 3)
        {
            throw new BeatmapFormatException($"{fullPath}: Mode is {mode}; this tool accepts native osu!mania files only (Mode:3).");
        }

        double circleSize = ParseRequiredDouble(values, fullPath, "Difficulty", "CircleSize");
        int keyCount = checked((int)Math.Round(circleSize, MidpointRounding.AwayFromZero));
        if (Math.Abs(circleSize - keyCount) > 0.0001 || keyCount is < 1 or > 18)
        {
            throw new BeatmapFormatException($"{fullPath}: CircleSize={circleSize.ToString(CultureInfo.InvariantCulture)} is not a supported integer key count from 1 through 18.");
        }

        var objects = new List<ManiaHitObject>(rawHitObjects.Count);
        foreach ((int sourceLine, string text) in rawHitObjects)
        {
            string[] fields = text.Split(',');
            if (fields.Length < 5)
            {
                throw Error(fullPath, sourceLine, "HitObject has fewer than five fields.");
            }

            int x = ParseInt(fields[0], fullPath, sourceLine, "x");
            int startTime = ParseInt(fields[2], fullPath, sourceLine, "time");
            int type = ParseInt(fields[3], fullPath, sourceLine, "type");
            bool isHold = (type & 128) != 0;
            bool isTap = (type & 1) != 0;

            if (!isHold && !isTap)
            {
                throw Error(fullPath, sourceLine, $"Unsupported mania HitObject type={type}.");
            }

            int endTime = startTime;
            if (isHold)
            {
                if (fields.Length < 6)
                {
                    throw Error(fullPath, sourceLine, "Long note is missing its endTime field.");
                }

                string endText = fields[5].Split(':', 2)[0];
                endTime = ParseInt(endText, fullPath, sourceLine, "endTime");
                if (endTime <= startTime)
                {
                    throw Error(fullPath, sourceLine, $"Long-note endTime={endTime} must be greater than startTime={startTime}.");
                }
            }

            int lane = Math.Clamp((int)Math.Floor(x * keyCount / 512d), 0, keyCount - 1);
            objects.Add(new ManiaHitObject(x, lane, startTime, endTime, isHold, type, sourceLine));
        }

        objects.Sort(static (left, right) =>
        {
            int byStart = left.StartTime.CompareTo(right.StartTime);
            return byStart != 0 ? byStart : left.Lane.CompareTo(right.Lane);
        });

        if (objects.Count == 0)
        {
            throw new BeatmapFormatException($"{fullPath}: [HitObjects] contains no mania objects.");
        }

        return new ManiaBeatmap(
            fullPath,
            formatVersion,
            mode,
            keyCount,
            GetValue(values, "Metadata", "Artist"),
            GetValue(values, "Metadata", "Title"),
            GetValue(values, "Metadata", "Version"),
            GetValue(values, "General", "AudioFilename"),
            objects);
    }

    private static int ParseRequiredInt(
        IReadOnlyDictionary<string, Dictionary<string, string>> values,
        string path,
        string section,
        string key)
    {
        string text = GetRequiredValue(values, path, section, key);
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
        {
            throw new BeatmapFormatException($"{path}: [{section}] {key} is not an integer: {text}");
        }
        return value;
    }

    private static double ParseRequiredDouble(
        IReadOnlyDictionary<string, Dictionary<string, string>> values,
        string path,
        string section,
        string key)
    {
        string text = GetRequiredValue(values, path, section, key);
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
        {
            throw new BeatmapFormatException($"{path}: [{section}] {key} is not numeric: {text}");
        }
        return value;
    }

    private static int ParseInt(string text, string path, int lineNumber, string field)
    {
        if (!int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
        {
            throw Error(path, lineNumber, $"HitObject {field} is not an integer: {text}");
        }
        return value;
    }

    private static string GetRequiredValue(
        IReadOnlyDictionary<string, Dictionary<string, string>> values,
        string path,
        string section,
        string key)
    {
        string value = GetValue(values, section, key);
        if (value.Length == 0)
        {
            throw new BeatmapFormatException($"{path}: missing [{section}] {key}.");
        }
        return value;
    }

    private static string GetValue(
        IReadOnlyDictionary<string, Dictionary<string, string>> values,
        string section,
        string key)
    {
        return values.TryGetValue(section, out Dictionary<string, string>? sectionValues)
            && sectionValues.TryGetValue(key, out string? value)
            ? value
            : string.Empty;
    }

    private static BeatmapFormatException Error(string path, int lineNumber, string message)
        => new($"{path}:{lineNumber}: {message}");
}
