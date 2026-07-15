using System.Globalization;
using System.Text;

namespace OsuReverseEngineering.Taiko;

public static class BeatmapParser
{
    // The native Taiko hit-object manager scales slider length before converting
    // it to a drumroll duration. This is separate from SliderMultiplier.
    private const double TaikoDrumRollLengthMultiplier = 1.4;
    private const int HitSoundWhistle = 2;
    private const int HitSoundFinish = 4;
    private const int HitSoundClap = 8;

    private sealed record RawHitObject(int LineNumber, string Text);

    private readonly record struct TimingState(double BeatLength, double VelocityMultiplier);

    public static TaikoBeatmapDocument Parse(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A beatmap path is required.", nameof(path));
        if (!File.Exists(path))
            throw new FileNotFoundException("Beatmap file was not found.", path);

        var fullPath = Path.GetFullPath(path);
        var formatVersion = -1;
        var mode = -1;
        var overallDifficulty = double.NaN;
        var sliderMultiplier = double.NaN;
        var sliderTickRate = 1.0;
        var artist = string.Empty;
        var title = string.Empty;
        var creator = string.Empty;
        var version = string.Empty;
        var section = string.Empty;
        var lineNumber = 0;
        var timingPoints = new List<TaikoTimingPoint>();
        var rawObjects = new List<RawHitObject>();

        using var reader = new StreamReader(fullPath, Encoding.UTF8, true);
        while (reader.ReadLine() is { } raw)
        {
            lineNumber++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal))
                continue;

            if (lineNumber == 1 && line.StartsWith("osu file format v", StringComparison.OrdinalIgnoreCase))
            {
                formatVersion = ParseInt(fullPath, lineNumber, line[17..], "format version");
                continue;
            }

            if (line[0] == '[' && line[^1] == ']')
            {
                section = line[1..^1].Trim();
                continue;
            }

            if (section.Equals("TimingPoints", StringComparison.OrdinalIgnoreCase))
            {
                timingPoints.Add(ParseTimingPoint(fullPath, lineNumber, line));
                continue;
            }

            if (section.Equals("HitObjects", StringComparison.OrdinalIgnoreCase))
            {
                rawObjects.Add(new RawHitObject(lineNumber, line));
                continue;
            }

            var separator = line.IndexOf(':');
            if (separator <= 0)
                continue;

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (section.Equals("General", StringComparison.OrdinalIgnoreCase)
                && key.Equals("Mode", StringComparison.OrdinalIgnoreCase))
            {
                mode = ParseInt(fullPath, lineNumber, value, "Mode");
            }
            else if (section.Equals("Difficulty", StringComparison.OrdinalIgnoreCase))
            {
                if (key.Equals("OverallDifficulty", StringComparison.OrdinalIgnoreCase))
                    overallDifficulty = ParseDouble(fullPath, lineNumber, value, key);
                else if (key.Equals("SliderMultiplier", StringComparison.OrdinalIgnoreCase))
                    sliderMultiplier = ParseDouble(fullPath, lineNumber, value, key);
                else if (key.Equals("SliderTickRate", StringComparison.OrdinalIgnoreCase))
                    sliderTickRate = ParseDouble(fullPath, lineNumber, value, key);
            }
            else if (section.Equals("Metadata", StringComparison.OrdinalIgnoreCase))
            {
                if (key.Equals("Artist", StringComparison.OrdinalIgnoreCase))
                    artist = value;
                else if (key.Equals("Title", StringComparison.OrdinalIgnoreCase))
                    title = value;
                else if (key.Equals("Creator", StringComparison.OrdinalIgnoreCase))
                    creator = value;
                else if (key.Equals("Version", StringComparison.OrdinalIgnoreCase))
                    version = value;
            }
        }

        if (formatVersion < 1)
            throw Error(fullPath, 1, "missing or invalid osu file format header");
        if (mode != 1)
            throw new InvalidDataException($"{fullPath}: expected native Mode:1, got {mode}.");
        if (!double.IsFinite(overallDifficulty))
            throw new InvalidDataException($"{fullPath}: OverallDifficulty is missing or invalid.");
        if (!double.IsFinite(sliderMultiplier) || sliderMultiplier <= 0)
            throw new InvalidDataException($"{fullPath}: SliderMultiplier must be positive.");
        if (!double.IsFinite(sliderTickRate) || sliderTickRate <= 0)
            throw new InvalidDataException($"{fullPath}: SliderTickRate must be positive.");
        if (timingPoints.Count == 0 || !timingPoints.Any(point => point.Uninherited && point.BeatLength > 0))
            throw new InvalidDataException($"{fullPath}: no positive uninherited timing point was found.");
        if (rawObjects.Count == 0)
            throw new InvalidDataException($"{fullPath}: [HitObjects] is empty.");

        timingPoints.Sort((left, right) =>
        {
            var byTime = left.Time.CompareTo(right.Time);
            return byTime != 0 ? byTime : left.SourceLine.CompareTo(right.SourceLine);
        });

        var hitObjects = rawObjects
            .Select(rawObject => ParseHitObject(
                fullPath,
                rawObject,
                timingPoints,
                sliderMultiplier))
            .OrderBy(hitObject => hitObject.StartTime)
            .ThenBy(hitObject => hitObject.SourceLine)
            .ToList();

        return new TaikoBeatmapDocument
        {
            Path = fullPath,
            FormatVersion = formatVersion,
            Mode = mode,
            OverallDifficulty = overallDifficulty,
            SliderMultiplier = sliderMultiplier,
            SliderTickRate = sliderTickRate,
            Artist = artist,
            Title = title,
            Creator = creator,
            Version = version,
            TimingPoints = timingPoints,
            HitObjects = hitObjects
        };
    }

    public static bool TryReadMode(string path, out int mode)
    {
        mode = -1;
        var section = string.Empty;
        using var reader = new StreamReader(path, Encoding.UTF8, true);
        while (reader.ReadLine() is { } raw)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal))
                continue;
            if (line[0] == '[' && line[^1] == ']')
            {
                section = line[1..^1].Trim();
                if (section.Equals("HitObjects", StringComparison.OrdinalIgnoreCase))
                    return false;
                continue;
            }
            if (!section.Equals("General", StringComparison.OrdinalIgnoreCase))
                continue;
            var separator = line.IndexOf(':');
            if (separator <= 0 || !line[..separator].Trim().Equals("Mode", StringComparison.OrdinalIgnoreCase))
                continue;
            return int.TryParse(
                line[(separator + 1)..].Trim(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out mode);
        }
        return false;
    }

    private static TaikoTimingPoint ParseTimingPoint(string path, int lineNumber, string line)
    {
        var fields = line.Split(',');
        if (fields.Length < 2)
            throw Error(path, lineNumber, "timing point has fewer than two fields");
        var time = ParseDouble(path, lineNumber, fields[0], "timing point time");
        var beatLength = ParseDouble(path, lineNumber, fields[1], "beatLength");
        var uninherited = fields.Length < 7 || ParseInt(path, lineNumber, fields[6], "uninherited") != 0;
        if (uninherited && beatLength <= 0)
            throw Error(path, lineNumber, "uninherited timing point must have a positive beatLength");
        if (!uninherited && beatLength >= 0)
            throw Error(path, lineNumber, "inherited timing point must have a negative beatLength");
        return new TaikoTimingPoint
        {
            Time = time,
            BeatLength = beatLength,
            Uninherited = uninherited,
            SourceLine = lineNumber
        };
    }

    private static TaikoHitObject ParseHitObject(
        string path,
        RawHitObject rawObject,
        IReadOnlyList<TaikoTimingPoint> timingPoints,
        double sliderMultiplier)
    {
        var fields = rawObject.Text.Split(',');
        if (fields.Length < 5)
            throw Error(path, rawObject.LineNumber, "hit object has fewer than five fields");

        var startTime = ParseInt(path, rawObject.LineNumber, fields[2], "time");
        var type = ParseInt(path, rawObject.LineNumber, fields[3], "type");
        var hitSound = ParseInt(path, rawObject.LineNumber, fields[4], "hitSound");
        var colour = (hitSound & (HitSoundWhistle | HitSoundClap)) != 0
            ? TaikoColour.Kat
            : TaikoColour.Don;
        var strong = (hitSound & HitSoundFinish) != 0;

        if ((type & 1) != 0)
        {
            return new TaikoHitObject
            {
                Kind = TaikoObjectKind.Circle,
                StartTime = startTime,
                EndTime = startTime,
                RawType = type,
                HitSound = hitSound,
                SourceLine = rawObject.LineNumber,
                Colour = colour,
                IsStrong = strong
            };
        }

        if ((type & 2) != 0)
        {
            if (fields.Length < 8)
                throw Error(path, rawObject.LineNumber, "slider has fewer than eight fields");
            var repeatCount = ParseInt(path, rawObject.LineNumber, fields[6], "repeat count");
            var pixelLength = ParseDouble(path, rawObject.LineNumber, fields[7], "pixel length");
            if (repeatCount < 1)
                throw Error(path, rawObject.LineNumber, "slider repeat count must be at least one");
            if (pixelLength <= 0)
                throw Error(path, rawObject.LineNumber, "slider pixel length must be positive");

            var timing = ResolveTimingState(timingPoints, startTime);
            var spanDuration = pixelLength * TaikoDrumRollLengthMultiplier * timing.BeatLength
                / (sliderMultiplier * 100.0 * timing.VelocityMultiplier);
            // The stable Taiko manager truncates the computed duration before
            // adding it to the integer object start time.
            var endTime = startTime + (int)(spanDuration * repeatCount);
            if (!double.IsFinite(endTime) || endTime <= startTime)
                throw Error(path, rawObject.LineNumber, "computed slider end time is invalid");

            return new TaikoHitObject
            {
                Kind = TaikoObjectKind.DrumRoll,
                StartTime = startTime,
                EndTime = endTime,
                RawType = type,
                HitSound = hitSound,
                SourceLine = rawObject.LineNumber,
                Colour = colour,
                IsStrong = strong,
                RepeatCount = repeatCount,
                PixelLength = pixelLength,
                BeatLength = timing.BeatLength,
                SliderVelocityMultiplier = timing.VelocityMultiplier
            };
        }

        if ((type & 8) != 0)
        {
            if (fields.Length < 6)
                throw Error(path, rawObject.LineNumber, "spinner has no end time");
            var endTime = ParseInt(path, rawObject.LineNumber, fields[5], "spinner end time");
            if (endTime <= startTime)
                throw Error(path, rawObject.LineNumber, "spinner end time must be after its start");
            return new TaikoHitObject
            {
                Kind = TaikoObjectKind.Spinner,
                StartTime = startTime,
                EndTime = endTime,
                RawType = type,
                HitSound = hitSound,
                SourceLine = rawObject.LineNumber,
                Colour = colour,
                IsStrong = strong
            };
        }

        throw Error(path, rawObject.LineNumber, $"unsupported Mode 1 hit object type={type}");
    }

    private static TimingState ResolveTimingState(
        IReadOnlyList<TaikoTimingPoint> timingPoints,
        int objectTime)
    {
        double? beatLength = null;
        var velocityMultiplier = 1.0;
        foreach (var point in timingPoints)
        {
            if (point.Time > objectTime)
                break;
            if (point.Uninherited)
            {
                beatLength = point.BeatLength;
                velocityMultiplier = 1.0;
            }
            else
            {
                velocityMultiplier = Math.Clamp(-100.0 / point.BeatLength, 0.1, 10.0);
            }
        }

        if (beatLength is null)
        {
            beatLength = timingPoints
                .Where(point => point.Uninherited && point.BeatLength > 0)
                .OrderBy(point => point.Time)
                .First().BeatLength;
        }
        return new TimingState(beatLength.Value, velocityMultiplier);
    }

    private static int ParseInt(string path, int lineNumber, string text, string field)
    {
        if (!int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            throw Error(path, lineNumber, $"{field} is not an integer: {text}");
        return value;
    }

    private static double ParseDouble(string path, int lineNumber, string text, string field)
    {
        if (!double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || !double.IsFinite(value))
        {
            throw Error(path, lineNumber, $"{field} is not numeric: {text}");
        }
        return value;
    }

    private static InvalidDataException Error(string path, int lineNumber, string message) =>
        new($"{path}:{lineNumber}: {message}");
}
