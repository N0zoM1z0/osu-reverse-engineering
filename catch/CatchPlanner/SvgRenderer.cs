using System.Globalization;
using System.Security;
using System.Text;

namespace OsuReverseEngineering.Catch;

public static class SvgRenderer
{
    private const int Width = 1800;
    private const int Height = 760;
    private const int MarginLeft = 82;
    private const int MarginRight = 36;
    private const int MarginTop = 74;
    private const int MarginBottom = 64;

    public static void Write(CatchPlan plan, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (string.IsNullOrWhiteSpace(outputPath))
            throw new ArgumentException("An SVG output path is required.", nameof(outputPath));
        if (plan.Waypoints.Count < 2)
            throw new InvalidOperationException("The plan has too few waypoints to render.");

        var fullPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, Render(plan), new UTF8Encoding(false));
    }

    public static string Render(CatchPlan plan)
    {
        var startTime = plan.Waypoints[0].Time;
        var endTime = plan.Waypoints[^1].Time;
        var timeSpan = Math.Max(1, endTime - startTime);
        var plotWidth = Width - MarginLeft - MarginRight;
        var plotHeight = Height - MarginTop - MarginBottom;
        var builder = new StringBuilder(1024 * 128);
        var title = SecurityElement.Escape(plan.Conversion.Beatmap.DisplayName) ?? "Catch plan";

        builder.AppendLine($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{Width}\" height=\"{Height}\" viewBox=\"0 0 {Width} {Height}\">");
        builder.AppendLine("<style>text{font-family:Inter,Segoe UI,sans-serif;fill:#c9d1d9}.minor{font-size:11px}.label{font-size:13px}.title{font-size:20px;font-weight:650}.subtitle{font-size:12px;fill:#8b949e}</style>");
        builder.AppendLine("<rect width=\"100%\" height=\"100%\" fill=\"#0d1117\"/>");
        builder.AppendLine($"<text x=\"{MarginLeft}\" y=\"30\" class=\"title\">{title}</text>");
        builder.AppendLine($"<text x=\"{MarginLeft}\" y=\"51\" class=\"subtitle\">hard viability tube · projected smooth trajectory · stable Catch conversion</text>");
        builder.AppendLine($"<rect x=\"{MarginLeft}\" y=\"{MarginTop}\" width=\"{plotWidth}\" height=\"{plotHeight}\" fill=\"#161b22\" stroke=\"#30363d\"/>");

        foreach (var catcherX in new[] { 0, 128, 256, 384, 512 })
        {
            var y = MapCatcherX(catcherX);
            builder.AppendLine($"<line x1=\"{MarginLeft}\" y1=\"{F(y)}\" x2=\"{Width - MarginRight}\" y2=\"{F(y)}\" stroke=\"#30363d\" stroke-width=\"1\"/>");
            builder.AppendLine($"<text x=\"{MarginLeft - 12}\" y=\"{F(y + 4)}\" text-anchor=\"end\" class=\"minor\">{catcherX}</text>");
        }

        var gridStep = ChooseGridStep(timeSpan);
        var firstGrid = (int)Math.Ceiling(startTime / (double)gridStep) * gridStep;
        for (var time = firstGrid; time <= endTime; time += gridStep)
        {
            var x = MapTime(time);
            builder.AppendLine($"<line x1=\"{F(x)}\" y1=\"{MarginTop}\" x2=\"{F(x)}\" y2=\"{Height - MarginBottom}\" stroke=\"#21262d\" stroke-width=\"1\"/>");
            builder.AppendLine($"<text x=\"{F(x)}\" y=\"{Height - MarginBottom + 21}\" text-anchor=\"middle\" class=\"minor\">{F(time / 1000.0)}s</text>");
        }

        var tube = new StringBuilder();
        foreach (var waypoint in plan.Waypoints)
            tube.Append(F(MapTime(waypoint.Time))).Append(',').Append(F(MapCatcherX(waypoint.ViableWindow.Max))).Append(' ');
        foreach (var waypoint in plan.Waypoints.Reverse())
            tube.Append(F(MapTime(waypoint.Time))).Append(',').Append(F(MapCatcherX(waypoint.ViableWindow.Min))).Append(' ');
        builder.AppendLine($"<polygon points=\"{tube}\" fill=\"#1f6feb\" fill-opacity=\"0.16\" stroke=\"none\"/>");

        foreach (var waypoint in plan.Waypoints.Where(waypoint => !waypoint.IsSyntheticStart))
        {
            var x = MapTime(waypoint.Time);
            builder.AppendLine($"<line x1=\"{F(x)}\" y1=\"{F(MapCatcherX(waypoint.ObjectWindow.Min))}\" x2=\"{F(x)}\" y2=\"{F(MapCatcherX(waypoint.ObjectWindow.Max))}\" stroke=\"#58a6ff\" stroke-opacity=\"0.38\" stroke-width=\"1.3\"/>");
        }

        foreach (var hitObject in plan.Conversion.Objects)
        {
            var colour = hitObject.Kind switch
            {
                CatchObjectKind.Fruit => "#f0f6fc",
                CatchObjectKind.Droplet => "#79c0ff",
                CatchObjectKind.TinyDroplet => "#d2a8ff",
                CatchObjectKind.Banana => "#e3b341",
                _ => "#8b949e"
            };
            var radius = hitObject.Kind switch
            {
                CatchObjectKind.Fruit => 2.6,
                CatchObjectKind.Droplet => 2.0,
                CatchObjectKind.TinyDroplet => 1.2,
                _ => 1.1
            };
            builder.AppendLine($"<circle cx=\"{F(MapTime(hitObject.Time))}\" cy=\"{F(MapCatcherX(hitObject.X))}\" r=\"{F(radius)}\" fill=\"{colour}\" fill-opacity=\"0.72\"/>");
        }

        var trajectory = new StringBuilder();
        if (plan.Controls.Count > 0)
        {
            trajectory.Append(F(MapTime(plan.Controls[0].StartTime))).Append(',')
                .Append(F(MapCatcherX(plan.Controls[0].StartX))).Append(' ');
            foreach (var phase in plan.Controls)
                trajectory.Append(F(MapTime(phase.EndTime))).Append(',').Append(F(MapCatcherX(phase.EndX))).Append(' ');
        }
        builder.AppendLine($"<polyline points=\"{trajectory}\" fill=\"none\" stroke=\"#f0f6fc\" stroke-width=\"2.1\" stroke-linejoin=\"round\" stroke-linecap=\"round\"/>");

        foreach (var phase in plan.Controls.Where(phase => phase.Input is
                     CatchInputState.DashLeft or CatchInputState.DashRight
                     or CatchInputState.HyperDashLeft or CatchInputState.HyperDashRight))
        {
            var hyper = phase.Input is CatchInputState.HyperDashLeft or CatchInputState.HyperDashRight;
            builder.AppendLine($"<line x1=\"{F(MapTime(phase.StartTime))}\" y1=\"{F(MapCatcherX(phase.StartX))}\" x2=\"{F(MapTime(phase.EndTime))}\" y2=\"{F(MapCatcherX(phase.EndX))}\" stroke=\"{(hyper ? "#f0883e" : "#3fb950")}\" stroke-width=\"{(hyper ? "3.2" : "2.7")}\" stroke-linecap=\"round\"/>");
        }

        builder.AppendLine($"<text x=\"{MarginLeft + plotWidth / 2}\" y=\"{Height - 15}\" text-anchor=\"middle\" class=\"label\">beatmap time</text>");
        builder.AppendLine($"<text x=\"20\" y=\"{MarginTop + plotHeight / 2}\" text-anchor=\"middle\" class=\"label\" transform=\"rotate(-90 20 {MarginTop + plotHeight / 2})\">catcher x</text>");
        builder.AppendLine($"<g transform=\"translate({Width - 520},28)\"><line x1=\"0\" y1=\"0\" x2=\"28\" y2=\"0\" stroke=\"#f0f6fc\" stroke-width=\"2\"/><text x=\"36\" y=\"4\" class=\"minor\">walk / idle</text><line x1=\"135\" y1=\"0\" x2=\"163\" y2=\"0\" stroke=\"#3fb950\" stroke-width=\"3\"/><text x=\"171\" y=\"4\" class=\"minor\">dash</text><line x1=\"225\" y1=\"0\" x2=\"253\" y2=\"0\" stroke=\"#f0883e\" stroke-width=\"3\"/><text x=\"261\" y=\"4\" class=\"minor\">hyperdash</text><rect x=\"365\" y=\"-7\" width=\"25\" height=\"13\" fill=\"#1f6feb\" fill-opacity=\"0.3\"/><text x=\"398\" y=\"4\" class=\"minor\">viability</text></g>");
        builder.AppendLine("</svg>");
        return builder.ToString();

        double MapTime(double time) => MarginLeft + (time - startTime) / timeSpan * plotWidth;
        double MapCatcherX(double catcherX) => MarginTop + (512 - catcherX) / 512.0 * plotHeight;
    }

    private static int ChooseGridStep(int timeSpan)
    {
        foreach (var candidate in new[] { 5000, 10000, 20000, 30000, 60000, 120000 })
        {
            if (timeSpan / candidate <= 14)
                return candidate;
        }
        return 300000;
    }

    private static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
