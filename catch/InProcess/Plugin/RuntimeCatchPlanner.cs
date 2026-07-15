using System;
using System.Collections.Generic;

namespace LocalCatchAgent.Plugin
{
    internal enum RuntimeCatchObjectKind
    {
        Fruit,
        Droplet,
        TinyDroplet,
        Banana
    }

    internal sealed class RuntimeCatchObject
    {
        public int Id;
        public int Time;
        public double X;
        public RuntimeCatchObjectKind Kind;
        public int HyperTargetId = -1;
        public object Source;
    }

    internal struct CatchInterval
    {
        public CatchInterval(double minimum, double maximum)
        {
            Minimum = minimum;
            Maximum = maximum;
        }

        public readonly double Minimum;
        public readonly double Maximum;

        public bool IsEmpty { get { return Minimum > Maximum; } }
        public double Midpoint { get { return (Minimum + Maximum) * 0.5; } }
        public double Width { get { return IsEmpty ? 0.0 : Maximum - Minimum; } }

        public CatchInterval Intersect(CatchInterval other)
        {
            return new CatchInterval(
                Math.Max(Minimum, other.Minimum),
                Math.Min(Maximum, other.Maximum));
        }

        public CatchInterval Expand(double amount)
        {
            return new CatchInterval(Minimum - amount, Maximum + amount);
        }

        public double Clamp(double value)
        {
            return Math.Max(Minimum, Math.Min(Maximum, value));
        }

        public bool Contains(double value, double epsilon)
        {
            return !IsEmpty
                && value >= Minimum - epsilon
                && value <= Maximum + epsilon;
        }

        public static CatchInterval Point(double value)
        {
            return new CatchInterval(value, value);
        }
    }

    internal sealed class RuntimeCatchConstraint
    {
        public int Index;
        public int Time;
        public CatchInterval ObjectWindow;
        public double PreferredX;
        public int[] ObjectIds;
        public bool IsSyntheticStart;
        public int ForcedHyperTargetId = -1;
        public int HyperSegmentTargetId = -1;
        public int HyperSegmentSourceConstraintIndex = -1;
        public int OutgoingHyperTargetId = -1;
        public bool DepartsByHyperDash;
    }

    internal sealed class RuntimeCatchWaypoint
    {
        public int Time;
        public double X;
        public CatchInterval ViableWindow;
        public CatchInterval ObjectWindow;
        public bool IsSyntheticStart;
        public bool ArrivedByHyperDash;
        public bool DepartsByHyperDash;
        public int HyperSegmentSourceConstraintIndex = -1;
        public int HyperSegmentTargetId = -1;
        public double HyperTargetX;
        public int OutgoingHyperTargetId = -1;
        public double OutgoingHyperTargetX;
        public int[] ObjectIds;
    }

    internal enum CatchInputIntent
    {
        Idle,
        WalkLeft,
        WalkRight,
        DashLeft,
        DashRight,
        HyperDashLeft,
        HyperDashRight
    }

    internal sealed class RuntimeCatchControlPhase
    {
        public double StartTime;
        public double EndTime;
        public double StartX;
        public double EndX;
        public CatchInputIntent Input;
        public double NominalSpeed;

        public bool IsDash
        {
            get
            {
                return Input == CatchInputIntent.DashLeft
                    || Input == CatchInputIntent.DashRight
                    || Input == CatchInputIntent.HyperDashLeft
                    || Input == CatchInputIntent.HyperDashRight;
            }
        }

        public bool IsHyperDash
        {
            get
            {
                return Input == CatchInputIntent.HyperDashLeft
                    || Input == CatchInputIntent.HyperDashRight;
            }
        }
    }

    internal sealed class RuntimeCatchPlan
    {
        private const double Epsilon = 0.000001;

        public string MapPath;
        public List<RuntimeCatchObject> Objects;
        public List<RuntimeCatchConstraint> Constraints;
        public List<RuntimeCatchWaypoint> Waypoints;
        public List<RuntimeCatchControlPhase> Controls;
        public double CatcherWidth;
        public double CollisionRadius;
        public double SafetyMargin;
        public double LocalSafetyTarget;
        public int FruitCount;
        public int DropletCount;
        public int TinyDropletCount;
        public int BananaCount;
        public int HyperDashCount;
        public double PlannedDashMilliseconds;
        public double PlannedHyperDashMilliseconds;

        public int FirstObjectTime
        {
            get { return Waypoints.Count > 1 ? Waypoints[1].Time : Int32.MaxValue; }
        }

        public int LastObjectTime
        {
            get { return Waypoints.Count > 1 ? Waypoints[Waypoints.Count - 1].Time : Int32.MinValue; }
        }

        public RuntimeCatchControlPhase PhaseAt(double time)
        {
            if (Controls.Count == 0) return null;
            int low = 0;
            int high = Controls.Count - 1;
            while (low <= high)
            {
                int middle = (low + high) / 2;
                RuntimeCatchControlPhase phase = Controls[middle];
                if (time < phase.StartTime) high = middle - 1;
                else if (time > phase.EndTime) low = middle + 1;
                else return phase;
            }
            if (high >= 0 && high < Controls.Count) return Controls[high];
            return Controls[0];
        }

        public double ReferenceX(double time)
        {
            RuntimeCatchControlPhase phase = PhaseAt(time);
            if (phase == null) return 256.0;
            double duration = phase.EndTime - phase.StartTime;
            if (duration <= Epsilon || time >= phase.EndTime) return phase.EndX;
            if (time <= phase.StartTime) return phase.StartX;
            double amount = (time - phase.StartTime) / duration;
            return phase.StartX + (phase.EndX - phase.StartX) * amount;
        }

        public int NextWaypointIndex(double time)
        {
            int low = 1;
            int high = Waypoints.Count - 1;
            int result = Waypoints.Count;
            while (low <= high)
            {
                int middle = (low + high) / 2;
                if (Waypoints[middle].Time >= time)
                {
                    result = middle;
                    high = middle - 1;
                }
                else low = middle + 1;
            }
            return result;
        }
    }

    /// <summary>
    /// Stable Catch has a one-dimensional control problem.  Every object induces
    /// a legal catcher interval.  We first propagate those intervals backwards
    /// and forwards under |dx/dt| &lt;= 1, then optimise only inside the resulting
    /// viability tube.  Style noise is therefore projected, never accepted as a
    /// reason to make a required object unreachable.
    /// </summary>
    internal static class RuntimeCatchPlanner
    {
        private const double WalkSpeed = 0.5;
        private const double DashSpeed = 1.0;
        private const double HyperArrivalLead = 1000.0 / 60.0;
        private const double PreferredLocalSafety = 9.0;
        private const double Epsilon = 0.000001;

        public static RuntimeCatchPlan Build(
            IList<RuntimeCatchObject> sourceObjects,
            double catcherWidth,
            AgentOptionsSnapshot options,
            int variationSeed,
            string mapPath)
        {
            if (sourceObjects == null) throw new ArgumentNullException("sourceObjects");
            if (options == null) throw new ArgumentNullException("options");
            if (catcherWidth <= 0.0 || catcherWidth > 512.0)
                throw new ArgumentOutOfRangeException("catcherWidth");

            double collisionRadius = catcherWidth * 0.4;
            if (options.SafetyMargin <= 0.0 || options.SafetyMargin >= collisionRadius)
                throw new ArgumentOutOfRangeException("options", "Safety margin must be inside the catch window.");

            List<RuntimeCatchObject> objects = new List<RuntimeCatchObject>(sourceObjects.Count);
            for (int index = 0; index < sourceObjects.Count; index++)
                objects.Add(sourceObjects[index]);
            objects.Sort(CompareObjects);

            List<RuntimeCatchConstraint> constraints = BuildConstraints(
                objects,
                collisionRadius,
                options,
                variationSeed);
            Dictionary<int, RuntimeCatchObject> byId = IndexObjects(objects);
            CatchInterval[] effectiveWindows = new CatchInterval[constraints.Count];
            for (int index = 0; index < constraints.Count; index++)
                effectiveWindows[index] = constraints[index].ObjectWindow;

            LinkHyperDashConstraints(constraints, byId, effectiveWindows);
            CatchInterval[] backward = PropagateBackward(constraints, effectiveWindows);
            CatchInterval[] reachable = PropagateForward(constraints, backward);
            double[] positions = SelectFeasibleTrajectory(constraints, backward, reachable);
            SmoothTrajectory(constraints, reachable, positions, options);
            ProjectHyperDashSegments(constraints, byId, effectiveWindows, positions);
            VerifyTrajectory(constraints, effectiveWindows, positions);

            List<RuntimeCatchWaypoint> waypoints = new List<RuntimeCatchWaypoint>(constraints.Count);
            for (int index = 0; index < constraints.Count; index++)
            {
                RuntimeCatchConstraint constraint = constraints[index];
                RuntimeCatchWaypoint waypoint = new RuntimeCatchWaypoint();
                waypoint.Time = constraint.Time;
                waypoint.X = positions[index];
                waypoint.ViableWindow = reachable[index];
                waypoint.ObjectWindow = constraint.ObjectWindow;
                waypoint.IsSyntheticStart = constraint.IsSyntheticStart;
                waypoint.ArrivedByHyperDash = constraint.HyperSegmentTargetId >= 0;
                waypoint.DepartsByHyperDash = constraint.DepartsByHyperDash;
                waypoint.HyperSegmentSourceConstraintIndex = constraint.HyperSegmentSourceConstraintIndex;
                waypoint.HyperSegmentTargetId = constraint.HyperSegmentTargetId;
                if (constraint.HyperSegmentTargetId >= 0)
                    waypoint.HyperTargetX = byId[constraint.HyperSegmentTargetId].X;
                waypoint.OutgoingHyperTargetId = constraint.OutgoingHyperTargetId;
                if (constraint.OutgoingHyperTargetId >= 0)
                    waypoint.OutgoingHyperTargetX = byId[constraint.OutgoingHyperTargetId].X;
                waypoint.ObjectIds = constraint.ObjectIds;
                waypoints.Add(waypoint);
            }

            List<RuntimeCatchControlPhase> controls = BuildControls(waypoints, options.Style);
            RuntimeCatchPlan plan = new RuntimeCatchPlan();
            plan.MapPath = mapPath ?? String.Empty;
            plan.Objects = objects;
            plan.Constraints = constraints;
            plan.Waypoints = waypoints;
            plan.Controls = controls;
            plan.CatcherWidth = catcherWidth;
            plan.CollisionRadius = collisionRadius;
            plan.SafetyMargin = options.SafetyMargin;
            plan.LocalSafetyTarget = Math.Max(options.SafetyMargin, PreferredLocalSafety);
            CountObjects(plan);
            CountControls(plan);
            return plan;
        }

        private static List<RuntimeCatchConstraint> BuildConstraints(
            List<RuntimeCatchObject> objects,
            double collisionRadius,
            AgentOptionsSnapshot options,
            int variationSeed)
        {
            List<RuntimeCatchObject> hard = new List<RuntimeCatchObject>();
            for (int index = 0; index < objects.Count; index++)
            {
                RuntimeCatchObject hitObject = objects[index];
                if (hitObject.Kind == RuntimeCatchObjectKind.Fruit
                    || hitObject.Kind == RuntimeCatchObjectKind.Droplet
                    || options.IncludeTinyDropletsAsHardConstraints
                        && hitObject.Kind == RuntimeCatchObjectKind.TinyDroplet)
                {
                    hard.Add(hitObject);
                }
            }
            if (hard.Count == 0)
                throw new InvalidOperationException("The runtime object list has no Catch constraints yet.");

            double radius = collisionRadius - options.SafetyMargin;
            List<RuntimeCatchConstraint> result = new List<RuntimeCatchConstraint>();
            RuntimeCatchConstraint start = new RuntimeCatchConstraint();
            start.Index = 0;
            start.Time = Math.Min(0, hard[0].Time - 1000);
            start.ObjectWindow = CatchInterval.Point(256.0);
            start.PreferredX = 256.0;
            start.ObjectIds = new int[0];
            start.IsSyntheticStart = true;
            result.Add(start);

            Random random = new Random(variationSeed);
            int cursor = 0;
            int lastTime = hard[hard.Count - 1].Time;
            int firstTime = hard[0].Time;
            while (cursor < hard.Count)
            {
                int time = hard[cursor].Time;
                int end = cursor + 1;
                while (end < hard.Count && hard[end].Time == time) end++;

                CatchInterval window = new CatchInterval(0.0, 512.0);
                double weightedX = 0.0;
                double totalWeight = 0.0;
                int[] ids = new int[end - cursor];
                for (int index = cursor; index < end; index++)
                {
                    RuntimeCatchObject hitObject = hard[index];
                    window = window.Intersect(new CatchInterval(
                        Math.Max(0.0, hitObject.X - radius),
                        Math.Min(512.0, hitObject.X + radius)));
                    double weight = ObjectWeight(hitObject.Kind);
                    weightedX += hitObject.X * weight;
                    totalWeight += weight;
                    ids[index - cursor] = hitObject.Id;
                }

                RuntimeCatchConstraint constraint = new RuntimeCatchConstraint();
                constraint.Index = result.Count;
                constraint.Time = time;
                constraint.ObjectWindow = window;
                constraint.ObjectIds = ids;
                constraint.IsSyntheticStart = false;
                EnsureNotEmpty(window, constraint, "simultaneous catch windows do not overlap");

                double preferred = totalWeight > 0.0 ? weightedX / totalWeight : window.Midpoint;
                double progress = lastTime <= firstTime
                    ? 0.0
                    : (time - firstTime) / (double)(lastTime - firstTime);
                double fatigueScale = options.FatigueEnabled ? 1.0 + 0.65 * progress * progress : 1.0;
                double noise = NextGaussianLike(random);
                preferred += noise * options.WanderPixels * fatigueScale;
                constraint.PreferredX = window.Clamp(preferred);
                result.Add(constraint);
                cursor = end;
            }
            return result;
        }

        private static void LinkHyperDashConstraints(
            List<RuntimeCatchConstraint> constraints,
            Dictionary<int, RuntimeCatchObject> byId,
            CatchInterval[] effectiveWindows)
        {
            Dictionary<int, int> constraintByObjectId = new Dictionary<int, int>();
            for (int index = 1; index < constraints.Count; index++)
            {
                for (int objectIndex = 0; objectIndex < constraints[index].ObjectIds.Length; objectIndex++)
                    constraintByObjectId[constraints[index].ObjectIds[objectIndex]] = index;
            }

            for (int sourceIndex = 1; sourceIndex < constraints.Count; sourceIndex++)
            {
                int targetId = -1;
                RuntimeCatchConstraint sourceConstraint = constraints[sourceIndex];
                for (int objectIndex = 0; objectIndex < sourceConstraint.ObjectIds.Length; objectIndex++)
                {
                    RuntimeCatchObject source;
                    if (!byId.TryGetValue(sourceConstraint.ObjectIds[objectIndex], out source)
                        || source.HyperTargetId < 0) continue;
                    if (targetId >= 0 && targetId != source.HyperTargetId)
                        throw new InvalidOperationException("Multiple hyperdash targets leave the constraint at "
                            + sourceConstraint.Time + "ms.");
                    targetId = source.HyperTargetId;
                }
                if (targetId < 0) continue;

                RuntimeCatchObject target;
                int targetIndex;
                if (!byId.TryGetValue(targetId, out target)
                    || !constraintByObjectId.TryGetValue(targetId, out targetIndex)
                    || targetIndex <= sourceIndex)
                {
                    throw new InvalidOperationException("Hyperdash target " + targetId
                        + " is not a later hard constraint.");
                }

                sourceConstraint.DepartsByHyperDash = true;
                sourceConstraint.OutgoingHyperTargetId = targetId;
                constraints[targetIndex].ForcedHyperTargetId = targetId;

                double arrivalTime = Math.Max(sourceConstraint.Time + 1.0,
                    target.Time - HyperArrivalLead);
                CatchInterval postArrivalReachable = CatchInterval.Point(target.X);
                double postArrivalTime = arrivalTime;
                for (int index = sourceIndex + 1; index <= targetIndex; index++)
                {
                    if (constraints[index].Time + Epsilon < arrivalTime)
                        continue;
                    double delta = constraints[index].Time - postArrivalTime;
                    postArrivalReachable = effectiveWindows[index].Intersect(
                        postArrivalReachable.Expand(DashSpeed * delta));
                    EnsureNotEmpty(
                        postArrivalReachable,
                        constraints[index],
                        "post-arrival hyperdash departure cannot remain inside object windows");
                    postArrivalTime = constraints[index].Time;
                }
                effectiveWindows[targetIndex] = postArrivalReachable;

                for (int index = sourceIndex + 1; index <= targetIndex; index++)
                {
                    if (constraints[index].HyperSegmentTargetId >= 0
                        && constraints[index].HyperSegmentTargetId != targetId)
                    {
                        throw new InvalidOperationException("Overlapping hyperdash segments meet at "
                            + constraints[index].Time + "ms.");
                    }
                    constraints[index].HyperSegmentTargetId = targetId;
                    constraints[index].HyperSegmentSourceConstraintIndex = sourceIndex;
                }

                CatchInterval sourceWindow = effectiveWindows[sourceIndex];
                double travelDuration = arrivalTime - sourceConstraint.Time;
                for (int index = sourceIndex + 1; index <= targetIndex; index++)
                {
                    if (constraints[index].Time + Epsilon >= arrivalTime)
                        continue;
                    double amount = (constraints[index].Time - sourceConstraint.Time) / travelDuration;
                    amount = Math.Max(0.0, Math.Min(1.0, amount));
                    double sourceWeight = 1.0 - amount;
                    CatchInterval requiredSourceWindow = new CatchInterval(
                        (effectiveWindows[index].Minimum - amount * target.X) / sourceWeight,
                        (effectiveWindows[index].Maximum - amount * target.X) / sourceWeight);
                    sourceWindow = sourceWindow.Intersect(requiredSourceWindow);
                }
                effectiveWindows[sourceIndex] = sourceWindow;
                EnsureNotEmpty(sourceWindow, sourceConstraint,
                    "no source position keeps the complete hyperdash segment inside its object windows");
            }
        }

        private static CatchInterval[] PropagateBackward(
            List<RuntimeCatchConstraint> constraints,
            CatchInterval[] windows)
        {
            CatchInterval[] backward = new CatchInterval[constraints.Count];
            backward[backward.Length - 1] = windows[windows.Length - 1];
            EnsureNotEmpty(backward[backward.Length - 1], constraints[constraints.Count - 1], "empty final window");
            for (int index = constraints.Count - 2; index >= 0; index--)
            {
                if (constraints[index + 1].HyperSegmentTargetId >= 0)
                {
                    // Once the source is caught, stable computes a speed that lands
                    // at the target centre one frame early.  The source window itself
                    // is therefore the complete predecessor set.
                    backward[index] = windows[index];
                }
                else
                {
                    int delta = constraints[index + 1].Time - constraints[index].Time;
                    if (delta < 0) throw new InvalidOperationException("Catch constraints are not time ordered.");
                    backward[index] = windows[index].Intersect(backward[index + 1].Expand(DashSpeed * delta));
                }
                EnsureNotEmpty(backward[index], constraints[index], "backward viability set is empty");
            }
            return backward;
        }

        private static CatchInterval[] PropagateForward(
            List<RuntimeCatchConstraint> constraints,
            CatchInterval[] backward)
        {
            CatchInterval[] reachable = new CatchInterval[constraints.Count];
            reachable[0] = backward[0].Intersect(CatchInterval.Point(256.0));
            EnsureNotEmpty(reachable[0], constraints[0], "initial catcher position cannot reach the map");
            for (int index = 1; index < constraints.Count; index++)
            {
                if (constraints[index].HyperSegmentTargetId >= 0)
                {
                    reachable[index] = backward[index];
                }
                else
                {
                    int delta = constraints[index].Time - constraints[index - 1].Time;
                    reachable[index] = backward[index].Intersect(reachable[index - 1].Expand(DashSpeed * delta));
                }
                EnsureNotEmpty(reachable[index], constraints[index], "forward reachable set is empty");
            }
            return reachable;
        }

        private static double[] SelectFeasibleTrajectory(
            List<RuntimeCatchConstraint> constraints,
            CatchInterval[] backward,
            CatchInterval[] reachable)
        {
            double[] positions = new double[constraints.Count];
            positions[0] = 256.0;
            for (int index = 1; index < constraints.Count; index++)
            {
                CatchInterval available;
                if (constraints[index].HyperSegmentTargetId >= 0)
                {
                    available = backward[index];
                }
                else
                {
                    int delta = constraints[index].Time - constraints[index - 1].Time;
                    available = backward[index].Intersect(new CatchInterval(
                        positions[index - 1] - DashSpeed * delta,
                        positions[index - 1] + DashSpeed * delta));
                }
                available = available.Intersect(reachable[index]);
                EnsureNotEmpty(available, constraints[index], "greedy feasible selection failed");
                positions[index] = available.Clamp(constraints[index].PreferredX);
            }
            return positions;
        }

        private static void SmoothTrajectory(
            List<RuntimeCatchConstraint> constraints,
            CatchInterval[] reachable,
            double[] positions,
            AgentOptionsSnapshot options)
        {
            double centreWeight;
            double smoothnessWeight;
            double playfieldWeight;
            double localInset = Math.Max(0.0, PreferredLocalSafety - options.SafetyMargin);
            int passes;
            switch (options.Style)
            {
                case CatchPathStyle.Centered:
                    centreWeight = 0.85;
                    smoothnessWeight = 0.32;
                    playfieldWeight = 0.008;
                    passes = 32;
                    break;
                case CatchPathStyle.Lively:
                    centreWeight = 0.24;
                    smoothnessWeight = 0.82;
                    playfieldWeight = 0.012;
                    passes = 48;
                    break;
                case CatchPathStyle.LastMoment:
                    centreWeight = 0.36;
                    smoothnessWeight = 0.72;
                    playfieldWeight = 0.010;
                    passes = 40;
                    break;
                default:
                    centreWeight = 0.16;
                    smoothnessWeight = 1.0;
                    playfieldWeight = 0.015;
                    passes = 48;
                    break;
            }

            for (int pass = 0; pass < passes; pass++)
            {
                SmoothSweep(1, constraints.Count - 1, 1, constraints, reachable, positions,
                    centreWeight, smoothnessWeight, playfieldWeight, localInset);
                SmoothSweep(constraints.Count - 2, 0, -1, constraints, reachable, positions,
                    centreWeight, smoothnessWeight, playfieldWeight, localInset);
            }
        }

        private static void SmoothSweep(
            int start,
            int endExclusive,
            int step,
            List<RuntimeCatchConstraint> constraints,
            CatchInterval[] reachable,
            double[] positions,
            double centreWeight,
            double smoothnessWeight,
            double playfieldWeight,
            double localInset)
        {
            for (int index = start; index != endExclusive; index += step)
            {
                if (index <= 0 || constraints[index].HyperSegmentTargetId >= 0) continue;

                CatchInterval allowed = reachable[index];
                int previousDelta = constraints[index].Time - constraints[index - 1].Time;
                allowed = allowed.Intersect(new CatchInterval(
                    positions[index - 1] - DashSpeed * previousDelta,
                    positions[index - 1] + DashSpeed * previousDelta));
                if (index + 1 < constraints.Count
                    && constraints[index + 1].HyperSegmentTargetId < 0)
                {
                    int nextDelta = constraints[index + 1].Time - constraints[index].Time;
                    allowed = allowed.Intersect(new CatchInterval(
                        positions[index + 1] - DashSpeed * nextDelta,
                        positions[index + 1] + DashSpeed * nextDelta));
                }
                if (allowed.IsEmpty) continue;

                // A single global margin is necessarily dictated by the hardest
                // transition in the map.  Most objects have considerably more
                // room.  Prefer the locally inset window whenever the current
                // neighbour positions can reach it; fall back to the proven
                // global viability tube only for genuinely tight transitions.
                // Repeated forward/backward sweeps let adjacent waypoints move
                // together instead of sacrificing every object to one bottleneck.
                if (localInset > Epsilon)
                {
                    CatchInterval locallySafe = allowed.Intersect(new CatchInterval(
                        constraints[index].ObjectWindow.Minimum + localInset,
                        constraints[index].ObjectWindow.Maximum - localInset));
                    if (!locallySafe.IsEmpty) allowed = locallySafe;
                }

                if (constraints[index].DepartsByHyperDash)
                {
                    // A direction key is pre-armed shortly before this source is
                    // caught.  Keep the source near its object-centred preference
                    // so that the pre-arm movement has real collision headroom.
                    positions[index] = allowed.Clamp(constraints[index].PreferredX);
                    continue;
                }

                double smoothTarget;
                if (index + 1 < constraints.Count)
                {
                    int totalDelta = constraints[index + 1].Time - constraints[index - 1].Time;
                    double fraction = totalDelta <= 0
                        ? 0.5
                        : (constraints[index].Time - constraints[index - 1].Time) / (double)totalDelta;
                    smoothTarget = positions[index - 1]
                        + (positions[index + 1] - positions[index - 1]) * fraction;
                }
                else smoothTarget = positions[index - 1];

                double totalWeight = centreWeight + smoothnessWeight + playfieldWeight;
                double desired = (
                    constraints[index].PreferredX * centreWeight
                    + smoothTarget * smoothnessWeight
                    + 256.0 * playfieldWeight) / totalWeight;
                positions[index] = allowed.Clamp(desired);
            }
        }

        private static void ProjectHyperDashSegments(
            List<RuntimeCatchConstraint> constraints,
            Dictionary<int, RuntimeCatchObject> byId,
            CatchInterval[] effectiveWindows,
            double[] positions)
        {
            for (int sourceIndex = 1; sourceIndex < constraints.Count; sourceIndex++)
            {
                int targetId = constraints[sourceIndex].OutgoingHyperTargetId;
                if (targetId < 0) continue;

                RuntimeCatchObject target = byId[targetId];
                double arrivalTime = Math.Max(constraints[sourceIndex].Time + 1.0,
                    target.Time - HyperArrivalLead);
                double travelDuration = arrivalTime - constraints[sourceIndex].Time;
                List<int> postArrivalIndexes = new List<int>();
                for (int index = sourceIndex + 1;
                    index < constraints.Count
                        && constraints[index].HyperSegmentSourceConstraintIndex == sourceIndex;
                    index++)
                {
                    if (constraints[index].Time + Epsilon >= arrivalTime)
                    {
                        postArrivalIndexes.Add(index);
                        continue;
                    }
                    double amount = (constraints[index].Time - constraints[sourceIndex].Time)
                        / travelDuration;
                    amount = Math.Max(0.0, Math.Min(1.0, amount));
                    positions[index] = positions[sourceIndex]
                        + (target.X - positions[sourceIndex]) * amount;
                    EnsureContains(effectiveWindows[index], positions[index], constraints[index],
                        "projected hyperdash trajectory left an object window");
                }

                if (postArrivalIndexes.Count == 0)
                {
                    throw new InvalidOperationException("Hyperdash segment from "
                        + constraints[sourceIndex].Time + "ms has no target constraint.");
                }

                CatchInterval[] postBackward = new CatchInterval[postArrivalIndexes.Count];
                int last = postArrivalIndexes.Count - 1;
                int targetIndex = postArrivalIndexes[last];
                postBackward[last] = effectiveWindows[targetIndex].Intersect(
                    CatchInterval.Point(positions[targetIndex]));
                EnsureNotEmpty(
                    postBackward[last],
                    constraints[targetIndex],
                    "selected hyperdash departure target left its reachable window");
                for (int post = last - 1; post >= 0; post--)
                {
                    int index = postArrivalIndexes[post];
                    int nextIndex = postArrivalIndexes[post + 1];
                    int delta = constraints[nextIndex].Time - constraints[index].Time;
                    postBackward[post] = effectiveWindows[index].Intersect(
                        postBackward[post + 1].Expand(DashSpeed * delta));
                    EnsureNotEmpty(
                        postBackward[post],
                        constraints[index],
                        "post-arrival hyperdash path cannot reach the selected target position");
                }

                double previousTime = arrivalTime;
                double previousX = target.X;
                for (int post = 0; post < postArrivalIndexes.Count; post++)
                {
                    int index = postArrivalIndexes[post];
                    double delta = constraints[index].Time - previousTime;
                    CatchInterval available = postBackward[post].Intersect(new CatchInterval(
                        previousX - DashSpeed * delta,
                        previousX + DashSpeed * delta));
                    EnsureNotEmpty(
                        available,
                        constraints[index],
                        "post-arrival hyperdash path is unreachable from the target centre");
                    positions[index] = available.Clamp(constraints[index].PreferredX);
                    previousTime = constraints[index].Time;
                    previousX = positions[index];
                }
            }
        }

        private static List<RuntimeCatchControlPhase> BuildControls(
            List<RuntimeCatchWaypoint> waypoints,
            CatchPathStyle style)
        {
            List<RuntimeCatchControlPhase> controls = new List<RuntimeCatchControlPhase>();
            for (int index = 1; index < waypoints.Count; index++)
            {
                RuntimeCatchWaypoint previous = waypoints[index - 1];
                RuntimeCatchWaypoint current = waypoints[index];
                if (current.ArrivedByHyperDash)
                {
                    int sourceIndex = current.HyperSegmentSourceConstraintIndex;
                    if (sourceIndex != index - 1)
                        throw new InvalidOperationException("Hyperdash segment begins without its source at "
                            + current.Time + "ms.");
                    int targetIndex = index;
                    while (targetIndex + 1 < waypoints.Count
                        && waypoints[targetIndex + 1].HyperSegmentSourceConstraintIndex == sourceIndex)
                    {
                        targetIndex++;
                    }

                    RuntimeCatchWaypoint source = waypoints[sourceIndex];
                    RuntimeCatchWaypoint target = waypoints[targetIndex];
                    double targetCentre = target.HyperTargetX;
                    double hyperDisplacement = targetCentre - source.X;
                    double hyperDistance = Math.Abs(hyperDisplacement);
                    double travelEnd = Math.Max(source.Time + 1.0,
                        target.Time - HyperArrivalLead);
                    if (hyperDistance <= Epsilon)
                    {
                        AddPhase(controls, source.Time, travelEnd, source.X, targetCentre,
                            CatchInputIntent.Idle, 0.0);
                    }
                    else
                    {
                        AddPhase(controls, source.Time, travelEnd, source.X, targetCentre,
                            hyperDisplacement < 0
                                ? CatchInputIntent.HyperDashLeft
                                : CatchInputIntent.HyperDashRight,
                            hyperDistance / Math.Max(1.0, travelEnd - source.Time));
                    }

                    double departureTime = travelEnd;
                    double departureX = targetCentre;
                    for (int departureIndex = index;
                        departureIndex <= targetIndex;
                        departureIndex++)
                    {
                        RuntimeCatchWaypoint departure = waypoints[departureIndex];
                        if (departure.Time + Epsilon < travelEnd)
                            continue;
                        AddOrdinaryPhases(
                            controls,
                            departureTime,
                            departure.Time,
                            departureX,
                            departure.X,
                            style,
                            departureIndex);
                        departureTime = departure.Time;
                        departureX = departure.X;
                    }
                    index = targetIndex;
                    continue;
                }

                AddOrdinaryPhases(
                    controls,
                    previous.Time,
                    current.Time,
                    previous.X,
                    current.X,
                    style,
                    index);
            }
            return controls;
        }

        private static void AddOrdinaryPhases(
            List<RuntimeCatchControlPhase> controls,
            double startTime,
            double endTime,
            double startX,
            double endX,
            CatchPathStyle style,
            int styleIndex)
        {
            double duration = endTime - startTime;
            double displacement = endX - startX;
            double distance = Math.Abs(displacement);
            if (duration <= Epsilon)
            {
                if (distance > Epsilon)
                    throw new InvalidOperationException("Non-zero Catch movement at equal timestamps.");
                return;
            }

            if (distance <= Epsilon)
            {
                AddPhase(controls, startTime, endTime, startX, endX,
                    CatchInputIntent.Idle, 0.0);
                return;
            }

            int direction = Math.Sign(displacement);
            if (distance <= WalkSpeed * duration + Epsilon)
            {
                double walkDuration = distance / WalkSpeed;
                double slack = Math.Max(0.0, duration - walkDuration);
                double slackBefore;
                if (style == CatchPathStyle.LastMoment) slackBefore = slack;
                else if (style == CatchPathStyle.Lively)
                    slackBefore = slack * (styleIndex % 2 == 0 ? 0.35 : 0.65);
                else slackBefore = slack * 0.5;
                double movementStart = startTime + slackBefore;
                AddPhase(controls, startTime, movementStart, startX, startX,
                    CatchInputIntent.Idle, 0.0);
                AddPhase(controls, movementStart, movementStart + walkDuration, startX, endX,
                    DirectionalIntent(direction, false, false), WalkSpeed);
                AddPhase(controls, movementStart + walkDuration, endTime, endX, endX,
                    CatchInputIntent.Idle, 0.0);
                return;
            }

            double dashDuration = Math.Max(0.0, Math.Min(duration, 2.0 * distance - duration));
            double walkDurationTotal = duration - dashDuration;
            double walkBefore;
            if (style == CatchPathStyle.LastMoment) walkBefore = walkDurationTotal;
            else if (style == CatchPathStyle.Lively)
                walkBefore = walkDurationTotal * (styleIndex % 2 == 0 ? 0.30 : 0.70);
            else walkBefore = walkDurationTotal * 0.5;
            double afterFirstWalkX = startX + direction * WalkSpeed * walkBefore;
            double afterDashX = afterFirstWalkX + direction * DashSpeed * dashDuration;
            AddPhase(controls, startTime, startTime + walkBefore,
                startX, afterFirstWalkX, DirectionalIntent(direction, false, false), WalkSpeed);
            AddPhase(controls, startTime + walkBefore, startTime + walkBefore + dashDuration,
                afterFirstWalkX, afterDashX, DirectionalIntent(direction, true, false), DashSpeed);
            AddPhase(controls, startTime + walkBefore + dashDuration, endTime,
                afterDashX, endX, DirectionalIntent(direction, false, false), WalkSpeed);
        }

        private static void AddPhase(
            List<RuntimeCatchControlPhase> controls,
            double startTime,
            double endTime,
            double startX,
            double endX,
            CatchInputIntent input,
            double speed)
        {
            if (endTime - startTime <= Epsilon) return;
            RuntimeCatchControlPhase phase = new RuntimeCatchControlPhase();
            phase.StartTime = startTime;
            phase.EndTime = endTime;
            phase.StartX = startX;
            phase.EndX = endX;
            phase.Input = input;
            phase.NominalSpeed = speed;
            controls.Add(phase);
        }

        private static CatchInputIntent DirectionalIntent(int direction, bool dash, bool hyper)
        {
            if (direction < 0)
                return hyper ? CatchInputIntent.HyperDashLeft
                    : dash ? CatchInputIntent.DashLeft : CatchInputIntent.WalkLeft;
            return hyper ? CatchInputIntent.HyperDashRight
                : dash ? CatchInputIntent.DashRight : CatchInputIntent.WalkRight;
        }

        private static void VerifyTrajectory(
            List<RuntimeCatchConstraint> constraints,
            CatchInterval[] effectiveWindows,
            double[] positions)
        {
            for (int index = 0; index < constraints.Count; index++)
            {
                if (!effectiveWindows[index].Contains(positions[index], 0.00001))
                    throw new InvalidOperationException("Trajectory left object window at " + constraints[index].Time + "ms.");
                if (index == 0 || constraints[index].HyperSegmentTargetId >= 0) continue;
                double distance = Math.Abs(positions[index] - positions[index - 1]);
                double available = constraints[index].Time - constraints[index - 1].Time;
                if (distance > DashSpeed * available + 0.00001)
                    throw new InvalidOperationException("Trajectory exceeded dash speed at " + constraints[index].Time + "ms.");
            }
        }

        private static void CountObjects(RuntimeCatchPlan plan)
        {
            for (int index = 0; index < plan.Objects.Count; index++)
            {
                RuntimeCatchObject hitObject = plan.Objects[index];
                if (hitObject.Kind == RuntimeCatchObjectKind.Fruit) plan.FruitCount++;
                else if (hitObject.Kind == RuntimeCatchObjectKind.Droplet) plan.DropletCount++;
                else if (hitObject.Kind == RuntimeCatchObjectKind.TinyDroplet) plan.TinyDropletCount++;
                else if (hitObject.Kind == RuntimeCatchObjectKind.Banana) plan.BananaCount++;
                if (hitObject.HyperTargetId >= 0) plan.HyperDashCount++;
            }
        }

        private static void CountControls(RuntimeCatchPlan plan)
        {
            for (int index = 0; index < plan.Controls.Count; index++)
            {
                RuntimeCatchControlPhase phase = plan.Controls[index];
                double duration = Math.Max(0.0, phase.EndTime - phase.StartTime);
                if (phase.IsDash) plan.PlannedDashMilliseconds += duration;
                if (phase.IsHyperDash) plan.PlannedHyperDashMilliseconds += duration;
            }
        }

        private static Dictionary<int, RuntimeCatchObject> IndexObjects(List<RuntimeCatchObject> objects)
        {
            Dictionary<int, RuntimeCatchObject> result = new Dictionary<int, RuntimeCatchObject>();
            for (int index = 0; index < objects.Count; index++) result.Add(objects[index].Id, objects[index]);
            return result;
        }

        private static int CompareObjects(RuntimeCatchObject left, RuntimeCatchObject right)
        {
            int time = left.Time.CompareTo(right.Time);
            return time != 0 ? time : left.Id.CompareTo(right.Id);
        }

        private static double ObjectWeight(RuntimeCatchObjectKind kind)
        {
            if (kind == RuntimeCatchObjectKind.Fruit) return 1.0;
            if (kind == RuntimeCatchObjectKind.Droplet) return 0.72;
            if (kind == RuntimeCatchObjectKind.TinyDroplet) return 0.20;
            return 0.0;
        }

        private static double NextGaussianLike(Random random)
        {
            // Irwin-Hall(6), normalised to approximately N(0, 1), without a
            // logarithm/square-root tail that could create an extreme waypoint.
            double sum = 0.0;
            for (int index = 0; index < 6; index++) sum += random.NextDouble();
            return (sum - 3.0) / Math.Sqrt(0.5);
        }

        private static void EnsureNotEmpty(
            CatchInterval interval,
            RuntimeCatchConstraint constraint,
            string reason)
        {
            if (!interval.IsEmpty) return;
            throw new InvalidOperationException(reason + " at " + constraint.Time
                + "ms (constraint " + constraint.Index + ").");
        }

        private static void EnsureContains(
            CatchInterval interval,
            double value,
            RuntimeCatchConstraint constraint,
            string reason)
        {
            if (interval.Contains(value, 0.00001)) return;
            throw new InvalidOperationException(reason + " at " + constraint.Time
                + "ms (constraint " + constraint.Index + ").");
        }
    }
}
