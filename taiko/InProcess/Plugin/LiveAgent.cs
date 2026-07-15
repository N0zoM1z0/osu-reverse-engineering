using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace LocalTaikoAgent.Plugin
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
        private const int ScoreValidityFieldToken = 0x04001990;
        private const int ScoreSubmissionStateGetterToken = 0x06002B4D;
        private const int LoggedInMethodToken = 0x0600469B;
        private const int GlobalOsuModeFieldToken = 0x04002C6D;
        private const int BindingGetterMethodToken = 0x06002C4F;
        private const int SubmissionObservationTimeoutMilliseconds = 120000;

        private const int TaikoMode = 1;
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
        private static readonly UIntPtr InjectionMarker = new UIntPtr(0x5441494Bu); // "TAIK"

        private readonly Action<string> log;
        private readonly MethodInfo getCurrentBeatmap;
        private readonly MethodInfo getBeatmapPath;
        private readonly FieldInfo currentScoreField;
        private readonly FieldInfo playerPausedField;
        private readonly FieldInfo replayModeField;
        private readonly FieldInfo replayScoreField;
        private readonly FieldInfo songClockField;
        private readonly FieldInfo scoreValidityField;
        private readonly MethodInfo getScoreSubmissionState;
        private readonly MethodInfo isLoggedIn;
        private readonly FieldInfo globalOsuModeField;
        private readonly MethodInfo bindingGetter;
        private readonly Type bindingEnumType;
        private readonly int processId;
        private readonly int tapMilliseconds;
        private readonly int offsetMilliseconds;
        private readonly int maximumLatenessMilliseconds;
        private readonly int clockStallMilliseconds;
        private readonly bool submissionDiagnostics;

        private object candidateScore;
        private LiveTaikoPlan candidatePlan;
        private List<LiveTaikoKeySpec> candidateKeys;
        private HumanizedPlanResult candidateHumanization;
        private AgentOptionsSnapshot candidateOptions;
        private int candidateStartedTick;

        private object sessionScore;
        private LiveTaikoPlan sessionPlan;
        private List<LiveTaikoKeySpec> sessionKeys;
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
        private int lateBatches;
        private int lateRecoveryInputs;
        private bool firstBatchLogged;
        private bool suspended;
        private int suspendedClock;

        // Agent-local idempotence marker only. This never mutates osu!'s score-validity state.
        private object handledScore;
        private object observedScore;
        private bool observedScoreInCurrentField;
        private bool observedValidity;
        private bool observedLoggedIn;
        private int observedSubmissionState;
        private int observedDetachedTick;
        private bool timerResolutionActive;
        private bool shutdown;
        private string idleDetail = "waiting for Taiko Player mode";

        public LiveAgent(Assembly game, Action<string> logger)
        {
            if (game == null) throw new ArgumentNullException("game");
            if (logger == null) throw new ArgumentNullException("logger");
            log = logger;

            Module module = game.ManifestModule;
            getCurrentBeatmap = RequireMethod(module.ResolveMethod(CurrentBeatmapMethodToken), "current beatmap getter");
            getBeatmapPath = RequireMethod(module.ResolveMethod(BeatmapPathMethodToken), "beatmap path getter");
            currentScoreField = module.ResolveField(CurrentScoreFieldToken);
            playerPausedField = module.ResolveField(PlayerPausedFieldToken);
            replayModeField = module.ResolveField(ReplayModeFieldToken);
            replayScoreField = module.ResolveField(ReplayScoreFieldToken);
            songClockField = module.ResolveField(SongClockFieldToken);
            scoreValidityField = module.ResolveField(ScoreValidityFieldToken);
            getScoreSubmissionState = RequireMethod(
                module.ResolveMethod(ScoreSubmissionStateGetterToken),
                "score submission-state getter");
            isLoggedIn = RequireMethod(
                module.ResolveMethod(LoggedInMethodToken),
                "logged-in predicate");
            globalOsuModeField = module.ResolveField(GlobalOsuModeFieldToken);
            bindingGetter = RequireMethod(module.ResolveMethod(BindingGetterMethodToken), "binding getter");
            bindingEnumType = bindingGetter.GetParameters()[0].ParameterType;
            ValidateTargets();

            processId = System.Diagnostics.Process.GetCurrentProcess().Id;
            tapMilliseconds = ReadInteger("TAIKO_AGENT_TAP_MS", 8, 1, 100);
            offsetMilliseconds = ReadInteger("TAIKO_AGENT_OFFSET_MS", 0, -5000, 5000);
            maximumLatenessMilliseconds = ReadInteger("TAIKO_AGENT_MAX_LATE_MS", 70, 10, 1000);
            clockStallMilliseconds = ReadInteger("TAIKO_AGENT_CLOCK_STALL_MS", 250, 100, 5000);
            submissionDiagnostics = ReadBoolean("TAIKO_SUBMISSION_DIAGNOSTICS", false);
            log("live-agent targets validated; tap=" + tapMilliseconds
                + "ms, offset=" + offsetMilliseconds
                + "ms, max-late=" + maximumLatenessMilliseconds
                + "ms, clock-stall=" + clockStallMilliseconds + "ms"
                + ", submission-diag=" + (submissionDiagnostics ? "on" : "off"));
        }

        public bool IsTimingCritical
        {
            get { return candidateScore != null || sessionScore != null; }
        }

        public AgentRuntimeStatus GetRuntimeStatus()
        {
            if (sessionScore != null && sessionPlan != null)
            {
                string timing = sessionHumanization == null
                    ? String.Empty
                    : "  UR=" + sessionHumanization.UnstableRate.ToString("0")
                        + "  100=" + sessionHumanization.Grade100;
                return new AgentRuntimeStatus(
                    suspended ? "PAUSED" : "PLAYING",
                    Path.GetFileName(sessionPlan.Path),
                    AgentOptionsSnapshot.StyleName(sessionOptions.Style)
                        + timing + "  " + nextBatch + "/" + sessionPlan.Batches.Count
                        + " batches  clock=" + lastClock + "ms"
                        + (transitionsSkipped > 0 ? "  skipped=" + transitionsSkipped : String.Empty));
            }
            if (candidateScore != null && candidatePlan != null)
            {
                return new AgentRuntimeStatus(
                    "ARMED",
                    Path.GetFileName(candidatePlan.Path),
                    AgentOptionsSnapshot.StyleName(candidateOptions.Style)
                        + "  waiting for song-clock reset");
            }
            return AgentRuntimeStatus.Idle(idleDetail);
        }

        public void Tick(AgentOptionsSnapshot options, int playMode, int selectedMods)
        {
            if (shutdown) return;
            if (options == null) throw new ArgumentNullException("options");

            object score = currentScoreField.GetValue(null);
            ObserveSubmission(score, playMode == TaikoMode);
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
                idleDetail = "agent on; waiting for a Taiko map";
                return;
            }
            if (playMode != TaikoMode)
            {
                StopForGate("left Taiko", score, true);
                idleDetail = "agent on; current ruleset is not Taiko";
                return;
            }
            if ((selectedMods & ForbiddenAutomationMods) != 0)
            {
                StopForGate("automation/replay mod selected", score, true);
                if (score != null && !Object.ReferenceEquals(score, handledScore))
                {
                    handledScore = score;
                    log("score refused: Relax/Auto/Relax2/Cinema selected; mods=0x"
                        + selectedMods.ToString("X"));
                }
                return;
            }
            if (score == null)
            {
                StopForGate("current score cleared", null, true);
                idleDetail = "agent on; waiting for normal Player score";
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
                    log("score refused: replay source detected; this agent only runs in normal Player mode");
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
            if (shutdown) return;
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

                LiveTaikoPlan source = LivePlanBuilder.ParseAndBuild(path, tapMilliseconds, selectedMods);
                HumanizedPlanResult humanization = Humanizer.Apply(
                    source,
                    options,
                    selectedMods,
                    tapMilliseconds,
                    null);
                List<LiveTaikoKeySpec> keys = ResolveCurrentBindings();

                candidateScore = score;
                candidatePlan = humanization.Plan;
                candidateKeys = keys;
                candidateHumanization = humanization;
                candidateOptions = options;
                candidateStartedTick = Environment.TickCount;
                BeginTimerResolution();

                log("live Taiko plan prepared: map=" + source.Path
                    + ", objects=" + source.ObjectCount
                    + " (circles=" + source.CircleCount
                    + ", rolls=" + source.DrumRollCount
                    + ", spinners=" + source.SpinnerCount + ")"
                    + ", strikes=" + candidatePlan.Strikes.Count
                    + ", batches=" + candidatePlan.Batches.Count
                    + ", keys=" + DescribeKeys(keys));
                log("humanization prepared: " + humanization.Describe(options));
                for (int index = 0; index < candidatePlan.Warnings.Count; index++)
                    log("plan warning: " + candidatePlan.Warnings[index]);
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
                if (ElapsedSince(candidateStartedTick) >= 2000)
                {
                    handledScore = score;
                    log("score refused: song clock did not reset; clock=" + clock
                        + "ms, first-due=" + firstDue + "ms");
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
            pressed = new bool[4];
            rescuedReleaseAt = new int[] { -1, -1, -1, -1 };
            nextBatch = 0;
            lastClock = clock;
            clockLastChangedTick = Environment.TickCount;
            batchesSent = 0;
            transitionsSent = 0;
            transitionsSkipped = 0;
            maximumObservedLateness = 0;
            lateBatches = 0;
            lateRecoveryInputs = 0;
            firstBatchLogged = false;
            suspended = false;
            suspendedClock = 0;
            ClearCandidate();
            log("live Taiko agent armed in normal Player mode at song-clock=" + clock
                + "ms; first-due=" + firstDue + "ms; no Auto frames or replay list were created");
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
            bool paused = Convert.ToBoolean(playerPausedField.GetValue(null));
            if (paused)
            {
                SuspendSession(clock);
                return;
            }
            if (suspended)
            {
                if (clock < suspendedClock - 25)
                {
                    StopSession("song clock moved backwards while paused", true, false);
                    return;
                }
                suspended = false;
                suspendedClock = 0;
                lastClock = clock;
                clockLastChangedTick = Environment.TickCount;
                log("live Taiko agent resumed at song-clock=" + clock + "ms");
            }

            if (clock < lastClock - 25)
            {
                int firstDue = checked(sessionPlan.Batches[0].Time + offsetMilliseconds);
                if (batchesSent == 0 && !AnyPressed() && clock < firstDue)
                {
                    lastClock = clock;
                    clockLastChangedTick = Environment.TickCount;
                }
                else
                {
                    StopSession("song clock moved backwards from " + lastClock + "ms to " + clock + "ms", true, false);
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
                StopSession("song clock stalled without pause flag", true, false);
                return;
            }

            ReleaseRescued(clock);
            while (nextBatch < sessionPlan.Batches.Count)
            {
                LiveTaikoTransitionBatch batch = sessionPlan.Batches[nextBatch];
                int due = checked(batch.Time + offsetMilliseconds);
                if (clock < due)
                    break;
                int lateness = clock - due;
                if (lateness > maximumLatenessMilliseconds)
                {
                    lateBatches++;
                    lateRecoveryInputs += InjectLateBatch(batch, clock);
                }
                else
                    InjectBatch(batch);
                batchesSent++;
                maximumObservedLateness = Math.Max(maximumObservedLateness, lateness);
                nextBatch++;
            }

            if (nextBatch >= sessionPlan.Batches.Count && !HasPendingRescue())
                StopSession("plan completed", true, false);
        }

        private void InjectBatch(LiveTaikoTransitionBatch batch)
        {
            bool[] next = (bool[])pressed.Clone();
            List<NativeInput> inputs = new List<NativeInput>();
            for (int index = 0; index < batch.Transitions.Count; index++)
            {
                LiveTaikoTransition transition = batch.Transitions[index];
                if (rescuedReleaseAt[transition.Key] >= 0 && !transition.IsDown)
                    continue;
                if (next[transition.Key] == transition.IsDown)
                    continue;
                inputs.Add(CreateKeyboardInput(sessionKeys[transition.Key], !transition.IsDown));
                next[transition.Key] = transition.IsDown;
            }
            if (inputs.Count > 0)
            {
                Send(inputs);
                transitionsSent += inputs.Count;
                Array.Copy(next, pressed, pressed.Length);
                LogFirstInput(batch.Time);
            }
        }

        private int InjectLateBatch(LiveTaikoTransitionBatch batch, int clock)
        {
            List<NativeInput> inputs = new List<NativeInput>();
            bool[] next = (bool[])pressed.Clone();
            for (int index = 0; index < batch.Transitions.Count; index++)
            {
                LiveTaikoTransition transition = batch.Transitions[index];
                if (!transition.IsDown)
                {
                    if (rescuedReleaseAt[transition.Key] >= 0)
                        continue;
                    if (next[transition.Key])
                    {
                        inputs.Add(CreateKeyboardInput(sessionKeys[transition.Key], true));
                        next[transition.Key] = false;
                    }
                    else
                    {
                        transitionsSkipped++;
                    }
                    continue;
                }

                int absolute = Math.Abs(clock - transition.ReferenceTime);
                bool canRescue = transition.RequiredForCombo
                    && absolute < sessionHumanization.SafeHitWindow
                    && !next[transition.Key]
                    && rescuedReleaseAt[transition.Key] < 0;
                if (canRescue)
                {
                    inputs.Add(CreateKeyboardInput(sessionKeys[transition.Key], false));
                    next[transition.Key] = true;
                    rescuedReleaseAt[transition.Key] = checked(clock + Math.Max(4, tapMilliseconds));
                }
                else
                {
                    transitionsSkipped++;
                }
            }
            if (inputs.Count > 0)
            {
                Send(inputs);
                transitionsSent += inputs.Count;
                Array.Copy(next, pressed, pressed.Length);
                LogFirstInput(batch.Time);
            }
            // Never perform per-batch file I/O here. A late batch already means the timing
            // thread is under pressure; synchronous logging creates a self-amplifying stall.
            return inputs.Count;
        }

        private void ReleaseRescued(int clock)
        {
            List<NativeInput> releases = new List<NativeInput>();
            List<int> keys = new List<int>();
            for (int key = 0; key < rescuedReleaseAt.Length; key++)
            {
                if (rescuedReleaseAt[key] < 0 || clock < rescuedReleaseAt[key])
                    continue;
                if (pressed[key])
                {
                    releases.Add(CreateKeyboardInput(sessionKeys[key], true));
                    keys.Add(key);
                }
                rescuedReleaseAt[key] = -1;
            }
            if (releases.Count > 0)
            {
                Send(releases);
                transitionsSent += releases.Count;
                for (int index = 0; index < keys.Count; index++)
                    pressed[keys[index]] = false;
            }
        }

        private void LogFirstInput(int plannedTime)
        {
            if (firstBatchLogged) return;
            firstBatchLogged = true;
            log("first real Taiko key transition sent through SendInput; planned="
                + plannedTime + "ms, song-clock=" + lastClock + "ms");
        }

        private void SuspendSession(int clock)
        {
            if (suspended) return;
            TryReleaseAll(false);
            suspended = true;
            suspendedClock = clock;
            lastClock = clock;
            clockLastChangedTick = Environment.TickCount;
            log("live Taiko agent suspended at song-clock=" + clock + "ms");
        }

        private void StopForGate(string reason, object score, bool markCandidateHandled)
        {
            if (sessionScore != null)
                StopSession(reason, true, false);
            if (candidateScore != null)
                CancelCandidate(reason, markCandidateHandled);
            if (score == null)
                handledScore = null;
        }

        private void CancelCandidate(string reason, bool markHandled)
        {
            if (candidateScore == null) return;
            object score = candidateScore;
            if (markHandled) handledScore = score;
            log("live Taiko candidate cancelled: " + reason);
            ClearCandidate();
            EndTimerResolutionIfIdle();
        }

        private void StopSession(string reason, bool markHandled, bool forceAllReleases)
        {
            if (sessionScore == null) return;
            object score = sessionScore;
            string path = sessionPlan == null ? "<unknown>" : sessionPlan.Path;
            TryReleaseAll(forceAllReleases);
            if (markHandled) handledScore = score;
            log("live Taiko agent stopped: " + reason
                + "; map=" + path
                + ", batches=" + batchesSent
                + ", transitions=" + transitionsSent
                + ", skipped=" + transitionsSkipped
                + ", max-late=" + maximumObservedLateness + "ms"
                + (lateBatches > 0
                    ? ", late-batches=" + lateBatches
                        + ", late-recovery-inputs=" + lateRecoveryInputs
                    : String.Empty));
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
            if (sessionKeys == null || pressed == null) return;
            List<NativeInput> releases = new List<NativeInput>();
            for (int key = 0; key < sessionKeys.Count; key++)
            {
                if (forceAll || pressed[key])
                    releases.Add(CreateKeyboardInput(sessionKeys[key], true));
            }
            try
            {
                if (releases.Count > 0) Send(releases);
            }
            catch (Exception exception)
            {
                log("key release cleanup failed: " + UsefulMessage(exception));
            }
            finally
            {
                for (int key = 0; key < pressed.Length; key++)
                    pressed[key] = false;
            }
        }

        private List<LiveTaikoKeySpec> ResolveCurrentBindings()
        {
            string[] labels = new string[]
            {
                "InnerLeft", "InnerRight", "OuterLeft", "OuterRight"
            };
            List<LiveTaikoKeySpec> result = new List<LiveTaikoKeySpec>(4);
            HashSet<ushort> seen = new HashSet<ushort>();
            for (int index = 0; index < 4; index++)
            {
                object binding = Enum.ToObject(bindingEnumType, 6 + index);
                object key = bindingGetter.Invoke(null, new object[] { binding });
                ushort virtualKey = checked((ushort)Convert.ToUInt32(key));
                if (virtualKey == 0)
                    throw new InvalidOperationException("Taiko " + labels[index] + " is unbound");
                if (!seen.Add(virtualKey))
                    throw new InvalidOperationException("Taiko bindings contain a duplicate key");
                result.Add(new LiveTaikoKeySpec(labels[index] + ":" + key, virtualKey));
            }
            return result;
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
            if (pressed == null) return false;
            for (int index = 0; index < pressed.Length; index++)
                if (pressed[index]) return true;
            return false;
        }

        private bool HasPendingRescue()
        {
            if (rescuedReleaseAt == null) return false;
            for (int index = 0; index < rescuedReleaseAt.Length; index++)
                if (rescuedReleaseAt[index] >= 0) return true;
            return false;
        }

        private int ReadSongClock()
        {
            return Convert.ToInt32(songClockField.GetValue(null));
        }

        private void ObserveSubmission(object currentScore, bool modeMatches)
        {
            if (!submissionDiagnostics) return;

            if (observedScore != null
                && currentScore != null
                && !Object.ReferenceEquals(currentScore, observedScore))
            {
                SampleObservedSubmission();
                EndSubmissionObservation("score object replaced");
            }

            if (observedScore == null)
            {
                if (currentScore == null || !modeMatches) return;
                observedScore = currentScore;
                observedScoreInCurrentField = true;
                observedDetachedTick = 0;
                try
                {
                    ReadSubmissionSnapshot(
                        observedScore,
                        out observedValidity,
                        out observedLoggedIn,
                        out observedSubmissionState);
                    log("submission diag attached: validity=" + observedValidity
                        + ", logged-in=" + observedLoggedIn
                        + ", state=" + observedSubmissionState
                        + "; no account value was logged");
                }
                catch (Exception exception)
                {
                    log("submission diag could not attach: " + UsefulMessage(exception));
                    ClearSubmissionObservation();
                }
                return;
            }

            bool inCurrentField = Object.ReferenceEquals(currentScore, observedScore);
            if (inCurrentField != observedScoreInCurrentField)
            {
                observedScoreInCurrentField = inCurrentField;
                if (inCurrentField)
                {
                    observedDetachedTick = 0;
                    log("submission diag: observed score returned to the current-score field");
                }
                else
                {
                    observedDetachedTick = Environment.TickCount;
                    log("submission diag: current-score field detached; continuing read-only"
                        + " observation for up to 120 seconds");
                }
            }

            SampleObservedSubmission();
            if (!observedScoreInCurrentField
                && observedDetachedTick != 0
                && ElapsedSince(observedDetachedTick) >= SubmissionObservationTimeoutMilliseconds)
            {
                EndSubmissionObservation("post-score observation timeout");
            }
        }

        private void SampleObservedSubmission()
        {
            if (observedScore == null) return;
            try
            {
                bool validity;
                bool loggedIn;
                int submissionState;
                ReadSubmissionSnapshot(
                    observedScore,
                    out validity,
                    out loggedIn,
                    out submissionState);

                List<string> changes = new List<string>();
                if (validity != observedValidity)
                    changes.Add("validity " + observedValidity + "->" + validity);
                if (loggedIn != observedLoggedIn)
                    changes.Add("logged-in " + observedLoggedIn + "->" + loggedIn);
                if (submissionState != observedSubmissionState)
                    changes.Add("state " + observedSubmissionState + "->" + submissionState);

                observedValidity = validity;
                observedLoggedIn = loggedIn;
                observedSubmissionState = submissionState;
                if (changes.Count > 0)
                {
                    log("submission diag changed: " + String.Join(", ", changes.ToArray())
                        + "; song-clock=" + ReadSongClock() + "ms");
                }
            }
            catch (Exception exception)
            {
                log("submission diag stopped after read failure: " + UsefulMessage(exception));
                ClearSubmissionObservation();
            }
        }

        private void ReadSubmissionSnapshot(
            object score,
            out bool validity,
            out bool loggedIn,
            out int submissionState)
        {
            validity = Convert.ToBoolean(scoreValidityField.GetValue(score));
            loggedIn = Convert.ToBoolean(isLoggedIn.Invoke(null, null));
            submissionState = Convert.ToInt32(getScoreSubmissionState.Invoke(score, null));
        }

        private void EndSubmissionObservation(string reason)
        {
            if (observedScore == null) return;
            log("submission diag ended: " + reason
                + "; final validity=" + observedValidity
                + ", logged-in=" + observedLoggedIn
                + ", state=" + observedSubmissionState);
            ClearSubmissionObservation();
        }

        private void ClearSubmissionObservation()
        {
            observedScore = null;
            observedScoreInCurrentField = false;
            observedValidity = false;
            observedLoggedIn = false;
            observedSubmissionState = 0;
            observedDetachedTick = 0;
        }

        private bool IsCurrentProcessForeground()
        {
            IntPtr window = GetForegroundWindow();
            if (window == IntPtr.Zero) return false;
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
            if (!timerResolutionActive) return;
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
                throw new InvalidOperationException("beatmap metadata tokens failed validation");
            }
            if (!currentScoreField.IsStatic
                || !playerPausedField.IsStatic
                || playerPausedField.FieldType != typeof(bool)
                || !replayModeField.IsStatic
                || replayModeField.FieldType != typeof(bool)
                || !replayScoreField.IsStatic
                || replayModeField.DeclaringType != replayScoreField.DeclaringType
                || currentScoreField.FieldType != replayScoreField.FieldType
                || scoreValidityField.IsStatic
                || scoreValidityField.DeclaringType != currentScoreField.FieldType
                || scoreValidityField.FieldType != typeof(bool)
                || getScoreSubmissionState.IsStatic
                || getScoreSubmissionState.DeclaringType != currentScoreField.FieldType
                || getScoreSubmissionState.GetParameters().Length != 0
                || !getScoreSubmissionState.ReturnType.IsEnum
                || !isLoggedIn.IsStatic
                || isLoggedIn.GetParameters().Length != 0
                || isLoggedIn.ReturnType != typeof(bool))
            {
                throw new InvalidOperationException("score/replay metadata tokens failed validation");
            }
            if (!songClockField.IsStatic || songClockField.FieldType != typeof(int))
                throw new InvalidOperationException("song-clock metadata token failed validation");
            if (!globalOsuModeField.IsStatic
                || !globalOsuModeField.FieldType.IsEnum
                || !String.Equals(globalOsuModeField.FieldType.FullName, "osu.OsuModes", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("global osu mode metadata token failed validation");
            }
            ParameterInfo[] parameters = bindingGetter.GetParameters();
            if (!bindingGetter.IsStatic
                || parameters.Length != 1
                || !parameters[0].ParameterType.IsEnum
                || !String.Equals(parameters[0].ParameterType.FullName, "osu.Input.Bindings", StringComparison.Ordinal)
                || !bindingGetter.ReturnType.IsEnum
                || !String.Equals(bindingGetter.ReturnType.FullName, "Microsoft.Xna.Framework.Input.Keys", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("configured-binding getter failed validation");
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
            if (String.IsNullOrWhiteSpace(text)) return defaultValue;
            int value;
            if (!Int32.TryParse(text, out value) || value < minimum || value > maximum)
                throw new InvalidOperationException(name + " must be " + minimum + ".." + maximum);
            return value;
        }

        private static bool ReadBoolean(string name, bool defaultValue)
        {
            string text = Environment.GetEnvironmentVariable(name);
            if (String.IsNullOrWhiteSpace(text)) return defaultValue;
            return String.Equals(text, "1", StringComparison.Ordinal)
                || String.Equals(text, "true", StringComparison.OrdinalIgnoreCase)
                || String.Equals(text, "yes", StringComparison.OrdinalIgnoreCase)
                || String.Equals(text, "on", StringComparison.OrdinalIgnoreCase);
        }

        private static string DescribeKeys(List<LiveTaikoKeySpec> keys)
        {
            string[] names = new string[keys.Count];
            for (int index = 0; index < keys.Count; index++)
                names[index] = ((LiveTaikoKey)index) + "->" + keys[index].Name;
            return String.Join(",", names);
        }

        private static int ElapsedSince(int startTick)
        {
            return unchecked(Environment.TickCount - startTick);
        }

        private static string UsefulMessage(Exception exception)
        {
            TargetInvocationException invocation = exception as TargetInvocationException;
            return invocation != null && invocation.InnerException != null
                ? invocation.InnerException.ToString()
                : exception.ToString();
        }

        private static NativeInput CreateKeyboardInput(LiveTaikoKeySpec key, bool keyUp)
        {
            uint mapped = MapVirtualKeyW(key.VirtualKey, MapVkToScanCodeEx);
            if (mapped == 0) mapped = MapVirtualKeyW(key.VirtualKey, MapVkToScanCode);
            if (mapped == 0)
                throw new Win32Exception("Could not map " + key.Name + " to a scan code");
            byte prefix = (byte)((mapped >> 8) & 0xFF);
            uint flags = KeyEventScanCode;
            if (keyUp) flags |= KeyEventKeyUp;
            if (prefix == 0xE0 || prefix == 0xE1) flags |= KeyEventExtendedKey;
            NativeInput result = new NativeInput();
            result.Type = InputKeyboard;
            result.Union.Keyboard.VirtualKey = 0;
            result.Union.Keyboard.ScanCode = (ushort)(mapped & 0xFF);
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
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SendInput sent " + sent + "/" + array.Length);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeInput { public uint Type; public InputUnion Union; }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public MouseInput Mouse;
            [FieldOffset(0)] public KeyboardInput Keyboard;
            [FieldOffset(0)] public HardwareInput Hardware;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MouseInput
        {
            public int X; public int Y; public uint MouseData; public uint Flags; public uint Time; public UIntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KeyboardInput
        {
            public ushort VirtualKey; public ushort ScanCode; public uint Flags; public uint Time; public UIntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HardwareInput { public uint Message; public ushort ParameterLow; public ushort ParameterHigh; }

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
