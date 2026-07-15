namespace OsuReverseEngineering.Taiko;

public enum TaikoObjectKind
{
    Circle,
    DrumRoll,
    Spinner
}

public enum TaikoColour
{
    Don,
    Kat
}

public enum TaikoKey
{
    InnerLeft,
    InnerRight,
    OuterLeft,
    OuterRight
}

[Flags]
public enum TaikoGameplayModifiers
{
    None = 0,
    Easy = 1 << 0,
    HardRock = 1 << 1,
    DoubleTime = 1 << 2,
    HalfTime = 1 << 3
}

public sealed class TaikoTimingPoint
{
    public required double Time { get; init; }
    public required double BeatLength { get; init; }
    public required bool Uninherited { get; init; }
    public required int SourceLine { get; init; }
}

public sealed class TaikoHitObject
{
    public required TaikoObjectKind Kind { get; init; }
    public required int StartTime { get; init; }
    public required double EndTime { get; init; }
    public required int RawType { get; init; }
    public required int HitSound { get; init; }
    public required int SourceLine { get; init; }
    public TaikoColour Colour { get; init; }
    public bool IsStrong { get; init; }
    public int RepeatCount { get; init; }
    public double PixelLength { get; init; }
    public double BeatLength { get; init; }
    public double SliderVelocityMultiplier { get; init; } = 1;
}

public sealed class TaikoBeatmapDocument
{
    public required string Path { get; init; }
    public required int FormatVersion { get; init; }
    public required int Mode { get; init; }
    public required double OverallDifficulty { get; init; }
    public required double SliderMultiplier { get; init; }
    public required double SliderTickRate { get; init; }
    public required string Artist { get; init; }
    public required string Title { get; init; }
    public required string Creator { get; init; }
    public required string Version { get; init; }
    public required IReadOnlyList<TaikoTimingPoint> TimingPoints { get; init; }
    public required IReadOnlyList<TaikoHitObject> HitObjects { get; init; }
}

public sealed class TaikoStrike
{
    public required int Time { get; init; }
    public required IReadOnlyList<TaikoKey> Keys { get; init; }
    public required TaikoObjectKind SourceKind { get; init; }
    public required int SourceLine { get; init; }
    public required bool RequiredForCombo { get; init; }
}

public sealed class TaikoKeyTransition
{
    public required int Time { get; init; }
    public required TaikoKey Key { get; init; }
    public required bool IsDown { get; init; }
    public required TaikoObjectKind SourceKind { get; init; }
    public required int SourceLine { get; init; }
}

public sealed class TaikoPlayerPlan
{
    public required string Path { get; init; }
    public required IReadOnlyList<TaikoStrike> Strikes { get; init; }
    public required IReadOnlyList<TaikoKeyTransition> Transitions { get; init; }
}
