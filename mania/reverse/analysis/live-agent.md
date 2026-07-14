# osu!stable mania Player-mode live agent

Target: local `osu!.exe` 1.3.3.8, SHA-256
`6e182c10d1813209d12753dbc70b3a5bba00fef4ecf64bc42051870e6dfe4b7d`.

This document records the transition from the v0.2 replay-list proof to the v0.5.0 agent that
plays through the normal keyboard path. The target is deliberately hash-locked. Metadata tokens
below must not be reused on another build without re-analysis.

## What "live agent" means here

Planning may parse the whole `.osu` file before the first note. Execution is still live:

1. Remain in normal `osu.OsuModes.Play` and `osu_common.PlayModes.OsuMania`.
2. Never set `Autoplay`, `Cinema`, `Relax`, or `Relax2`.
3. Reject both the replay-mode flag and replay-source score.
4. Read the game's current song clock every worker tick.
5. When a planned transition becomes due, emit a real scan-code key down/up with Win32
   `SendInput`.
6. Let the ordinary mania columns, hit judgement, health/combo, and normal replay recorder observe
   those keyboard states.

No replay-frame list is created, replaced, or consumed by v0.5.0. The old `ReplayInjector.cs` is
retained as reverse-engineering evidence but is excluded from `LocalManiaAuto.Plugin.dll`.

## Recovered normal input route

In the local ILSpy tree, the mania column class was emitted as the opaque file
`--zbl6TCg55jzbJhj7SJQWu5u6llo6ljjgHORfJgOuNQikB4MBO-A--.cs`.

Its update method `#=z0qON8zE=` polls the configured primary and secondary keyboard keys, then calls
`#=ztihpWvblpGV9(bool)`. That setter updates the previous/current lane state and the normal column
visual state. `#=zhergNwfG8qLQ()` maps a column to `1 << columnIndex`.

The Player class was emitted as `--zOzP1rPg3E8uUj1uRyHI8VvJbIxgp.cs`. The complete decompiler tree
is intentionally not distributed; these opaque names are included only as reproducibility indexes
for readers who generate their own local tree.

Around the normal replay-recorder block, mania records the live aggregate key mask together with
the same song clock. This is important: a v0.3 key is first consumed as current player input; any
replay data written afterwards is the ordinary recorder observing that input, not the source of it.

## Runtime targets

| Token | Shape | Recovered purpose |
|---|---|---|
| `0x04000CC6` | static `osu_common.Mods` | selected mods; read-only gate |
| `0x06002232` | static `osu_common.PlayModes ()` | require `OsuMania` (`3`) |
| `0x04002C6D` | static `osu.OsuModes` | require `Play` (`2`) |
| `0x04002A7C` | static `bool` | replay-mode flag; require false |
| `0x04002A7F` | static score reference | replay source; require null |
| `0x040013C3` | static score reference | current Player score/session identity |
| `0x06002C63` | static beatmap getter | obtain current beatmap object |
| `0x06001BF0` | instance `string ()` | obtain current `.osu` path |
| `0x04002358` | static `int` | gameplay song clock in milliseconds |
| `0x06002B5A` | instance `void ()` | mark score invalid/ineligible |
| `0x04001990` | instance `bool` | score validity flag cleared above |

The metadata probe validates static/instance shape, declaring and return types, the x86 Win32
`INPUT` layout (28 bytes), and absence of the old `ReplayInjector` type from the active plugin.

## Score-submission safety gate

The score method at `0x06002B5A` decompiles to one assignment:

```csharp
internal void MarkInvalid()
{
    validityFlag = false;
}
```

Its exact IL in the locked target is:

```text
02 16 7D 90 19 00 04 2A
ldarg.0; ldc.i4.0; stfld 0x04001990; ret
```

Player's score eligibility predicates require `0x04001990 == true`. v0.3 checks the exact IL,
invokes the method on the new score object before arming, and reads `0x04001990` back. Any mismatch,
exception, or still-true result refuses the session before `SendInput` can run. This avoids changing
gameplay mods merely to make a score unranked.

## Planner, humanizer, and executor

`LivePlanBuilder.cs` independently parses native Mode 3 files:

```text
keyCount = integer CircleSize, 1..18
lane = clamp(floor(x * keyCount / 512), 0, keyCount - 1)
tap = down(start), up(start + configured tap duration)
LN  = down(start), up(end - 1)
```

Tap duration defaults to 8 ms because the original Auto replay's 1 ms pulse can be missed by a
real keyboard-state poll. A release is moved before the next object on the same lane when required;
simultaneous lane transitions are sent in one `SendInput` batch.

The executor compares each batch against `0x04002358 + configured offset`. DT/HT do not need an
external rate conversion because this is the clock used by gameplay itself.

If the worker/game jumps beyond the normal lateness threshold, the executor consumes overdue
transitions into a logical lane state and emits only the physical difference. v0.5.0 also retains
each transition's original note time and tap/LN type. A completely elapsed tap is conservatively
re-pressed only when the current clock remains inside the recovered 100 safety window and the short
rescue pulse cannot collide with the next down on that lane. Already-expired notes and ambiguous
dense/LN states remain collapsed rather than injecting a damaging extra transition.

`Humanizer.cs` sits between parsing and execution. It pairs each source-line down/up, generates one
timeline when the score is prepared, then re-batches the realized transitions. Its v0.5.0 model has:

- an AR(1) timing process whose correlation decays with inter-note time;
- chord-shared error, small per-lane bias, density-sensitive independent error, and chord roll;
- a core timing distribution calibrated as `base UR = 10 * population sigma(ms)`;
- user timing bias (`-30..+30 ms`) plus persistent 3-to-7-group early rush bursts;
- separately controlled 200 and 100 tail mixtures, with an extra density/jack multiplier;
- native, 240, 120, or 60 Hz input cadence with correlated phase wander and rare frame hitches;
- optional fatigue, delayed presses (jam), and delayed releases (sticky finger).

The exact stable mania windows are recovered in `mania-judgement-windows.md`. Explicit 200/100
samples are drawn inside their own bands. Afterwards a backwards/forwards lane feasibility pass
projects both press and release times into the 100 window with a frame-size guard while preserving
`down < up < nextDown`. No style intentionally emits a 50 or miss. Repeatable mode uses an FNV-1a
seed over map path, selected mods, and settings; new-each-play mode mixes a fresh GUID/tick seed.

## In-process control overlay

osu!stable's main OpenTK surface is hosted by a WinForms top-level window. `AgentOverlay.cs` creates
an owned, borderless WinForms overlay in the same process. It uses `WS_EX_NOACTIVATE`,
`WS_EX_TRANSPARENT`, and `ShowWindow(SW_SHOWNOACTIVATE)`, follows the game client rectangle, hides
when osu! loses foreground, and never takes keyboard or mouse focus. This is an in-process game
overlay, not an osu sprite-tree mutation.

The worker continues polling hotkeys so the overlay remains presentation-only:

- `Ctrl+Alt+F7`: open/close settings;
- `Ctrl+Alt+F8`: switch between Player/self and Agent;
- `Ctrl+Alt+Up/Down`: choose a row;
- `Ctrl+Alt+Left/Right/Enter`: change the selected value.

The launcher defaults to Player/self. In this state the plugin remains loaded and visible but does
not invalidate a score or send input. Humanization changes apply when the next score timeline is
prepared; switching Agent off during a score releases all tracked keys immediately.

Fail-safe stops release every tracked key on:

- plugin toggle off;
- mode, score, or map session change;
- replay/automation mode detection;
- osu! losing foreground focus;
- the song clock stalling beyond the configured threshold;
- song clock moving backwards outside the initial pre-note lead-in reset;
- any exception or plugin shutdown.

## Verification performed

- CLR default-AppDomain loader test passes.
- Reflection-only metadata and exact invalidator-IL probe passes.
- Active plugin reports version `0.5.0.0`; `ReplayInjector` is absent; overlay/control/humanizer
  types are present.
- x86 `SendInput` `INPUT` structure is 28 bytes.
- The net40 live planner matches the independent net8 baseline transition-for-transition on all 13
  locally installed native Mode 3 maps (348 through 6744 transitions per map at tap=8 ms).
- All 13 maps pass all four humanization profiles (52 timelines): transition count is preserved,
  batch times remain monotonic, every lane alternates down/up to a released final state, and the
  OD/mod-aware predictor reports no generated 50 or miss.
- Synthetic distribution tests calibrate a requested core UR 70 to within +/-5, verify correlated
  rush notes, require the dense 100 rate to exceed 3x the sparse population, and cover NM, EZ, HR,
  DT, HT, and HR+DT window transforms without 50/miss.
- The existing native Auto frame parity remains a separate v0.2 research baseline.

## Current limitations

- Only the exact analysed executable hash is supported.
- The default layouts cover 1K through 9K. Higher key counts require `MANIA_AGENT_KEYS`.
- Keys must match osu!'s current mania bindings. The plugin intentionally does not read `osu!.cfg`.
- Loss of foreground focus aborts the current score rather than attempting a cross-window resume.
- The overlay is an owned WinForms surface rather than a native osu sprite, so exclusive-fullscreen
  composition depends on Windows; borderless/windowed mode is the reliable target.
- A Player pause flag suspends and resumes the session. An unflagged clock stall still terminates
  that score because the plugin cannot safely distinguish every abnormal game state.
