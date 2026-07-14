namespace LocalManiaAuto;

internal sealed record LaneTransition(int Time, int Lane, bool IsDown, int SourceLine);

internal sealed record TransitionBatch(int Time, IReadOnlyList<LaneTransition> Transitions);

internal sealed record LiveTimeline(
    IReadOnlyList<TransitionBatch> Batches,
    IReadOnlyList<string> Warnings);

internal static class LiveTimelineBuilder
{
    public static LiveTimeline Build(ManiaBeatmap beatmap, int tapHoldMilliseconds)
    {
        if (tapHoldMilliseconds is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(tapHoldMilliseconds), "tap-ms must be between 1 and 100.");
        }

        var transitions = new List<LaneTransition>(beatmap.HitObjects.Count * 2);
        var warnings = new List<string>();

        for (int lane = 0; lane < beatmap.KeyCount; lane++)
        {
            ManiaHitObject[] laneObjects = beatmap.HitObjects
                .Where(o => o.Lane == lane)
                .OrderBy(static o => o.StartTime)
                .ThenBy(static o => o.EndTime)
                .ToArray();

            for (int index = 0; index < laneObjects.Length; index++)
            {
                ManiaHitObject hitObject = laneObjects[index];
                int nextStart = index + 1 < laneObjects.Length ? laneObjects[index + 1].StartTime : int.MaxValue;
                if (nextStart == hitObject.StartTime)
                {
                    throw new BeatmapFormatException(
                        $"lane {lane + 1} has two objects at {hitObject.StartTime}ms (including line {hitObject.SourceLine}); one physical key cannot express this chart." );
                }
                int releaseTime;

                if (hitObject.IsHold)
                {
                    releaseTime = hitObject.EndTime - 1;
                    if (nextStart <= releaseTime)
                    {
                        warnings.Add($"lane {lane + 1}: the LN at line {hitObject.SourceLine} overlaps the next object; release moved to {nextStart - 1}ms.");
                        releaseTime = nextStart - 1;
                    }
                }
                else
                {
                    releaseTime = hitObject.StartTime + tapHoldMilliseconds;
                    if (nextStart <= releaseTime)
                    {
                        releaseTime = nextStart - 1;
                    }
                }

                releaseTime = Math.Max(hitObject.StartTime + 1, releaseTime);
                transitions.Add(new LaneTransition(hitObject.StartTime, lane, IsDown: true, hitObject.SourceLine));
                transitions.Add(new LaneTransition(releaseTime, lane, IsDown: false, hitObject.SourceLine));
            }
        }

        TransitionBatch[] batches = transitions
            .GroupBy(static transition => transition.Time)
            .OrderBy(static group => group.Key)
            .Select(static group => new TransitionBatch(
                group.Key,
                group.OrderBy(static transition => transition.IsDown ? 1 : 0)
                    .ThenBy(static transition => transition.Lane)
                    .ToArray()))
            .ToArray();

        return new LiveTimeline(batches, warnings);
    }
}
