using System;
using LocalManiaAuto.Plugin;

namespace LocalManiaAuto.LivePlanTest
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            bool printAll = args.Length == 3 && args[2] == "--all";
            if (args.Length < 1 || args.Length > 3 || (args.Length == 3 && !printAll))
            {
                Console.Error.WriteLine(
                    "usage: LocalManiaAuto.LivePlanTest.exe <map.osu> [tap-ms] [--all]");
                return 2;
            }

            try
            {
                int tapMilliseconds = args.Length >= 2 ? Int32.Parse(args[1]) : 8;
                LiveManiaPlan plan = LivePlanBuilder.ParseAndBuild(args[0], tapMilliseconds);
                int transitionCount = 0;
                for (int index = 0; index < plan.Batches.Count; index++)
                    transitionCount += plan.Batches[index].Transitions.Count;

                Console.WriteLine("keys=" + plan.KeyCount
                    + " objects=" + plan.ObjectCount
                    + " batches=" + plan.Batches.Count
                    + " transitions=" + transitionCount
                    + " first=" + plan.FirstObjectTime
                    + " last=" + plan.LastObjectTime);

                int emitted = 0;
                int limit = printAll ? Int32.MaxValue : 12;
                for (int batchIndex = 0; batchIndex < plan.Batches.Count && emitted < limit; batchIndex++)
                {
                    LiveTransitionBatch batch = plan.Batches[batchIndex];
                    for (int transitionIndex = 0;
                        transitionIndex < batch.Transitions.Count && emitted < limit;
                        transitionIndex++)
                    {
                        LiveLaneTransition transition = batch.Transitions[transitionIndex];
                        Console.WriteLine(transition.Time + ","
                            + (transition.Lane + 1) + ","
                            + (transition.IsDown ? "down" : "up") + ","
                            + transition.SourceLine);
                        emitted++;
                    }
                }

                for (int index = 0; index < plan.Warnings.Count; index++)
                    Console.Error.WriteLine("warning: " + plan.Warnings[index]);
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                return 1;
            }
        }
    }
}
