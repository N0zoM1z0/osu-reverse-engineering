namespace LocalManiaAuto;

internal sealed record ReplayFrame(int Time, int KeyMask);

internal static class ReplayFrameBuilder
{
    // Mirrors the stable client algorithm recovered from the local managed assembly:
    // lane mask = 1 << lane; tap release = start + 1; hold release = end - 1.
    public static IReadOnlyList<ReplayFrame> Build(ManiaBeatmap beatmap)
    {
        var frames = new List<ReplayFrame> { new(0, 0) };

        foreach (ManiaHitObject hitObject in beatmap.HitObjects)
        {
            int bit = 1 << hitObject.Lane;
            ToggleAt(frames, hitObject.StartTime, bit);

            if (hitObject.IsHold)
            {
                for (int index = 0; index < frames.Count; index++)
                {
                    ReplayFrame frame = frames[index];
                    if (frame.Time > hitObject.StartTime && frame.Time < hitObject.EndTime)
                    {
                        frames[index] = frame with { KeyMask = Toggle(frame.KeyMask, bit) };
                    }
                }
                ToggleAt(frames, hitObject.EndTime - 1, -bit);
            }
            else
            {
                ToggleAt(frames, hitObject.StartTime + 1, -bit);
            }
        }

        return frames;
    }

    private static void ToggleAt(List<ReplayFrame> frames, int time, int signedBit)
    {
        int previousIndex = FindLastIndexAtOrBefore(frames, time);
        if (previousIndex < 0)
        {
            frames.Add(new ReplayFrame(time, Toggle(0, signedBit)));
            frames.Sort(static (left, right) => left.Time.CompareTo(right.Time));
            return;
        }

        if (time == 0)
        {
            frames.Insert(previousIndex + 1, new ReplayFrame(0, Toggle(frames[previousIndex].KeyMask, signedBit)));
        }
        else if (frames[previousIndex].Time == time)
        {
            ReplayFrame frame = frames[previousIndex];
            frames[previousIndex] = frame with { KeyMask = Toggle(frame.KeyMask, signedBit) };
        }
        else
        {
            frames.Insert(previousIndex + 1, new ReplayFrame(time, Toggle(frames[previousIndex].KeyMask, signedBit)));
        }
    }

    private static int FindLastIndexAtOrBefore(IReadOnlyList<ReplayFrame> frames, int time)
    {
        int low = 0;
        int high = frames.Count - 1;
        int result = -1;
        while (low <= high)
        {
            int middle = low + ((high - low) / 2);
            if (frames[middle].Time <= time)
            {
                result = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }
        return result;
    }

    private static int Toggle(int mask, int signedBit)
    {
        if (signedBit > 0)
        {
            return (mask & signedBit) == 0 ? mask | signedBit : mask & ~signedBit;
        }

        int bit = -signedBit;
        return (mask & bit) != 0 ? mask & ~bit : mask;
    }
}
