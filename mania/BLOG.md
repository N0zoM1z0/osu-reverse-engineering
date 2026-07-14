# Building a Live osu!mania Agent from Obfuscated Managed Code

This is the engineering story behind a small reverse-engineering project for one fingerprinted
osu!stable build. It started with a narrow question - how does stable generate mania Auto input? - and
ended with a user-controlled plugin that stays in normal Player mode, reads the internal song
clock, and produces real key transitions through the same input route as a player.

The interesting part was not writing a beatmap parser. The interesting part was moving from an
obfuscated managed binary to a reproducible semantic model, proving that model independently,
loading code into the correct CLR AppDomain without patching the executable, and then designing a
timing model that can look human without turning random jitter into accidental misses.

The target discussed here is intentionally narrow:

```text
Product:      osu!stable
Version:      1.3.3.8
Architecture: PE32 / x86 managed CLR v4
SHA-256:      6e182c10d1813209d12753dbc70b3a5bba00fef4ecf64bc42051870e6dfe4b7d
```

Nothing in the metadata-token layer should be assumed to work on a different executable.

## 1. Establishing the analysis environment

The game was running on Windows, while most analysis and source work happened in WSL. The first
step was not to attach a debugger. It was to classify the binaries and create a repeatable static
analysis path.

`osu!.exe`, `osu!gameplay.dll`, and `osu!ui.dll` were PE32 managed assemblies. That immediately
changed the tool choice. IDA can display the native CLR entry machinery and metadata-related stubs,
but it is not the most productive first view for a large managed application. Searches in the
initial IDA database did not expose useful mania strings or readable gameplay structure. ILSpy,
on the other hand, could reconstruct C# control flow, inheritance, enum references, and metadata
tokens even when names were obfuscated.

The pinned decompiler command was:

```bash
dotnet tool install --global ilspycmd --version 9.1.0.7988

./reverse/scripts/decompile-osu.sh /mnt/c/Games/osu/osu!.exe
```

The script deliberately takes the executable path as an argument. Its output is a local working
artifact and is excluded from this repository; the public tree keeps only compact analysis notes,
pseudocode, and original implementations.

Before following any call graph, the executable was fingerprinted. Version strings are not enough:
stable builds can share broad version labels while changing metadata layout. The SHA-256 became the
primary compatibility key used by both the research notes and the runtime plugin.

## 2. How to reverse an obfuscated managed application without trusting names

The application was obfuscated, but obfuscation did not erase the type system or runtime behavior.
Most user-defined identifiers became names such as `#=z...`, yet several useful facts remained:

- public enum type names and values used across assemblies;
- base classes and virtual override relationships;
- method signatures and field types;
- generic instantiations such as `List<T>`;
- integer constants, float formulas, and switch values;
- call sites in central dispatchers;
- metadata tokens, which are stable inside one exact binary;
- data flow from input polling to replay recording and scoring.

The analysis therefore used a graph-first workflow.

### Anchor on enums and central switches

`osu_common.PlayModes` retained the value `OsuMania = 3`. `osu_common.Mods` retained useful values,
including `Autoplay = 0x800`. Searching references to those enum values led to Player's ruleset
switch and automation branches. The mania branch constructed one opaque gameplay component. That
component became the first semantic anchor: its name was irrelevant; its position in the mode
switch established its role.

### Follow overrides, not class names

Several ruleset components overrode the same virtual method when Auto was selected. Comparing the
four overrides made the mania-specific behavior stand out. Standard treated replay-frame fields as
coordinates and button state. Mania repeatedly converted a column value into powers of two and
wrote the aggregate into a float field. That was a strong clue that the field was being used as a
bit mask.

### Confirm a hypothesis from the opposite direction

A bit-mask hypothesis is not proof. The next step was to locate the normal mania column update.
The recovered column type polled its configured primary and secondary keyboard keys, changed a
current/previous state pair, and exposed a method equivalent to:

```csharp
int ColumnBit() => 1 << columnIndex;
```

Then Player's normal replay-recorder path was inspected. In mania mode, it wrote the aggregate live
lane state into the same replay-frame field. The generator and recorder therefore agreed on the
meaning of the data: replay-frame `x` is the mania key mask.

This cross-checking pattern was used throughout the project. An opaque member received a semantic
label only when multiple call sites, types, or behavioral tests supported it.

## 3. Recovering the native mania Auto algorithm

The built-in Auto model begins with a frame at time zero and mask zero. For each mania object it
computes a lane bit and inserts state changes into an ordered frame list.

```text
lane n bit = 1 << n

tap:
  set bit at startTime
  clear bit at startTime + 1

long note:
  set bit at startTime
  keep bit set in every intermediate frame
  clear bit at endTime - 1
```

If another frame already exists at the target time, the generator edits that frame rather than
adding a duplicate. Otherwise it copies the previous aggregate mask, changes one lane bit, and
inserts a new frame. When it processes a long note, it also ORs the lane bit into all existing
frames strictly between start and end. That propagation step is essential: events on other lanes
must not make the held lane disappear.

A cleaned form of the algorithm is:

```csharp
frames = [new Frame(0, keyMask: 0, scrollSpeed)];

foreach (note in hitObjects)
{
    int bit = 1 << note.lane;
    ToggleAt(note.startTime, bit, down: true);

    if (note.isHold)
    {
        foreach (frame in frames)
            if (note.startTime < frame.time && frame.time < note.endTime)
                frame.keyMask |= bit;

        ToggleAt(note.endTime - 1, bit, down: false);
    }
    else
    {
        ToggleAt(note.startTime + 1, bit, down: false);
    }
}
```

The one-millisecond tap makes sense inside a replay stream because the replay consumer observes
both frames directly. It is not a safe duration for a simulated physical key, because a gameplay
poll can occur before the down and after the up and never observe the pressed state.

## 4. Parsing native mania beatmaps independently

The readable reference implementation in `ManiaAuto/` was written without depending on game
runtime types. Native mania requires:

```text
[General]
Mode:3

[Difficulty]
CircleSize:<integer key count>
```

The horizontal coordinate maps to a lane with:

```text
lane = clamp(floor(x * keyCount / 512), 0, keyCount - 1)
```

The hit-object type bits are:

```text
tap  = (type & 1) != 0
hold = (type & 128) != 0
```

For a long note, `endTime` is the integer before the first colon in the sixth comma-separated
field. Object timestamps are already absolute beatmap milliseconds. Timing points are relevant to
musical timing and presentation, but the parser does not integrate them to reconstruct object
positions in time.

Two outputs were built from this parser:

1. The exact native Auto frame list, preserving the one-millisecond tap.
2. A physical-input timeline with explicit lane down/up transitions and an eight-millisecond
   default tap.

For a dense same-lane sequence, a release is moved before the next down when necessary. Two objects
at exactly the same time on one lane are rejected because one physical key cannot express two
independent presses at that instant.

## 5. Proving the model with parity instead of intuition

Reverse-engineered pseudocode is easy to make plausible and hard to make exact. The project used
independent implementations as a defense against shared assumptions:

- a .NET 8 baseline in `ManiaAuto/ReplayFrames.cs` and `LiveTimeline.cs`;
- .NET Framework 4 builders in `InProcess/Plugin/NativeFrameBuilder.cs` and
  `LivePlanBuilder.cs`.

The verification scripts enumerate native Mode 3 maps supplied by the caller. For each map they
export the complete sequence, normalize it, hash it, and compare implementations.

```bash
./InProcess/scripts/verify-frame-parity.sh /mnt/c/Games/osu/Songs
./InProcess/scripts/verify-event-parity.sh /mnt/c/Games/osu/Songs
```

The development corpus contained 13 maps. Native Auto parity passed all 13, from 340 to 4,180
frames. Physical event parity also passed all 13, from 348 to 6,744 transitions. These were complete
timeline comparisons, not sampled notes.

## 6. The first in-process proof - and why it was not enough

The first plugin experiment used the game's own replay-list path. It enabled the controlled Auto
path, waited for the built-in frame list, generated an independent list, compared every frame, and
only on exact parity rebuilt a typed `List<frame>` using the game's frame constructor.

This experiment was useful because it proved all of the difficult plumbing:

- code was running in the correct AppDomain;
- the current ruleset, beatmap, and score could be found;
- metadata tokens and generic frame lists were correctly understood;
- the independent Auto model agreed with the live game.

But the game still consumed a replay. The plugin was replacing generated input data, not behaving
like an agent pressing keys. That distinction drove the redesign. `ReplayInjector.cs` remains in
the source tree as historical evidence, but the active plugin build excludes it.

## 7. Loading the plugin through CLR v4

Because the target is a managed CLR v4 process, the bootstrap could use a runtime feature instead
of patching the PE or creating a native remote thread.

The launcher gives its child two environment variables:

```text
APPDOMAIN_MANAGER_ASM=LocalManiaAuto.Loader, Version=1.0.0.0, ...
APPDOMAIN_MANAGER_TYPE=LocalManiaAuto.Loader.LocalManiaAutoDomainManager
```

CLR resolves the loader from the application base and instantiates it in `DefaultDomain`. The
loader then reads a child-only `MANIA_AUTO_PLUGIN` path, loads the plugin with `Assembly.LoadFrom`,
and invokes its static start method.

The environment is temporary. `launch-osu.ps1` saves any previous values, starts the GUI child with
ShellExecute, and restores the caller immediately. A normal shortcut launch has no AppDomain
manager configuration and therefore does not load the plugin.

This bootstrap also avoids the editor's AiMod loader. The editor creates a separate AppDomain for
ruleset checks through a marshal-by-reference boundary. That is useful isolation for linting, but
its static game state is not the default gameplay state we need.

## 8. Treating metadata tokens as assertions, not APIs

Opaque names are inconvenient, but metadata tokens provide precise addresses inside one assembly.
The live agent uses the following important targets:

| Token | Recovered role |
|---:|---|
| `0x04000CC6` | selected mods |
| `0x06002232` | current play-mode getter |
| `0x04002C6D` | global osu mode |
| `0x04002A7C` | replay-mode flag |
| `0x04002A7F` | replay-source score |
| `0x040013C3` | current Player score/session identity |
| `0x06002C63` | current beatmap getter |
| `0x06001BF0` | beatmap path getter |
| `0x04002358` | gameplay song clock in milliseconds |
| `0x06002B5A` | score invalidation method |
| `0x04001990` | score validity field |

The plugin does not blindly resolve and cast them. It first verifies the whole executable SHA-256,
then checks whether each member is static or instance, its argument count, return type, field type,
and relationships between declaring types. A reflection-only metadata probe repeats these checks
outside gameplay.

One critical method was small enough to verify byte-for-byte. The score-invalidation method has the
following IL in the locked build:

```text
02 16 7D 90 19 00 04 2A
ldarg.0
ldc.i4.0
stfld 0x04001990
ret
```

The plugin verifies that exact body, invokes it on the current score before arming, and reads the
field back. If the method shape or result differs, the session is rejected before input begins.

## 9. Finding and using the normal Player input route

The recovered mania column update polls the configured primary and secondary keys and updates its
lane state. Player then aggregates all lane states. The normal replay recorder observes that
aggregate after it has already been consumed as live input.

That ordering is the architectural key:

```text
SendInput scan-code state
        |
        v
normal mania key polling
        |
        +--> column visuals and hit judgement
        +--> combo, health, and score logic
        +--> ordinary replay recorder observes the resulting lane mask
```

The v0.5.0 executor therefore stays in ordinary Player mode. It never creates, replaces, or
consumes a replay-frame list. Every worker tick reads the internal song clock. When a planned batch
is due, it emits scan-code `KEYBDINPUT` records with Win32 `SendInput`.

Using scan codes avoids dependence on the active keyboard layout. The project validates that the
x86 `INPUT` structure is 28 bytes; using the x64 layout in an x86 target would corrupt the native
call. Extended-key prefixes are preserved, and every injected input carries a constant marker in
`dwExtraInfo` for diagnostics.

`timeBeginPeriod(1)` improves timer resolution while a candidate or session is active. It is paired
with `timeEndPeriod(1)` during cleanup. The imported entry-point names are explicit because allowing
P/Invoke name inference from differently cased managed method names caused a real
`EntryPointNotFoundException` during development.

## 10. Session state, pause, stalls, and key cleanup

Planning can inspect the full beatmap in advance, but execution remains clock-driven. A session is
identified by the current score object and map path. Runtime gates require normal global Play mode,
mania ruleset, no replay source, and no automation mod.

The state machine has three broad phases:

```text
IDLE -> CANDIDATE -> ARMED/PLAYING -> STOPPED
```

A candidate is parsed and humanized before the first event. It arms only when the song clock is in
a plausible pre-first-note position and osu! is the foreground process. The executor then advances
through sorted transition batches.

The Player pause flag suspends the session and releases plugin-owned keys. On resume, held-lane
state is reconstructed from already consumed transitions. A backward clock jump outside the normal
initial lead-in reset stops the session.

If the game or scheduler jumps past several transitions, blindly replaying every stale down/up can
damage later lane state. The catch-up logic first folds overdue events into a logical aggregate
state and emits only the difference from the physical state. v0.5.0 retains original note times and
tap/LN identity. A fully elapsed tap is rescued only when:

- the current clock is still inside the recovered 100 safety window;
- it is a tap, not a long note;
- the lane was not already held;
- a short rescue pulse cannot collide with the next down on that lane.

Otherwise the event remains unrecovered. This is conservative by design: saving one stale tap is
not worth corrupting a dense jack pattern or long note.

Every stop path attempts to release all plugin-owned keys. Disabling Agent mode, changing score or
map, leaving gameplay, focus loss, replay detection, an unflagged clock stall, an exception, and
plugin shutdown all converge on cleanup.

## 11. Building an in-process UI without touching the game's sprite tree

The goal was a user-controlled plugin: loading it should not force automatic play. The launcher
therefore defaults to `PLAYER / SELF`, and `Ctrl+Alt+F8` toggles Agent mode.

Instead of reverse-engineering the full osu! sprite/UI framework, the plugin uses a smaller surface
already present in the process. Stable's OpenTK game surface is hosted by a WinForms top-level
window. `AgentOverlay.cs` creates an owned borderless WinForms form with:

```text
WS_EX_NOACTIVATE
WS_EX_TRANSPARENT
tool-window / layered-window behavior
ShowWindow(SW_SHOWNOACTIVATE)
```

The overlay follows the osu! client rectangle, hides when osu! is not foreground, and does not take
keyboard or mouse focus. It is an in-process owned window, not an injected game sprite.

Hotkeys remain in the worker, so the overlay is presentation rather than the input authority:

- `Ctrl+Alt+F7` expands or collapses settings;
- `Ctrl+Alt+F8` switches Player/self and Agent;
- `Ctrl+Alt+Up/Down` selects a row;
- `Ctrl+Alt+Left/Right/Enter` adjusts it.

The twelve rows expose style, base UR, timing bias, rush bursts, 200 mix, 100 mix, density boost,
frame cadence, fatigue, finger trouble, and variation seed policy.

## 12. Designing a human timing model

Adding independent Gaussian noise is easy, but it does not look much like a person. It produces
uncorrelated hits, makes chords split unnaturally, and eventually creates arbitrary misses. The
humanizer instead builds a correlated model and then solves physical constraints.

### Core timing and UR

For note groups ordered by original down time, an AR(1) state evolves as:

```text
rho = exp(-deltaTime / correlationTime)
state = rho * state + sqrt(1 - rho^2) * Normal(0, 1)
```

Notes in the same chord share a group error. Small lane bias, chord roll, and density-sensitive
independent error are added. The resulting raw population is standardized, then scaled so:

```text
targetSigmaMilliseconds = baseUR / 10
offset = timingBias + targetSigmaMilliseconds * standardizedError
```

This gives the central distribution an interpretable control. The final displayed UR can be higher
than base UR because explicit 200/100 tail events are part of the final population.

### Persistent rushing

A human tendency to rush is rarely one isolated early hit. When a rush starts, it lasts three to
seven note groups and applies a negative shift whose magnitude decays across the burst. The UI's
Rush percentage controls the expected occupancy of these correlated early regions, not simply the
probability that one note receives a negative sign.

The low-grade tail sampler also prefers the early side during an active rush. That creates visible
clusters of early 200s or 100s rather than a symmetric collection of unrelated outliers.

### Fatigue and finger trouble

Optional fatigue adds a progress-dependent late trend and increasing independent variance. Finger
trouble is represented by two separate events:

- a jam delays a press by a style-specific range;
- a sticky finger delays release independently.

These effects are still passed through the final judgement and lane-order constraints. They are
descriptive timing features, not permission to miss.

### Frame cadence

Input does not arrive at arbitrary real numbers. The model can remain native or quantize to 240,
120, or 60 Hz. A slowly correlated phase wander prevents every event from landing on one perfectly
fixed grid. Rare frame hitches move a group by one additional period.

A simplified quantizer is:

```text
movingPhase = basePhase + phaseWander
frame = movingPhase
      + ceil((desired - movingPhase - period/2) / period) * period

if hitch:
    frame += period
```

Chords share the same frame state so they remain coherent.

## 13. Recovering judgement windows and adding controlled 200/100 results

To place 200s and 100s intentionally without allowing misses, the exact stable mania timing windows
were recovered from the gameplay manager and cross-checked against result flags in the scoring
switch.

Let:

```text
d = clamp(10 - OverallDifficulty, 0, 10)
```

Before timing-mod adjustment:

```text
320 = 16
300 = 34 + 3d
200 = 67 + 3d
100 = 97 + 3d
50  = 121 + 3d
```

Each value is transformed in this order:

```text
if HR:      value /= 1.4
else if EZ: value *= 1.4

if DT:      value *= 1.5
else if HT: value *= 0.75

window = (int)value
```

At OD 8 with no timing mod, the limits are 16, 40, 73, 103, and 127 ms.

The UI exposes independent base probabilities for 200 and 100. Local density is estimated from the
number of notes within +/-500 ms and the interval since the previous object on the same lane. If
`density` is normalized to `[0,1]`, the effective probabilities are approximately:

```text
p100 = base100 * (1 + denseBoost * density * 2)
p200 = base200 * (1 + denseBoost * density)
```

Both are capped, and their sum is capped again. The stronger 100 multiplier makes low judgements
more plausible in dense streams and fast jacks while keeping sparse sections cleaner.

When a 200 or 100 is selected, its magnitude is sampled inside the interior of that judgement's
band, leaving a small margin on both sides. It is not generated by making the Gaussian distribution
unbounded.

## 14. Preventing random jitter from becoming a miss

Sampling is only the first half of the problem. Independent timing changes can make a release occur
before its press, overlap the next note on the same physical key, or push a note outside the intended
window after frame quantization.

The final pass treats each lane as a constrained schedule. First it computes a no-miss absolute
limit:

```text
safetyGuard = max(4ms, ceil(framePeriod * 0.55))
safeHitWindow = judgement100Window - safetyGuard
```

For each lane, a backwards pass calculates the latest feasible down time for every note while
reserving at least two milliseconds before the next down. A forward pass clamps the requested down
time between the earliest hit-safe value and that latest feasible value. Releases are then clamped
to:

```text
originalRelease - safeHitWindow
    <= release
    <= originalRelease + safeHitWindow

down + 1 <= release < nextDown
```

If no hit-safe physical solution exists, planning fails instead of silently dropping a transition.
The generated plan always retains the same number of down/up transitions as the source plan.

This is why the UI has 200 and 100 controls but no random-miss control. Human-looking variation
comes from correlation, tails, density, frame cadence, fatigue, and finger behavior. It does not
need arbitrary misses.

## 15. Verification of the final model

The final validation layers were:

1. **Reference self-test.** A synthetic 4K map checks parser behavior, native frames, physical
   timeline generation, and lane mapping.
2. **CLR loader test.** A clean test process confirms the AppDomain manager and plugin run in
   `DefaultDomain`.
3. **Metadata probe.** Reflection-only loading verifies executable hash, token shapes, exact
   invalidator IL, plugin version, absent replay injector, and 28-byte x86 `INPUT`.
4. **Auto-frame parity.** Thirteen supplied maps pass complete net8/net40 frame comparison.
5. **Live-event parity.** The same maps pass complete physical transition comparison.
6. **Distribution tests.** Requested core UR 70 must remain within +/-5 before explicit tails; rush
   must produce correlated early notes; dense 100 rate must exceed three times the sparse rate in a
   synthetic mixed-density population.
7. **Mod matrix.** NM, EZ, HR, DT, HT, and HR+DT must all generate zero predicted 50s and misses.
8. **Full-map humanizer test.** Thirteen maps times four styles preserve transition count, monotonic
   batches, legal alternating lane state, released final state, and zero generated 50/miss.

One representative dense map under default HUMAN settings and a fixed test seed produced:

```text
base UR:       65
timing bias:   -4 ms
rush mix:      20%
200 / 100:     1.0% / 0.2%
dense boost:   125%
frame cadence: 240 Hz

predicted presses:
320: 3149
300:  139
200:   62
100:   22
 50:    0
miss:   0
```

The count is a press-offset prediction. Long-note final scoring also depends on hold and release
behavior, so the game remains the authority for the final LN result.

## 16. Packaging the work without publishing private or proprietary data

The public repository is intentionally a curated reconstruction, not a dump of the lab machine.
It includes original source, scripts, compact reverse-engineering notes, a synthetic `.osu` file,
and two core DLLs built from the source.

It excludes:

- the game executable and supporting proprietary assemblies;
- the complete ILSpy project;
- installed beatmaps and replay files;
- user configuration and runtime logs;
- build `bin/obj` trees and PDBs;
- machine-specific paths, usernames, process IDs, and session notes.

Scripts require explicit paths or use the neutral `C:\Games\osu!` example. The ignore rules also
block common game and generated file types, reducing the chance of a future accidental commit.

## 17. Lessons and next directions

Several broader lessons came out of the project:

- Managed obfuscation changes how code is indexed, not whether types, signatures, constants, and
  data flow exist.
- A behavior should be named only after independent references agree.
- A parity oracle is valuable even when its architecture is not the final architecture.
- Metadata tokens are excellent exact-build locators and terrible cross-version APIs.
- Real input and replay input can encode the same lane mask while having very different timing and
  polling constraints.
- Humanization is better modeled as correlated structure plus constrained optimization than as
  unbounded random noise.
- A small owned native UI can be a practical control surface when reversing a full proprietary
  sprite framework would add risk without improving the experiment.

The repository is structured around ruleset modules. Mania is the first module, not the root of the
entire project. Future standard, taiko, or catch work can use the same discipline: fingerprint the
target, recover semantics from multiple directions, implement an independent model, verify complete
behavior, and publish only the original, reproducible core.
