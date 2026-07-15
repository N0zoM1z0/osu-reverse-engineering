namespace OsuReverseEngineering.Catch;

internal static class SelfTest
{
    public static void Run()
    {
        VerifyFastRandom();
        VerifySliderPaths();
        VerifyParserConverterAndPlanner();
        VerifyHyperDashReachability();
        Console.WriteLine("CATCH SELF-TEST: PASS");
    }

    private static void VerifyFastRandom()
    {
        var first = new LegacyFastRandom(1337);
        var second = new LegacyFastRandom(1337);
        var sequenceA = Enumerable.Range(0, 32).Select(_ => first.Next(-20, 20)).ToArray();
        var sequenceB = Enumerable.Range(0, 32).Select(_ => second.Next(-20, 20)).ToArray();
        Assert(sequenceA.SequenceEqual(sequenceB), "stable random determinism");
        Assert(sequenceA.Distinct().Count() > 12, "stable random range");
        Assert(sequenceA.All(value => value >= -20 && value < 20), "stable random bounds");
    }

    private static void VerifySliderPaths()
    {
        var linear = new SliderPath(
            SliderCurveKind.Linear,
            new[] { new Point2(0, 0), new Point2(100, 0) },
            100);
        Assert(Near(linear.PositionAtProgress(0.5).X, 50, 1e-6), "linear midpoint");

        var bezier = new SliderPath(
            SliderCurveKind.Bezier,
            new[] { new Point2(0, 0), new Point2(50, 100), new Point2(100, 0) },
            147.8);
        Assert(bezier.Points.Count > 4, "adaptive Bezier subdivision");
        Assert(Near(bezier.Points[0].X, 0, 1e-6), "Bezier start");

        var perfect = new SliderPath(
            SliderCurveKind.Perfect,
            new[] { new Point2(0, 0), new Point2(50, 50), new Point2(100, 0) },
            Math.PI * 50);
        var perfectMiddle = perfect.PositionAtProgress(0.5);
        Assert(Near(perfectMiddle.X, 50, 1.5) && Near(perfectMiddle.Y, 50, 1.5), "perfect-curve midpoint");

        var catmull = new SliderPath(
            SliderCurveKind.Catmull,
            new[] { new Point2(0, 0), new Point2(50, 100), new Point2(100, 0) },
            1000);
        Assert(catmull.Points.Count >= 100, "Catmull uses 50 subdivisions per span");
    }

    private static void VerifyParserConverterAndPlanner()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "minimal-catch.osu");
        var beatmap = BeatmapParser.Parse(path);
        Assert(beatmap.Mode == 2, "Mode");
        Assert(beatmap.HitObjects.Count == 7, "raw object count");
        Assert(beatmap.HitObjects.Count(hitObject => hitObject.Kind == RawCatchObjectKind.Slider) == 4, "slider count");
        Assert(beatmap.HitObjects
            .Where(hitObject => hitObject.Slider is not null)
            .Select(hitObject => hitObject.Slider!.CurveKind)
            .SequenceEqual(new[]
            {
                SliderCurveKind.Linear,
                SliderCurveKind.Bezier,
                SliderCurveKind.Perfect,
                SliderCurveKind.Catmull
            }), "curve parsing");

        var conversion = CatchObjectConverter.Convert(beatmap);
        Assert(Near(conversion.CatcherWidth, 121.695, 0.001), "CS4 catcher width");
        Assert(conversion.Objects.Count(hitObject => hitObject.Kind == CatchObjectKind.Fruit) >= 10, "slider fruit conversion");
        Assert(conversion.Objects.Any(hitObject => hitObject.Kind == CatchObjectKind.Droplet), "slider droplets");
        Assert(conversion.Objects.Any(hitObject => hitObject.Kind == CatchObjectKind.TinyDroplet), "slider tiny droplets");
        Assert(conversion.Objects.Any(hitObject => hitObject.Kind == CatchObjectKind.Banana), "spinner bananas");

        var plan = ViabilityPlanner.Build(conversion);
        Assert(plan.Audit.PredictedFruitMisses == 0, "planned fruit misses");
        Assert(plan.Audit.PredictedDropletMisses == 0, "planned droplet misses");
        Assert(plan.Controls.Count > 0, "control generation");
        Assert(plan.Waypoints.All(waypoint => waypoint.ViableWindow.Contains(waypoint.X)), "waypoints remain viable");
    }

    private static void VerifyHyperDashReachability()
    {
        var beatmap = EmptyDocument();
        var source = new ConvertedCatchObject
        {
            Id = 0,
            Kind = CatchObjectKind.Fruit,
            Time = 1000,
            X = 0,
            SourceLine = 1,
            SourceObjectIndex = 0,
            HyperDashTargetId = 2
        };
        var tiny = new ConvertedCatchObject
        {
            Id = 1,
            Kind = CatchObjectKind.TinyDroplet,
            Time = 1050,
            X = 300,
            SourceLine = 2,
            SourceObjectIndex = 1
        };
        var target = new ConvertedCatchObject
        {
            Id = 2,
            Kind = CatchObjectKind.Fruit,
            Time = 1100,
            X = 512,
            SourceLine = 3,
            SourceObjectIndex = 2
        };
        var conversion = new CatchConversionResult
        {
            Beatmap = beatmap,
            Objects = new[] { source, tiny, target },
            CatcherWidth = 100,
            CollisionRadius = 40
        };

        var plan = ViabilityPlanner.Build(conversion);
        Assert(plan.Waypoints.Count == 4, "hyperdash intermediate waypoint count");
        Assert(plan.Waypoints[2].ArrivedByHyperDash, "intermediate tiny belongs to hyperdash segment");
        Assert(plan.Waypoints[^1].ArrivedByHyperDash, "hyperdash relation");
        Assert(Near(plan.Waypoints[^1].X, 512, 1e-6), "hyperdash exact target");
        Assert(Math.Abs(ViabilityPlanner.PositionAt(plan.Controls, tiny.Time) - tiny.X) < conversion.CollisionRadius,
            "hyperdash trajectory catches intermediate tiny");
        Assert(plan.Controls.Count(phase => phase.Input == CatchInputState.HyperDashRight) == 1,
            "hyperdash segment has one continuous control");

        source.HyperDashTargetId = null;
        var failed = false;
        try
        {
            _ = ViabilityPlanner.Build(conversion);
        }
        catch (InvalidOperationException)
        {
            failed = true;
        }
        Assert(failed, "impossible non-hyper movement is rejected");
    }

    private static CatchBeatmapDocument EmptyDocument() => new()
    {
        Path = "synthetic.osu",
        FormatVersion = 14,
        Mode = 2,
        CircleSize = 5,
        OverallDifficulty = 5,
        SliderMultiplier = 1.4,
        SliderTickRate = 1,
        Artist = "Synthetic",
        Title = "Hyper",
        Creator = "SelfTest",
        Version = "Unit",
        TimingPoints = Array.Empty<CatchTimingPoint>(),
        HitObjects = Array.Empty<RawCatchHitObject>()
    };

    private static bool Near(double actual, double expected, double tolerance) =>
        Math.Abs(actual - expected) <= tolerance;

    private static void Assert(bool condition, string label)
    {
        if (!condition)
            throw new InvalidOperationException($"Self-test failed: {label}.");
    }
}
