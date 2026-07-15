namespace OsuReverseEngineering.Catch;

/// <summary>
/// Computes a hard reachable tube first, then minimises a smoothness objective
/// inside that tube. Feasibility is never traded for a prettier trajectory.
/// </summary>
public static class ViabilityPlanner
{
    private const double WalkSpeed = 0.5;
    private const double DashSpeed = 1.0;
    private const double HyperArrivalLead = 1000.0 / 60.0;
    private const double Epsilon = 1e-6;

    public static CatchPlan Build(
        CatchConversionResult conversion,
        CatchPlanOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(conversion);
        options ??= new CatchPlanOptions();
        if (options.SafetyMargin < 0 || options.SafetyMargin >= conversion.CollisionRadius)
            throw new ArgumentOutOfRangeException(nameof(options), "Safety margin must be inside the catch window.");

        var constraints = BuildConstraints(conversion, options);
        var effectiveWindows = constraints.Select(constraint => constraint.ObjectWindow).ToArray();
        var objectsById = conversion.Objects.ToDictionary(hitObject => hitObject.Id);

        for (var index = 1; index < constraints.Count; index++)
        {
            var targetId = constraints[index].ForcedHyperTargetObjectId;
            if (targetId is null)
                continue;
            var targetX = objectsById[targetId.Value].X;
            effectiveWindows[index] = effectiveWindows[index].Intersect(Interval.Point(targetX));
            EnsureNotEmpty(effectiveWindows[index], constraints[index], "hyperdash target centre is outside a simultaneous-object window");
        }

        var backward = PropagateBackward(constraints, effectiveWindows);
        var reachable = PropagateForward(constraints, backward);
        var positions = SelectFeasibleTrajectory(constraints, backward, reachable);
        SmoothTrajectory(constraints, reachable, positions, options);
        VerifyTrajectory(constraints, effectiveWindows, positions);

        var waypoints = constraints.Select((constraint, index) => new CatchWaypoint
        {
            Time = constraint.Time,
            X = positions[index],
            ViableWindow = reachable[index],
            ObjectWindow = constraint.ObjectWindow,
            IsSyntheticStart = constraint.IsSyntheticStart,
            ArrivedByHyperDash = constraint.ForcedHyperTargetObjectId is not null,
            ObjectIds = constraint.ObjectIds
        }).ToList();
        var controls = BuildControls(waypoints);
        var audit = Audit(conversion, controls);

        return new CatchPlan
        {
            Conversion = conversion,
            Options = options,
            Constraints = constraints,
            Waypoints = waypoints,
            Controls = controls,
            Audit = audit
        };
    }

    public static double PositionAt(IReadOnlyList<CatchControlPhase> controls, double time)
    {
        if (controls.Count == 0)
            return 256;
        if (time <= controls[0].StartTime)
            return controls[0].StartX;
        if (time >= controls[^1].EndTime)
            return controls[^1].EndX;

        var low = 0;
        var high = controls.Count - 1;
        while (low <= high)
        {
            var middle = (low + high) / 2;
            var phase = controls[middle];
            if (time < phase.StartTime)
            {
                high = middle - 1;
            }
            else if (time > phase.EndTime)
            {
                low = middle + 1;
            }
            else
            {
                if (phase.EndTime - phase.StartTime <= Epsilon)
                    return phase.EndX;
                return phase.StartX + (phase.EndX - phase.StartX)
                    * ((time - phase.StartTime) / (phase.EndTime - phase.StartTime));
            }
        }
        return controls[Math.Clamp(high, 0, controls.Count - 1)].EndX;
    }

    private static List<CatchConstraint> BuildConstraints(
        CatchConversionResult conversion,
        CatchPlanOptions options)
    {
        var hardObjects = conversion.Objects
            .Where(hitObject => hitObject.Kind is CatchObjectKind.Fruit or CatchObjectKind.Droplet
                || options.IncludeTinyDropletsAsHardConstraints && hitObject.Kind == CatchObjectKind.TinyDroplet)
            .OrderBy(hitObject => hitObject.Time)
            .ThenBy(hitObject => hitObject.Id)
            .ToList();
        if (hardObjects.Count == 0)
            throw new InvalidOperationException("The converted beatmap has no catch constraints.");

        var radius = conversion.CollisionRadius - options.SafetyMargin;
        var constraints = new List<CatchConstraint>();
        var startTime = Math.Min(0, hardObjects[0].Time - 1000);
        constraints.Add(new CatchConstraint
        {
            Index = 0,
            Time = startTime,
            ObjectWindow = Interval.Point(256),
            PreferredX = 256,
            ObjectIds = Array.Empty<int>(),
            IsSyntheticStart = true
        });

        foreach (var group in hardObjects.GroupBy(hitObject => hitObject.Time))
        {
            var grouped = group.ToList();
            var window = new Interval(0, 512);
            var weightedX = 0.0;
            var totalWeight = 0.0;
            foreach (var hitObject in grouped)
            {
                window = window.Intersect(new Interval(
                    Math.Max(0, hitObject.X - radius),
                    Math.Min(512, hitObject.X + radius)));
                var weight = hitObject.Kind switch
                {
                    CatchObjectKind.Fruit => 1.0,
                    CatchObjectKind.Droplet => 0.72,
                    CatchObjectKind.TinyDroplet => 0.2,
                    _ => 0.0
                };
                weightedX += hitObject.X * weight;
                totalWeight += weight;
            }

            var constraint = new CatchConstraint
            {
                Index = constraints.Count,
                Time = group.Key,
                ObjectWindow = window,
                PreferredX = totalWeight > 0 ? weightedX / totalWeight : window.Midpoint,
                ObjectIds = grouped.Select(hitObject => hitObject.Id).ToArray(),
                IsSyntheticStart = false
            };
            EnsureNotEmpty(window, constraint, "simultaneous catch windows do not overlap");
            constraints.Add(constraint);
        }

        var objectById = conversion.Objects.ToDictionary(hitObject => hitObject.Id);
        for (var index = 1; index < constraints.Count; index++)
        {
            var currentIds = constraints[index].ObjectIds.ToHashSet();
            int? targetId = null;
            foreach (var previousId in constraints[index - 1].ObjectIds)
            {
                if (!objectById.TryGetValue(previousId, out var previous)
                    || previous.HyperDashTargetId is not { } candidate
                    || !currentIds.Contains(candidate))
                {
                    continue;
                }
                targetId = candidate;
                break;
            }
            constraints[index].ForcedHyperTargetObjectId = targetId;
        }
        return constraints;
    }

    private static Interval[] PropagateBackward(
        IReadOnlyList<CatchConstraint> constraints,
        IReadOnlyList<Interval> windows)
    {
        var backward = new Interval[constraints.Count];
        backward[^1] = windows[^1];
        EnsureNotEmpty(backward[^1], constraints[^1], "empty final window");

        for (var index = constraints.Count - 2; index >= 0; index--)
        {
            if (constraints[index + 1].ForcedHyperTargetObjectId is not null)
            {
                // stable computes the boost from the actual catcher position and
                // remaining target time. Holding dash therefore guarantees arrival
                // at the target centre from any point in the source catch window.
                backward[index] = windows[index];
            }
            else
            {
                var delta = constraints[index + 1].Time - constraints[index].Time;
                if (delta < 0)
                    throw new InvalidOperationException("Catch constraints are not time ordered.");
                backward[index] = windows[index].Intersect(backward[index + 1].Expand(DashSpeed * delta));
            }
            EnsureNotEmpty(backward[index], constraints[index], "backward viability set is empty");
        }
        return backward;
    }

    private static Interval[] PropagateForward(
        IReadOnlyList<CatchConstraint> constraints,
        IReadOnlyList<Interval> backward)
    {
        var forward = new Interval[constraints.Count];
        forward[0] = backward[0].Intersect(Interval.Point(256));
        EnsureNotEmpty(forward[0], constraints[0], "initial catcher position cannot reach the map");

        for (var index = 1; index < constraints.Count; index++)
        {
            if (constraints[index].ForcedHyperTargetObjectId is not null)
            {
                forward[index] = backward[index];
            }
            else
            {
                var delta = constraints[index].Time - constraints[index - 1].Time;
                forward[index] = backward[index].Intersect(forward[index - 1].Expand(DashSpeed * delta));
            }
            EnsureNotEmpty(forward[index], constraints[index], "forward reachable set is empty");
        }
        return forward;
    }

    private static double[] SelectFeasibleTrajectory(
        IReadOnlyList<CatchConstraint> constraints,
        IReadOnlyList<Interval> backward,
        IReadOnlyList<Interval> reachable)
    {
        var positions = new double[constraints.Count];
        positions[0] = 256;
        for (var index = 1; index < constraints.Count; index++)
        {
            Interval available;
            if (constraints[index].ForcedHyperTargetObjectId is not null)
            {
                available = backward[index];
            }
            else
            {
                var delta = constraints[index].Time - constraints[index - 1].Time;
                available = backward[index].Intersect(
                    new Interval(positions[index - 1] - DashSpeed * delta, positions[index - 1] + DashSpeed * delta));
            }
            available = available.Intersect(reachable[index]);
            EnsureNotEmpty(available, constraints[index], "greedy feasible selection failed");
            positions[index] = available.Clamp(constraints[index].PreferredX);
        }
        return positions;
    }

    private static void SmoothTrajectory(
        IReadOnlyList<CatchConstraint> constraints,
        IReadOnlyList<Interval> reachable,
        double[] positions,
        CatchPlanOptions options)
    {
        for (var pass = 0; pass < options.SmoothingPasses; pass++)
        {
            Sweep(1, constraints.Count - 1, 1);
            Sweep(constraints.Count - 2, 0, -1);
        }

        void Sweep(int start, int endExclusive, int step)
        {
            for (var index = start; index != endExclusive; index += step)
            {
                if (index <= 0 || constraints[index].ForcedHyperTargetObjectId is not null)
                    continue;

                var allowed = reachable[index];
                if (constraints[index].ForcedHyperTargetObjectId is null)
                {
                    var previousDelta = constraints[index].Time - constraints[index - 1].Time;
                    allowed = allowed.Intersect(new Interval(
                        positions[index - 1] - DashSpeed * previousDelta,
                        positions[index - 1] + DashSpeed * previousDelta));
                }
                if (index + 1 < constraints.Count
                    && constraints[index + 1].ForcedHyperTargetObjectId is null)
                {
                    var nextDelta = constraints[index + 1].Time - constraints[index].Time;
                    allowed = allowed.Intersect(new Interval(
                        positions[index + 1] - DashSpeed * nextDelta,
                        positions[index + 1] + DashSpeed * nextDelta));
                }
                if (allowed.IsEmpty)
                    continue;

                var smoothTarget = constraints[index].PreferredX;
                if (index + 1 < constraints.Count)
                {
                    var totalDelta = constraints[index + 1].Time - constraints[index - 1].Time;
                    var fraction = totalDelta <= 0
                        ? 0.5
                        : (constraints[index].Time - constraints[index - 1].Time) / (double)totalDelta;
                    smoothTarget = positions[index - 1]
                        + (positions[index + 1] - positions[index - 1]) * fraction;
                }
                else
                {
                    smoothTarget = positions[index - 1];
                }

                var totalWeight = options.CentreWeight
                    + options.SmoothnessWeight
                    + options.PlayfieldCentreWeight;
                var desired = (
                    constraints[index].PreferredX * options.CentreWeight
                    + smoothTarget * options.SmoothnessWeight
                    + 256 * options.PlayfieldCentreWeight) / totalWeight;
                positions[index] = allowed.Clamp(desired);
            }
        }
    }

    private static void VerifyTrajectory(
        IReadOnlyList<CatchConstraint> constraints,
        IReadOnlyList<Interval> effectiveWindows,
        IReadOnlyList<double> positions)
    {
        for (var index = 0; index < constraints.Count; index++)
        {
            if (!effectiveWindows[index].Contains(positions[index], 1e-5))
                throw new InvalidOperationException($"Trajectory left object window at {constraints[index].Time}ms.");
            if (index == 0 || constraints[index].ForcedHyperTargetObjectId is not null)
                continue;
            var distance = Math.Abs(positions[index] - positions[index - 1]);
            var available = constraints[index].Time - constraints[index - 1].Time;
            if (distance > DashSpeed * available + 1e-5)
                throw new InvalidOperationException($"Trajectory exceeded dash speed at {constraints[index].Time}ms.");
        }
    }

    private static IReadOnlyList<CatchControlPhase> BuildControls(IReadOnlyList<CatchWaypoint> waypoints)
    {
        var controls = new List<CatchControlPhase>();
        for (var index = 1; index < waypoints.Count; index++)
        {
            var previous = waypoints[index - 1];
            var current = waypoints[index];
            var duration = current.Time - previous.Time;
            var displacement = current.X - previous.X;
            var distance = Math.Abs(displacement);
            if (duration <= Epsilon)
            {
                if (distance > Epsilon)
                    throw new InvalidOperationException($"Non-zero movement at equal timestamps ({current.Time}ms).");
                continue;
            }

            if (distance <= Epsilon)
            {
                AddPhase(previous.Time, current.Time, previous.X, current.X, CatchInputState.Idle, 0);
                continue;
            }

            var direction = Math.Sign(displacement);
            if (current.ArrivedByHyperDash)
            {
                var travelDuration = Math.Max(1, duration - HyperArrivalLead);
                var travelEnd = Math.Min(current.Time, previous.Time + travelDuration);
                AddPhase(
                    previous.Time,
                    travelEnd,
                    previous.X,
                    current.X,
                    direction < 0 ? CatchInputState.HyperDashLeft : CatchInputState.HyperDashRight,
                    distance / Math.Max(1, travelEnd - previous.Time));
                AddPhase(travelEnd, current.Time, current.X, current.X, CatchInputState.Idle, 0);
                continue;
            }

            if (distance <= WalkSpeed * duration + Epsilon)
            {
                var walkDuration = distance / WalkSpeed;
                var movementStart = current.Time - walkDuration;
                AddPhase(previous.Time, movementStart, previous.X, previous.X, CatchInputState.Idle, 0);
                AddPhase(
                    movementStart,
                    current.Time,
                    previous.X,
                    current.X,
                    direction < 0 ? CatchInputState.WalkLeft : CatchInputState.WalkRight,
                    WalkSpeed);
                continue;
            }

            var dashDuration = Math.Clamp(2 * distance - duration, 0, duration);
            var walkDurationMixed = duration - dashDuration;
            var afterWalkX = previous.X + direction * WalkSpeed * walkDurationMixed;
            AddPhase(
                previous.Time,
                previous.Time + walkDurationMixed,
                previous.X,
                afterWalkX,
                direction < 0 ? CatchInputState.WalkLeft : CatchInputState.WalkRight,
                WalkSpeed);
            AddPhase(
                previous.Time + walkDurationMixed,
                current.Time,
                afterWalkX,
                current.X,
                direction < 0 ? CatchInputState.DashLeft : CatchInputState.DashRight,
                DashSpeed);
        }
        return controls;

        void AddPhase(
            double startTime,
            double endTime,
            double startX,
            double endX,
            CatchInputState input,
            double speed)
        {
            if (endTime - startTime <= Epsilon)
                return;
            controls.Add(new CatchControlPhase
            {
                StartTime = startTime,
                EndTime = endTime,
                StartX = startX,
                EndX = endX,
                Input = input,
                Speed = speed
            });
        }
    }

    private static CatchPlanAudit Audit(
        CatchConversionResult conversion,
        IReadOnlyList<CatchControlPhase> controls)
    {
        var fruits = 0;
        var droplets = 0;
        var tinyDroplets = 0;
        var bananas = 0;
        var fruitMisses = 0;
        var dropletMisses = 0;
        var tinyMisses = 0;
        var bananasCaught = 0;
        foreach (var hitObject in conversion.Objects)
        {
            var caught = Math.Abs(PositionAt(controls, hitObject.Time) - hitObject.X)
                < conversion.CollisionRadius;
            switch (hitObject.Kind)
            {
                case CatchObjectKind.Fruit:
                    fruits++;
                    if (!caught)
                        fruitMisses++;
                    break;
                case CatchObjectKind.Droplet:
                    droplets++;
                    if (!caught)
                        dropletMisses++;
                    break;
                case CatchObjectKind.TinyDroplet:
                    tinyDroplets++;
                    if (!caught)
                        tinyMisses++;
                    break;
                case CatchObjectKind.Banana:
                    bananas++;
                    if (caught)
                        bananasCaught++;
                    break;
            }
        }

        return new CatchPlanAudit
        {
            Fruits = fruits,
            Droplets = droplets,
            TinyDroplets = tinyDroplets,
            Bananas = bananas,
            PredictedFruitMisses = fruitMisses,
            PredictedDropletMisses = dropletMisses,
            PredictedTinyDropletMisses = tinyMisses,
            PredictedBananasCaught = bananasCaught,
            DashMilliseconds = controls
                .Where(phase => phase.Input is CatchInputState.DashLeft or CatchInputState.DashRight)
                .Sum(phase => phase.EndTime - phase.StartTime),
            HyperDashMilliseconds = controls
                .Where(phase => phase.Input is CatchInputState.HyperDashLeft or CatchInputState.HyperDashRight)
                .Sum(phase => phase.EndTime - phase.StartTime)
        };
    }

    private static void EnsureNotEmpty(Interval interval, CatchConstraint constraint, string reason)
    {
        if (!interval.IsEmpty)
            return;
        throw new InvalidOperationException(
            $"Catch plan is infeasible at {constraint.Time}ms (constraint #{constraint.Index}): {reason}.");
    }
}
