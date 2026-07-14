# Reconstructing osu!stable's native mania Auto model

## Result

In the analysed stable build, mania Auto does not directly assign a judgement to each note. It
constructs a replay-frame timeline and lets the normal replay/input consumer drive gameplay. Mania
repurposes replay-frame `x` as an integer aggregate key mask; replay-frame `y` carries scroll speed.

The recovered rules are:

- lane `n` is bit `1 << n`, with the leftmost lane at bit zero;
- a tap sets its bit at `startTime` and clears it at `startTime + 1`;
- a long note sets its bit at `startTime` and clears it at `endTime - 1`;
- simultaneous changes merge into the same frame mask;
- when a long note is inserted, its bit is propagated through every existing frame strictly inside
  its interval;
- the special Coop layout rotates the lane mapping by one column; ordinary native Mode 3 does not.

## Working through obfuscation

Names were not treated as evidence. Most application types and methods had opaque identifiers, but
the following anchors survived and were enough to recover the graph:

1. `osu_common.Mods` retained enum values such as `Autoplay = 0x800`.
2. `osu_common.PlayModes` retained `OsuMania = 3`.
3. Player's ruleset switch constructed one opaque gameplay component in the mania branch.
4. Four gameplay components overrode the same virtual method when Autoplay was enabled. The object
   returned by that method had replay-frame-like fields and constructors.
5. The mania override iterated hit objects, called a column method returning a power of two, and
   stored the result in a float field.
6. The normal Player recorder later wrote the aggregate live mania lane state into the same float
   field, confirming that the value was a key mask rather than a coordinate.

The useful semantic map was therefore derived from behavior:

| Opaque role | Evidence used to identify it |
|---|---|
| mania gameplay component | Constructed by Player's `OsuMania` branch; overrides the Auto-frame generator |
| mania hit object | Carries start/end time, lane, and hold/tap state |
| mania column | Polls two configured keys; its mask getter returns `2^columnIndex` |
| replay frame | Constructor and fields match time, x, y, and button state |
| Player | Owns mode selection, normal input recording, and score/replay references |
| mania object manager | Converts parsed hit objects into mania tap/hold runtime objects |

This approach was more reliable than trying to globally rename the decompiler output. A semantic
name was assigned only after multiple independent references agreed.

## Clean pseudocode

```csharp
frames = [new Frame(time: 0, keyMask: 0, scrollSpeed)];

foreach (note in hitObjects)
{
    int bit = 1 << note.lane;
    ToggleAt(frames, note.startTime, bit, down: true);

    if (note.isHold)
    {
        foreach (frame in frames.Where(f => note.startTime < f.time && f.time < note.endTime))
            frame.keyMask |= bit;

        ToggleAt(frames, note.endTime - 1, bit, down: false);
    }
    else
    {
        ToggleAt(frames, note.startTime + 1, bit, down: false);
    }
}
```

`ToggleAt` finds the last frame with `frame.time <= targetTime`. If a frame already exists at the
target time, it modifies that mask. Otherwise it copies the previous mask, changes one bit, and
inserts the new frame in time order. This copy-and-toggle behavior is what preserves unrelated lanes
through a long note.

## Native `.osu` mapping

The readable parser accepts native mania only:

```text
[General] Mode: 3
[Difficulty] CircleSize: integer key count
```

For each `[HitObjects]` line:

```text
lane = clamp(floor(x * keyCount / 512), 0, keyCount - 1)
tap  = (type & 1) != 0
hold = (type & 128) != 0
```

For a long note, the sixth comma-separated field contains `endTime` before its first colon. Hit
object times are already absolute beatmap milliseconds. Timing points are not integrated to locate
objects; they affect musical interpretation and scroll behavior, not the stored object timestamps.

## Two independent implementations

The algorithm was implemented twice:

- `ManiaAuto/ReplayFrames.cs`, a readable .NET 8 baseline;
- `InProcess/Plugin/NativeFrameBuilder.cs`, a .NET Framework 4 version compiled with the plugin
  toolchain.

The parity scripts export `(time, mask)` from both implementations, normalize line endings, hash
the complete output, and compare every locally supplied Mode 3 map. The development corpus passed
13/13 maps, ranging from 340 to 4,180 replay frames. This was full-output parity, not a first-frame
spot check.

## Why the real-input prototype holds taps longer

The replay consumer sees a one-millisecond pulse as part of its own update stream. A physical key
state polled by gameplay can miss a one-millisecond `SendInput` pulse if both transitions fall
between updates. The external and in-process real-input planners therefore default to an eight-
millisecond tap. If the next object on the same lane begins sooner, release is moved to one
millisecond before that object.

That distinction became the turning point of the project: matching Auto frames proved the model,
but replay generation was not the desired final architecture. The active plugin now uses the native
Player input path described in [live-agent.md](live-agent.md).
