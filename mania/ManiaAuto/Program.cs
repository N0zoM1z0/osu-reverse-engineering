using System.Globalization;
using System.Text;

namespace LocalManiaAuto;

internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        try
        {
            if (args.Length == 0 || IsHelp(args[0]))
            {
                PrintHelp();
                return 0;
            }

            string command = args[0].ToLowerInvariant();
            string[] commandArgs = args[1..];
            return command switch
            {
                "inspect" => Inspect(commandArgs),
                "frames" => Frames(commandArgs),
                "events" => Events(commandArgs),
                "play" => Play(commandArgs),
                "self-test" => RunSelfTest(commandArgs),
                _ => throw new ArgumentException($"Unknown command '{args[0]}'. Run mania-auto --help for usage."),
            };
        }
        catch (Exception exception) when (exception is not StackOverflowException and not OutOfMemoryException)
        {
            Console.Error.WriteLine($"Error: {exception.Message}");
            return 2;
        }
    }

    private static int Inspect(string[] args)
    {
        ParsedArguments options = ParsedArguments.Parse(args, "limit");
        if (options.Help)
        {
            Console.WriteLine("Usage: mania-auto inspect <beatmap.osu> [--limit 12]");
            return 0;
        }

        ManiaBeatmap beatmap = BeatmapParser.Parse(options.RequireSinglePath());
        int limit = options.GetInt("limit", 12, minimum: 0, maximum: 1000);
        IReadOnlyList<ReplayFrame> frames = ReplayFrameBuilder.Build(beatmap);

        int tapCount = beatmap.HitObjects.Count(static hitObject => !hitObject.IsHold);
        int holdCount = beatmap.HitObjects.Count - tapCount;
        int maximumChord = beatmap.HitObjects.GroupBy(static hitObject => hitObject.StartTime).Max(static group => group.Count());

        Console.WriteLine($"File: {beatmap.Path}");
        Console.WriteLine($"Beatmap: {beatmap.Artist} - {beatmap.Title} [{beatmap.Difficulty}]");
        Console.WriteLine($"Format: v{beatmap.FormatVersion}, Mode {beatmap.Mode}, {beatmap.KeyCount}K");
        Console.WriteLine($"Objects: {beatmap.HitObjects.Count:N0} (tap {tapCount:N0}, LN {holdCount:N0}), maximum chord {maximumChord}");
        Console.WriteLine($"Time: {beatmap.FirstObjectTime}ms -> {beatmap.LastObjectTime}ms ({FormatDuration(beatmap.LastObjectTime - beatmap.FirstObjectTime)})");
        Console.WriteLine($"Native Auto model: {frames.Count:N0} replay frames");
        Console.WriteLine("Objects per lane: " + string.Join(", ",
            Enumerable.Range(0, beatmap.KeyCount).Select(lane =>
                $"{lane + 1}:{beatmap.HitObjects.Count(hitObject => hitObject.Lane == lane):N0}")));

        if (limit > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"First {Math.Min(limit, beatmap.HitObjects.Count)} objects:");
            Console.WriteLine("  start     end lane kind source");
            foreach (ManiaHitObject hitObject in beatmap.HitObjects.Take(limit))
            {
                Console.WriteLine(
                    $"  {hitObject.StartTime,7} {hitObject.EndTime,7} {hitObject.Lane + 1,4} {(hitObject.IsHold ? "LN" : "tap"),4} line {hitObject.SourceLine}");
            }

            Console.WriteLine();
            Console.WriteLine($"First {Math.Min(limit, frames.Count)} native Auto frames:");
            Console.WriteLine("  time_ms mask binary");
            foreach (ReplayFrame frame in frames.Take(limit))
            {
                Console.WriteLine($"  {frame.Time,7} {frame.KeyMask,4} {Mask(frame.KeyMask, beatmap.KeyCount)}");
            }
        }
        return 0;
    }

    private static int Frames(string[] args)
    {
        ParsedArguments options = ParsedArguments.Parse(args, "limit");
        if (options.Help)
        {
            Console.WriteLine("Usage: mania-auto frames <beatmap.osu> [--limit N]");
            return 0;
        }

        ManiaBeatmap beatmap = BeatmapParser.Parse(options.RequireSinglePath());
        IReadOnlyList<ReplayFrame> frames = ReplayFrameBuilder.Build(beatmap);
        int limit = options.GetInt("limit", frames.Count, minimum: 0, maximum: int.MaxValue);

        Console.WriteLine("time_ms,key_mask,key_mask_binary");
        foreach (ReplayFrame frame in frames.Take(limit))
        {
            Console.WriteLine($"{frame.Time},{frame.KeyMask},{Mask(frame.KeyMask, beatmap.KeyCount)}");
        }
        return 0;
    }

    private static int Events(string[] args)
    {
        ParsedArguments options = ParsedArguments.Parse(args, "tap-ms", "limit");
        if (options.Help)
        {
            Console.WriteLine("Usage: mania-auto events <beatmap.osu> [--tap-ms 8] [--limit N]");
            return 0;
        }

        ManiaBeatmap beatmap = BeatmapParser.Parse(options.RequireSinglePath());
        int tapMilliseconds = options.GetInt("tap-ms", 8, minimum: 1, maximum: 100);
        LiveTimeline timeline = LiveTimelineBuilder.Build(beatmap, tapMilliseconds);
        int transitionCount = timeline.Batches.Sum(static batch => batch.Transitions.Count);
        int limit = options.GetInt("limit", transitionCount, minimum: 0, maximum: int.MaxValue);

        WriteWarnings(timeline.Warnings);
        Console.WriteLine("time_ms,lane_1based,action,source_line");
        foreach (LaneTransition transition in timeline.Batches.SelectMany(static batch => batch.Transitions).Take(limit))
        {
            Console.WriteLine($"{transition.Time},{transition.Lane + 1},{(transition.IsDown ? "down" : "up")},{transition.SourceLine}");
        }
        return 0;
    }

    private static int Play(string[] args)
    {
        ParsedArguments options = ParsedArguments.Parse(args, "keys", "tap-ms", "anchor-ms", "rate", "offset-ms");
        if (options.Help)
        {
            Console.WriteLine("Usage: mania-auto play <beatmap.osu> [--keys D,F,J,K] [--tap-ms 8] [--anchor-ms first] [--rate 1.0] [--offset-ms 0]");
            return 0;
        }

        ManiaBeatmap beatmap = BeatmapParser.Parse(options.RequireSinglePath());
        int tapMilliseconds = options.GetInt("tap-ms", 8, minimum: 1, maximum: 100);
        double rate = options.GetDouble("rate", 1, minimum: 0.25, maximum: 4);
        double offset = options.GetDouble("offset-ms", 0, minimum: -5000, maximum: 5000);
        int anchor = ParseAnchor(options.Get("anchor-ms"), beatmap.FirstObjectTime);
        if (anchor > beatmap.FirstObjectTime)
        {
            throw new ArgumentException($"anchor-ms={anchor} is later than the first object at {beatmap.FirstObjectTime}ms; this prototype can only start at or before the first object.", "anchor-ms");
        }

        string? layout = options.Get("keys") ?? KeyBindings.GetDefaultLayout(beatmap.KeyCount);
        if (layout is null)
        {
            throw new ArgumentException($"{beatmap.KeyCount}K has no built-in key layout; provide --keys explicitly.", "keys");
        }
        IReadOnlyList<VirtualKeySpec> keys = KeyBindings.Parse(layout, beatmap.KeyCount);
        LiveTimeline timeline = LiveTimelineBuilder.Build(beatmap, tapMilliseconds);
        WriteWarnings(timeline.Warnings);

        Console.WriteLine($"Beatmap: {beatmap.Artist} - {beatmap.Title} [{beatmap.Difficulty}], {beatmap.KeyCount}K");
        Console.WriteLine("Lane -> key: " + string.Join(", ", keys.Select((key, lane) => $"{lane + 1}->{key.Name}")));
        Console.WriteLine($"Events: {timeline.Batches.Count:N0} batches; tap={tapMilliseconds}ms; rate={rate.ToString("0.###", CultureInfo.InvariantCulture)}x; offset={offset.ToString("0.###", CultureInfo.InvariantCulture)}ms");
        Console.WriteLine(anchor == beatmap.FirstObjectTime
            ? $"Anchor: first object at {anchor}ms; press F6 when that note reaches the judgement line."
            : $"Anchor: {anchor}ms; press F6 when gameplay reaches that time.");

        PlaybackResult result = WindowsPlayback.Run(timeline, keys, anchor, rate, offset);
        Console.WriteLine($"{result.Message} batches={result.BatchesSent:N0}, inputs={result.TransitionsInjected:N0}, max_late={result.MaximumLatenessMilliseconds:0.###}ms");
        return result.StopReason == PlaybackStopReason.Completed ? 0 : 3;
    }

    private static int RunSelfTest(string[] args)
    {
        ParsedArguments options = ParsedArguments.Parse(args);
        if (options.Help)
        {
            Console.WriteLine("Usage: mania-auto self-test");
            return 0;
        }
        options.RequireNoPositionals();
        SelfTest.Run();
        Console.WriteLine("self-test: PASS (native frames, live timeline, 4K key mapping)");
        return 0;
    }

    private static int ParseAnchor(string? text, int firstObjectTime)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Equals("first", StringComparison.OrdinalIgnoreCase))
        {
            return firstObjectTime;
        }
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) || value < 0)
        {
            throw new ArgumentException("anchor-ms must be a non-negative integer or 'first'.", "anchor-ms");
        }
        return value;
    }

    private static void WriteWarnings(IReadOnlyList<string> warnings)
    {
        foreach (string warning in warnings)
        {
            Console.Error.WriteLine($"Warning: {warning}");
        }
    }

    private static string Mask(int mask, int keyCount)
        => Convert.ToString(mask, 2).PadLeft(keyCount, '0');

    private static string FormatDuration(int milliseconds)
        => TimeSpan.FromMilliseconds(milliseconds).ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture);

    private static bool IsHelp(string value)
        => value is "-h" or "--help" or "help";

    private static void PrintHelp()
    {
        Console.WriteLine(
            """
            mania-auto - local osu!mania Auto model and Windows input prototype

            Usage:
              mania-auto inspect <beatmap.osu> [--limit 12]
              mania-auto frames  <beatmap.osu> [--limit N]
              mania-auto events  <beatmap.osu> [--tap-ms 8] [--limit N]
              mania-auto play    <beatmap.osu> [--keys D,F,J,K] [--tap-ms 8]
                                  [--anchor-ms first] [--rate 1.0] [--offset-ms 0]
              mania-auto self-test

            play behavior:
              Starts only while the foreground process is osu!; F7, Esc, Ctrl+C,
              or focus loss stops playback and releases every injected key.
            """);
    }

    private sealed class ParsedArguments
    {
        private readonly Dictionary<string, string> _values;
        private readonly List<string> _positionals;

        private ParsedArguments(Dictionary<string, string> values, List<string> positionals, bool help)
        {
            _values = values;
            _positionals = positionals;
            Help = help;
        }

        public bool Help { get; }

        public static ParsedArguments Parse(string[] args, params string[] allowedOptions)
        {
            var allowed = new HashSet<string>(allowedOptions, StringComparer.OrdinalIgnoreCase);
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var positionals = new List<string>();
            bool help = false;

            for (int index = 0; index < args.Length; index++)
            {
                string argument = args[index];
                if (IsHelp(argument))
                {
                    help = true;
                    continue;
                }
                if (!argument.StartsWith("--", StringComparison.Ordinal))
                {
                    positionals.Add(argument);
                    continue;
                }

                string optionText = argument[2..];
                int separator = optionText.IndexOf('=');
                string name = separator >= 0 ? optionText[..separator] : optionText;
                if (!allowed.Contains(name))
                {
                    throw new ArgumentException($"Unknown option --{name}.");
                }

                string value;
                if (separator >= 0)
                {
                    value = optionText[(separator + 1)..];
                }
                else
                {
                    if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        throw new ArgumentException($"Option --{name} requires a value.");
                    }
                    value = args[++index];
                }

                if (value.Length == 0 || !values.TryAdd(name, value))
                {
                    throw new ArgumentException(value.Length == 0 ? $"Option --{name} requires a value." : $"Option --{name} was provided more than once.");
                }
            }

            return new ParsedArguments(values, positionals, help);
        }

        public string RequireSinglePath()
        {
            if (_positionals.Count != 1)
            {
                throw new ArgumentException("This command requires exactly one .osu path.");
            }
            return _positionals[0];
        }

        public void RequireNoPositionals()
        {
            if (_positionals.Count != 0)
            {
                throw new ArgumentException("This command does not accept positional arguments.");
            }
        }

        public string? Get(string name)
            => _values.TryGetValue(name, out string? value) ? value : null;

        public int GetInt(string name, int defaultValue, int minimum, int maximum)
        {
            string? text = Get(name);
            if (text is null)
            {
                return defaultValue;
            }
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
                || value < minimum
                || value > maximum)
            {
                throw new ArgumentException($"--{name} must be between {minimum} and {maximum}.", name);
            }
            return value;
        }

        public double GetDouble(string name, double defaultValue, double minimum, double maximum)
        {
            string? text = Get(name);
            if (text is null)
            {
                return defaultValue;
            }
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                || !double.IsFinite(value)
                || value < minimum
                || value > maximum)
            {
                throw new ArgumentException($"--{name} must be between {minimum} and {maximum}.", name);
            }
            return value;
        }
    }
}
