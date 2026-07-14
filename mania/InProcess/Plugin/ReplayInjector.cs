using System;
using System.Collections;
using System.Reflection;

namespace LocalManiaAuto.Plugin
{
    internal sealed class ReplayInjector
    {
        private const int CurrentBeatmapMethodToken = 0x06002C63;
        private const int BeatmapPathMethodToken = 0x06001BF0;
        private const int CurrentScoreFieldToken = 0x040013C3;
        private const int ReplayScoreFieldToken = 0x04002A7F;
        private const int FramesFieldToken = 0x04001980;
        private const int FrameConstructorToken = 0x0600219B;
        private const int FrameMaskFieldToken = 0x04001307;
        private const int FrameScrollSpeedFieldToken = 0x04001308;
        private const int FrameTimeFieldToken = 0x04001310;

        private readonly Action<string> log;
        private readonly MethodInfo getCurrentBeatmap;
        private readonly MethodInfo getBeatmapPath;
        private readonly FieldInfo currentScoreField;
        private readonly FieldInfo replayScoreField;
        private readonly FieldInfo framesField;
        private readonly ConstructorInfo frameConstructor;
        private readonly FieldInfo frameMaskField;
        private readonly FieldInfo frameScrollSpeedField;
        private readonly FieldInfo frameTimeField;
        private readonly Type buttonStateType;

        private object candidateScore;
        private object candidateFrames;
        private int candidateCount;
        private int stableTicks;
        private object processedScore;
        private object injectedFrames;
        private object rejectedFrames;

        public ReplayInjector(Assembly game, Action<string> logger)
        {
            log = logger;
            Module module = game.ManifestModule;
            getCurrentBeatmap = RequireMethod(module.ResolveMethod(CurrentBeatmapMethodToken), "current beatmap getter");
            getBeatmapPath = RequireMethod(module.ResolveMethod(BeatmapPathMethodToken), "beatmap path getter");
            currentScoreField = module.ResolveField(CurrentScoreFieldToken);
            replayScoreField = module.ResolveField(ReplayScoreFieldToken);
            framesField = module.ResolveField(FramesFieldToken);
            frameConstructor = module.ResolveMethod(FrameConstructorToken) as ConstructorInfo;
            frameMaskField = module.ResolveField(FrameMaskFieldToken);
            frameScrollSpeedField = module.ResolveField(FrameScrollSpeedFieldToken);
            frameTimeField = module.ResolveField(FrameTimeFieldToken);

            if (frameConstructor == null)
                throw new InvalidOperationException("replay frame constructor token did not resolve to ConstructorInfo");
            ParameterInfo[] parameters = frameConstructor.GetParameters();
            if (parameters.Length != 4
                || parameters[0].ParameterType != typeof(int)
                || parameters[1].ParameterType != typeof(float)
                || parameters[2].ParameterType != typeof(float)
                || !parameters[3].ParameterType.IsEnum)
            {
                throw new InvalidOperationException("replay frame constructor failed structural validation");
            }
            buttonStateType = parameters[3].ParameterType;

            Type frameType = frameConstructor.DeclaringType;
            Type[] genericArguments = framesField.FieldType.GetGenericArguments();
            if (!currentScoreField.IsStatic
                || !replayScoreField.IsStatic
                || genericArguments.Length != 1
                || genericArguments[0] != frameType
                || frameMaskField.DeclaringType != frameType
                || frameMaskField.FieldType != typeof(float)
                || frameScrollSpeedField.DeclaringType != frameType
                || frameScrollSpeedField.FieldType != typeof(float)
                || frameTimeField.DeclaringType != frameType
                || frameTimeField.FieldType != typeof(int)
                || !getCurrentBeatmap.IsStatic
                || getCurrentBeatmap.GetParameters().Length != 0
                || getBeatmapPath.IsStatic
                || getBeatmapPath.GetParameters().Length != 0
                || getBeatmapPath.ReturnType != typeof(string))
            {
                throw new InvalidOperationException("replay injection metadata tokens failed structural validation");
            }

            log("custom replay targets validated");
        }

        public void Tick()
        {
            object score = currentScoreField.GetValue(null);
            if (score == null)
            {
                ResetCandidate();
                return;
            }

            object framesObject = framesField.GetValue(score);
            IList frames = framesObject as IList;
            if (frames == null || frames.Count == 0)
            {
                ResetCandidate();
                return;
            }

            if (Object.ReferenceEquals(score, processedScore)
                && Object.ReferenceEquals(framesObject, injectedFrames))
                return;
            if (Object.ReferenceEquals(framesObject, rejectedFrames))
                return;

            if (!Object.ReferenceEquals(score, candidateScore)
                || !Object.ReferenceEquals(framesObject, candidateFrames)
                || frames.Count != candidateCount)
            {
                candidateScore = score;
                candidateFrames = framesObject;
                candidateCount = frames.Count;
                stableTicks = 0;
                return;
            }

            stableTicks++;
            if (stableTicks < 2)
                return;

            TryReplace(score, framesObject, frames);
        }

        public void ResetInactive()
        {
            ResetCandidate();
            rejectedFrames = null;
        }

        private void TryReplace(object score, object originalFramesObject, IList originalFrames)
        {
            try
            {
                object beatmap = getCurrentBeatmap.Invoke(null, null);
                if (beatmap == null)
                    throw new InvalidOperationException("current beatmap is null");
                string path = getBeatmapPath.Invoke(beatmap, null) as string;
                ParsedManiaMap parsed = NativeFrameBuilder.ParseAndBuild(path);

                string mismatch;
                if (!Matches(originalFrames, parsed.Frames, out mismatch))
                {
                    rejectedFrames = originalFramesObject;
                    log("custom replay parity rejected for " + path + ": " + mismatch
                        + "; keeping the game's original Auto frames");
                    return;
                }

                float scrollSpeed = Convert.ToSingle(frameScrollSpeedField.GetValue(originalFrames[0]));
                object none = Enum.ToObject(buttonStateType, 0);
                IList replacement = Activator.CreateInstance(framesField.FieldType) as IList;
                if (replacement == null)
                    throw new InvalidOperationException("could not instantiate typed replay frame list");

                object[] constructorArguments = new object[4];
                constructorArguments[2] = scrollSpeed;
                constructorArguments[3] = none;
                for (int index = 0; index < parsed.Frames.Count; index++)
                {
                    NativeFrameData frame = parsed.Frames[index];
                    constructorArguments[0] = frame.Time;
                    constructorArguments[1] = (float)frame.KeyMask;
                    replacement.Add(frameConstructor.Invoke(constructorArguments));
                }

                if (!Object.ReferenceEquals(framesField.GetValue(score), originalFramesObject)
                    || originalFrames.Count != candidateCount)
                {
                    ResetCandidate();
                    return;
                }

                object replayScore = replayScoreField.GetValue(null);
                if (replayScore != null
                    && Object.ReferenceEquals(framesField.GetValue(replayScore), originalFramesObject))
                {
                    framesField.SetValue(replayScore, replacement);
                }
                framesField.SetValue(score, replacement);

                processedScore = score;
                injectedFrames = replacement;
                candidateScore = null;
                candidateFrames = null;
                rejectedFrames = null;
                log("custom replay injected: map=" + path
                    + ", objects=" + parsed.ObjectCount
                    + ", frames=" + parsed.Frames.Count
                    + ", keys=" + parsed.KeyCount
                    + ", first=" + parsed.FirstObjectTime + "ms");
            }
            catch (Exception exception)
            {
                rejectedFrames = originalFramesObject;
                log("custom replay injection failed; keeping original Auto frames: " + UsefulMessage(exception));
            }
        }

        private bool Matches(IList original, System.Collections.Generic.List<NativeFrameData> generated, out string mismatch)
        {
            if (original.Count != generated.Count)
            {
                mismatch = "frame count " + original.Count + " != " + generated.Count;
                return false;
            }

            for (int index = 0; index < generated.Count; index++)
            {
                object originalFrame = original[index];
                int time = Convert.ToInt32(frameTimeField.GetValue(originalFrame));
                int mask = checked((int)Convert.ToSingle(frameMaskField.GetValue(originalFrame)));
                NativeFrameData generatedFrame = generated[index];
                if (time != generatedFrame.Time || mask != generatedFrame.KeyMask)
                {
                    mismatch = "frame[" + index + "] game=(" + time + "," + mask
                        + ") custom=(" + generatedFrame.Time + "," + generatedFrame.KeyMask + ")";
                    return false;
                }
            }

            mismatch = null;
            return true;
        }

        private static MethodInfo RequireMethod(MethodBase method, string label)
        {
            MethodInfo result = method as MethodInfo;
            if (result == null)
                throw new InvalidOperationException(label + " token did not resolve to MethodInfo");
            return result;
        }

        private static string UsefulMessage(Exception exception)
        {
            TargetInvocationException invocation = exception as TargetInvocationException;
            if (invocation != null && invocation.InnerException != null)
                return invocation.InnerException.ToString();
            return exception.ToString();
        }

        private void ResetCandidate()
        {
            candidateScore = null;
            candidateFrames = null;
            candidateCount = 0;
            stableTicks = 0;
        }
    }
}
