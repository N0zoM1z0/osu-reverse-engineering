namespace OsuReverseEngineering.Catch;

public static class CatchObjectConverter
{
    private const double HyperDashFrameAllowance = 1000.0 / 240.0;
    private const int StableRandomSeed = 1337;

    private readonly record struct TimingState(double BeatLength, double SliderVelocityMultiplier);
    private readonly record struct SliderEvent(int Time, bool IsRepeat);

    public static CatchConversionResult Convert(
        CatchBeatmapDocument beatmap,
        CatchGameplayModifiers modifiers = CatchGameplayModifiers.None)
    {
        ArgumentNullException.ThrowIfNull(beatmap);
        var random = new LegacyFastRandom(StableRandomSeed);
        var objects = new List<ConvertedCatchObject>();
        var nextId = 0;

        for (var sourceIndex = 0; sourceIndex < beatmap.HitObjects.Count; sourceIndex++)
        {
            var source = beatmap.HitObjects[sourceIndex];
            switch (source.Kind)
            {
                case RawCatchObjectKind.Circle:
                    AddObject(CatchObjectKind.Fruit, source.StartTime, source.Position.X, source, sourceIndex);
                    break;

                case RawCatchObjectKind.Slider:
                    ConvertSlider(source, sourceIndex);
                    break;

                case RawCatchObjectKind.Spinner:
                    ConvertSpinner(source, sourceIndex);
                    break;

                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        objects.Sort((left, right) =>
        {
            var byTime = left.Time.CompareTo(right.Time);
            return byTime != 0 ? byTime : left.Id.CompareTo(right.Id);
        });

        var catcherWidth = CalculateCatcherWidth(beatmap.CircleSize, modifiers);
        AssignHyperDashTargets(objects, catcherWidth);
        return new CatchConversionResult
        {
            Beatmap = beatmap,
            Objects = objects,
            CatcherWidth = catcherWidth,
            CollisionRadius = catcherWidth * 0.4
        };

        void ConvertSlider(RawCatchHitObject source, int sourceIndex)
        {
            var slider = source.Slider ?? throw new InvalidDataException("Slider definition is missing.");
            var path = new SliderPath(slider.CurveKind, slider.ControlPoints, slider.PixelLength);
            var timing = ResolveTimingState(beatmap.TimingPoints, source.StartTime);
            var scoringDistance = 100.0 * beatmap.SliderMultiplier * timing.SliderVelocityMultiplier;
            var velocityPixelsPerMillisecond = scoringDistance / timing.BeatLength;
            var velocityPixelsPerSecond = velocityPixelsPerMillisecond * 1000.0;
            var spanDuration = slider.PixelLength / velocityPixelsPerMillisecond;
            var endTime = source.StartTime + (int)(spanDuration * slider.RepeatCount);
            var tickDistance = beatmap.FormatVersion < 8
                ? 100.0 * beatmap.SliderMultiplier / beatmap.SliderTickRate
                : scoringDistance / beatmap.SliderTickRate;
            tickDistance = Math.Min(tickDistance, slider.PixelLength);
            var events = BuildSliderEvents(
                source.StartTime,
                slider,
                velocityPixelsPerSecond,
                tickDistance,
                endTime);

            AddObject(CatchObjectKind.Fruit, source.StartTime, source.Position.X, source, sourceIndex);
            var previousEventTime = source.StartTime;
            for (var eventIndex = 0; eventIndex < events.Count; eventIndex++)
            {
                var sliderEvent = events[eventIndex];
                AddTinyDroplets(previousEventTime, sliderEvent.Time);
                previousEventTime = sliderEvent.Time;

                // The final event only bounds tiny-droplet generation. stable Catch
                // creates a separate fruit at the slider's true end time.
                if (eventIndex == events.Count - 1)
                    continue;

                var position = PositionAtTime(path, source.StartTime, endTime, slider.RepeatCount, sliderEvent.Time);
                if (sliderEvent.IsRepeat)
                {
                    AddObject(CatchObjectKind.Fruit, sliderEvent.Time, position.X, source, sourceIndex);
                }
                else
                {
                    _ = random.NextDouble(); // Droplet visual rotation consumes the shared generator.
                    AddObject(CatchObjectKind.Droplet, sliderEvent.Time, position.X, source, sourceIndex);
                }
            }

            var tail = PositionAtTime(path, source.StartTime, endTime, slider.RepeatCount, endTime);
            AddObject(CatchObjectKind.Fruit, endTime, tail.X, source, sourceIndex);

            void AddTinyDroplets(int previous, int current)
            {
                var gap = current - previous;
                if (gap <= 80)
                    return;
                var step = (float)gap;
                while (step > 100)
                    step /= 2;
                for (var time = previous + step; time < current; time += step)
                {
                    var integerTime = (int)time;
                    var position = PositionAtTime(path, source.StartTime, endTime, slider.RepeatCount, integerTime);
                    AddObject(
                        CatchObjectKind.TinyDroplet,
                        integerTime,
                        position.X + random.Next(-20, 20),
                        source,
                        sourceIndex);
                }
            }
        }

        void ConvertSpinner(RawCatchHitObject source, int sourceIndex)
        {
            var step = (float)(source.EndTime - source.StartTime);
            while (step > 100)
                step /= 2;
            if (step <= 0)
                return;
            for (var time = (float)source.StartTime; time <= source.EndTime; time += step)
            {
                var x = random.Next(0, 512);
                _ = random.NextDouble(); // Banana visual state consumes the shared generator.
                AddObject(CatchObjectKind.Banana, (int)time, x, source, sourceIndex);
            }
        }

        void AddObject(
            CatchObjectKind kind,
            int time,
            double x,
            RawCatchHitObject source,
            int sourceIndex)
        {
            objects.Add(new ConvertedCatchObject
            {
                Id = nextId++,
                Kind = kind,
                Time = time,
                X = Math.Clamp(x, 0, 512),
                SourceLine = source.SourceLine,
                SourceObjectIndex = sourceIndex
            });
        }
    }

    public static double CalculateCatcherWidth(
        double circleSize,
        CatchGameplayModifiers modifiers = CatchGameplayModifiers.None)
    {
        var modifiedCircleSize = circleSize;
        if (modifiers.HasFlag(CatchGameplayModifiers.Easy))
            modifiedCircleSize *= 0.5;
        if (modifiers.HasFlag(CatchGameplayModifiers.HardRock))
            modifiedCircleSize = Math.Min(10, modifiedCircleSize * 1.3);
        return 106.75 * (1.0 - 0.7 * ((modifiedCircleSize - 5.0) / 5.0));
    }

    public static Point2 PositionAtTime(
        SliderPath path,
        int startTime,
        int endTime,
        int repeatCount,
        int time)
    {
        if (endTime <= startTime)
            return path.Points[0];
        var spanProgress = (time - startTime) / ((endTime - startTime) / (double)repeatCount);
        var fractional = spanProgress % 1.0;
        if (fractional < 0)
            fractional += 1;
        var progress = spanProgress % 2.0 >= 1.0 ? 1.0 - fractional : fractional;
        if (time >= endTime)
            progress = repeatCount % 2 == 0 ? 0 : 1;
        return path.PositionAtProgress(progress);
    }

    private static IReadOnlyList<SliderEvent> BuildSliderEvents(
        int startTime,
        CatchSliderDefinition slider,
        double velocityPixelsPerSecond,
        double tickDistance,
        int endTime)
    {
        var events = new List<SliderEvent>();
        var minimumDistanceFromSpanEnd = 0.01 * velocityPixelsPerSecond;
        for (var span = 0; span < slider.RepeatCount; span++)
        {
            for (var distance = tickDistance;
                 distance < slider.PixelLength - minimumDistanceFromSpanEnd;
                 distance += tickDistance)
            {
                var totalDistance = span * slider.PixelLength + distance;
                var time = (int)(startTime + totalDistance / velocityPixelsPerSecond * 1000.0);
                events.Add(new SliderEvent(time, false));
            }

            var spanEndDistance = (span + 1) * slider.PixelLength;
            var spanEndTime = (int)(startTime + spanEndDistance / velocityPixelsPerSecond * 1000.0);
            events.Add(new SliderEvent(spanEndTime, span < slider.RepeatCount - 1));
        }

        if (events.Count > 0)
        {
            var final = events[^1];
            events[^1] = final with
            {
                Time = Math.Max(startTime + (endTime - startTime) / 2, final.Time - 36)
            };
        }
        return events;
    }

    private static TimingState ResolveTimingState(
        IReadOnlyList<CatchTimingPoint> timingPoints,
        int objectTime)
    {
        double? beatLength = null;
        var sliderVelocityMultiplier = 1.0;
        foreach (var point in timingPoints)
        {
            if (point.Time > objectTime)
                break;
            if (point.Uninherited)
            {
                beatLength = point.BeatLength;
                sliderVelocityMultiplier = 1.0;
            }
            else
            {
                sliderVelocityMultiplier = Math.Clamp(100.0 / -point.BeatLength, 0.1, 10.0);
            }
        }

        beatLength ??= timingPoints
            .Where(point => point.Uninherited && point.BeatLength > 0)
            .OrderBy(point => point.Time)
            .First().BeatLength;
        return new TimingState(beatLength.Value, sliderVelocityMultiplier);
    }

    private static void AssignHyperDashTargets(
        IReadOnlyList<ConvertedCatchObject> allObjects,
        double catcherWidth)
    {
        var objects = allObjects
            .Where(hitObject => hitObject.ParticipatesInHyperDash)
            .OrderBy(hitObject => hitObject.Time)
            .ThenBy(hitObject => hitObject.Id)
            .ToList();
        var residualMargin = catcherWidth * 0.5;
        var previousDirection = 0;

        for (var index = 0; index < objects.Count - 1; index++)
        {
            var current = objects[index];
            var next = objects[index + 1];
            var direction = Math.Sign(next.X - current.X);
            var available = next.Time - current.Time - HyperDashFrameAllowance;
            var required = Math.Abs(next.X - current.X)
                - (direction == previousDirection ? residualMargin : catcherWidth * 0.5);

            if (available < required)
            {
                current.HyperDashTargetId = next.Id;
                residualMargin = catcherWidth * 0.5;
            }
            else
            {
                current.HyperDashOffset = available - required;
                residualMargin = Math.Clamp(available - required, 0, catcherWidth * 0.5);
            }
            previousDirection = direction;
        }
    }
}
