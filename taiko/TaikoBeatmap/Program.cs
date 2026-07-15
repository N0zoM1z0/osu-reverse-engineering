using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OsuReverseEngineering.Taiko;

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

            switch (args[0].ToLowerInvariant())
            {
                case "self-test":
                    SelfTest.Run();
                    return 0;

                case "analyze":
                    return Analyze(args);

                case "plan":
                    return Plan(args);

                case "corpus":
                    return Corpus(args);

                default:
                    return Usage();
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static int Analyze(string[] args)
    {
        if (args.Length < 2)
            return Usage();
        var analysis = BeatmapAnalysis.From(BeatmapParser.Parse(args[1]));
        if (args.Contains("--json", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine(JsonSerializer.Serialize(analysis, JsonOptions));
            return 0;
        }

        Console.WriteLine(analysis.DisplayName);
        Console.WriteLine($"format=v{analysis.FormatVersion}, mode=1, OD={Format(analysis.OverallDifficulty)}");
        Console.WriteLine($"objects={analysis.ObjectCount}, circles={analysis.CircleCount}, don={analysis.DonCount}, kat={analysis.KatCount}, strong={analysis.StrongCount}");
        Console.WriteLine($"drumrolls={analysis.DrumRollCount}, spinners={analysis.SpinnerCount}, timing-points={analysis.TimingPointCount} ({analysis.InheritedTimingPointCount} inherited)");
        Console.WriteLine($"range={analysis.FirstObjectTime}..{Format(analysis.LastObjectTime)}ms, min-circle-gap={analysis.MinimumCircleGap}ms, peak-1s={analysis.PeakCirclesPerSecond}");
        return 0;
    }

    private static int Plan(string[] args)
    {
        if (args.Length < 2)
            return Usage();
        var tap = ReadOption(args, "--tap-ms", 8);
        var roll = ReadOption(args, "--roll-ms", 0);
        var spinner = ReadOption(args, "--spinner-ms", 0);
        var plan = PlayerPlanBuilder.Build(BeatmapParser.Parse(args[1]), tap, roll, spinner);
        if (args.Contains("--json", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine(JsonSerializer.Serialize(plan, JsonOptions));
            return 0;
        }

        Console.WriteLine($"strikes={plan.Strikes.Count}, required={plan.Strikes.Count(strike => strike.RequiredForCombo)}, transitions={plan.Transitions.Count}");
        Console.WriteLine($"tap={tap}ms, drumroll={DisplayInterval(roll)}, spinner={DisplayInterval(spinner)}");
        foreach (var transition in plan.Transitions.Take(20))
        {
            Console.WriteLine($"{transition.Time,8}  {transition.Key,-11}  {(transition.IsDown ? "down" : "up"),4}  {transition.SourceKind}");
        }
        if (plan.Transitions.Count > 20)
            Console.WriteLine($"... {plan.Transitions.Count - 20} more transitions (use --json for full output)");
        return 0;
    }

    private static int Corpus(string[] args)
    {
        if (args.Length != 2)
            return Usage();
        var root = Path.GetFullPath(args[1]);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException(root);

        var maps = 0;
        long circles = 0;
        long dons = 0;
        long kats = 0;
        long strong = 0;
        long rolls = 0;
        long spinners = 0;
        long strikes = 0;
        long transitions = 0;
        var failures = new List<string>();

        foreach (var path in Directory.EnumerateFiles(root, "*.osu", SearchOption.AllDirectories))
        {
            if (!BeatmapParser.TryReadMode(path, out var mode) || mode != 1)
                continue;
            try
            {
                var beatmap = BeatmapParser.Parse(path);
                var analysis = BeatmapAnalysis.From(beatmap);
                var plan = PlayerPlanBuilder.Build(beatmap);
                maps++;
                circles += analysis.CircleCount;
                dons += analysis.DonCount;
                kats += analysis.KatCount;
                strong += analysis.StrongCount;
                rolls += analysis.DrumRollCount;
                spinners += analysis.SpinnerCount;
                strikes += plan.Strikes.Count;
                transitions += plan.Transitions.Count;
            }
            catch (Exception exception)
            {
                failures.Add($"{Path.GetFileName(path)}: {exception.Message}");
            }
        }

        if (failures.Count > 0)
        {
            foreach (var failure in failures)
                Console.Error.WriteLine(failure);
            throw new InvalidOperationException($"Taiko corpus validation failed for {failures.Count} map(s).");
        }
        if (maps == 0)
            throw new InvalidOperationException("No native Mode 1 beatmaps were found.");

        Console.WriteLine("TAIKO CORPUS: PASS");
        Console.WriteLine($"maps={maps}, circles={circles}, don={dons}, kat={kats}, strong={strong}, drumrolls={rolls}, spinners={spinners}");
        Console.WriteLine($"player-plan-strikes={strikes}, transitions={transitions}");
        return 0;
    }

    private static int ReadOption(string[] args, string option, int fallback)
    {
        for (var index = 0; index < args.Length; index++)
        {
            if (!args[index].Equals(option, StringComparison.OrdinalIgnoreCase))
                continue;
            if (index + 1 >= args.Length
                || !int.TryParse(args[index + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                throw new ArgumentException($"{option} requires an integer value.");
            }
            return value;
        }
        return fallback;
    }

    private static string Format(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string DisplayInterval(int value) => value == 0 ? "native" : $"{value}ms";

    private static int Usage()
    {
        Console.Error.WriteLine("usage:");
        Console.Error.WriteLine("  TaikoBeatmap self-test");
        Console.Error.WriteLine("  TaikoBeatmap analyze <map.osu> [--json]");
        Console.Error.WriteLine("  TaikoBeatmap plan <map.osu> [--tap-ms N] [--roll-ms N|0] [--spinner-ms N|0] [--json]");
        Console.Error.WriteLine("  TaikoBeatmap corpus <Songs-directory>");
        return 2;
    }
}
