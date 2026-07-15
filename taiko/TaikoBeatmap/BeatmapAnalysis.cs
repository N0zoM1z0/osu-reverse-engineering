namespace OsuReverseEngineering.Taiko;

public sealed class BeatmapAnalysis
{
    public required string Path { get; init; }
    public required string DisplayName { get; init; }
    public required int FormatVersion { get; init; }
    public required double OverallDifficulty { get; init; }
    public required int TimingPointCount { get; init; }
    public required int InheritedTimingPointCount { get; init; }
    public required int ObjectCount { get; init; }
    public required int CircleCount { get; init; }
    public required int DonCount { get; init; }
    public required int KatCount { get; init; }
    public required int StrongCount { get; init; }
    public required int DrumRollCount { get; init; }
    public required int SpinnerCount { get; init; }
    public required int FirstObjectTime { get; init; }
    public required double LastObjectTime { get; init; }
    public required int MinimumCircleGap { get; init; }
    public required int PeakCirclesPerSecond { get; init; }

    public static BeatmapAnalysis From(TaikoBeatmapDocument beatmap)
    {
        ArgumentNullException.ThrowIfNull(beatmap);
        var circles = beatmap.HitObjects
            .Where(hitObject => hitObject.Kind == TaikoObjectKind.Circle)
            .ToList();
        var displayName = string.Join(
            " - ",
            new[] { beatmap.Artist, beatmap.Title }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
        if (!string.IsNullOrWhiteSpace(beatmap.Version))
            displayName += $" [{beatmap.Version}]";
        if (string.IsNullOrWhiteSpace(displayName))
            displayName = System.IO.Path.GetFileNameWithoutExtension(beatmap.Path);

        var minimumGap = 0;
        if (circles.Count > 1)
        {
            minimumGap = int.MaxValue;
            for (var index = 1; index < circles.Count; index++)
                minimumGap = Math.Min(minimumGap, circles[index].StartTime - circles[index - 1].StartTime);
        }

        var peak = 0;
        var left = 0;
        for (var right = 0; right < circles.Count; right++)
        {
            while (circles[right].StartTime - circles[left].StartTime >= 1000)
                left++;
            peak = Math.Max(peak, right - left + 1);
        }

        return new BeatmapAnalysis
        {
            Path = beatmap.Path,
            DisplayName = displayName,
            FormatVersion = beatmap.FormatVersion,
            OverallDifficulty = beatmap.OverallDifficulty,
            TimingPointCount = beatmap.TimingPoints.Count,
            InheritedTimingPointCount = beatmap.TimingPoints.Count(point => !point.Uninherited),
            ObjectCount = beatmap.HitObjects.Count,
            CircleCount = circles.Count,
            DonCount = circles.Count(hitObject => hitObject.Colour == TaikoColour.Don),
            KatCount = circles.Count(hitObject => hitObject.Colour == TaikoColour.Kat),
            StrongCount = circles.Count(hitObject => hitObject.IsStrong),
            DrumRollCount = beatmap.HitObjects.Count(hitObject => hitObject.Kind == TaikoObjectKind.DrumRoll),
            SpinnerCount = beatmap.HitObjects.Count(hitObject => hitObject.Kind == TaikoObjectKind.Spinner),
            FirstObjectTime = beatmap.HitObjects.Min(hitObject => hitObject.StartTime),
            LastObjectTime = beatmap.HitObjects.Max(hitObject => hitObject.EndTime),
            MinimumCircleGap = minimumGap,
            PeakCirclesPerSecond = peak
        };
    }
}
