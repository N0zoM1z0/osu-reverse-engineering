namespace OsuReverseEngineering.Catch;

public readonly record struct Point2(double X, double Y)
{
    public static Point2 operator +(Point2 left, Point2 right) =>
        new(left.X + right.X, left.Y + right.Y);

    public static Point2 operator -(Point2 left, Point2 right) =>
        new(left.X - right.X, left.Y - right.Y);

    public static Point2 operator *(Point2 point, double scale) =>
        new(point.X * scale, point.Y * scale);

    public static Point2 operator /(Point2 point, double scale) =>
        new(point.X / scale, point.Y / scale);

    public double Length => Math.Sqrt(X * X + Y * Y);

    public static double Distance(Point2 left, Point2 right) => (left - right).Length;

    public static Point2 Lerp(Point2 left, Point2 right, double amount) =>
        left + (right - left) * amount;
}

public readonly record struct Interval(double Min, double Max)
{
    public bool IsEmpty => Min > Max;
    public double Width => IsEmpty ? 0 : Max - Min;
    public double Midpoint => (Min + Max) * 0.5;

    public bool Contains(double value, double epsilon = 1e-7) =>
        !IsEmpty && value >= Min - epsilon && value <= Max + epsilon;

    public double Clamp(double value) => Math.Clamp(value, Min, Max);

    public Interval Expand(double amount) => new(Min - amount, Max + amount);

    public Interval Intersect(Interval other) =>
        new(Math.Max(Min, other.Min), Math.Min(Max, other.Max));

    public static Interval Point(double value) => new(value, value);
}

public enum RawCatchObjectKind
{
    Circle,
    Slider,
    Spinner
}

public enum SliderCurveKind
{
    Linear,
    Bezier,
    Perfect,
    Catmull
}

public enum CatchObjectKind
{
    Fruit,
    Droplet,
    TinyDroplet,
    Banana
}

[Flags]
public enum CatchGameplayModifiers
{
    None = 0,
    Easy = 1 << 0,
    HardRock = 1 << 1,
    DoubleTime = 1 << 2,
    HalfTime = 1 << 3
}

public sealed class CatchTimingPoint
{
    public required double Time { get; init; }
    public required double BeatLength { get; init; }
    public required bool Uninherited { get; init; }
    public required int SourceLine { get; init; }
}

public sealed class CatchSliderDefinition
{
    public required SliderCurveKind CurveKind { get; init; }
    public required IReadOnlyList<Point2> ControlPoints { get; init; }
    public required int RepeatCount { get; init; }
    public required double PixelLength { get; init; }
}

public sealed class RawCatchHitObject
{
    public required RawCatchObjectKind Kind { get; init; }
    public required Point2 Position { get; init; }
    public required int StartTime { get; init; }
    public required int EndTime { get; init; }
    public required int RawType { get; init; }
    public required int HitSound { get; init; }
    public required int SourceLine { get; init; }
    public CatchSliderDefinition? Slider { get; init; }
}

public sealed class CatchBeatmapDocument
{
    public required string Path { get; init; }
    public required int FormatVersion { get; init; }
    public required int Mode { get; init; }
    public required double CircleSize { get; init; }
    public required double OverallDifficulty { get; init; }
    public required double SliderMultiplier { get; init; }
    public required double SliderTickRate { get; init; }
    public required string Artist { get; init; }
    public required string Title { get; init; }
    public required string Creator { get; init; }
    public required string Version { get; init; }
    public required IReadOnlyList<CatchTimingPoint> TimingPoints { get; init; }
    public required IReadOnlyList<RawCatchHitObject> HitObjects { get; init; }

    public string DisplayName => $"{Artist} - {Title} ({Creator}) [{Version}]";
}

public sealed class ConvertedCatchObject
{
    public required int Id { get; init; }
    public required CatchObjectKind Kind { get; init; }
    public required int Time { get; init; }
    public required double X { get; init; }
    public required int SourceLine { get; init; }
    public required int SourceObjectIndex { get; init; }
    public int? HyperDashTargetId { get; set; }
    public double HyperDashOffset { get; set; }

    public bool ParticipatesInHyperDash =>
        Kind is CatchObjectKind.Fruit or CatchObjectKind.Droplet;
}

public sealed class CatchConversionResult
{
    public required CatchBeatmapDocument Beatmap { get; init; }
    public required IReadOnlyList<ConvertedCatchObject> Objects { get; init; }
    public required double CatcherWidth { get; init; }
    public required double CollisionRadius { get; init; }
}

public sealed class CatchConstraint
{
    public required int Index { get; init; }
    public required int Time { get; init; }
    public required Interval ObjectWindow { get; init; }
    public required double PreferredX { get; init; }
    public required IReadOnlyList<int> ObjectIds { get; init; }
    public required bool IsSyntheticStart { get; init; }
    public int? ForcedHyperTargetObjectId { get; set; }
}

public sealed class CatchWaypoint
{
    public required int Time { get; init; }
    public required double X { get; set; }
    public required Interval ViableWindow { get; init; }
    public required Interval ObjectWindow { get; init; }
    public required bool IsSyntheticStart { get; init; }
    public required bool ArrivedByHyperDash { get; init; }
    public required IReadOnlyList<int> ObjectIds { get; init; }
}

public enum CatchInputState
{
    Idle,
    WalkLeft,
    WalkRight,
    DashLeft,
    DashRight,
    HyperDashLeft,
    HyperDashRight
}

public sealed class CatchControlPhase
{
    public required double StartTime { get; init; }
    public required double EndTime { get; init; }
    public required double StartX { get; init; }
    public required double EndX { get; init; }
    public required CatchInputState Input { get; init; }
    public required double Speed { get; init; }
}

public sealed class CatchPlanOptions
{
    public double SafetyMargin { get; init; } = 1.0;
    public bool IncludeTinyDropletsAsHardConstraints { get; init; } = true;
    public double CentreWeight { get; init; } = 0.16;
    public double SmoothnessWeight { get; init; } = 1.0;
    public double PlayfieldCentreWeight { get; init; } = 0.015;
    public int SmoothingPasses { get; init; } = 48;
}

public sealed class CatchPlanAudit
{
    public required int Fruits { get; init; }
    public required int Droplets { get; init; }
    public required int TinyDroplets { get; init; }
    public required int Bananas { get; init; }
    public required int PredictedFruitMisses { get; init; }
    public required int PredictedDropletMisses { get; init; }
    public required int PredictedTinyDropletMisses { get; init; }
    public required int PredictedBananasCaught { get; init; }
    public required double DashMilliseconds { get; init; }
    public required double HyperDashMilliseconds { get; init; }
}

public sealed class CatchPlan
{
    public required CatchConversionResult Conversion { get; init; }
    public required CatchPlanOptions Options { get; init; }
    public required IReadOnlyList<CatchConstraint> Constraints { get; init; }
    public required IReadOnlyList<CatchWaypoint> Waypoints { get; init; }
    public required IReadOnlyList<CatchControlPhase> Controls { get; init; }
    public required CatchPlanAudit Audit { get; init; }
}
