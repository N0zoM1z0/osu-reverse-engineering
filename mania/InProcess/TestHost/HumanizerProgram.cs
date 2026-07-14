using System;
using System.Collections.Generic;
using LocalManiaAuto.Plugin;

namespace LocalManiaAuto.HumanizerTest
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try
            {
                if (args.Length > 1)
                {
                    Console.Error.WriteLine("usage: LocalManiaAuto.HumanizerTest.exe [map.osu]");
                    return 2;
                }
                if (args.Length == 1)
                {
                    RunRealMap(args[0]);
                    return 0;
                }

                LiveManiaPlan source = BuildSyntheticPlan();
                ValidateControlState();
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
                HumanizedPlanResult cleanResult = Humanizer.Apply(source, clean, 12345);
                Assert(Fingerprint(source) == Fingerprint(cleanResult.Plan),
                    "CLEAN with zero custom effects must preserve the exact timeline");

                AgentOptionsSnapshot human = new AgentOptionsSnapshot(
                    true,
                    HumanStyle.Human,
                    65,
                    -3,
                    20,
                    30,
                    10,
                    150,
                    FrameCadence.Hz240,
                    true,
                    2,
                    true);
                HumanizedPlanResult first = Humanizer.Apply(source, human, null);
                HumanizedPlanResult second = Humanizer.Apply(source, human, null);
                Assert(first.Seed == second.Seed, "repeatable variation changed seed");
                Assert(Fingerprint(first.Plan) == Fingerprint(second.Plan),
                    "repeatable variation changed timeline");
                ValidatePlan(first.Plan, CountTransitions(source));
                Assert(first.Grade200 > 0, "controlled 200 mix produced no 200s");
                Assert(first.Grade100 > 0, "dense controlled 100 mix produced no 100s");
                Assert(first.Grade50 == 0 && first.Miss == 0,
                    "miss guard allowed a 50 or miss");

                AgentOptionsSnapshot chaos = new AgentOptionsSnapshot(
                    true,
                    HumanStyle.Chaos,
                    120,
                    -8,
                    30,
                    60,
                    20,
                    200,
                    FrameCadence.Hz60,
                    true,
                    10,
                    false);
                HumanizedPlanResult chaosResult = Humanizer.Apply(source, chaos, 987654);
                ValidatePlan(chaosResult.Plan, CountTransitions(source));
                Assert(chaosResult.JammedPresses > 0, "100-note chaos sample produced no jammed press");
                Assert(chaosResult.StickyReleases > 0, "100-note chaos sample produced no sticky release");
                Assert(chaosResult.MaximumOffset > first.MaximumOffset,
                    "CHAOS did not realize a wider late offset than HUMAN");
                Assert(chaosResult.Grade50 == 0 && chaosResult.Miss == 0,
                    "CHAOS escaped the no-miss safety band");

                ValidateDistributionModel();

                Console.WriteLine("clean fingerprint=" + Fingerprint(cleanResult.Plan));
                Console.WriteLine("human " + first.Describe(human));
                Console.WriteLine("chaos " + chaosResult.Describe(chaos));
                Console.WriteLine("HUMANIZER TEST: PASS");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                return 1;
            }
        }

        private static void ValidateControlState()
        {
            AgentControlState controls = new AgentControlState(false);
            Assert(!controls.GetOptions().Enabled, "control state did not start in Player mode");
            Assert(controls.IsMenuVisible, "control menu did not start visible");
            controls.ToggleEnabled();
            Assert(controls.GetOptions().Enabled, "quick toggle did not enable agent");
            controls.MoveSelection(1);
            controls.AdjustSelected(1);
            Assert(controls.GetOptions().Style == HumanStyle.Tired,
                "profile row did not cycle HUMAN -> TIRED");
            controls.Disable();
            Assert(!controls.GetOptions().Enabled, "control disable did not restore Player mode");
        }

        private static void ValidateDistributionModel()
        {
            LiveManiaPlan coreSource = BuildSyntheticPlan();
            AgentOptionsSnapshot coreOptions = new AgentOptionsSnapshot(
                true,
                HumanStyle.Human,
                70,
                -4,
                25,
                0,
                0,
                0,
                FrameCadence.Native,
                false,
                0,
                true);
            HumanizedPlanResult core = Humanizer.Apply(coreSource, coreOptions, 24680);
            Assert(core.UnstableRate >= 65.0 && core.UnstableRate <= 75.0,
                "base UR calibration escaped 70 +/- 5: " + core.UnstableRate);
            Assert(core.RushedNotes > 0, "rush model produced no correlated early notes");
            Assert(core.Grade50 == 0 && core.Miss == 0,
                "core timing model escaped the miss guard");

            LiveManiaPlan densitySource = BuildDensityPlan();
            AgentOptionsSnapshot densityOptions = new AgentOptionsSnapshot(
                true,
                HumanStyle.Human,
                0,
                0,
                0,
                0,
                20,
                250,
                FrameCadence.Native,
                false,
                0,
                true);
            HumanizedPlanResult density = Humanizer.Apply(densitySource, densityOptions, 13579);
            Assert(density.DenseNotes > 500 && density.SparseNotes > 300,
                "density classifier did not retain both test populations");
            Assert(density.DenseGrade100 > 0 && density.SparseGrade100 > 0,
                "density test produced an empty 100 population");
            double denseRate = density.DenseGrade100 / (double)density.DenseNotes;
            double sparseRate = density.SparseGrade100 / (double)density.SparseNotes;
            Assert(denseRate > sparseRate * 3.0,
                "dense boost did not materially raise 100 rate: dense="
                    + denseRate + ", sparse=" + sparseRate);
            Assert(density.Grade50 == 0 && density.Miss == 0,
                "density-tail model escaped the miss guard");

            int[] timingMods = new int[] { 0, 0x2, 0x10, 0x40, 0x100, 0x10 | 0x40 };
            for (int index = 0; index < timingMods.Length; index++)
            {
                HumanizedPlanResult modded = Humanizer.Apply(
                    densitySource,
                    densityOptions,
                    timingMods[index],
                    97531 + index);
                Assert(modded.Grade50 == 0 && modded.Miss == 0,
                    "mod-adjusted judgement windows escaped miss guard: mods=0x"
                        + timingMods[index].ToString("X"));
            }
        }

        private static void RunRealMap(string path)
        {
            LiveManiaPlan source = LivePlanBuilder.ParseAndBuild(path, 8);
            int expectedTransitions = CountTransitions(source);
            string cleanFingerprint = null;
            for (int styleValue = 0; styleValue < 4; styleValue++)
            {
                HumanStyle style = (HumanStyle)styleValue;
                AgentOptionsSnapshot options = new AgentOptionsSnapshot(
                    true,
                    style,
                    style == HumanStyle.Clean ? 0 : 60 + styleValue * 20,
                    style == HumanStyle.Clean ? 0 : -2 - styleValue,
                    style == HumanStyle.Clean ? 0 : 10 + styleValue * 5,
                    style == HumanStyle.Clean ? 0 : styleValue * 10,
                    style == HumanStyle.Clean ? 0 : styleValue * 3,
                    style == HumanStyle.Clean ? 0 : 100 + styleValue * 25,
                    style == HumanStyle.Clean
                        ? FrameCadence.Native
                        : (FrameCadence)Math.Min(3, styleValue),
                    style == HumanStyle.Tired || style == HumanStyle.Chaos,
                    styleValue == 0 ? 0 : Math.Min(10, styleValue * 2),
                    true);
                HumanizedPlanResult result = Humanizer.Apply(
                    source,
                    options,
                    10000 + styleValue);
                ValidatePlan(result.Plan, expectedTransitions);
                Assert(result.Grade50 == 0 && result.Miss == 0,
                    "real-map profile escaped the no-miss safety band");
                if (style == HumanStyle.Clean)
                {
                    cleanFingerprint = Fingerprint(result.Plan);
                    Assert(cleanFingerprint == Fingerprint(source),
                        "real-map CLEAN timeline changed");
                }
            }
            AgentOptionsSnapshot defaultHuman = new AgentOptionsSnapshot(
                true,
                HumanStyle.Human,
                65,
                -4,
                20,
                10,
                2,
                125,
                FrameCadence.Hz240,
                false,
                1,
                true);
            HumanizedPlanResult defaultResult = Humanizer.Apply(
                source,
                defaultHuman,
                424242);
            ValidatePlan(defaultResult.Plan, expectedTransitions);
            Assert(defaultResult.Grade50 == 0 && defaultResult.Miss == 0,
                "default HUMAN escaped the no-miss safety band");
            Console.WriteLine("HUMAN-DIST\t" + defaultResult.Describe(defaultHuman));
            Console.WriteLine("PASS\t" + expectedTransitions + " transitions\t" + path);
        }

        private static LiveManiaPlan BuildSyntheticPlan()
        {
            List<LiveLaneTransition> transitions = new List<LiveLaneTransition>();
            const int objectCount = 160;
            for (int index = 0; index < objectCount; index++)
            {
                int lane = index % 4;
                int chord = index / 4;
                int down = 1000 + chord * 90;
                int duration = chord % 9 == 0 ? 70 : 8;
                int sourceLine = 100 + index;
                transitions.Add(new LiveLaneTransition(down, lane, true, sourceLine));
                transitions.Add(new LiveLaneTransition(down + duration, lane, false, sourceLine));
            }
            return BuildPlan(
                @"C:\synthetic\humanizer-test.osu",
                4,
                8.0,
                objectCount,
                transitions);
        }

        private static LiveManiaPlan BuildDensityPlan()
        {
            List<LiveLaneTransition> transitions = new List<LiveLaneTransition>();
            int sourceLine = 1000;
            int sparseCount = 600;
            for (int index = 0; index < sparseCount; index++)
            {
                int down = 1000 + index * 300;
                int lane = index % 4;
                transitions.Add(new LiveLaneTransition(down, lane, true, sourceLine));
                transitions.Add(new LiveLaneTransition(down + 8, lane, false, sourceLine));
                sourceLine++;
            }

            int denseStart = 1000 + sparseCount * 300 + 2000;
            int denseCount = 1200;
            for (int index = 0; index < denseCount; index++)
            {
                int chord = index / 4;
                int lane = index % 4;
                int down = denseStart + chord * 80;
                transitions.Add(new LiveLaneTransition(down, lane, true, sourceLine));
                transitions.Add(new LiveLaneTransition(down + 8, lane, false, sourceLine));
                sourceLine++;
            }

            return BuildPlan(
                @"C:\synthetic\density-test.osu",
                4,
                8.0,
                sparseCount + denseCount,
                transitions);
        }

        private static LiveManiaPlan BuildPlan(
            string path,
            int keyCount,
            double overallDifficulty,
            int objectCount,
            List<LiveLaneTransition> transitions)
        {
            transitions.Sort(delegate(LiveLaneTransition left, LiveLaneTransition right)
            {
                int byTime = left.Time.CompareTo(right.Time);
                if (byTime != 0)
                    return byTime;
                if (left.IsDown != right.IsDown)
                    return left.IsDown ? 1 : -1;
                return left.Lane.CompareTo(right.Lane);
            });

            List<LiveTransitionBatch> batches = new List<LiveTransitionBatch>();
            int transitionIndex = 0;
            while (transitionIndex < transitions.Count)
            {
                int time = transitions[transitionIndex].Time;
                List<LiveLaneTransition> batch = new List<LiveLaneTransition>();
                while (transitionIndex < transitions.Count && transitions[transitionIndex].Time == time)
                {
                    batch.Add(transitions[transitionIndex]);
                    transitionIndex++;
                }
                batches.Add(new LiveTransitionBatch(time, batch));
            }

            return new LiveManiaPlan(
                path,
                keyCount,
                overallDifficulty,
                objectCount,
                batches[0].Time,
                batches[batches.Count - 1].Time,
                batches,
                new List<string>());
        }

        private static void ValidatePlan(LiveManiaPlan plan, int expectedTransitions)
        {
            Assert(CountTransitions(plan) == expectedTransitions, "transition count changed");
            bool[] pressed = new bool[plan.KeyCount];
            int previousTime = Int32.MinValue;
            for (int batchIndex = 0; batchIndex < plan.Batches.Count; batchIndex++)
            {
                LiveTransitionBatch batch = plan.Batches[batchIndex];
                Assert(batch.Time >= previousTime, "batch order moved backwards");
                previousTime = batch.Time;
                for (int transitionIndex = 0; transitionIndex < batch.Transitions.Count; transitionIndex++)
                {
                    LiveLaneTransition transition = batch.Transitions[transitionIndex];
                    Assert(pressed[transition.Lane] != transition.IsDown,
                        "lane " + transition.Lane + " contains duplicate physical state");
                    pressed[transition.Lane] = transition.IsDown;
                }
            }
            for (int lane = 0; lane < pressed.Length; lane++)
                Assert(!pressed[lane], "lane " + lane + " remained held");
        }

        private static int CountTransitions(LiveManiaPlan plan)
        {
            int count = 0;
            for (int index = 0; index < plan.Batches.Count; index++)
                count += plan.Batches[index].Transitions.Count;
            return count;
        }

        private static string Fingerprint(LiveManiaPlan plan)
        {
            uint hash = 2166136261u;
            for (int batchIndex = 0; batchIndex < plan.Batches.Count; batchIndex++)
            {
                List<LiveLaneTransition> transitions = plan.Batches[batchIndex].Transitions;
                for (int transitionIndex = 0; transitionIndex < transitions.Count; transitionIndex++)
                {
                    LiveLaneTransition transition = transitions[transitionIndex];
                    hash = Mix(hash, transition.Time);
                    hash = Mix(hash, transition.Lane);
                    hash = Mix(hash, transition.IsDown ? 1 : 0);
                    hash = Mix(hash, transition.SourceLine);
                }
            }
            return hash.ToString("X8");
        }

        private static uint Mix(uint hash, int value)
        {
            unchecked
            {
                hash ^= (uint)value;
                hash *= 16777619u;
                return hash;
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException(message);
        }
    }
}
