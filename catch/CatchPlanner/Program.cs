using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OsuReverseEngineering.Catch;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0)
                return Usage();

            return args[0].ToLowerInvariant() switch
            {
                "self-test" => SelfTestCommand(),
                "analyze" => Analyze(args),
                "dump" => Dump(args),
                "plan" => Plan(args),
                "corpus" => Corpus(args),
                _ => Usage()
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static int SelfTestCommand()
    {
        SelfTest.Run();
        return 0;
    }

    private static int Analyze(string[] args)
    {
        if (args.Length < 2)
            return Usage();
        var beatmap = BeatmapParser.Parse(args[1]);
        var conversion = CatchObjectConverter.Convert(beatmap);
        var rawGroups = beatmap.HitObjects.GroupBy(hitObject => hitObject.Kind).ToDictionary(group => group.Key, group => group.Count());
        var convertedGroups = conversion.Objects.GroupBy(hitObject => hitObject.Kind).ToDictionary(group => group.Key, group => group.Count());
        var curves = beatmap.HitObjects
            .Where(hitObject => hitObject.Slider is not null)
            .GroupBy(hitObject => hitObject.Slider!.CurveKind)
            .ToDictionary(group => group.Key, group => group.Count());

        Console.WriteLine(beatmap.DisplayName);
        Console.WriteLine($"format=v{beatmap.FormatVersion}, mode=2, CS={F(beatmap.CircleSize)}, OD={F(beatmap.OverallDifficulty)}");
        Console.WriteLine($"raw: circles={Count(rawGroups, RawCatchObjectKind.Circle)}, sliders={Count(rawGroups, RawCatchObjectKind.Slider)}, spinners={Count(rawGroups, RawCatchObjectKind.Spinner)}");
        Console.WriteLine($"curves: L={Count(curves, SliderCurveKind.Linear)}, B={Count(curves, SliderCurveKind.Bezier)}, P={Count(curves, SliderCurveKind.Perfect)}, C={Count(curves, SliderCurveKind.Catmull)}");
        Console.WriteLine($"converted: fruits={Count(convertedGroups, CatchObjectKind.Fruit)}, droplets={Count(convertedGroups, CatchObjectKind.Droplet)}, tiny={Count(convertedGroups, CatchObjectKind.TinyDroplet)}, bananas={Count(convertedGroups, CatchObjectKind.Banana)}");
        Console.WriteLine($"catcher-width={F(conversion.CatcherWidth)}px, collision-radius={F(conversion.CollisionRadius)}px, hyperdash-links={conversion.Objects.Count(hitObject => hitObject.HyperDashTargetId is not null)}");
        return 0;
    }

    private static int Plan(string[] args)
    {
        if (args.Length < 2)
            return Usage();
        var options = ReadPlanOptions(args);
        var plan = ViabilityPlanner.Build(
            CatchObjectConverter.Convert(BeatmapParser.Parse(args[1])),
            options);
        var svg = ReadStringOption(args, "--svg");
        if (svg is not null)
        {
            SvgRenderer.Write(plan, svg);
            Console.WriteLine($"svg={Path.GetFullPath(svg)}");
        }

        if (args.Contains("--json", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine(JsonSerializer.Serialize(plan, JsonOptions));
            return 0;
        }

        PrintPlanSummary(plan);
        return 0;
    }

    private static int Dump(string[] args)
    {
        if (args.Length < 2)
            return Usage();
        var from = ReadIntOption(args, "--from", int.MinValue);
        var to = ReadIntOption(args, "--to", int.MaxValue);
        var conversion = CatchObjectConverter.Convert(BeatmapParser.Parse(args[1]));
        var byId = conversion.Objects.ToDictionary(hitObject => hitObject.Id);
        foreach (var hitObject in conversion.Objects.Where(hitObject => hitObject.Time >= from && hitObject.Time <= to))
        {
            var target = hitObject.HyperDashTargetId is { } targetId
                ? $" -> {targetId}@{byId[targetId].Time}:{F(byId[targetId].X)}"
                : string.Empty;
            Console.WriteLine($"{hitObject.Id,5} {hitObject.Time,8} {hitObject.X,8:0.###} {hitObject.Kind,-11} line={hitObject.SourceLine}{target}");
        }
        return 0;
    }

    private static int Corpus(string[] args)
    {
        if (args.Length < 2)
            return Usage();
        var root = Path.GetFullPath(args[1]);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException(root);
        var options = ReadPlanOptions(args);

        var maps = 0;
        long rawCircles = 0;
        long rawSliders = 0;
        long rawSpinners = 0;
        long fruits = 0;
        long droplets = 0;
        long tiny = 0;
        long hyper = 0;
        long tinyMisses = 0;
        var failures = new List<string>();

        foreach (var path in Directory.EnumerateFiles(root, "*.osu", SearchOption.AllDirectories))
        {
            if (!BeatmapParser.TryReadMode(path, out var mode) || mode != 2)
                continue;
            try
            {
                var beatmap = BeatmapParser.Parse(path);
                var conversion = CatchObjectConverter.Convert(beatmap);
                var plan = ViabilityPlanner.Build(conversion, options);
                maps++;
                rawCircles += beatmap.HitObjects.Count(hitObject => hitObject.Kind == RawCatchObjectKind.Circle);
                rawSliders += beatmap.HitObjects.Count(hitObject => hitObject.Kind == RawCatchObjectKind.Slider);
                rawSpinners += beatmap.HitObjects.Count(hitObject => hitObject.Kind == RawCatchObjectKind.Spinner);
                fruits += plan.Audit.Fruits;
                droplets += plan.Audit.Droplets;
                tiny += plan.Audit.TinyDroplets;
                hyper += conversion.Objects.Count(hitObject => hitObject.HyperDashTargetId is not null);
                tinyMisses += plan.Audit.PredictedTinyDropletMisses;
                Console.WriteLine($"PASS  {Path.GetFileName(path)}  F={plan.Audit.Fruits} D={plan.Audit.Droplets} T={plan.Audit.TinyDroplets} H={conversion.Objects.Count(hitObject => hitObject.HyperDashTargetId is not null)} tiny-miss={plan.Audit.PredictedTinyDropletMisses}");
            }
            catch (Exception exception)
            {
                failures.Add($"{Path.GetFileName(path)}: {exception.Message}");
            }
        }

        foreach (var failure in failures)
            Console.Error.WriteLine($"FAIL  {failure}");
        if (failures.Count > 0)
            throw new InvalidOperationException($"Catch corpus validation failed for {failures.Count} map(s).");
        if (maps == 0)
            throw new InvalidOperationException("No native Mode 2 beatmaps were found.");

        Console.WriteLine("CATCH CORPUS: PASS");
        Console.WriteLine($"maps={maps}, raw-circles={rawCircles}, raw-sliders={rawSliders}, raw-spinners={rawSpinners}");
        Console.WriteLine($"fruits={fruits}, droplets={droplets}, tiny={tiny}, hyperdash-links={hyper}, predicted-tiny-misses={tinyMisses}");
        return 0;
    }

    private static void PrintPlanSummary(CatchPlan plan)
    {
        var audit = plan.Audit;
        Console.WriteLine(plan.Conversion.Beatmap.DisplayName);
        Console.WriteLine($"constraints={plan.Constraints.Count - 1}, waypoints={plan.Waypoints.Count}, control-phases={plan.Controls.Count}");
        Console.WriteLine($"objects: fruit={audit.Fruits}, droplet={audit.Droplets}, tiny={audit.TinyDroplets}, banana={audit.Bananas}");
        Console.WriteLine($"predicted misses: fruit={audit.PredictedFruitMisses}, droplet={audit.PredictedDropletMisses}, tiny={audit.PredictedTinyDropletMisses}; bananas-caught={audit.PredictedBananasCaught}");
        Console.WriteLine($"dash={F(audit.DashMilliseconds)}ms, hyperdash={F(audit.HyperDashMilliseconds)}ms, safety={F(plan.Options.SafetyMargin)}px");
    }

    private static CatchPlanOptions ReadPlanOptions(string[] args) => new()
    {
        SafetyMargin = ReadDoubleOption(args, "--safety", 1.0),
        IncludeTinyDropletsAsHardConstraints = !args.Contains("--tiny-soft", StringComparer.OrdinalIgnoreCase)
    };

    private static double ReadDoubleOption(string[] args, string option, double fallback)
    {
        var text = ReadStringOption(args, option);
        if (text is null)
            return fallback;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || !double.IsFinite(value))
        {
            throw new ArgumentException($"{option} requires a numeric value.");
        }
        return value;
    }

    private static int ReadIntOption(string[] args, string option, int fallback)
    {
        var text = ReadStringOption(args, option);
        if (text is null)
            return fallback;
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            throw new ArgumentException($"{option} requires an integer value.");
        return value;
    }

    private static string? ReadStringOption(string[] args, string option)
    {
        for (var index = 0; index < args.Length; index++)
        {
            if (!args[index].Equals(option, StringComparison.OrdinalIgnoreCase))
                continue;
            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"{option} requires a value.");
            return args[index + 1];
        }
        return null;
    }

    private static int Count<TKey>(IReadOnlyDictionary<TKey, int> counts, TKey key)
        where TKey : notnull => counts.TryGetValue(key, out var value) ? value : 0;

    private static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static int Usage()
    {
        Console.Error.WriteLine("usage:");
        Console.Error.WriteLine("  CatchPlanner self-test");
        Console.Error.WriteLine("  CatchPlanner analyze <map.osu>");
        Console.Error.WriteLine("  CatchPlanner dump <map.osu> [--from ms] [--to ms]");
        Console.Error.WriteLine("  CatchPlanner plan <map.osu> [--svg output.svg] [--safety px] [--tiny-soft] [--json]");
        Console.Error.WriteLine("  CatchPlanner corpus <Songs-directory> [--safety px] [--tiny-soft]");
        return 2;
    }
}
