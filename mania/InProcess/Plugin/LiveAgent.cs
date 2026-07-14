using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace LocalManiaAuto.Plugin
{
    internal sealed class LiveAgent
    {
        private const int CurrentBeatmapMethodToken = 0x06002C63;
        private const int BeatmapPathMethodToken = 0x06001BF0;
        private const int CurrentScoreFieldToken = 0x040013C3;
        private const int PlayerPausedFieldToken = 0x0400136A;
        private const int ReplayModeFieldToken = 0x04002A7C;
        private const int ReplayScoreFieldToken = 0x04002A7F;
        private const int SongClockFieldToken = 0x04002358;
        private const int ScoreInvalidateMethodToken = 0x06002B5A;
        private const int ScoreValidityFieldToken = 0x04001990;
        private const int GlobalOsuModeFieldToken = 0x04002C6D;

        private const int ManiaMode = 3;
        private const int GlobalOsuModePlay = 2;
        private const int RelaxBit = 0x80;
        private const int AutoplayBit = 0x800;
        private const int Relax2Bit = 0x2000;
        private const int CinemaBit = 0x400000;
        private const int ForbiddenAutomationMods = RelaxBit | AutoplayBit | Relax2Bit | CinemaBit;

        private const uint InputKeyboard = 1;
        private const uint KeyEventExtendedKey = 0x0001;
        private const uint KeyEventKeyUp = 0x0002;
        private const uint KeyEventScanCode = 0x0008;
        private const uint MapVkToScanCode = 0;
        private const uint MapVkToScanCodeEx = 4;
        private static readonly UIntPtr InjectionMarker = new UIntPtr(0x4D414E49u); // "MANI"

        private readonly Action<string> log;
        private readonly MethodInfo getCurrentBeatmap;
        private readonly MethodInfo getBeatmapPath;
        private readonly FieldInfo currentScoreField;
        private readonly FieldInfo playerPausedField;
        private readonly FieldInfo replayModeField;
        private readonly FieldInfo replayScoreField;
        private readonly FieldInfo songClockField;
        private readonly MethodInfo invalidateScore;
        private readonly FieldInfo scoreValidityField;
        private readonly FieldInfo globalOsuModeField;
        private readonly int processId;
        private readonly int tapMilliseconds;
        private readonly int offsetMilliseconds;
        private readonly int maximumLatenessMilliseconds;
        private readonly int clockStallMilliseconds;
        private readonly string configuredKeys;

        private object candidateScore;
        private LiveManiaPlan candidatePlan;
        private List<LiveKeySpec> candidateKeys;
        private HumanizedPlanResult candidateHumanization;
        private AgentOptionsSnapshot candidateOptions;
        private int candidateStartedTick;

        private object sessionScore;
        private LiveManiaPlan sessionPlan;
        private List<LiveKeySpec> sessionKeys;
        private HumanizedPlanResult sessionHumanization;
        private AgentOptionsSnapshot sessionOptions;
        private bool[] pressed;
        private int[] rescuedReleaseAt;
        private int nextBatch;
        private int lastClock;
        private int clockLastChangedTick;
        private int batchesSent;
        private int transitionsSent;
        private int transitionsSkipped;
        private int maximumObservedLateness;
        private bool firstBatchLogged;
        private bool initialResetLogged;
        private bool suspended;
        private int suspendedClock;

        private sealed class OverdueTap
        {
            public int Lane;
            public int SourceLine;
            public int ReferenceDown;
            public bool HasDown;
            public bool HasUp;
            public bool IsHold;
        }

        private object handledScore;
        private bool timerResolutionActive;
        private bool shutdown;
        private string idleDetail = "waiting for mania Player mode";

        public LiveAgent(Assembly game, Action<string> logger)
        {
            if (game == null)
                throw new ArgumentNullException("game");
            if (logger == null)
                throw new ArgumentNullException("logger");

            log = logger;
            Module module = game.ManifestModule;
            getCurrentBeatmap = RequireMethod(
                module.ResolveMethod(CurrentBeatmapMethodToken),
                "current beatmap getter");
            getBeatmapPath = RequireMethod(
                module.ResolveMethod(BeatmapPathMethodToken),
                "beatmap path getter");
            currentScoreField = module.ResolveField(CurrentScoreFieldToken);
            playerPausedField = module.ResolveField(PlayerPausedFieldToken);
            replayModeField = module.ResolveField(ReplayModeFieldToken);
            replayScoreField = module.ResolveField(ReplayScoreFieldToken);
            songClockField = module.ResolveField(SongClockFieldToken);
            invalidateScore = RequireMethod(
                module.ResolveMethod(ScoreInvalidateMethodToken),
                "score invalidator");
            scoreValidityField = module.ResolveField(ScoreValidityFieldToken);
            globalOsuModeField = module.ResolveField(GlobalOsuModeFieldToken);
            ValidateTargets();

            processId = System.Diagnostics.Process.GetCurrentProcess().Id;
            tapMilliseconds = ReadInteger("MANIA_AGENT_TAP_MS", 8, 1, 100);
            offsetMilliseconds = ReadInteger("MANIA_AGENT_OFFSET_MS", 0, -5000, 5000);
            maximumLatenessMilliseconds = ReadInteger("MANIA_AGENT_MAX_LATE_MS", 80, 10, 1000);
            clockStallMilliseconds = ReadInteger("MANIA_AGENT_CLOCK_STALL_MS", 250, 100, 5000);
            configuredKeys = Environment.GetEnvironmentVariable("MANIA_AGENT_KEYS");

            log("live-agent targets validated; tap=" + tapMilliseconds
                + "ms, offset=" + offsetMilliseconds
                + "ms, max-late=" + maximumLatenessMilliseconds
                + "ms, clock-stall=" + clockStallMilliseconds + "ms");
        }

        public bool IsTimingCritical
        {
            get { return candidateScore != null || sessionScore != null; }
        }

        public AgentRuntimeStatus GetRuntimeStatus()
        {
            if (sessionScore != null && sessionPlan != null)
            {
                string style = sessionOptions == null
                    ? "AGENT"
                    : AgentOptionsSnapshot.StyleName(sessionOptions.Style);
                string timing = sessionHumanization == null
                    ? String.Empty
                    : "  UR=" + sessionHumanization.UnstableRate.ToString("0")
                        + "  200/100=" + sessionHumanization.Grade200
                        + "/" + sessionHumanization.Grade100;
                return new AgentRuntimeStatus(
                    suspended ? "PAUSED" : "PLAYING",
                    Path.GetFileName(sessionPlan.Path),
                    style + timing + "  " + nextBatch + "/" + sessionPlan.Batches.Count
                        + " batches  clock=" + lastClock + "ms"
                        + (transitionsSkipped > 0 ? "  skipped=" + transitionsSkipped : String.Empty));
            }
            if (candidateScore != null && candidatePlan != null)
            {
                string style = candidateOptions == null
                    ? "AGENT"
                    : AgentOptionsSnapshot.StyleName(candidateOptions.Style);
                return new AgentRuntimeStatus(
                    "ARMED",
                    Path.GetFileName(candidatePlan.Path),
                    style + "  waiting for song-clock reset");
            }
            return AgentRuntimeStatus.Idle(idleDetail);
        }

        public void Tick(AgentOptionsSnapshot options, int playMode, int selectedMods)
        {
            if (shutdown)
                return;
            if (options == null)
                throw new ArgumentNullException("options");

            object score = currentScoreField.GetValue(null);
            if (score == null)
                handledScore = null;

            if (!options.Enabled)
            {
                StopForGate("disabled", score, false);
                idleDetail = "Player mode: you are in control";
                return;
            }

            int globalMode = Convert.ToInt32(globalOsuModeField.GetValue(null));
            if (globalMode != GlobalOsuModePlay)
            {
                StopForGate("left osu.OsuModes.Play", score, true);
                idleDetail = "agent on; waiting for a mania map";
                return;
            }
            if (playMode != ManiaMode)
            {
                StopForGate("left OsuMania", score, true);
                idleDetail = "agent on; current ruleset is not mania";
                return;
            }
            if ((selectedMods & ForbiddenAutomationMods) != 0)
            {
                StopForGate(
                    "automation mod selected (Relax/Autoplay/Relax2/Cinema); Player-input agent refused",
                    score,
                    true);
                if (score != null && !Object.ReferenceEquals(score, handledScore))
                {
                    handledScore = score;
                    log("score refused: selected mods include an automation/replay mod; mods=0x"
                        + selectedMods.ToString("X"));
                }
                return;
            }
            if (score == null)
            {
                StopForGate("current score cleared", null, true);
                idleDetail = "agent on; waiting for Player score";
                return;
            }

            bool replayMode = Convert.ToBoolean(replayModeField.GetValue(null));
            object replayScore = replayScoreField.GetValue(null);
            if (replayMode || replayScore != null)
            {
                StopForGate("replay source detected", score, true);
                if (!Object.ReferenceEquals(score, handledScore))
                {
                    handledScore = score;
                    log("score refused: replay mode/source detected; live agent only runs in normal Player mode");
                }
                return;
            }

            if (sessionScore != null && !Object.ReferenceEquals(score, sessionScore))
                StopSession("score object changed", true, false);
            if (candidateScore != null && !Object.ReferenceEquals(score, candidateScore))
                CancelCandidate("score object changed before arm", true);
            if (Object.ReferenceEquals(score, handledScore))
                return;

            if (sessionScore == null && candidateScore == null)
                PrepareCandidate(score, options, selectedMods);
            if (candidateScore != null)
                TryArmCandidate(score);
            if (sessionScore != null)
                ExecuteTick(score);
        }

        public void EmergencyStop(string reason)
        {
            try
            {
                CancelCandidate(reason, true);
                StopSession(reason, true, true);
            }
            catch (Exception exception)
            {
                log("emergency cleanup failure: " + UsefulMessage(exception));
            }
        }

        public void Shutdown()
        {
            if (shutdown)
                return;
            shutdown = true;
            EmergencyStop("plugin shutdown");
            EndTimerResolution();
        }

        private void PrepareCandidate(object score, AgentOptionsSnapshot options, int selectedMods)
        {
            try
            {
                object beatmap = getCurrentBeatmap.Invoke(null, null);
                if (beatmap == null)
                    throw new InvalidOperationException("current beatmap is null");
                string path = getBeatmapPath.Invoke(beatmap, null) as string;
                if (String.IsNullOrEmpty(path))
                    throw new InvalidOperationException("current beatmap path is empty");

                LiveManiaPlan sourcePlan = LivePlanBuilder.ParseAndBuild(path, tapMilliseconds);
                HumanizedPlanResult humanization = Humanizer.Apply(
                    sourcePlan,
                    options,
                    selectedMods,
                    null);
                LiveManiaPlan plan = humanization.Plan;
                List<LiveKeySpec> keys = LivePlanBuilder.ResolveKeys(configuredKeys, plan.KeyCount);

                // This is the submission-safety gate. The analysed method only sets the
                // current score's validity flag false. If it cannot be invoked, no input is sent.
                invalidateScore.Invoke(score, null);
                if (Convert.ToBoolean(scoreValidityField.GetValue(score)))
                    throw new InvalidOperationException("score invalidation did not clear the validity flag");

                candidateScore = score;
                candidatePlan = plan;
                candidateKeys = keys;
                candidateHumanization = humanization;
                candidateOptions = options;
                candidateStartedTick = Environment.TickCount;
                BeginTimerResolution();

                int transitionCount = CountTransitions(plan);
                log("score marked local/ineligible; live plan prepared: map=" + plan.Path
                    + ", objects=" + plan.ObjectCount
                    + ", batches=" + plan.Batches.Count
                    + ", transitions=" + transitionCount
                    + ", keys=" + plan.KeyCount
                    + ", first=" + plan.FirstObjectTime + "ms"
                    + ", lane-keys=" + DescribeKeys(keys));
                log("humanization prepared: " + humanization.Describe(options));
                for (int index = 0; index < plan.Warnings.Count; index++)
                    log("plan warning: " + plan.Warnings[index]);
            }
            catch (Exception exception)
            {
                handledScore = score;
                ClearCandidate();
                EndTimerResolutionIfIdle();
                log("score refused before input: " + UsefulMessage(exception));
            }
        }

        private void TryArmCandidate(object score)
        {
            if (!Object.ReferenceEquals(score, candidateScore))
                return;

            int clock = ReadSongClock();
            int firstDue = checked(candidatePlan.Batches[0].Time + offsetMilliseconds);
            if (clock > firstDue + maximumLatenessMilliseconds)
            {
                int waited = ElapsedSince(candidateStartedTick);
                if (waited >= 2000)
                {
                    handledScore = score;
                    log("score refused: song clock did not reset before the first event; clock="
                        + clock + "ms, first-due=" + firstDue + "ms");
                    ClearCandidate();
                    EndTimerResolutionIfIdle();
                }
                return;
            }
            if (!IsCurrentProcessForeground())
                return;

            sessionScore = candidateScore;
            sessionPlan = candidatePlan;
            sessionKeys = candidateKeys;
            sessionHumanization = candidateHumanization;
            sessionOptions = candidateOptions;
            pressed = new bool[sessionKeys.Count];
            rescuedReleaseAt = new int[sessionKeys.Count];
            for (int lane = 0; lane < rescuedReleaseAt.Length; lane++)
                rescuedReleaseAt[lane] = -1;
            nextBatch = 0;
            lastClock = clock;
            clockLastChangedTick = Environment.TickCount;
            batchesSent = 0;
            transitionsSent = 0;
            transitionsSkipped = 0;
            maximumObservedLateness = 0;
            firstBatchLogged = false;
            initialResetLogged = false;
            suspended = false;
            suspendedClock = 0;
            ClearCandidate();

            log("live agent armed in normal Player mode at song-clock=" + clock
                + "ms; first-due=" + firstDue + "ms; style="
                + AgentOptionsSnapshot.StyleName(sessionOptions.Style)
                + "; no replay frames were created or consumed");
        }

        private void ExecuteTick(object score)
        {
            if (!Object.ReferenceEquals(score, sessionScore))
            {
                StopSession("score object changed", true, false);
                return;
            }
            if (!IsCurrentProcessForeground())
            {
                StopSession("osu! lost foreground focus", true, false);
                return;
            }

            int clock = ReadSongClock();
            bool gamePaused = Convert.ToBoolean(playerPausedField.GetValue(null));
            if (gamePaused)
            {
                SuspendSession("Player pause flag set", clock);
                return;
            }
            if (suspended)
            {
                if (clock < suspendedClock - 25)
                {
                    StopSession(
                        "song clock moved backwards while suspended from "
                            + suspendedClock + "ms to " + clock + "ms",
                        true,
                        false);
                    return;
                }
                ResumeSession(clock);
            }

            if (clock < lastClock - 25)
            {
                int firstDue = checked(sessionPlan.Batches[0].Time + offsetMilliseconds);
                if (batchesSent == 0 && !AnyPressed() && clock < firstDue)
                {
                    if (!initialResetLogged)
                    {
                        initialResetLogged = true;
                        log("initial song-clock lead-in reset accepted: " + lastClock
                            + "ms -> " + clock + "ms");
                    }
                    lastClock = clock;
                    clockLastChangedTick = Environment.TickCount;
                }
                else
                {
                    StopSession(
                        "song clock moved backwards from " + lastClock + "ms to " + clock + "ms",
                        true,
                        false);
                    return;
                }
            }
            if (clock != lastClock)
            {
                lastClock = clock;
                clockLastChangedTick = Environment.TickCount;
            }
            else if (batchesSent > 0 && ElapsedSince(clockLastChangedTick) >= clockStallMilliseconds)
            {
                StopSession(
                    "song clock stalled for " + ElapsedSince(clockLastChangedTick)
                        + "ms without the Player pause flag",
                    true,
                    false);
                return;
            }

            ReleaseRescuedTaps(clock);

            while (nextBatch < sessionPlan.Batches.Count)
            {
                LiveTransitionBatch batch = sessionPlan.Batches[nextBatch];
                int due = checked(batch.Time + offsetMilliseconds);
                if (clock < due)
                    break;

                int lateness = clock - due;
                if (lateness > maximumLatenessMilliseconds)
                {
                    CatchUpToClock(clock, lateness);
                    continue;
                }

                int sent = InjectBatch(batch);
                batchesSent++;
                transitionsSent += sent;
                maximumObservedLateness = Math.Max(maximumObservedLateness, lateness);
                nextBatch++;

                if (!firstBatchLogged)
                {
                    firstBatchLogged = true;
                    log("first real key transition sent through SendInput: song-clock="
                        + clock + "ms, due=" + due + "ms, late=" + lateness + "ms");
                }
            }

            if (nextBatch >= sessionPlan.Batches.Count && !HasPendingRescuedTap())
                StopSession("plan completed", true, false);
        }

        private void CatchUpToClock(int clock, int firstLateness)
        {
            int firstBatch = nextBatch;
            int plannedTransitions = 0;
            bool[] targetState = (bool[])pressed.Clone();
            bool[] startingState = (bool[])pressed.Clone();
            Dictionary<long, OverdueTap> overdue = new Dictionary<long, OverdueTap>();

            while (nextBatch < sessionPlan.Batches.Count)
            {
                LiveTransitionBatch batch = sessionPlan.Batches[nextBatch];
                int due = checked(batch.Time + offsetMilliseconds);
                if (clock < due)
                    break;

                for (int transitionIndex = 0;
                    transitionIndex < batch.Transitions.Count;
                    transitionIndex++)
                {
                    LiveLaneTransition transition = batch.Transitions[transitionIndex];
                    if (transition.Lane < 0 || transition.Lane >= sessionKeys.Count)
                        throw new InvalidOperationException("plan contains invalid lane " + transition.Lane);
                    targetState[transition.Lane] = transition.IsDown;
                    plannedTransitions++;

                    long key = ((long)transition.Lane << 32) | (uint)transition.SourceLine;
                    OverdueTap tap;
                    if (!overdue.TryGetValue(key, out tap))
                    {
                        tap = new OverdueTap
                        {
                            Lane = transition.Lane,
                            SourceLine = transition.SourceLine,
                            IsHold = transition.IsHold
                        };
                        overdue.Add(key, tap);
                    }
                    if (transition.IsDown)
                    {
                        tap.HasDown = true;
                        tap.ReferenceDown = transition.ReferenceTime;
                    }
                    else
                    {
                        tap.HasUp = true;
                    }
                }
                nextBatch++;
            }

            int rescueHold = Math.Max(4, Math.Min(12, tapMilliseconds));
            int rescued = 0;
            OverdueTap[] rescueByLane = new OverdueTap[sessionKeys.Count];
            foreach (KeyValuePair<long, OverdueTap> entry in overdue)
            {
                OverdueTap tap = entry.Value;
                if (tap.IsHold || !tap.HasDown || !tap.HasUp
                    || startingState[tap.Lane] || targetState[tap.Lane]
                    || rescuedReleaseAt[tap.Lane] >= 0
                    || sessionHumanization == null)
                {
                    continue;
                }

                int latestSafeClock = checked(
                    tap.ReferenceDown + offsetMilliseconds + sessionHumanization.SafeHitWindow);
                if (clock > latestSafeClock)
                    continue;

                OverdueTap existing = rescueByLane[tap.Lane];
                if (existing == null || tap.ReferenceDown > existing.ReferenceDown)
                    rescueByLane[tap.Lane] = tap;
            }

            for (int lane = 0; lane < rescueByLane.Length; lane++)
            {
                OverdueTap tap = rescueByLane[lane];
                if (tap == null)
                    continue;
                int releaseAt = checked(clock + rescueHold);
                int nextLaneDown = FindNextLaneDown(lane);
                if (nextLaneDown != Int32.MaxValue
                    && checked(nextLaneDown + offsetMilliseconds) <= releaseAt)
                {
                    continue;
                }

                targetState[lane] = true;
                rescuedReleaseAt[lane] = releaseAt;
                rescued++;
            }

            List<NativeInput> inputs = new List<NativeInput>(sessionKeys.Count);
            for (int lane = 0; lane < targetState.Length; lane++)
            {
                if (pressed[lane] != targetState[lane])
                    inputs.Add(CreateKeyboardInput(sessionKeys[lane], !targetState[lane]));
            }
            if (inputs.Count > 0)
                Send(inputs);
            Array.Copy(targetState, pressed, pressed.Length);

            int collapsed = Math.Max(0, plannedTransitions - inputs.Count - rescued);
            batchesSent += nextBatch - firstBatch;
            transitionsSent += inputs.Count;
            transitionsSkipped += collapsed;
            maximumObservedLateness = Math.Max(maximumObservedLateness, firstLateness);

            if (!firstBatchLogged && inputs.Count > 0)
            {
                firstBatchLogged = true;
                log("first real key transition sent during catch-up: song-clock=" + clock
                    + "ms, late=" + firstLateness + "ms");
            }
            log("live agent catch-up after scheduler/game stall: clock=" + clock
                + "ms, late=" + firstLateness + "ms, batches=" + (nextBatch - firstBatch)
                + ", planned-transitions=" + plannedTransitions
                + ", emitted-net-state=" + inputs.Count
                + ", rescued-taps=" + rescued
                + ", collapsed-unrecovered=" + collapsed);
        }

        private void ReleaseRescuedTaps(int clock)
        {
            if (rescuedReleaseAt == null)
                return;

            List<NativeInput> releases = new List<NativeInput>();
            List<int> releasedLanes = new List<int>();
            for (int lane = 0; lane < rescuedReleaseAt.Length; lane++)
            {
                if (rescuedReleaseAt[lane] < 0 || clock < rescuedReleaseAt[lane])
                    continue;
                if (!pressed[lane])
                {
                    rescuedReleaseAt[lane] = -1;
                    continue;
                }
                releases.Add(CreateKeyboardInput(sessionKeys[lane], true));
                releasedLanes.Add(lane);
            }
            if (releases.Count > 0)
            {
                Send(releases);
                transitionsSent += releases.Count;
                for (int index = 0; index < releasedLanes.Count; index++)
                {
                    int lane = releasedLanes[index];
                    pressed[lane] = false;
                    rescuedReleaseAt[lane] = -1;
                }
            }
        }

        private bool HasPendingRescuedTap()
        {
            if (rescuedReleaseAt == null)
                return false;
            for (int lane = 0; lane < rescuedReleaseAt.Length; lane++)
            {
                if (rescuedReleaseAt[lane] >= 0)
                    return true;
            }
            return false;
        }

        private int FindNextLaneDown(int lane)
        {
            for (int batchIndex = nextBatch;
                batchIndex < sessionPlan.Batches.Count;
                batchIndex++)
            {
                List<LiveLaneTransition> transitions = sessionPlan.Batches[batchIndex].Transitions;
                for (int transitionIndex = 0;
                    transitionIndex < transitions.Count;
                    transitionIndex++)
                {
                    LiveLaneTransition transition = transitions[transitionIndex];
                    if (transition.Lane == lane && transition.IsDown)
                        return sessionPlan.Batches[batchIndex].Time;
                }
            }
            return Int32.MaxValue;
        }

        private void SuspendSession(string reason, int clock)
        {
            if (sessionScore == null || suspended)
                return;

            TryReleaseAll(false);
            suspended = true;
            suspendedClock = clock;
            lastClock = clock;
            clockLastChangedTick = Environment.TickCount;
            log("live agent suspended: " + reason + "; song-clock=" + clock
                + "ms, next-batch=" + nextBatch);
        }

        private void ResumeSession(int clock)
        {
            if (!suspended)
                return;

            bool[] logicalState = new bool[sessionKeys.Count];
            for (int batchIndex = 0; batchIndex < nextBatch; batchIndex++)
            {
                List<LiveLaneTransition> transitions = sessionPlan.Batches[batchIndex].Transitions;
                for (int transitionIndex = 0; transitionIndex < transitions.Count; transitionIndex++)
                {
                    LiveLaneTransition transition = transitions[transitionIndex];
                    logicalState[transition.Lane] = transition.IsDown;
                }
            }

            List<NativeInput> inputs = new List<NativeInput>(sessionKeys.Count);
            for (int lane = 0; lane < logicalState.Length; lane++)
            {
                if (pressed[lane] != logicalState[lane])
                    inputs.Add(CreateKeyboardInput(sessionKeys[lane], !logicalState[lane]));
            }
            if (inputs.Count > 0)
                Send(inputs);
            Array.Copy(logicalState, pressed, pressed.Length);

            int pausedAt = suspendedClock;
            suspended = false;
            suspendedClock = 0;
            lastClock = clock;
            clockLastChangedTick = Environment.TickCount;
            log("live agent resumed: song-clock=" + pausedAt + "ms -> " + clock
                + "ms, restored-held-lanes=" + CountPressed());
        }

        private int InjectBatch(LiveTransitionBatch batch)
        {
            bool[] nextState = (bool[])pressed.Clone();
            List<NativeInput> inputs = new List<NativeInput>(batch.Transitions.Count);

            for (int index = 0; index < batch.Transitions.Count; index++)
            {
                LiveLaneTransition transition = batch.Transitions[index];
                if (transition.Lane < 0 || transition.Lane >= sessionKeys.Count)
                    throw new InvalidOperationException("plan contains invalid lane " + transition.Lane);
                if (nextState[transition.Lane] == transition.IsDown)
                    continue;

                inputs.Add(CreateKeyboardInput(
                    sessionKeys[transition.Lane],
                    !transition.IsDown));
                nextState[transition.Lane] = transition.IsDown;
            }

            if (inputs.Count == 0)
                return 0;

            Send(inputs);
            Array.Copy(nextState, pressed, pressed.Length);
            return inputs.Count;
        }

        private void StopForGate(string reason, object score, bool blockCandidate)
        {
            if (sessionScore != null)
                StopSession(reason, true, false);
            if (candidateScore != null)
                CancelCandidate(reason, blockCandidate);
            if (score == null)
                handledScore = null;
        }

        private void CancelCandidate(string reason, bool blockScore)
        {
            if (candidateScore == null)
                return;

            object score = candidateScore;
            if (blockScore)
                handledScore = score;
            log("live agent candidate cancelled: " + reason);
            ClearCandidate();
            EndTimerResolutionIfIdle();
        }

        private void StopSession(string reason, bool blockScore, bool forceAllReleases)
        {
            if (sessionScore == null)
                return;

            object score = sessionScore;
            string path = sessionPlan == null ? "<unknown>" : sessionPlan.Path;
            TryReleaseAll(forceAllReleases);
            if (blockScore)
                handledScore = score;

            log("live agent stopped: " + reason
                + "; map=" + path
                + ", batches=" + batchesSent
                + ", transitions=" + transitionsSent
                + ", skipped=" + transitionsSkipped
                + ", max-late=" + maximumObservedLateness + "ms");

            sessionScore = null;
            sessionPlan = null;
            sessionKeys = null;
            sessionHumanization = null;
            sessionOptions = null;
            pressed = null;
            rescuedReleaseAt = null;
            nextBatch = 0;
            suspended = false;
            suspendedClock = 0;
            EndTimerResolutionIfIdle();
        }

        private void TryReleaseAll(bool forceAll)
        {
            if (sessionKeys == null || pressed == null)
                return;

            List<NativeInput> releases = new List<NativeInput>(sessionKeys.Count);
            for (int lane = 0; lane < sessionKeys.Count; lane++)
            {
                if (forceAll || pressed[lane])
                    releases.Add(CreateKeyboardInput(sessionKeys[lane], true));
            }

            try
            {
                if (releases.Count > 0)
                    Send(releases);
            }
            catch (Exception exception)
            {
                log("key release cleanup failed: " + UsefulMessage(exception));
            }
            finally
            {
                for (int lane = 0; lane < pressed.Length; lane++)
                    pressed[lane] = false;
            }
        }

        private void ClearCandidate()
        {
            candidateScore = null;
            candidatePlan = null;
            candidateKeys = null;
            candidateHumanization = null;
            candidateOptions = null;
            candidateStartedTick = 0;
        }

        private bool AnyPressed()
        {
            if (pressed == null)
                return false;
            for (int index = 0; index < pressed.Length; index++)
            {
                if (pressed[index])
                    return true;
            }
            return false;
        }

        private int CountPressed()
        {
            if (pressed == null)
                return 0;
            int count = 0;
            for (int index = 0; index < pressed.Length; index++)
            {
                if (pressed[index])
                    count++;
            }
            return count;
        }

        private int ReadSongClock()
        {
            return Convert.ToInt32(songClockField.GetValue(null));
        }

        private bool IsCurrentProcessForeground()
        {
            IntPtr window = GetForegroundWindow();
            if (window == IntPtr.Zero)
                return false;

            uint foregroundProcessId;
            GetWindowThreadProcessId(window, out foregroundProcessId);
            return foregroundProcessId == (uint)processId;
        }

        private void BeginTimerResolution()
        {
            if (!timerResolutionActive && TimeBeginPeriod(1) == 0)
                timerResolutionActive = true;
        }

        private void EndTimerResolutionIfIdle()
        {
            if (candidateScore == null && sessionScore == null)
                EndTimerResolution();
        }

        private void EndTimerResolution()
        {
            if (!timerResolutionActive)
                return;
            TimeEndPeriod(1);
            timerResolutionActive = false;
        }

        private void ValidateTargets()
        {
            if (!getCurrentBeatmap.IsStatic
                || getCurrentBeatmap.GetParameters().Length != 0
                || getBeatmapPath.IsStatic
                || getBeatmapPath.GetParameters().Length != 0
                || getBeatmapPath.ReturnType != typeof(string))
            {
                throw new InvalidOperationException("beatmap metadata tokens failed structural validation");
            }
            if (!currentScoreField.IsStatic
                || !playerPausedField.IsStatic
                || playerPausedField.DeclaringType != currentScoreField.DeclaringType
                || playerPausedField.FieldType != typeof(bool)
                || !replayModeField.IsStatic
                || replayModeField.FieldType != typeof(bool)
                || !replayScoreField.IsStatic
                || replayModeField.DeclaringType != replayScoreField.DeclaringType
                || currentScoreField.FieldType != replayScoreField.FieldType
                || invalidateScore.IsStatic
                || invalidateScore.DeclaringType != currentScoreField.FieldType
                || invalidateScore.ReturnType != typeof(void)
                || invalidateScore.GetParameters().Length != 0
                || scoreValidityField.IsStatic
                || scoreValidityField.DeclaringType != currentScoreField.FieldType
                || scoreValidityField.FieldType != typeof(bool))
            {
                throw new InvalidOperationException("score/replay-gate metadata tokens failed structural validation");
            }
            byte[] body = invalidateScore.GetMethodBody().GetILAsByteArray();
            byte[] expectedBody = new byte[]
            {
                0x02,                         // ldarg.0
                0x16,                         // ldc.i4.0
                0x7D, 0x90, 0x19, 0x00, 0x04, // stfld 0x04001990
                0x2A                          // ret
            };
            if (!ByteArraysEqual(body, expectedBody))
                throw new InvalidOperationException("score invalidator IL body failed exact validation");
            if (!songClockField.IsStatic || songClockField.FieldType != typeof(int))
                throw new InvalidOperationException("song-clock metadata token failed structural validation");
            if (!globalOsuModeField.IsStatic
                || !globalOsuModeField.FieldType.IsEnum
                || !String.Equals(globalOsuModeField.FieldType.FullName, "osu.OsuModes", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("global osu mode metadata token failed structural validation");
            }
        }

        private static MethodInfo RequireMethod(MethodBase method, string label)
        {
            MethodInfo result = method as MethodInfo;
            if (result == null)
                throw new InvalidOperationException(label + " token did not resolve to MethodInfo");
            return result;
        }

        private static int ReadInteger(string name, int defaultValue, int minimum, int maximum)
        {
            string text = Environment.GetEnvironmentVariable(name);
            if (String.IsNullOrWhiteSpace(text))
                return defaultValue;

            int value;
            if (!Int32.TryParse(text, out value) || value < minimum || value > maximum)
            {
                throw new InvalidOperationException(
                    name + " must be an integer from " + minimum + " through " + maximum + ".");
            }
            return value;
        }

        private static int CountTransitions(LiveManiaPlan plan)
        {
            int count = 0;
            for (int index = 0; index < plan.Batches.Count; index++)
                count += plan.Batches[index].Transitions.Count;
            return count;
        }

        private static bool ByteArraysEqual(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length)
                return false;
            for (int index = 0; index < left.Length; index++)
            {
                if (left[index] != right[index])
                    return false;
            }
            return true;
        }

        private static string DescribeKeys(List<LiveKeySpec> keys)
        {
            string[] names = new string[keys.Count];
            for (int index = 0; index < keys.Count; index++)
                names[index] = (index + 1) + "->" + keys[index].Name;
            return String.Join(",", names);
        }

        private static int ElapsedSince(int startTick)
        {
            return unchecked(Environment.TickCount - startTick);
        }

        private static string UsefulMessage(Exception exception)
        {
            TargetInvocationException invocation = exception as TargetInvocationException;
            if (invocation != null && invocation.InnerException != null)
                return invocation.InnerException.ToString();
            return exception.ToString();
        }

        private static NativeInput CreateKeyboardInput(LiveKeySpec key, bool keyUp)
        {
            uint mappedScanCode = MapVirtualKeyW(key.VirtualKey, MapVkToScanCodeEx);
            if (mappedScanCode == 0)
                mappedScanCode = MapVirtualKeyW(key.VirtualKey, MapVkToScanCode);
            if (mappedScanCode == 0)
            {
                throw new Win32Exception(
                    "Could not map " + key.Name + " (VK 0x" + key.VirtualKey.ToString("X2")
                        + ") to a scan code.");
            }

            byte prefix = (byte)((mappedScanCode >> 8) & 0xFF);
            uint flags = KeyEventScanCode;
            if (keyUp)
                flags |= KeyEventKeyUp;
            if (prefix == 0xE0 || prefix == 0xE1)
                flags |= KeyEventExtendedKey;

            NativeInput result = new NativeInput();
            result.Type = InputKeyboard;
            result.Union.Keyboard.VirtualKey = 0;
            result.Union.Keyboard.ScanCode = (ushort)(mappedScanCode & 0xFF);
            result.Union.Keyboard.Flags = flags;
            result.Union.Keyboard.Time = 0;
            result.Union.Keyboard.ExtraInfo = InjectionMarker;
            return result;
        }

        private static void Send(List<NativeInput> inputs)
        {
            NativeInput[] array = inputs.ToArray();
            uint sent = SendInput((uint)array.Length, array, Marshal.SizeOf(typeof(NativeInput)));
            if (sent != array.Length)
            {
                int error = Marshal.GetLastWin32Error();
                throw new Win32Exception(
                    error,
                    "SendInput sent only " + sent + "/" + array.Length + " keyboard events.");
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeInput
        {
            public uint Type;
            public InputUnion Union;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)]
            public MouseInput Mouse;

            [FieldOffset(0)]
            public KeyboardInput Keyboard;

            [FieldOffset(0)]
            public HardwareInput Hardware;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MouseInput
        {
            public int X;
            public int Y;
            public uint MouseData;
            public uint Flags;
            public uint Time;
            public UIntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KeyboardInput
        {
            public ushort VirtualKey;
            public ushort ScanCode;
            public uint Flags;
            public uint Time;
            public UIntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HardwareInput
        {
            public uint Message;
            public ushort ParameterLow;
            public ushort ParameterHigh;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint inputCount, NativeInput[] inputs, int inputSize);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKeyW(uint code, uint mapType);

        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
        private static extern uint TimeBeginPeriod(uint period);

        [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
        private static extern uint TimeEndPeriod(uint period);
    }
}
