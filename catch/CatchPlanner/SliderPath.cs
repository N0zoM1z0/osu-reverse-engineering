namespace OsuReverseEngineering.Catch;

public sealed class SliderPath
{
    private const double BezierTolerance = 0.25;
    private const int MaximumBezierDepth = 20;

    private readonly IReadOnlyList<Point2> points;
    private readonly IReadOnlyList<double> cumulativeLengths;

    public SliderPath(
        SliderCurveKind curveKind,
        IReadOnlyList<Point2> controlPoints,
        double expectedLength)
    {
        if (controlPoints.Count < 2)
            throw new ArgumentException("A slider path needs at least two control points.", nameof(controlPoints));
        if (!double.IsFinite(expectedLength) || expectedLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(expectedLength));

        var approximated = curveKind switch
        {
            SliderCurveKind.Linear => ApproximateLinear(controlPoints),
            SliderCurveKind.Bezier => ApproximateBezierWithRedAnchors(controlPoints),
            SliderCurveKind.Perfect => ApproximatePerfect(controlPoints),
            SliderCurveKind.Catmull => ApproximateCatmull(controlPoints),
            _ => throw new ArgumentOutOfRangeException(nameof(curveKind))
        };

        points = FitToLength(RemoveZeroLengthSegments(approximated), expectedLength);
        var lengths = new List<double>(points.Count) { 0 };
        var running = 0.0;
        for (var index = 1; index < points.Count; index++)
        {
            running += Point2.Distance(points[index - 1], points[index]);
            lengths.Add(running);
        }
        cumulativeLengths = lengths;
        Length = running;
    }

    public double Length { get; }
    public IReadOnlyList<Point2> Points => points;

    public Point2 PositionAtProgress(double progress) =>
        PositionAtDistance(Math.Clamp(progress, 0, 1) * Length);

    public Point2 PositionAtDistance(double distance)
    {
        if (distance <= 0)
            return points[0];
        if (distance >= Length)
            return points[^1];

        var low = 0;
        var high = cumulativeLengths.Count - 1;
        while (low + 1 < high)
        {
            var middle = (low + high) / 2;
            if (cumulativeLengths[middle] < distance)
                low = middle;
            else
                high = middle;
        }

        var segmentLength = cumulativeLengths[high] - cumulativeLengths[low];
        if (segmentLength <= 1e-9)
            return points[high];
        return Point2.Lerp(
            points[low],
            points[high],
            (distance - cumulativeLengths[low]) / segmentLength);
    }

    private static List<Point2> ApproximateLinear(IReadOnlyList<Point2> controlPoints) =>
        controlPoints.ToList();

    private static List<Point2> ApproximateBezierWithRedAnchors(IReadOnlyList<Point2> controlPoints)
    {
        var output = new List<Point2> { controlPoints[0] };
        var segmentStart = 0;
        for (var index = 1; index < controlPoints.Count; index++)
        {
            if (!NearlyEqual(controlPoints[index], controlPoints[index - 1]))
                continue;
            AppendBezierSegment(controlPoints.Skip(segmentStart).Take(index - segmentStart).ToArray(), output);
            segmentStart = index;
        }
        AppendBezierSegment(controlPoints.Skip(segmentStart).ToArray(), output);
        return output;
    }

    private static void AppendBezierSegment(IReadOnlyList<Point2> segment, List<Point2> output)
    {
        if (segment.Count == 0)
            return;
        if (!NearlyEqual(output[^1], segment[0]))
            output.Add(segment[0]);
        if (segment.Count == 1)
            return;
        FlattenBezier(segment.ToArray(), output, 0);
    }

    private static void FlattenBezier(Point2[] controlPoints, List<Point2> output, int depth)
    {
        if (depth >= MaximumBezierDepth || IsBezierFlatEnough(controlPoints))
        {
            output.Add(controlPoints[^1]);
            return;
        }

        SplitBezier(controlPoints, out var left, out var right);
        FlattenBezier(left, output, depth + 1);
        FlattenBezier(right, output, depth + 1);
    }

    private static bool IsBezierFlatEnough(IReadOnlyList<Point2> points)
    {
        var threshold = BezierTolerance * BezierTolerance * 4;
        for (var index = 1; index < points.Count - 1; index++)
        {
            var secondX = points[index - 1].X - 2 * points[index].X + points[index + 1].X;
            var secondY = points[index - 1].Y - 2 * points[index].Y + points[index + 1].Y;
            if (secondX * secondX + secondY * secondY > threshold)
                return false;
        }
        return true;
    }

    private static void SplitBezier(
        IReadOnlyList<Point2> controlPoints,
        out Point2[] left,
        out Point2[] right)
    {
        var count = controlPoints.Count;
        left = new Point2[count];
        right = new Point2[count];
        var buffer = controlPoints.ToArray();
        for (var level = 0; level < count; level++)
        {
            left[level] = buffer[0];
            right[count - level - 1] = buffer[count - level - 1];
            for (var index = 0; index < count - level - 1; index++)
                buffer[index] = Point2.Lerp(buffer[index], buffer[index + 1], 0.5);
        }
    }

    private static List<Point2> ApproximateCatmull(IReadOnlyList<Point2> controlPoints)
    {
        var output = new List<Point2> { controlPoints[0] };
        for (var index = 0; index < controlPoints.Count - 1; index++)
        {
            var p0 = index > 0 ? controlPoints[index - 1] : controlPoints[index];
            var p1 = controlPoints[index];
            var p2 = controlPoints[index + 1];
            var p3 = index + 2 < controlPoints.Count ? controlPoints[index + 2] : p2;
            for (var sample = 1; sample <= 50; sample++)
                output.Add(CatmullRom(p0, p1, p2, p3, sample / 50.0));
        }
        return output;
    }

    private static Point2 CatmullRom(Point2 p0, Point2 p1, Point2 p2, Point2 p3, double time)
    {
        var time2 = time * time;
        var time3 = time2 * time;
        return new Point2(
            0.5 * ((2 * p1.X)
                + (-p0.X + p2.X) * time
                + (2 * p0.X - 5 * p1.X + 4 * p2.X - p3.X) * time2
                + (-p0.X + 3 * p1.X - 3 * p2.X + p3.X) * time3),
            0.5 * ((2 * p1.Y)
                + (-p0.Y + p2.Y) * time
                + (2 * p0.Y - 5 * p1.Y + 4 * p2.Y - p3.Y) * time2
                + (-p0.Y + 3 * p1.Y - 3 * p2.Y + p3.Y) * time3));
    }

    private static List<Point2> ApproximatePerfect(IReadOnlyList<Point2> controlPoints)
    {
        if (controlPoints.Count != 3 || !TryCircle(controlPoints[0], controlPoints[1], controlPoints[2], out var centre))
            return ApproximateBezierWithRedAnchors(controlPoints);

        var radius = Point2.Distance(controlPoints[0], centre);
        var startAngle = Math.Atan2(controlPoints[0].Y - centre.Y, controlPoints[0].X - centre.X);
        var middleAngle = Math.Atan2(controlPoints[1].Y - centre.Y, controlPoints[1].X - centre.X);
        var endAngle = Math.Atan2(controlPoints[2].Y - centre.Y, controlPoints[2].X - centre.X);
        var counterClockwise = Cross(controlPoints[1] - controlPoints[0], controlPoints[2] - controlPoints[1]) > 0;
        var sweep = counterClockwise
            ? PositiveAngle(endAngle - startAngle)
            : -PositiveAngle(startAngle - endAngle);

        var middleSweep = counterClockwise
            ? PositiveAngle(middleAngle - startAngle)
            : -PositiveAngle(startAngle - middleAngle);
        if ((counterClockwise && middleSweep > sweep) || (!counterClockwise && middleSweep < sweep))
            sweep += counterClockwise ? Math.PI * 2 : -Math.PI * 2;

        var arcLength = Math.Abs(sweep) * radius;
        var segmentCount = Math.Max(1, (int)(arcLength * 0.125));
        var output = new List<Point2>(segmentCount + 1) { controlPoints[0] };
        for (var index = 1; index < segmentCount; index++)
        {
            var angle = startAngle + sweep * index / segmentCount;
            output.Add(new Point2(
                centre.X + Math.Cos(angle) * radius,
                centre.Y + Math.Sin(angle) * radius));
        }
        output.Add(controlPoints[2]);
        return output;
    }

    private static bool TryCircle(Point2 a, Point2 b, Point2 c, out Point2 centre)
    {
        var determinant = 2 * (a.X * (b.Y - c.Y) + b.X * (c.Y - a.Y) + c.X * (a.Y - b.Y));
        if (Math.Abs(determinant) < 1e-7)
        {
            centre = default;
            return false;
        }

        var a2 = a.X * a.X + a.Y * a.Y;
        var b2 = b.X * b.X + b.Y * b.Y;
        var c2 = c.X * c.X + c.Y * c.Y;
        centre = new Point2(
            (a2 * (b.Y - c.Y) + b2 * (c.Y - a.Y) + c2 * (a.Y - b.Y)) / determinant,
            (a2 * (c.X - b.X) + b2 * (a.X - c.X) + c2 * (b.X - a.X)) / determinant);
        return true;
    }

    private static List<Point2> RemoveZeroLengthSegments(IReadOnlyList<Point2> input)
    {
        var output = new List<Point2> { input[0] };
        for (var index = 1; index < input.Count; index++)
        {
            if (Point2.Distance(output[^1], input[index]) > 1e-7)
                output.Add(input[index]);
        }
        return output;
    }

    private static IReadOnlyList<Point2> FitToLength(IReadOnlyList<Point2> input, double expectedLength)
    {
        if (input.Count == 1)
            return new[] { input[0], input[0] + new Point2(expectedLength, 0) };

        var output = new List<Point2> { input[0] };
        var consumed = 0.0;
        for (var index = 1; index < input.Count; index++)
        {
            var segment = Point2.Distance(input[index - 1], input[index]);
            if (consumed + segment >= expectedLength)
            {
                output.Add(Point2.Lerp(input[index - 1], input[index], (expectedLength - consumed) / segment));
                return output;
            }
            output.Add(input[index]);
            consumed += segment;
        }

        var direction = output[^1] - output[^2];
        var directionLength = direction.Length;
        if (directionLength <= 1e-9)
            direction = new Point2(1, 0);
        else
            direction /= directionLength;
        output.Add(output[^1] + direction * (expectedLength - consumed));
        return output;
    }

    private static double Cross(Point2 left, Point2 right) => left.X * right.Y - left.Y * right.X;

    private static double PositiveAngle(double angle)
    {
        angle %= Math.PI * 2;
        return angle < 0 ? angle + Math.PI * 2 : angle;
    }

    private static bool NearlyEqual(Point2 left, Point2 right) =>
        Math.Abs(left.X - right.X) < 1e-7 && Math.Abs(left.Y - right.Y) < 1e-7;
}
