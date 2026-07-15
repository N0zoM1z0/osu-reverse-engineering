using System.Globalization;
using System.Text;

namespace OsuReverseEngineering.Catch;

public static class BeatmapParser
{
    private sealed record RawLine(int LineNumber, string Text);

    public static CatchBeatmapDocument Parse(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A beatmap path is required.", nameof(path));
        if (!File.Exists(path))
            throw new FileNotFoundException("Beatmap file was not found.", path);

        var fullPath = Path.GetFullPath(path);
        var formatVersion = -1;
        var mode = -1;
        var circleSize = double.NaN;
        var overallDifficulty = double.NaN;
        var sliderMultiplier = double.NaN;
        var sliderTickRate = 1.0;
        var artist = string.Empty;
        var title = string.Empty;
        var creator = string.Empty;
        var version = string.Empty;
        var section = string.Empty;
        var lineNumber = 0;
        var timingPoints = new List<CatchTimingPoint>();
        var rawObjects = new List<RawLine>();

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
                rawObjects.Add(new RawLine(lineNumber, line));
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
                if (key.Equals("CircleSize", StringComparison.OrdinalIgnoreCase))
                    circleSize = ParseDouble(fullPath, lineNumber, value, key);
                else if (key.Equals("OverallDifficulty", StringComparison.OrdinalIgnoreCase))
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

        ValidateHeader(
            fullPath,
            formatVersion,
            mode,
            circleSize,
            overallDifficulty,
            sliderMultiplier,
            sliderTickRate,
            timingPoints,
            rawObjects);

        timingPoints.Sort((left, right) =>
        {
            var byTime = left.Time.CompareTo(right.Time);
            return byTime != 0 ? byTime : left.SourceLine.CompareTo(right.SourceLine);
        });

        var hitObjects = rawObjects
            .Select(rawObject => ParseHitObject(fullPath, rawObject))
            .OrderBy(hitObject => hitObject.StartTime)
            .ThenBy(hitObject => hitObject.SourceLine)
            .ToList();

        return new CatchBeatmapDocument
        {
            Path = fullPath,
            FormatVersion = formatVersion,
            Mode = mode,
            CircleSize = circleSize,
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

    private static CatchTimingPoint ParseTimingPoint(string path, int lineNumber, string line)
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
        return new CatchTimingPoint
        {
            Time = time,
            BeatLength = beatLength,
            Uninherited = uninherited,
            SourceLine = lineNumber
        };
    }

    private static RawCatchHitObject ParseHitObject(string path, RawLine rawObject)
    {
        var fields = rawObject.Text.Split(',');
        if (fields.Length < 5)
            throw Error(path, rawObject.LineNumber, "hit object has fewer than five fields");

        var x = ParseDouble(path, rawObject.LineNumber, fields[0], "x");
        var y = ParseDouble(path, rawObject.LineNumber, fields[1], "y");
        var startTime = ParseInt(path, rawObject.LineNumber, fields[2], "time");
        var type = ParseInt(path, rawObject.LineNumber, fields[3], "type");
        var hitSound = ParseInt(path, rawObject.LineNumber, fields[4], "hitSound");
        var position = new Point2(x, y);

        if ((type & 1) != 0)
        {
            return new RawCatchHitObject
            {
                Kind = RawCatchObjectKind.Circle,
                Position = position,
                StartTime = startTime,
                EndTime = startTime,
                RawType = type,
                HitSound = hitSound,
                SourceLine = rawObject.LineNumber
            };
        }

        if ((type & 2) != 0)
        {
            if (fields.Length < 8)
                throw Error(path, rawObject.LineNumber, "slider has fewer than eight fields");
            var curveFields = fields[5].Split('|');
            if (curveFields.Length < 2 || curveFields[0].Length == 0)
                throw Error(path, rawObject.LineNumber, "slider path has no control point");
            var curveKind = ParseCurveKind(path, rawObject.LineNumber, curveFields[0]);
            var points = new List<Point2> { position };
            for (var index = 1; index < curveFields.Length; index++)
            {
                var separator = curveFields[index].IndexOf(':');
                if (separator <= 0 || separator >= curveFields[index].Length - 1)
                    throw Error(path, rawObject.LineNumber, $"invalid slider control point: {curveFields[index]}");
                points.Add(new Point2(
                    ParseDouble(path, rawObject.LineNumber, curveFields[index][..separator], "control point x"),
                    ParseDouble(path, rawObject.LineNumber, curveFields[index][(separator + 1)..], "control point y")));
            }
            var repeatCount = ParseInt(path, rawObject.LineNumber, fields[6], "repeat count");
            var pixelLength = ParseDouble(path, rawObject.LineNumber, fields[7], "pixel length");
            if (repeatCount < 1)
                throw Error(path, rawObject.LineNumber, "slider repeat count must be at least one");
            if (pixelLength <= 0)
                throw Error(path, rawObject.LineNumber, "slider pixel length must be positive");

            return new RawCatchHitObject
            {
                Kind = RawCatchObjectKind.Slider,
                Position = position,
                StartTime = startTime,
                EndTime = startTime,
                RawType = type,
                HitSound = hitSound,
                SourceLine = rawObject.LineNumber,
                Slider = new CatchSliderDefinition
                {
                    CurveKind = curveKind,
                    ControlPoints = points,
                    RepeatCount = repeatCount,
                    PixelLength = pixelLength
                }
            };
        }

        if ((type & 8) != 0)
        {
            if (fields.Length < 6)
                throw Error(path, rawObject.LineNumber, "spinner has no end time");
            var endTime = ParseInt(path, rawObject.LineNumber, fields[5], "spinner end time");
            if (endTime <= startTime)
                throw Error(path, rawObject.LineNumber, "spinner end time must be after its start");
            return new RawCatchHitObject
            {
                Kind = RawCatchObjectKind.Spinner,
                Position = position,
                StartTime = startTime,
                EndTime = endTime,
                RawType = type,
                HitSound = hitSound,
                SourceLine = rawObject.LineNumber
            };
        }

        throw Error(path, rawObject.LineNumber, $"unsupported Mode 2 hit object type={type}");
    }

    private static SliderCurveKind ParseCurveKind(string path, int lineNumber, string text) =>
        char.ToUpperInvariant(text[0]) switch
        {
            'L' => SliderCurveKind.Linear,
            'B' => SliderCurveKind.Bezier,
            'P' => SliderCurveKind.Perfect,
            'C' => SliderCurveKind.Catmull,
            _ => throw Error(path, lineNumber, $"unknown slider curve type: {text}")
        };

    private static void ValidateHeader(
        string path,
        int formatVersion,
        int mode,
        double circleSize,
        double overallDifficulty,
        double sliderMultiplier,
        double sliderTickRate,
        IReadOnlyList<CatchTimingPoint> timingPoints,
        IReadOnlyList<RawLine> rawObjects)
    {
        if (formatVersion < 1)
            throw Error(path, 1, "missing or invalid osu file format header");
        if (mode != 2)
            throw new InvalidDataException($"{path}: expected native Mode:2, got {mode}.");
        if (!double.IsFinite(circleSize))
            throw new InvalidDataException($"{path}: CircleSize is missing or invalid.");
        if (!double.IsFinite(overallDifficulty))
            throw new InvalidDataException($"{path}: OverallDifficulty is missing or invalid.");
        if (!double.IsFinite(sliderMultiplier) || sliderMultiplier <= 0)
            throw new InvalidDataException($"{path}: SliderMultiplier must be positive.");
        if (!double.IsFinite(sliderTickRate) || sliderTickRate <= 0)
            throw new InvalidDataException($"{path}: SliderTickRate must be positive.");
        if (timingPoints.Count == 0 || !timingPoints.Any(point => point.Uninherited && point.BeatLength > 0))
            throw new InvalidDataException($"{path}: no positive uninherited timing point was found.");
        if (rawObjects.Count == 0)
            throw new InvalidDataException($"{path}: [HitObjects] is empty.");
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
