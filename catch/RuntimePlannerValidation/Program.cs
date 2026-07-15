using LocalCatchAgent.Plugin;
using OsuReverseEngineering.Catch;

if (args.Length != 1 || !Directory.Exists(args[0]))
{
    Console.Error.WriteLine("usage: RuntimePlannerValidation <Songs-directory>");
    return 2;
}

var maps = 0;
var builds = 0;
long constraints = 0;
long hyperLinks = 0;
foreach (var path in Directory.EnumerateFiles(Path.GetFullPath(args[0]), "*.osu", SearchOption.AllDirectories))
{
    if (!BeatmapParser.TryReadMode(path, out var mode) || mode != 2)
        continue;

    var conversion = CatchObjectConverter.Convert(BeatmapParser.Parse(path));
    var runtimeObjects = conversion.Objects.Select(hitObject => new RuntimeCatchObject
    {
        Id = hitObject.Id,
        Time = hitObject.Time,
        X = hitObject.X,
        Kind = ConvertKind(hitObject.Kind),
        HyperTargetId = hitObject.HyperDashTargetId ?? -1,
        Source = hitObject
    }).ToList();

    foreach (CatchPathStyle style in Enum.GetValues(typeof(CatchPathStyle)))
    {
        var options = Options(style);
        var first = BuildRobust(runtimeObjects, conversion.CatcherWidth, options, 1337, path);
        var second = BuildRobust(runtimeObjects, conversion.CatcherWidth, options, 1337, path);
        Verify(first, second);
        builds++;
        constraints += first.Constraints.Count - 1;
        hyperLinks += first.HyperDashCount;
    }
    maps++;
    var smoothPlan = BuildRobust(
        runtimeObjects,
        conversion.CatcherWidth,
        Options(CatchPathStyle.Smooth),
        1337,
        path);
    var clearance = MeasureClearance(smoothPlan);
    Console.WriteLine("PASS  " + Path.GetFileName(path)
        + "  objects=" + runtimeObjects.Count
        + "  styles=4  hyper=" + runtimeObjects.Count(hitObject => hitObject.HyperTargetId >= 0)
        + "  robust-safety=" + smoothPlan.SafetyMargin.ToString("0.##")
        + "  actual-min-clearance=" + clearance.Minimum.ToString("0.##")
        + "  under-4px=" + clearance.UnderFour + "/" + clearance.Count
        + "  under-8px=" + clearance.UnderEight + "/" + clearance.Count);
    if (clearance.Tightest.Length != 0)
        Console.WriteLine("      tightest: " + clearance.Tightest);
}

if (maps == 0)
    throw new InvalidOperationException("No native Mode 2 beatmaps were found.");
Console.WriteLine("RUNTIME CATCH CORPUS: PASS");
Console.WriteLine("maps=" + maps + ", style-builds=" + builds
    + ", aggregate-constraints=" + constraints + ", aggregate-hyper-links=" + hyperLinks);
return 0;

static AgentOptionsSnapshot Options(CatchPathStyle style)
{
    var wander = style == CatchPathStyle.Centered ? 0
        : style == CatchPathStyle.Lively ? 8 : 3;
    return new AgentOptionsSnapshot(
        true,
        style,
        1.0,
        wander,
        style == CatchPathStyle.Lively ? 1.0 : 0.75,
        true,
        style == CatchPathStyle.Lively,
        true);
}

static RuntimeCatchObjectKind ConvertKind(CatchObjectKind kind)
{
    return kind switch
    {
        CatchObjectKind.Droplet => RuntimeCatchObjectKind.Droplet,
        CatchObjectKind.TinyDroplet => RuntimeCatchObjectKind.TinyDroplet,
        CatchObjectKind.Banana => RuntimeCatchObjectKind.Banana,
        _ => RuntimeCatchObjectKind.Fruit
    };
}

static RuntimeCatchPlan BuildRobust(
    List<RuntimeCatchObject> objects,
    double catcherWidth,
    AgentOptionsSnapshot options,
    int seed,
    string path)
{
    var best = RuntimeCatchPlanner.Build(objects, catcherWidth, options, seed, path);
    var low = options.SafetyMargin;
    var high = Math.Min(10.0, catcherWidth * 0.4 - 0.5);
    for (var iteration = 0; iteration < 7 && high - low >= 0.20; iteration++)
    {
        var candidate = Math.Floor(((low + high) * 0.5) * 4.0) / 4.0;
        if (candidate <= low) break;
        try
        {
            var trialOptions = new AgentOptionsSnapshot(
                options.Enabled,
                options.Style,
                candidate,
                options.WanderPixels,
                options.TrackingDeadband,
                options.IncludeTinyDropletsAsHardConstraints,
                options.FatigueEnabled,
                options.RepeatableVariation);
            best = RuntimeCatchPlanner.Build(objects, catcherWidth, trialOptions, seed, path);
            low = candidate;
        }
        catch (InvalidOperationException)
        {
            high = candidate;
        }
    }
    return best;
}

static void Verify(RuntimeCatchPlan plan, RuntimeCatchPlan repeated)
{
    if (plan.Waypoints.Count != repeated.Waypoints.Count)
        throw new InvalidOperationException("repeatability waypoint-count mismatch");
    for (var index = 0; index < plan.Waypoints.Count; index++)
    {
        var waypoint = plan.Waypoints[index];
        if (!waypoint.ObjectWindow.Contains(waypoint.X, 0.00001))
            throw new InvalidOperationException("waypoint escaped object window at " + waypoint.Time + "ms");
        var referenceX = plan.ReferenceX(waypoint.Time);
        if (!waypoint.ObjectWindow.Contains(referenceX, 0.00001))
            throw new InvalidOperationException("control path escaped object window at "
                + waypoint.Time + "ms");
        if (Math.Abs(waypoint.X - repeated.Waypoints[index].X) > 0.0000001)
            throw new InvalidOperationException("fixed-seed path was not repeatable");
        if (index == 0 || waypoint.ArrivedByHyperDash)
            continue;
        var previous = plan.Waypoints[index - 1];
        if (Math.Abs(waypoint.X - previous.X) > waypoint.Time - previous.Time + 0.00001)
            throw new InvalidOperationException("waypoint exceeded dash speed at " + waypoint.Time + "ms");
    }
}

static (double Minimum, int UnderFour, int UnderEight, int Count, string Tightest) MeasureClearance(
    RuntimeCatchPlan plan)
{
    var byId = plan.Objects.ToDictionary(hitObject => hitObject.Id);
    var minimum = double.PositiveInfinity;
    var underFour = 0;
    var underEight = 0;
    var count = 0;
    var tightest = new List<(double Clearance, string Description)>();
    for (var index = 1; index < plan.Waypoints.Count; index++)
    {
        var waypoint = plan.Waypoints[index];
        var maximumDistance = 0.0;
        foreach (var id in waypoint.ObjectIds)
            maximumDistance = Math.Max(maximumDistance, Math.Abs(waypoint.X - byId[id].X));
        var clearance = plan.CollisionRadius - maximumDistance;
        minimum = Math.Min(minimum, clearance);
        if (clearance < 4.0) underFour++;
        if (clearance < 8.0) underEight++;
        if (clearance < 8.0)
        {
            var role = waypoint.ArrivedByHyperDash ? "hyper-target"
                : waypoint.DepartsByHyperDash ? "hyper-source" : "plain";
            tightest.Add((clearance,
                waypoint.Time + "ms=" + clearance.ToString("0.##") + "px/" + role));
        }
        count++;
    }
    return (
        minimum,
        underFour,
        underEight,
        count,
        string.Join(", ", tightest.OrderBy(value => value.Clearance).Take(8)
            .Select(value => value.Description)));
}
