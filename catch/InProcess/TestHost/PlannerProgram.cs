using System;
using System.Collections.Generic;
using LocalCatchAgent.Plugin;

internal static class PlannerProgram
{
    private static int Main()
    {
        try
        {
            AgentOptionsSnapshot options = new AgentOptionsSnapshot(
                true,
                CatchPathStyle.Lively,
                1.0,
                8,
                0.75,
                true,
                true,
                true);

            List<RuntimeCatchObject> objects = new List<RuntimeCatchObject>();
            objects.Add(Object(1, 1000, 256, RuntimeCatchObjectKind.Fruit, -1));
            objects.Add(Object(2, 1500, 400, RuntimeCatchObjectKind.Fruit, 3));
            objects.Add(Object(3, 1650, 100, RuntimeCatchObjectKind.Fruit, -1));
            objects.Add(Object(4, 1900, 160, RuntimeCatchObjectKind.TinyDroplet, -1));
            objects.Add(Object(5, 2100, 245, RuntimeCatchObjectKind.Droplet, -1));
            objects.Add(Object(6, 2300, 330, RuntimeCatchObjectKind.TinyDroplet, -1));
            objects.Add(Object(7, 2500, 410, RuntimeCatchObjectKind.Fruit, -1));

            RuntimeCatchPlan first = RuntimeCatchPlanner.Build(objects, 106.75, options, 1337, "synthetic.osu");
            RuntimeCatchPlan second = RuntimeCatchPlanner.Build(objects, 106.75, options, 1337, "synthetic.osu");
            Assert(first.Waypoints.Count == 8, "unexpected waypoint count");
            Assert(first.HyperDashCount == 1, "hyperdash link was not retained");
            Assert(first.TinyDropletCount == 2, "tiny droplets were not retained");
            Assert(first.Waypoints[3].ArrivedByHyperDash, "hyperdash target was not forced");
            Assert(Math.Abs(first.Waypoints[3].X - 100.0) < 0.0001, "hyperdash target is not centred");

            for (int index = 0; index < first.Waypoints.Count; index++)
            {
                RuntimeCatchWaypoint waypoint = first.Waypoints[index];
                Assert(waypoint.ObjectWindow.Contains(waypoint.X, 0.0001),
                    "waypoint escaped object window at " + waypoint.Time);
                Assert(Math.Abs(waypoint.X - second.Waypoints[index].X) < 0.0000001,
                    "repeatable variation changed between builds");
                if (index == 0 || waypoint.ArrivedByHyperDash) continue;
                RuntimeCatchWaypoint previous = first.Waypoints[index - 1];
                Assert(Math.Abs(waypoint.X - previous.X) <= waypoint.Time - previous.Time + 0.0001,
                    "waypoint exceeded dash speed at " + waypoint.Time);
            }

            for (int time = first.Waypoints[0].Time; time <= first.LastObjectTime; time += 7)
            {
                double reference = first.ReferenceX(time);
                Assert(reference >= -0.001 && reference <= 512.001, "reference left playfield");
            }

            Console.WriteLine("CATCH NET40 PLANNER: PASS");
            Console.WriteLine("objects=" + first.Objects.Count
                + ", constraints=" + (first.Constraints.Count - 1)
                + ", phases=" + first.Controls.Count
                + ", hyper=" + first.HyperDashCount);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static RuntimeCatchObject Object(
        int id,
        int time,
        double x,
        RuntimeCatchObjectKind kind,
        int hyperTargetId)
    {
        RuntimeCatchObject result = new RuntimeCatchObject();
        result.Id = id;
        result.Time = time;
        result.X = x;
        result.Kind = kind;
        result.HyperTargetId = hyperTargetId;
        result.Source = new object();
        return result;
    }

    private static void Assert(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
