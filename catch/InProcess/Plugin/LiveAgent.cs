using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace LocalCatchAgent.Plugin
{
    internal sealed class LiveAgent
    {
        private const int CurrentBeatmapMethodToken = 0x06002C63;
        private const int BeatmapPathMethodToken = 0x06001BF0;
        private const int CurrentScoreFieldToken = 0x040013C3;
        private const int PlayerPausedFieldToken = 0x0400136A;
        private const int CurrentPlayerFieldToken = 0x0400136D;
        private const int PlayerRulesetManagerFieldToken = 0x040013A4;
        private const int ReplayModeFieldToken = 0x04002A7C;
        private const int ReplayScoreFieldToken = 0x04002A7F;
        private const int SongClockFieldToken = 0x04002358;
        private const int GlobalOsuModeFieldToken = 0x04002C6D;
        private const int BindingGetterMethodToken = 0x06002C4F;

        private const int CatchManagerTypeToken = 0x020001BA;
        private const int ManagerObjectManagerFieldToken = 0x040005F4;
        private const int ManagerCatcherFieldToken = 0x040006DF;
        private const int ManagerCatcherWidthFieldToken = 0x040006E0;
        private const int ObjectManagerAllObjectsFieldToken = 0x040017FB;
        private const int FruitBaseTypeToken = 0x0200052C;
        private const int TinyDropletTypeToken = 0x0200031B;
        private const int DropletTypeToken = 0x0200088E;
        private const int BananaTypeToken = 0x0200081F;
        private const int FruitCaughtFieldToken = 0x04001745;
        private const int FruitHyperTargetFieldToken = 0x04001747;
        private const int HitObjectStartTimeFieldToken = 0x04002523;
        private const int HitObjectPositionFieldToken = 0x0400252C;
        private const int CatcherPositionFieldToken = 0x04002CF6;

        private const int CatchMode = 2;
        private const int GlobalOsuModePlay = 2;
        private const int RelaxBit = 0x80;
        private const int AutoplayBit = 0x800;
        private const int Relax2Bit = 0x2000;
        private const int CinemaBit = 0x400000;
        private const int ForbiddenAutomationMods = RelaxBit | AutoplayBit | Relax2Bit | CinemaBit;

        private const int BindingFruitsLeft = 11;
        private const int BindingFruitsRight = 12;
        private const int BindingFruitsDash = 13;
        private const int ObjectListStableMilliseconds = 120;
        private const int CandidateTimeoutMilliseconds = 10000;
        private const int ClockStallMilliseconds = 750;
        private const double HyperPreArmMilliseconds = 12.0;
        private const double FinalApproachMilliseconds = 40.0;

        private const uint InputKeyboard = 1;
        private const uint KeyEventExtendedKey = 0x0001;
        private const uint KeyEventKeyUp = 0x0002;
        private const uint KeyEventScanCode = 0x0008;
        private const uint MapVkToScanCode = 0;
        private const uint MapVkToScanCodeEx = 4;
        private static readonly UIntPtr InjectionMarker = new UIntPtr(0x43544348u); // "CTCH"

        private readonly Action<string> log;
        private readonly MethodInfo getCurrentBeatmap;
        private readonly MethodInfo getBeatmapPath;
        private readonly FieldInfo currentScoreField;
        private readonly FieldInfo playerPausedField;
        private readonly FieldInfo currentPlayerField;
        private readonly FieldInfo playerRulesetManagerField;
        private readonly FieldInfo replayModeField;
        private readonly FieldInfo replayScoreField;
        private readonly FieldInfo songClockField;
        private readonly FieldInfo globalOsuModeField;
        private readonly MethodInfo bindingGetter;
        private readonly Type bindingEnumType;
        private readonly Type catchManagerType;
        private readonly FieldInfo managerObjectManagerField;
        private readonly FieldInfo managerCatcherField;
        private readonly FieldInfo managerCatcherWidthField;
        private readonly FieldInfo objectManagerAllObjectsField;
        private readonly Type fruitBaseType;
        private readonly Type tinyDropletType;
        private readonly Type dropletType;
        private readonly Type bananaType;
        private readonly FieldInfo fruitCaughtField;
        private readonly FieldInfo fruitHyperTargetField;
        private readonly FieldInfo hitObjectStartTimeField;
        private readonly FieldInfo hitObjectPositionField;
        private readonly FieldInfo catcherPositionField;
        private readonly FieldInfo vectorXField;
        private readonly int processId;

        private object observedScore;
        private int observedObjectCount;
        private int observedLastObjectTime;
        private int observedSignature;
        private int observedStableSince;
        private int lastPreparationLogTick;

        private object candidateScore;
        private object candidateManager;
        private RuntimeCatchPlan candidatePlan;
        private List<CatchKeySpec> candidateKeys;
        private AgentOptionsSnapshot candidateOptions;
        private int candidatePreparedTick;

        private object sessionScore;
        private object sessionManager;
        private RuntimeCatchPlan sessionPlan;
        private List<CatchKeySpec> sessionKeys;
        private AgentOptionsSnapshot sessionOptions;
        private bool[] pressed;
        private int lastClock;
        private int clockLastChangedTick;
        private bool clockTrusted;
        private int clockProbeLast;
        private int clockProbeStartedTick;
        private int clockForwardSamples;
        private bool suspended;
        private int suspendedClock;
        private int transitionsSent;
        private int corrections;
        private int maximumTrackingError;
        private double[] observedWaypointX;
        private int nextObservedWaypoint;
        private bool firstInputLogged;
        private bool timerResolutionActive;
        private object handledScore;
        private bool shutdown;
        private string idleDetail = "waiting for Catch Player mode";

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
            currentPlayerField = module.ResolveField(CurrentPlayerFieldToken);
            playerRulesetManagerField = module.ResolveField(PlayerRulesetManagerFieldToken);
            replayModeField = module.ResolveField(ReplayModeFieldToken);
            replayScoreField = module.ResolveField(ReplayScoreFieldToken);
            songClockField = module.ResolveField(SongClockFieldToken);
            globalOsuModeField = module.ResolveField(GlobalOsuModeFieldToken);
            bindingGetter = RequireMethod(module.ResolveMethod(BindingGetterMethodToken), "binding getter");
            bindingEnumType = bindingGetter.GetParameters()[0].ParameterType;

            catchManagerType = module.ResolveType(CatchManagerTypeToken);
            managerObjectManagerField = module.ResolveField(ManagerObjectManagerFieldToken);
            managerCatcherField = module.ResolveField(ManagerCatcherFieldToken);
            managerCatcherWidthField = module.ResolveField(ManagerCatcherWidthFieldToken);
            objectManagerAllObjectsField = module.ResolveField(ObjectManagerAllObjectsFieldToken);
            fruitBaseType = module.ResolveType(FruitBaseTypeToken);
            tinyDropletType = module.ResolveType(TinyDropletTypeToken);
            dropletType = module.ResolveType(DropletTypeToken);
            bananaType = module.ResolveType(BananaTypeToken);
            fruitCaughtField = module.ResolveField(FruitCaughtFieldToken);
            fruitHyperTargetField = module.ResolveField(FruitHyperTargetFieldToken);
            hitObjectStartTimeField = module.ResolveField(HitObjectStartTimeFieldToken);
            hitObjectPositionField = module.ResolveField(HitObjectPositionFieldToken);
            catcherPositionField = module.ResolveField(CatcherPositionFieldToken);
            vectorXField = hitObjectPositionField.FieldType.GetField("X", BindingFlags.Instance | BindingFlags.Public);
            ValidateTargets();

            processId = System.Diagnostics.Process.GetCurrentProcess().Id;
            log("live Catch targets validated; manager=" + EscapedName(catchManagerType.FullName)
                + ", architecture=runtime converted objects + normal Player bindings + position feedback");
        }

        public bool IsTimingCritical
        {
            get { return candidateScore != null || sessionScore != null; }
        }

        public AgentRuntimeStatus GetRuntimeStatus()
        {
            if (sessionScore != null && sessionPlan != null)
            {
                int clock = lastClock;
                int next = sessionPlan.NextWaypointIndex(clock);
                string progress = next >= sessionPlan.Waypoints.Count
                    ? "complete"
                    : next + "/" + (sessionPlan.Waypoints.Count - 1)
                        + " next=" + sessionPlan.Waypoints[next].Time + "ms";
                return new AgentRuntimeStatus(
                    suspended ? "PAUSED" : clockTrusted ? "PLAYING" : "SYNC",
                    Path.GetFileName(sessionPlan.MapPath),
                    AgentOptionsSnapshot.StyleName(sessionOptions.Style)
                        + "  " + progress
                        + "  clock=" + clock + "ms"
                        + "  err<=" + maximumTrackingError + "px");
            }
            if (candidateScore != null && candidatePlan != null)
            {
                return new AgentRuntimeStatus(
                    "ARMED",
                    Path.GetFileName(candidatePlan.MapPath),
                    "runtime plan ready; waiting for song clock / foreground");
            }
            return AgentRuntimeStatus.Idle(idleDetail);
        }

        public void Tick(AgentOptionsSnapshot options, int playMode, int selectedMods)
        {
            if (shutdown) return;
            if (options == null) throw new ArgumentNullException("options");

            object score = currentScoreField.GetValue(null);
            if (score == null)
            {
                handledScore = null;
                ResetObservation();
            }

            if (!options.Enabled)
            {
                StopForGate("disabled", score, sessionScore != null);
                idleDetail = "Player mode: you are in control";
                return;
            }

            int globalMode = Convert.ToInt32(globalOsuModeField.GetValue(null));
            if (globalMode != GlobalOsuModePlay)
            {
                StopForGate("left osu.OsuModes.Play", score, true);
                idleDetail = "agent on; waiting for a Catch map";
                return;
            }
            if (playMode != CatchMode)
            {
                StopForGate("left Catch", score, true);
                idleDetail = "agent on; current ruleset is not Catch";
                return;
            }
            if ((selectedMods & ForbiddenAutomationMods) != 0)
            {
                StopForGate("automation/replay mod selected", score, true);
                idleDetail = "Relax/Auto/Relax2/Cinema is incompatible with Player-input mode";
                return;
            }
            if (score == null)
            {
                StopForGate("current score cleared", null, false);
                idleDetail = "agent on; waiting for normal Catch Player score";
                return;
            }

            bool replayMode = Convert.ToBoolean(replayModeField.GetValue(null));
            object replayScore = replayScoreField.GetValue(null);
            if (replayMode || replayScore != null)
            {
                StopForGate("replay source detected", score, true);
                idleDetail = "agent only runs in normal Player mode";
                return;
            }

            if (sessionScore != null && !Object.ReferenceEquals(score, sessionScore))
                StopSession("score object changed", true, false);
            if (candidateScore != null && !Object.ReferenceEquals(score, candidateScore))
                CancelCandidate("score object changed before arm", true);
            if (Object.ReferenceEquals(score, handledScore)) return;

            if (sessionScore == null && candidateScore == null)
                ObserveAndPrepare(score, options);
            if (candidateScore != null)
                TryArmCandidate(score);
            if (sessionScore != null)
                ExecuteTick(score);
        }

        public void EmergencyStop(string reason)
        {
            try
            {
                CancelCandidate(reason, false);
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

        private void ObserveAndPrepare(object score, AgentOptionsSnapshot options)
        {
            try
            {
                object manager = ReadCatchManager();
                if (manager == null)
                {
                    idleDetail = "Catch manager is still loading";
                    return;
                }

                double catcherWidth;
                List<RuntimeCatchObject> objects = ReadRuntimeObjects(manager, out catcherWidth);
                int lastTime = objects.Count == 0 ? Int32.MinValue : objects[objects.Count - 1].Time;
                int signature = ObjectSignature(objects);
                if (!Object.ReferenceEquals(observedScore, score)
                    || observedObjectCount != objects.Count
                    || observedLastObjectTime != lastTime
                    || observedSignature != signature)
                {
                    observedScore = score;
                    observedObjectCount = objects.Count;
                    observedLastObjectTime = lastTime;
                    observedSignature = signature;
                    observedStableSince = Environment.TickCount;
                    idleDetail = "reading stable converted object list (" + objects.Count + " objects)";
                    return;
                }
                if (ElapsedSince(observedStableSince) < ObjectListStableMilliseconds)
                    return;

                string path = ReadCurrentBeatmapPath();
                int seed = StableSeed(path, objects);
                if (!options.RepeatableVariation) seed ^= Environment.TickCount;
                RuntimeCatchPlan plan = BuildRobustPlan(
                    objects,
                    catcherWidth,
                    options,
                    seed,
                    path);
                List<CatchKeySpec> keys = ResolveCurrentBindings();

                candidateScore = score;
                candidateManager = manager;
                candidatePlan = plan;
                candidateKeys = keys;
                candidateOptions = options;
                candidatePreparedTick = Environment.TickCount;
                BeginTimerResolution();
                idleDetail = "runtime plan prepared";
                log("live Catch plan prepared from stable runtime list: map=" + path
                    + ", width=" + catcherWidth.ToString("0.###")
                    + ", radius=" + plan.CollisionRadius.ToString("0.###")
                    + ", safety=" + plan.SafetyMargin.ToString("0.##")
                    + ", fruit=" + plan.FruitCount
                    + ", droplet=" + plan.DropletCount
                    + ", tiny=" + plan.TinyDropletCount
                    + ", banana=" + plan.BananaCount
                    + ", constraints=" + (plan.Constraints.Count - 1)
                    + ", hyper=" + plan.HyperDashCount
                    + ", phases=" + plan.Controls.Count
                    + ", keys=" + DescribeKeys(keys));
                log("Catch path options: " + options.Describe()
                    + "; viability is hard, style variation is projected inside it");
            }
            catch (Exception exception)
            {
                idleDetail = "waiting for a complete Catch object list";
                if (lastPreparationLogTick == 0 || ElapsedSince(lastPreparationLogTick) >= 1000)
                {
                    lastPreparationLogTick = Environment.TickCount;
                    log("Catch plan not ready yet: " + UsefulMessage(exception));
                }
            }
        }

        private RuntimeCatchPlan BuildRobustPlan(
            List<RuntimeCatchObject> objects,
            double catcherWidth,
            AgentOptionsSnapshot options,
            int seed,
            string path)
        {
            double floor = options.SafetyMargin;
            RuntimeCatchPlan best = RuntimeCatchPlanner.Build(
                objects, catcherWidth, CopyWithSafety(options, floor), seed, path);
            double low = floor;
            double high = Math.Min(10.0, catcherWidth * 0.4 - 0.5);
            for (int iteration = 0; iteration < 7 && high - low >= 0.20; iteration++)
            {
                double candidate = Math.Floor(((low + high) * 0.5) * 4.0) / 4.0;
                if (candidate <= low) break;
                try
                {
                    RuntimeCatchPlan trial = RuntimeCatchPlanner.Build(
                        objects,
                        catcherWidth,
                        CopyWithSafety(options, candidate),
                        seed,
                        path);
                    best = trial;
                    low = candidate;
                }
                catch (InvalidOperationException)
                {
                    high = candidate;
                }
            }
            return best;
        }

        private static AgentOptionsSnapshot CopyWithSafety(
            AgentOptionsSnapshot source,
            double safetyMargin)
        {
            return new AgentOptionsSnapshot(
                source.Enabled,
                source.Style,
                safetyMargin,
                source.WanderPixels,
                source.TrackingDeadband,
                source.IncludeTinyDropletsAsHardConstraints,
                source.FatigueEnabled,
                source.RepeatableVariation);
        }

        private void TryArmCandidate(object score)
        {
            if (!Object.ReferenceEquals(score, candidateScore)) return;
            object manager = ReadCatchManager();
            if (!Object.ReferenceEquals(manager, candidateManager))
            {
                CancelCandidate("Catch manager changed before arm", false);
                return;
            }

            int clock = ReadSongClock();
            if (clock > candidatePlan.FirstObjectTime + 40)
            {
                if (ElapsedSince(candidatePreparedTick) >= CandidateTimeoutMilliseconds)
                {
                    handledScore = score;
                    log("Catch candidate was prepared after gameplay had started; restart the map to arm safely"
                        + " (clock=" + clock + "ms, first=" + candidatePlan.FirstObjectTime + "ms)");
                    ClearCandidate();
                    EndTimerResolutionIfIdle();
                }
                return;
            }
            if (!IsCurrentProcessForeground()) return;

            sessionScore = candidateScore;
            sessionManager = candidateManager;
            sessionPlan = candidatePlan;
            sessionKeys = candidateKeys;
            sessionOptions = candidateOptions;
            pressed = new bool[3];
            observedWaypointX = new double[sessionPlan.Waypoints.Count];
            for (int index = 0; index < observedWaypointX.Length; index++)
                observedWaypointX[index] = Double.NaN;
            nextObservedWaypoint = 1;
            lastClock = clock;
            clockLastChangedTick = Environment.TickCount;
            clockTrusted = false;
            clockProbeLast = clock;
            clockProbeStartedTick = Environment.TickCount;
            clockForwardSamples = 0;
            suspended = false;
            suspendedClock = 0;
            transitionsSent = 0;
            corrections = 0;
            maximumTrackingError = 0;
            firstInputLogged = false;
            ClearCandidate();
            log("live Catch agent armed in normal Player mode at song-clock=" + clock
                + "ms; first-object=" + sessionPlan.FirstObjectTime
                + "ms; no replay frames or Auto list were created");
        }

        private void ExecuteTick(object score)
        {
            if (!Object.ReferenceEquals(score, sessionScore))
            {
                StopSession("score object changed", true, false);
                return;
            }
            object manager = ReadCatchManager();
            if (!Object.ReferenceEquals(manager, sessionManager))
            {
                StopSession("Catch manager changed", true, false);
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
                log("live Catch agent resumed at song-clock=" + clock + "ms");
            }

            if (!clockTrusted)
            {
                QualifySongClock(clock);
                return;
            }

            if (clock < lastClock - 25)
            {
                StopSession("song clock moved backwards from " + lastClock + "ms to " + clock + "ms", true, false);
                return;
            }
            if (clock != lastClock)
            {
                lastClock = clock;
                clockLastChangedTick = Environment.TickCount;
            }
            else if (clock > sessionPlan.FirstObjectTime
                && ElapsedSince(clockLastChangedTick) >= ClockStallMilliseconds)
            {
                StopSession("song clock stalled without pause flag", true, false);
                return;
            }

            if (clock > sessionPlan.LastObjectTime + 1000)
            {
                StopSession("path completed", true, false);
                return;
            }

            double actualX = ReadCatcherX(sessionManager);
            RecordWaypointObservations(clock, actualX);
            ControlDecision decision = ComputeControl(clock, actualX);
            int trackingError = (int)Math.Ceiling(Math.Abs(decision.ReferenceX - actualX));
            if (trackingError > maximumTrackingError) maximumTrackingError = trackingError;
            SetDesiredKeys(decision.Left, decision.Right, decision.Dash, clock, actualX, decision.ReferenceX);
        }

        private void RecordWaypointObservations(int clock, double actualX)
        {
            if (observedWaypointX == null || sessionPlan == null) return;
            while (nextObservedWaypoint < sessionPlan.Waypoints.Count
                && sessionPlan.Waypoints[nextObservedWaypoint].Time <= clock)
            {
                observedWaypointX[nextObservedWaypoint] = actualX;
                nextObservedWaypoint++;
            }
        }

        private void QualifySongClock(int clock)
        {
            // During Catch construction stable exposes a convincing but false zero:
            // -1020 -> 0 -> -1020.  A one-sample range check cannot distinguish it
            // from gameplay.  We require several genuinely advancing samples over
            // real time before the controller is allowed to emit its first key.
            if (clock < clockProbeLast - 25 || clock > clockProbeLast + 500)
            {
                TryReleaseAll(false);
                clockProbeStartedTick = Environment.TickCount;
                clockForwardSamples = 0;
            }
            else if (clock > clockProbeLast)
            {
                clockForwardSamples++;
            }
            clockProbeLast = clock;
            lastClock = clock;
            clockLastChangedTick = Environment.TickCount;

            if (clockForwardSamples < 3 || ElapsedSince(clockProbeStartedTick) < 50)
                return;

            clockTrusted = true;
            log("Catch song clock qualified after " + clockForwardSamples
                + " forward samples; controller released at " + clock + "ms");
        }

        private ControlDecision ComputeControl(int clock, double actualX)
        {
            ControlDecision decision = new ControlDecision();
            decision.ReferenceX = sessionPlan.ReferenceX(clock + 1.5);
            int nextIndex = sessionPlan.NextWaypointIndex(clock);
            if (nextIndex >= sessionPlan.Waypoints.Count)
                return decision;

            RuntimeCatchWaypoint next = sessionPlan.Waypoints[nextIndex];
            RuntimeCatchControlPhase phase = sessionPlan.PhaseAt(clock + 1.5);
            double remaining = Math.Max(0.5, next.Time - clock);
            double targetError = next.X - actualX;
            double referenceError = decision.ReferenceX - actualX;
            double clearance = Math.Min(
                next.X - next.ObjectWindow.Minimum,
                next.ObjectWindow.Maximum - next.X);
            double deadband = Math.Min(
                sessionOptions.TrackingDeadband,
                Math.Max(0.50, clearance * 0.35));

            // Before a hyper source, next still denotes the source itself.  Looking
            // only for ArrivedByHyperDash starts one frame too late.  Pre-arm from
            // the source's outgoing edge while it is still the active constraint.
            // A chained source is different: it is also the target of the previous
            // hyperdash, whose residual speed can be several pixels per millisecond.
            // Reversing 12 ms early there can travel 40-60 px and miss the source.
            // Hold the incoming direction through collision; on the next controller
            // sample the outgoing target becomes `next` and supplies the new direction.
            if (next.DepartsByHyperDash
                && !next.ArrivedByHyperDash
                && nextIndex + 1 < sessionPlan.Waypoints.Count
                && clock >= next.Time - HyperPreArmMilliseconds)
            {
                RuntimeCatchWaypoint hyperTarget = sessionPlan.Waypoints[nextIndex + 1];
                SetDirection(decision, hyperTarget.X - actualX);
                decision.Dash = true;
                return decision;
            }

            if (next.ArrivedByHyperDash && clock < next.Time)
            {
                if (Math.Abs(targetError) > deadband)
                    SetDirection(decision, targetError);
                decision.Dash = true;
                return decision;
            }

            if (remaining <= FinalApproachMilliseconds)
            {
                if (Math.Abs(targetError) <= deadband) return decision;
                SetDirection(decision, targetError);
                decision.Dash = Math.Abs(targetError) / remaining > 0.46;
                corrections++;
                return decision;
            }

            int nominalDirection = phase == null ? 0 : IntentDirection(phase.Input);
            if (nominalDirection == 0)
            {
                // Stay still through planned slack.  Only intervene when the live
                // catcher has consumed that slack and needs a continuous recovery
                // arc.  This replaces the old 1 ms PWM chatter.
                double guardedRemaining = Math.Max(1.0, remaining - 8.0);
                double requiredSpeed = Math.Abs(targetError) / guardedRemaining;
                if (requiredSpeed <= 0.42 || Math.Abs(targetError) <= deadband)
                    return decision;
                SetDirection(decision, targetError);
                decision.Dash = requiredSpeed > 0.58;
                corrections++;
                return decision;
            }

            double alongReference = nominalDirection * referenceError;
            double rescueBand = Math.Max(7.0, sessionOptions.TrackingDeadband * 1.5);
            double aheadBand = Math.Max(9.0, sessionOptions.TrackingDeadband * 2.0);
            if (alongReference < -aheadBand)
            {
                // Do not reverse for a small overshoot: release and let the planned
                // path catch up.  Reversal is reserved for final approach only.
                return decision;
            }

            SetDirection(decision, nominalDirection);
            decision.Dash = phase.IsDash || alongReference > rescueBand;
            if (alongReference > rescueBand) corrections++;
            return decision;
        }

        private static int IntentDirection(CatchInputIntent intent)
        {
            if (intent == CatchInputIntent.WalkLeft
                || intent == CatchInputIntent.DashLeft
                || intent == CatchInputIntent.HyperDashLeft) return -1;
            if (intent == CatchInputIntent.WalkRight
                || intent == CatchInputIntent.DashRight
                || intent == CatchInputIntent.HyperDashRight) return 1;
            return 0;
        }

        private static void SetDirection(ControlDecision decision, double errorOrDirection)
        {
            decision.Left = errorOrDirection < 0.0;
            decision.Right = errorOrDirection > 0.0;
        }

        private object ReadCatchManager()
        {
            object player = currentPlayerField.GetValue(null);
            if (player == null) return null;
            object manager = playerRulesetManagerField.GetValue(player);
            if (manager == null || manager.GetType() != catchManagerType) return null;
            return manager;
        }

        private List<RuntimeCatchObject> ReadRuntimeObjects(object manager, out double catcherWidth)
        {
            catcherWidth = Convert.ToDouble(managerCatcherWidthField.GetValue(manager));
            if (catcherWidth <= 1.0 || catcherWidth > 512.0)
                throw new InvalidOperationException("runtime catcher width is not initialised");

            object objectManager = managerObjectManagerField.GetValue(manager);
            if (objectManager == null)
                throw new InvalidOperationException("runtime hit-object manager is null");
            IList list = objectManagerAllObjectsField.GetValue(objectManager) as IList;
            if (list == null)
                throw new InvalidOperationException("runtime all-object list is null");

            List<object> sources = new List<object>();
            int count = list.Count;
            for (int index = 0; index < count; index++)
            {
                object source = list[index];
                if (source != null && fruitBaseType.IsAssignableFrom(source.GetType())) sources.Add(source);
            }
            if (sources.Count == 0)
                throw new InvalidOperationException("runtime Catch object list is still empty");

            Dictionary<object, int> ids = new Dictionary<object, int>(ReferenceComparer.Instance);
            List<RuntimeCatchObject> result = new List<RuntimeCatchObject>(sources.Count);
            for (int index = 0; index < sources.Count; index++)
            {
                object source = sources[index];
                ids.Add(source, index);
                RuntimeCatchObject hitObject = new RuntimeCatchObject();
                hitObject.Id = index;
                hitObject.Time = Convert.ToInt32(hitObjectStartTimeField.GetValue(source));
                hitObject.X = ReadVectorX(hitObjectPositionField.GetValue(source));
                hitObject.Kind = ClassifyObject(source.GetType());
                hitObject.Source = source;
                if (hitObject.X < -0.01 || hitObject.X > 512.01)
                    throw new InvalidOperationException("runtime Catch object X is outside the playfield");
                result.Add(hitObject);
            }

            for (int index = 0; index < result.Count; index++)
            {
                object target = fruitHyperTargetField.GetValue(result[index].Source);
                int targetId;
                if (target != null && ids.TryGetValue(target, out targetId))
                    result[index].HyperTargetId = targetId;
            }
            result.Sort(CompareRuntimeObjects);
            return result;
        }

        private RuntimeCatchObjectKind ClassifyObject(Type type)
        {
            if (type == tinyDropletType) return RuntimeCatchObjectKind.TinyDroplet;
            if (type == dropletType) return RuntimeCatchObjectKind.Droplet;
            if (type == bananaType) return RuntimeCatchObjectKind.Banana;
            return RuntimeCatchObjectKind.Fruit;
        }

        private double ReadCatcherX(object manager)
        {
            object catcher = managerCatcherField.GetValue(manager);
            if (catcher == null) throw new InvalidOperationException("runtime catcher sprite is null");
            return ReadVectorX(catcherPositionField.GetValue(catcher));
        }

        private double ReadVectorX(object vector)
        {
            if (vector == null) throw new InvalidOperationException("boxed Vector2 is null");
            return Convert.ToDouble(vectorXField.GetValue(vector));
        }

        private string ReadCurrentBeatmapPath()
        {
            object beatmap = getCurrentBeatmap.Invoke(null, null);
            if (beatmap == null) throw new InvalidOperationException("current beatmap is null");
            string path = getBeatmapPath.Invoke(beatmap, null) as string;
            if (String.IsNullOrEmpty(path)) throw new InvalidOperationException("current beatmap path is empty");
            return path;
        }

        private List<CatchKeySpec> ResolveCurrentBindings()
        {
            int[] bindings = new int[] { BindingFruitsLeft, BindingFruitsRight, BindingFruitsDash };
            string[] labels = new string[] { "Left", "Right", "Dash" };
            List<CatchKeySpec> result = new List<CatchKeySpec>(3);
            HashSet<ushort> seen = new HashSet<ushort>();
            for (int index = 0; index < bindings.Length; index++)
            {
                object binding = Enum.ToObject(bindingEnumType, bindings[index]);
                object key = bindingGetter.Invoke(null, new object[] { binding });
                ushort virtualKey = checked((ushort)Convert.ToUInt32(key));
                if (virtualKey == 0)
                    throw new InvalidOperationException("Catch " + labels[index] + " is unbound");
                if (!seen.Add(virtualKey))
                    throw new InvalidOperationException("Catch bindings contain a duplicate key");
                result.Add(new CatchKeySpec(labels[index] + ":" + key, virtualKey));
            }
            return result;
        }

        private void SetDesiredKeys(
            bool left,
            bool right,
            bool dash,
            int clock,
            double actualX,
            double referenceX)
        {
            bool[] desired = new bool[] { left, right, dash };
            List<NativeInput> inputs = new List<NativeInput>();
            bool[] next = (bool[])pressed.Clone();

            // Release before pressing another direction.  This avoids one frame in
            // which stable sees both left and right and accumulates contradictory state.
            for (int index = 0; index < desired.Length; index++)
            {
                if (pressed[index] && !desired[index])
                {
                    inputs.Add(CreateKeyboardInput(sessionKeys[index], true));
                    next[index] = false;
                }
            }
            for (int index = 0; index < desired.Length; index++)
            {
                if (!pressed[index] && desired[index])
                {
                    inputs.Add(CreateKeyboardInput(sessionKeys[index], false));
                    next[index] = true;
                }
            }
            if (inputs.Count == 0) return;

            Send(inputs);
            transitionsSent += inputs.Count;
            Array.Copy(next, pressed, next.Length);
            if (!firstInputLogged)
            {
                firstInputLogged = true;
                log("first real Catch key transition sent through SendInput; song-clock=" + clock
                    + "ms, catcher-x=" + actualX.ToString("0.##")
                    + ", reference-x=" + referenceX.ToString("0.##"));
            }
        }

        private void SuspendSession(int clock)
        {
            if (suspended) return;
            TryReleaseAll(false);
            suspended = true;
            suspendedClock = clock;
            lastClock = clock;
            clockLastChangedTick = Environment.TickCount;
            log("live Catch agent suspended at song-clock=" + clock + "ms");
        }

        private void StopForGate(string reason, object score, bool markHandled)
        {
            if (sessionScore != null) StopSession(reason, markHandled, false);
            if (candidateScore != null) CancelCandidate(reason, markHandled);
            if (score == null) handledScore = null;
        }

        private void CancelCandidate(string reason, bool markHandled)
        {
            if (candidateScore == null) return;
            object score = candidateScore;
            if (markHandled) handledScore = score;
            log("live Catch candidate cancelled: " + reason);
            ClearCandidate();
            EndTimerResolutionIfIdle();
        }

        private void StopSession(string reason, bool markHandled, bool forceAllReleases)
        {
            if (sessionScore == null) return;
            object score = sessionScore;
            string path = sessionPlan == null ? "<unknown>" : sessionPlan.MapPath;
            string missedDetail;
            string caughtSummary = ReadCaughtSummary(out missedDetail);
            TryReleaseAll(forceAllReleases);
            if (markHandled) handledScore = score;
            log("live Catch agent stopped: " + reason
                + "; map=" + path
                + ", transitions=" + transitionsSent
                + ", corrections=" + corrections
                + ", max-tracking-error=" + maximumTrackingError + "px"
                + caughtSummary);
            if (!String.IsNullOrEmpty(missedDetail))
                log("Catch miss detail: " + missedDetail);
            sessionScore = null;
            sessionManager = null;
            sessionPlan = null;
            sessionKeys = null;
            sessionOptions = null;
            pressed = null;
            observedWaypointX = null;
            nextObservedWaypoint = 0;
            clockTrusted = false;
            clockProbeLast = 0;
            clockProbeStartedTick = 0;
            clockForwardSamples = 0;
            suspended = false;
            suspendedClock = 0;
            EndTimerResolutionIfIdle();
        }

        private void TryReleaseAll(bool forceAll)
        {
            if (sessionKeys == null || pressed == null) return;
            List<NativeInput> releases = new List<NativeInput>();
            for (int index = 0; index < sessionKeys.Count; index++)
            {
                if (forceAll || pressed[index])
                    releases.Add(CreateKeyboardInput(sessionKeys[index], true));
            }
            try
            {
                if (releases.Count > 0) Send(releases);
            }
            catch (Exception exception)
            {
                log("Catch key-release cleanup failed: " + UsefulMessage(exception));
            }
            finally
            {
                for (int index = 0; index < pressed.Length; index++) pressed[index] = false;
            }
        }

        private string ReadCaughtSummary(out string missedDetail)
        {
            missedDetail = String.Empty;
            if (sessionPlan == null || fruitCaughtField == null) return String.Empty;
            int[] due = new int[3];
            int[] caught = new int[3];
            try
            {
                HashSet<int> hyperTargets = new HashSet<int>();
                for (int index = 0; index < sessionPlan.Objects.Count; index++)
                {
                    int target = sessionPlan.Objects[index].HyperTargetId;
                    if (target >= 0) hyperTargets.Add(target);
                }
                System.Text.StringBuilder misses = new System.Text.StringBuilder();
                int missedLogged = 0;
                for (int index = 0; index < sessionPlan.Objects.Count; index++)
                {
                    RuntimeCatchObject hitObject = sessionPlan.Objects[index];
                    if (hitObject.Source == null || hitObject.Time > lastClock - 25) continue;
                    int bucket;
                    if (hitObject.Kind == RuntimeCatchObjectKind.Fruit) bucket = 0;
                    else if (hitObject.Kind == RuntimeCatchObjectKind.Droplet) bucket = 1;
                    else if (hitObject.Kind == RuntimeCatchObjectKind.TinyDroplet) bucket = 2;
                    else continue;
                    due[bucket]++;
                    bool wasCaught = Convert.ToBoolean(fruitCaughtField.GetValue(hitObject.Source));
                    if (wasCaught)
                    {
                        caught[bucket]++;
                    }
                    else if (missedLogged < 40)
                    {
                        if (misses.Length > 0) misses.Append(" | ");
                        int waypointIndex = FindWaypointIndex(hitObject.Time);
                        double observed = waypointIndex >= 0 && observedWaypointX != null
                            ? observedWaypointX[waypointIndex]
                            : Double.NaN;
                        double planned = waypointIndex >= 0
                            ? sessionPlan.Waypoints[waypointIndex].X
                            : Double.NaN;
                        string role = hitObject.HyperTargetId >= 0
                            ? "hyper-source"
                            : hyperTargets.Contains(hitObject.Id) ? "hyper-target" : "plain";
                        misses.Append(hitObject.Kind).Append('#').Append(hitObject.Id)
                            .Append('@').Append(hitObject.Time).Append("ms:x=")
                            .Append(hitObject.X.ToString("0.##"))
                            .Append(',').Append(role)
                            .Append(",catcher=").Append(observed.ToString("0.##"))
                            .Append(",plan=").Append(planned.ToString("0.##"));
                        missedLogged++;
                    }
                }
                missedDetail = misses.ToString();
                return ", observed-caught=F " + caught[0] + "/" + due[0]
                    + " D " + caught[1] + "/" + due[1]
                    + " T " + caught[2] + "/" + due[2];
            }
            catch (Exception exception)
            {
                missedDetail = String.Empty;
                return ", observed-caught=unavailable(" + exception.GetType().Name + ")";
            }
        }

        private int FindWaypointIndex(int time)
        {
            int low = 1;
            int high = sessionPlan.Waypoints.Count - 1;
            while (low <= high)
            {
                int middle = (low + high) / 2;
                int candidate = sessionPlan.Waypoints[middle].Time;
                if (candidate < time) low = middle + 1;
                else if (candidate > time) high = middle - 1;
                else return middle;
            }
            return -1;
        }

        private void ClearCandidate()
        {
            candidateScore = null;
            candidateManager = null;
            candidatePlan = null;
            candidateKeys = null;
            candidateOptions = null;
            candidatePreparedTick = 0;
        }

        private void ResetObservation()
        {
            observedScore = null;
            observedObjectCount = 0;
            observedLastObjectTime = 0;
            observedSignature = 0;
            observedStableSince = 0;
        }

        private void BeginTimerResolution()
        {
            if (!timerResolutionActive && TimeBeginPeriod(1) == 0) timerResolutionActive = true;
        }

        private void EndTimerResolutionIfIdle()
        {
            if (candidateScore == null && sessionScore == null) EndTimerResolution();
        }

        private void EndTimerResolution()
        {
            if (!timerResolutionActive) return;
            TimeEndPeriod(1);
            timerResolutionActive = false;
        }

        private bool IsCurrentProcessForeground()
        {
            IntPtr window = GetForegroundWindow();
            if (window == IntPtr.Zero) return false;
            uint foregroundProcessId;
            GetWindowThreadProcessId(window, out foregroundProcessId);
            return foregroundProcessId == (uint)processId;
        }

        private int ReadSongClock()
        {
            return Convert.ToInt32(songClockField.GetValue(null));
        }

        private void ValidateTargets()
        {
            if (!getCurrentBeatmap.IsStatic
                || getCurrentBeatmap.GetParameters().Length != 0
                || getBeatmapPath.IsStatic
                || getBeatmapPath.GetParameters().Length != 0
                || getBeatmapPath.ReturnType != typeof(string))
                throw new InvalidOperationException("beatmap metadata tokens failed validation");

            if (!currentScoreField.IsStatic
                || !playerPausedField.IsStatic
                || playerPausedField.FieldType != typeof(bool)
                || !currentPlayerField.IsStatic
                || playerRulesetManagerField.IsStatic
                || playerRulesetManagerField.DeclaringType != currentPlayerField.FieldType
                || !replayModeField.IsStatic
                || replayModeField.FieldType != typeof(bool)
                || !replayScoreField.IsStatic
                || replayModeField.DeclaringType != replayScoreField.DeclaringType
                || currentScoreField.FieldType != replayScoreField.FieldType)
                throw new InvalidOperationException("Player/score/replay metadata tokens failed validation");

            if (!songClockField.IsStatic || songClockField.FieldType != typeof(int))
                throw new InvalidOperationException("song-clock metadata token failed validation");
            if (!globalOsuModeField.IsStatic
                || !globalOsuModeField.FieldType.IsEnum
                || !String.Equals(globalOsuModeField.FieldType.FullName, "osu.OsuModes", StringComparison.Ordinal))
                throw new InvalidOperationException("global osu mode metadata token failed validation");

            if (catchManagerType == null
                || managerObjectManagerField.IsStatic
                || !managerObjectManagerField.DeclaringType.IsAssignableFrom(catchManagerType)
                || managerCatcherField.IsStatic
                || managerCatcherField.DeclaringType != catchManagerType
                || managerCatcherWidthField.IsStatic
                || managerCatcherWidthField.DeclaringType != catchManagerType
                || managerCatcherWidthField.FieldType != typeof(float)
                || objectManagerAllObjectsField.IsStatic
                || objectManagerAllObjectsField.DeclaringType != managerObjectManagerField.FieldType)
                throw new InvalidOperationException("Catch manager metadata tokens failed validation");

            if (fruitBaseType == null
                || !fruitBaseType.IsAssignableFrom(tinyDropletType)
                || !fruitBaseType.IsAssignableFrom(dropletType)
                || !fruitBaseType.IsAssignableFrom(bananaType)
                || fruitCaughtField.IsStatic
                || fruitCaughtField.DeclaringType != fruitBaseType
                || fruitCaughtField.FieldType != typeof(bool)
                || fruitHyperTargetField.IsStatic
                || fruitHyperTargetField.DeclaringType != fruitBaseType
                || hitObjectStartTimeField.IsStatic
                || hitObjectStartTimeField.FieldType != typeof(int)
                || hitObjectPositionField.IsStatic
                || catcherPositionField.IsStatic
                || hitObjectPositionField.FieldType != catcherPositionField.FieldType
                || vectorXField == null
                || vectorXField.FieldType != typeof(float))
                throw new InvalidOperationException("Catch object/Vector2 metadata tokens failed validation");

            ParameterInfo[] parameters = bindingGetter.GetParameters();
            if (!bindingGetter.IsStatic
                || parameters.Length != 1
                || !parameters[0].ParameterType.IsEnum
                || !String.Equals(parameters[0].ParameterType.FullName, "osu.Input.Bindings", StringComparison.Ordinal)
                || !bindingGetter.ReturnType.IsEnum
                || !String.Equals(bindingGetter.ReturnType.FullName, "Microsoft.Xna.Framework.Input.Keys", StringComparison.Ordinal))
                throw new InvalidOperationException("configured-binding getter failed validation");
        }

        private static int CompareRuntimeObjects(RuntimeCatchObject left, RuntimeCatchObject right)
        {
            int time = left.Time.CompareTo(right.Time);
            return time != 0 ? time : left.Id.CompareTo(right.Id);
        }

        private static int ObjectSignature(List<RuntimeCatchObject> objects)
        {
            unchecked
            {
                int hash = 17;
                for (int index = 0; index < objects.Count; index++)
                {
                    RuntimeCatchObject hitObject = objects[index];
                    hash = hash * 31 + hitObject.Time;
                    hash = hash * 31 + (int)Math.Round(hitObject.X * 16.0);
                    hash = hash * 31 + (int)hitObject.Kind;
                    hash = hash * 31 + hitObject.HyperTargetId;
                }
                return hash;
            }
        }

        private static int StableSeed(string path, List<RuntimeCatchObject> objects)
        {
            unchecked
            {
                int hash = unchecked((int)2166136261u);
                string text = path ?? String.Empty;
                for (int index = 0; index < text.Length; index++) hash = (hash ^ text[index]) * 16777619;
                hash = (hash ^ ObjectSignature(objects)) * 16777619;
                return hash;
            }
        }

        private static string DescribeKeys(List<CatchKeySpec> keys)
        {
            string[] names = new string[keys.Count];
            for (int index = 0; index < keys.Count; index++) names[index] = keys[index].Name;
            return String.Join(",", names);
        }

        private static string EscapedName(string value)
        {
            if (value == null) return String.Empty;
            System.Text.StringBuilder builder = new System.Text.StringBuilder();
            for (int index = 0; index < value.Length; index++)
            {
                char character = value[index];
                if (character >= 0x20 && character <= 0x7e) builder.Append(character);
                else builder.Append("\\u" + ((int)character).ToString("x4"));
            }
            return builder.ToString();
        }

        private static MethodInfo RequireMethod(MethodBase method, string label)
        {
            MethodInfo result = method as MethodInfo;
            if (result == null) throw new InvalidOperationException(label + " token did not resolve to MethodInfo");
            return result;
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

        private static NativeInput CreateKeyboardInput(CatchKeySpec key, bool keyUp)
        {
            uint mapped = MapVirtualKeyW(key.VirtualKey, MapVkToScanCodeEx);
            if (mapped == 0) mapped = MapVirtualKeyW(key.VirtualKey, MapVkToScanCode);
            if (mapped == 0) throw new Win32Exception("Could not map " + key.Name + " to a scan code");
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

        private sealed class CatchKeySpec
        {
            public CatchKeySpec(string name, ushort virtualKey)
            {
                Name = name;
                VirtualKey = virtualKey;
            }

            public readonly string Name;
            public readonly ushort VirtualKey;
        }

        private sealed class ControlDecision
        {
            public bool Left;
            public bool Right;
            public bool Dash;
            public double ReferenceX;
        }

        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceComparer Instance = new ReferenceComparer();
            public new bool Equals(object left, object right) { return Object.ReferenceEquals(left, right); }
            public int GetHashCode(object value) { return RuntimeHelpers.GetHashCode(value); }
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
