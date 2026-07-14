namespace LocalManiaAuto;

internal static class SelfTest
{
    public static void Run()
    {
        var objects = new ManiaHitObject[]
        {
            new(64, 0, 100, 100, IsHold: false, Type: 1, SourceLine: 1),
            new(192, 1, 200, 200, IsHold: false, Type: 1, SourceLine: 2),
            new(320, 2, 200, 200, IsHold: false, Type: 1, SourceLine: 3),
            new(448, 3, 300, 500, IsHold: true, Type: 128, SourceLine: 4),
            new(64, 0, 600, 800, IsHold: true, Type: 128, SourceLine: 5),
            new(192, 1, 700, 700, IsHold: false, Type: 1, SourceLine: 6),
        };
        var beatmap = new ManiaBeatmap(
            "<self-test>",
            14,
            3,
            4,
            "Test",
            "Native Auto model",
            "4K",
            "none.mp3",
            objects);

        ReplayFrame[] expectedFrames =
        {
            new(0, 0),
            new(100, 1),
            new(101, 0),
            new(200, 6),
            new(201, 0),
            new(300, 8),
            new(499, 0),
            new(600, 1),
            new(700, 3),
            new(701, 1),
            new(799, 0),
        };

        IReadOnlyList<ReplayFrame> actualFrames = ReplayFrameBuilder.Build(beatmap);
        AssertSequence(expectedFrames, actualFrames, "native replay frames");

        LiveTimeline live = LiveTimelineBuilder.Build(beatmap, tapHoldMilliseconds: 8);
        LaneTransition[] expectedTransitions =
        {
            new(100, 0, true, 1),
            new(108, 0, false, 1),
            new(200, 1, true, 2),
            new(200, 2, true, 3),
            new(208, 1, false, 2),
            new(208, 2, false, 3),
            new(300, 3, true, 4),
            new(499, 3, false, 4),
            new(600, 0, true, 5),
            new(700, 1, true, 6),
            new(708, 1, false, 6),
            new(799, 0, false, 5),
        };
        LaneTransition[] actualTransitions = live.Batches.SelectMany(static batch => batch.Transitions).ToArray();
        AssertSequence(expectedTransitions, actualTransitions, "live transitions");

        IReadOnlyList<VirtualKeySpec> keys = KeyBindings.Parse("D,F,J,K", 4);
        Assert(keys.Select(static key => key.VirtualKey).SequenceEqual(new ushort[] { 0x44, 0x46, 0x4A, 0x4B }), "4K key parsing");
        Assert(live.Warnings.Count == 0, "unexpected live timeline warning");
    }

    private static void AssertSequence<T>(IReadOnlyList<T> expected, IReadOnlyList<T> actual, string label)
    {
        if (expected.Count != actual.Count)
        {
            throw new InvalidOperationException($"self-test {label}: expected {expected.Count} entries, got {actual.Count}.");
        }
        for (int index = 0; index < expected.Count; index++)
        {
            if (!EqualityComparer<T>.Default.Equals(expected[index], actual[index]))
            {
                throw new InvalidOperationException(
                    $"self-test {label}[{index}]: expected {expected[index]}, got {actual[index]}.");
            }
        }
    }

    private static void Assert(bool condition, string label)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"self-test failed: {label}.");
        }
    }
}
