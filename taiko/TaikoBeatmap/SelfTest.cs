namespace OsuReverseEngineering.Taiko;

internal static class SelfTest
{
    public static void Run()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "minimal-taiko.osu");
        var beatmap = BeatmapParser.Parse(path);
        Assert(beatmap.Mode == 1, "Mode");
        Assert(beatmap.HitObjects.Count == 6, "object count");

        var objects = beatmap.HitObjects;
        AssertCircle(objects[0], TaikoColour.Don, false, 1000);
        AssertCircle(objects[1], TaikoColour.Kat, false, 1500);
        AssertCircle(objects[2], TaikoColour.Don, true, 2000);
        AssertCircle(objects[3], TaikoColour.Kat, true, 2250);
        Assert(objects[4].Kind == TaikoObjectKind.DrumRoll, "drumroll classification");
        Assert(Math.Abs(objects[4].EndTime - 3200) < 0.001, "native Taiko drumroll duration");
        Assert(objects[5].Kind == TaikoObjectKind.Spinner, "spinner classification");
        Assert(Math.Abs(objects[5].EndTime - 4000) < 0.001, "spinner end time");

        var plan = PlayerPlanBuilder.Build(
            beatmap,
            tapMilliseconds: 8,
            drumRollIntervalMilliseconds: 100,
            spinnerIntervalMilliseconds: 100);
        var required = plan.Strikes.Where(strike => strike.RequiredForCombo).ToList();
        Assert(required.Count == 4, "required strike count");
        AssertKeys(required[0], TaikoKey.InnerLeft);
        AssertKeys(required[1], TaikoKey.OuterRight);
        AssertKeys(required[2], TaikoKey.InnerLeft, TaikoKey.InnerRight);
        AssertKeys(required[3], TaikoKey.OuterLeft, TaikoKey.OuterRight);

        var drumRoll = plan.Strikes
            .Where(strike => strike.SourceKind == TaikoObjectKind.DrumRoll)
            .Take(4)
            .ToList();
        AssertKeys(drumRoll[0], TaikoKey.InnerLeft);
        AssertKeys(drumRoll[1], TaikoKey.InnerRight);
        AssertKeys(drumRoll[2], TaikoKey.InnerLeft);
        AssertKeys(drumRoll[3], TaikoKey.InnerRight);

        var spinner = plan.Strikes
            .Where(strike => strike.SourceKind == TaikoObjectKind.Spinner)
            .Take(4)
            .ToList();
        AssertKeys(spinner[0], TaikoKey.InnerLeft);
        AssertKeys(spinner[1], TaikoKey.OuterLeft);
        AssertKeys(spinner[2], TaikoKey.InnerRight);
        AssertKeys(spinner[3], TaikoKey.OuterRight);
        Assert(plan.Transitions.Count > plan.Strikes.Count, "transition generation");

        var nativePlan = PlayerPlanBuilder.Build(beatmap);
        var nativeRoll = nativePlan.Strikes
            .Where(strike => strike.SourceKind == TaikoObjectKind.DrumRoll)
            .Take(5)
            .ToList();
        Assert(nativeRoll.Select(strike => strike.Time).SequenceEqual(new[] { 2500, 2562, 2625, 2687, 2750 }),
            "native drumroll cadence");
        var nativeSpinner = nativePlan.Strikes
            .Where(strike => strike.SourceKind == TaikoObjectKind.Spinner)
            .Select(strike => strike.Time)
            .ToList();
        Assert(nativeSpinner.SequenceEqual(new[] { 3250, 3400, 3550, 3700, 3850 }),
            "native spinner cadence");
        Assert(PlayerPlanBuilder.CalculateNativeSpinnerRequiredHits(750, 5) == 4,
            "native spinner required hits");
        VerifyLegacyDrumRollCadence();
        VerifyGlobalHandAfterOddDrumRoll();

        Console.WriteLine("TAIKO SELF-TEST: PASS");
        Console.WriteLine($"objects={beatmap.HitObjects.Count}, strikes={plan.Strikes.Count}, transitions={plan.Transitions.Count}");
    }

    private static void AssertCircle(
        TaikoHitObject hitObject,
        TaikoColour colour,
        bool strong,
        int time)
    {
        Assert(hitObject.Kind == TaikoObjectKind.Circle, $"circle at {time}");
        Assert(hitObject.Colour == colour, $"colour at {time}");
        Assert(hitObject.IsStrong == strong, $"strength at {time}");
        Assert(hitObject.StartTime == time, $"time at {time}");
    }

    private static void AssertKeys(TaikoStrike strike, params TaikoKey[] expected)
    {
        Assert(strike.Keys.SequenceEqual(expected),
            $"keys at {strike.Time}: expected {string.Join('+', expected)}, got {string.Join('+', strike.Keys)}");
    }

    private static void VerifyLegacyDrumRollCadence()
    {
        var roll = new TaikoHitObject
        {
            Kind = TaikoObjectKind.DrumRoll,
            StartTime = 1000,
            EndTime = 2000,
            RawType = 2,
            HitSound = 0,
            SourceLine = 1,
            BeatLength = 600,
            SliderVelocityMultiplier = 2
        };
        var legacy = new TaikoBeatmapDocument
        {
            Path = "legacy-v7.osu",
            FormatVersion = 7,
            Mode = 1,
            OverallDifficulty = 5,
            SliderMultiplier = 1.4,
            SliderTickRate = 1.5,
            Artist = "",
            Title = "",
            Creator = "",
            Version = "",
            TimingPoints = Array.Empty<TaikoTimingPoint>(),
            HitObjects = new[] { roll }
        };
        Assert(Math.Abs(PlayerPlanBuilder.ResolveNativeDrumRollInterval(legacy, roll) - 75.0) < 0.0001,
            "legacy v7 cadence ignores modern special tick-rate subdivision");
    }

    private static void VerifyGlobalHandAfterOddDrumRoll()
    {
        var roll = new TaikoHitObject
        {
            Kind = TaikoObjectKind.DrumRoll,
            StartTime = 1000,
            EndTime = 1040,
            RawType = 2,
            HitSound = 0,
            SourceLine = 1,
            BeatLength = 500,
            SliderVelocityMultiplier = 1
        };
        var circle = new TaikoHitObject
        {
            Kind = TaikoObjectKind.Circle,
            StartTime = 1100,
            EndTime = 1100,
            RawType = 1,
            HitSound = 0,
            SourceLine = 2,
            Colour = TaikoColour.Don
        };
        var document = new TaikoBeatmapDocument
        {
            Path = "odd-roll-hand.osu",
            FormatVersion = 14,
            Mode = 1,
            OverallDifficulty = 5,
            SliderMultiplier = 1.4,
            SliderTickRate = 1,
            Artist = "",
            Title = "",
            Creator = "",
            Version = "",
            TimingPoints = Array.Empty<TaikoTimingPoint>(),
            HitObjects = new[] { roll, circle }
        };
        var plan = PlayerPlanBuilder.Build(document);
        var required = plan.Strikes.Single(strike => strike.RequiredForCombo);
        AssertKeys(required, TaikoKey.InnerLeft);
    }

    private static void Assert(bool condition, string label)
    {
        if (!condition)
            throw new InvalidOperationException($"Self-test failed: {label}.");
    }
}
