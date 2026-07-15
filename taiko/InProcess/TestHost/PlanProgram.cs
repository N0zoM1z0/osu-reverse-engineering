using System;
using System.Collections.Generic;
using LocalTaikoAgent.Plugin;

internal static class PlanProgram
{
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length != 1)
            {
                Console.Error.WriteLine("usage: LocalTaikoAgent.PlanTest.exe <native-taiko.osu>");
                return 2;
            }

            LiveTaikoPlan source = LivePlanBuilder.ParseAndBuild(args[0], 8, 0);
            Assert(source.ObjectCount == 6, "object count");
            Assert(source.CircleCount == 4, "circle count");
            Assert(source.DrumRollCount == 1, "drumroll count");
            Assert(source.SpinnerCount == 1, "spinner count");
            Assert(source.Strikes.Count == 21, "native strike count");

            List<LiveTaikoStrike> circles = source.Strikes.FindAll(delegate(LiveTaikoStrike strike)
            {
                return strike.RequiredForCombo;
            });
            Assert(circles.Count == 4, "required circle strikes");
            Assert(circles[0].Keys.Length == 1
                && circles[0].Keys[0] == (int)LiveTaikoKey.InnerLeft, "first don hand");
            Assert(circles[1].Keys.Length == 1
                && circles[1].Keys[0] == (int)LiveTaikoKey.OuterRight, "second kat hand");
            Assert(circles[2].Keys.Length == 2 && circles[3].Keys.Length == 2, "strong circles");

            List<LiveTaikoStrike> spinner = source.Strikes.FindAll(delegate(LiveTaikoStrike strike)
            {
                return strike.Kind == LiveTaikoObjectKind.Spinner;
            });
            int[] expectedSpinner = new int[] { 3250, 3400, 3550, 3700, 3850 };
            Assert(spinner.Count == expectedSpinner.Length, "spinner hit count");
            for (int index = 0; index < spinner.Count; index++)
                Assert(spinner[index].Time == expectedSpinner[index], "spinner cadence " + index);

            AgentOptionsSnapshot clean = new AgentOptionsSnapshot(
                true,
                HumanStyle.Clean,
                0,
                0,
                0,
                0,
                0,
                0,
                FrameCadence.Native,
                false,
                0,
                true);
            HumanizedPlanResult result = Humanizer.Apply(source, clean, 0, 8, 12345);
            Assert(result.Miss == 0, "clean humanizer misses");
            Assert(result.Grade100 == 0, "clean humanizer 100s");
            Assert(result.Grade300 == 4, "clean humanizer 300s");
            Assert(result.Plan.Batches.Count > 0, "transition batches");
            Assert(Math.Abs(LivePlanBuilder.CalculateDrumRollInterval(7, 1.5, 600.0, 2.0) - 75.0)
                < 0.0001, "legacy v7 drumroll cadence");
            VerifyComboMetadataWinsExactInputCollision();

            Console.WriteLine("TAIKO IN-PROCESS PLAN TEST: PASS");
            Console.WriteLine("objects=" + source.ObjectCount
                + ", strikes=" + source.Strikes.Count
                + ", batches=" + source.Batches.Count
                + ", spinner-required=" + LivePlanBuilder.CalculateSpinnerRequiredHits(750, 5.0, 0));
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static void Assert(bool condition, string label)
    {
        if (!condition)
            throw new InvalidOperationException("assertion failed: " + label);
    }

    private static void VerifyComboMetadataWinsExactInputCollision()
    {
        List<LiveTaikoStrike> strikes = new List<LiveTaikoStrike>
        {
            new LiveTaikoStrike
            {
                Time = 1000,
                ReferenceTime = 1000,
                ObjectStart = 900,
                ObjectEnd = 1100,
                SourceLine = 10,
                Kind = LiveTaikoObjectKind.DrumRoll,
                RequiredForCombo = false,
                IsStrong = false,
                Keys = new int[] { (int)LiveTaikoKey.InnerLeft },
                KeyDelays = new int[] { 0 }
            },
            new LiveTaikoStrike
            {
                Time = 1000,
                ReferenceTime = 1000,
                ObjectStart = 1000,
                ObjectEnd = 1000,
                SourceLine = 11,
                Kind = LiveTaikoObjectKind.Circle,
                RequiredForCombo = true,
                IsStrong = false,
                Keys = new int[] { (int)LiveTaikoKey.InnerLeft },
                KeyDelays = new int[] { 0 }
            }
        };
        LiveTaikoPlan source = new LiveTaikoPlan(
            "synthetic-collision.osu",
            14,
            5.0,
            2,
            1,
            1,
            0,
            900,
            1100,
            strikes,
            new List<LiveTaikoTransitionBatch>(),
            new List<string>());
        LiveTaikoPlan rebuilt = LivePlanBuilder.Rebuild(source, strikes, 8);
        LiveTaikoTransition down = rebuilt.Batches[0].Transitions[0];
        Assert(down.Time == 1000 && down.IsDown, "coalesced transition identity");
        Assert(down.RequiredForCombo, "combo metadata wins bonus collision");
        Assert(down.Kind == LiveTaikoObjectKind.Circle && down.SourceLine == 11,
            "combo source metadata retained");
        Assert(rebuilt.Warnings.Count == 1
            && rebuilt.Warnings[0].IndexOf("combo-relevant metadata retained", StringComparison.Ordinal) >= 0,
            "collision warning");
    }
}
