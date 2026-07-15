# Recovered osu!stable Catch runtime dynamics

## Scope and evidence

These notes describe the managed x86 client fingerprinted by this module. They intentionally stop
at the facts used by the accepted 2026-07-14 23:57 SGT baseline. Later frame-quantizer, typed-
accessor, deadline, and turnaround experiments are not part of the active source.

The evidence comes from managed IL/decompilation, reflection-only metadata probes, an independent
Mode 2 converter, cross-model corpus checks, and live telemetry from the game's own object fields.
No readable private name is treated as an anchor because the assembly is obfuscated.

| Runtime role | Metadata token |
|---|---:|
| Catch ruleset manager | `0x020001ba` |
| Runtime object list | `0x040017fb` |
| Catcher sprite | `0x040006df` |
| Catcher width | `0x040006e0` |
| Fruit base type | `0x0200052c` |
| Fruit caught flag | `0x04001745` |
| Fruit hyper target | `0x04001747` |
| Hit-object time | `0x04002523` |
| Hit-object position | `0x0400252c` |
| Catcher position | `0x04002cf6` |

The metadata probe checks each token's declaring type and member shape after verifying the target
SHA-256. A different executable must be ported and revalidated; changing the hash constant is not
a compatibility strategy.

## Collision interval

The Catch manager derives two horizontal extents from rendered catcher width `w`:

```text
halfWidth = 0.5 * w
edgeInset = 0.1 * w
```

For fruit coordinate `x_f` and catcher-centre coordinate `x_c`, the client uses strict bounds:

```text
x_c - halfWidth + edgeInset < x_f
x_c + halfWidth - edgeInset > x_f
```

Therefore the physical catcher-centre interval is

$$
|x_c-x_f| < 0.4w.
$$

For simultaneous objects, the legal region is the intersection of all corresponding intervals.
The planner shrinks each side by a configurable global safety margin `m`:

$$
W_i(m)=
\bigcap_{o\in O_i}
\left[
\max(0,x_o-0.4w+m),
\min(512,x_o+0.4w-m)
\right].
$$

This inset is a robustness preference, not another client collision rule. The active planner also
tries to keep ordinary waypoints nine pixels inside their physical windows when neighboring
waypoints can still reach that local inset. Tight transitions retain the smaller globally proven
margin instead of making the entire map infeasible.

## Catcher width and mods

The independent converter reproduces stable's width model:

$$
w=106.75\left(1-0.7\frac{CS'-5}{5}\right),
$$

where Easy applies `CS' = 0.5 CS` and Hard Rock applies
`CS' = min(10, 1.3 CS)`. The live agent does not need to recompute this value: it reads the actual
width from the active Catch manager, after the client has applied its runtime state.

## Ordinary keyboard movement

For keyboard input, stable integrates catcher movement from the real frame delta. A frame whose
real delta exceeds 33 ms returns before keyboard movement. Otherwise the movement has the form

$$
\Delta x=d\,r\,h\,\Delta t_{real}
\begin{cases}
1, & \text{dash held},\\
1/2, & \text{dash released},
\end{cases}
$$

where `d` is `-1` or `+1`, `r` is the active map-rate multiplier, and `h` is the current hyper
multiplier. The result is clamped to `[0,512]`. In map-time coordinates with no active hyper, the
planner therefore uses

$$
v_w=0.5\ \text{px/ms}, \qquad v_d=1\ \text{px/ms}.
$$

The plugin never writes `x_c`. It resolves the user's current Left, Right, and Dash bindings and
changes their physical scan-code state through `SendInput`.

## Hyperdash has two distinct models

It is useful to separate conversion-time link assignment from movement after a linked source is
caught.

### Link assignment

The portable converter walks consecutive catchable fruits and droplets. For current object `i`
and next object `i+1`, it compares required horizontal distance with available map time after a
`1000/240 ms` allowance. Residual catcher width is carried when movement continues in the same
direction. If ordinary dash is insufficient, object `i` receives object `i+1` as its hyper target.

The live agent does not infer those links a second time. It reads the target reference already
attached to each converted runtime object. Agreement between the portable converter and runtime
links is a cross-model test, not a reason to replace runtime truth.

### Movement after the source is caught

At a successful linked source, stable installs a multiplier derived from actual catcher position
and remaining target time. The recovered form is

$$
h=\frac{|x_t-x_c|}
        {\max(1,t_t-t_{now}-1000/60)}.
$$

The movement update clears that multiplier after crossing the target centre. The active planner
models this exceptional transition by pinning the successor constraint to `x_t` and exempting the
source-to-target edge from the ordinary one-pixel-per-millisecond bound. The generated control
phase holds the appropriate direction and Dash while stable performs the hyper movement.

This is deliberately smaller than a full frame simulator. It captures the structural invariant
needed for route construction and leaves actual motion to the client implementation being
observed.

## Viability propagation

Objects with the same timestamp are grouped into one interval. A synthetic start constraint fixes
the catcher at `x=256` before the first object. For an ordinary edge of duration
`Delta t_i = t_{i+1}-t_i`, the predecessor operation is

$$
\operatorname{Pre}(A,\Delta t_i)
=A\oplus[-v_d\Delta t_i,v_d\Delta t_i].
$$

Backward viability is

$$
B_i=W_i\cap\operatorname{Pre}(B_{i+1},\Delta t_i),
$$

and forward reachability is

$$
R_i=B_i\cap
\left[R_{i-1}^{min}-v_d\Delta t_{i-1},
      R_{i-1}^{max}+v_d\Delta t_{i-1}\right].
$$

At a runtime-linked hyper target, the effective window is the point interval `[x_t,x_t]`; the
hyper edge itself does not receive the ordinary-speed predecessor restriction. Empty intervals
abort planning with a diagnostic. Nothing silently drops a required fruit to make a route appear
feasible.

## Selecting and smoothing one route

A deterministic seed selects bounded waypoint preferences. Optional fatigue scales the preference
noise quadratically with map progress:

$$
s(p)=1+0.65p^2, \qquad 0\le p\le1.
$$

This is not unrestricted randomness. Every preferred point is clamped into its object window,
then a forward selection and repeated bidirectional smoothing keep it within the reachable tube
and both neighboring movement cones. The four path styles change center, smoothness, playfield,
and wander weights; none changes hard reachability.

Hyper targets remain fixed. Outgoing hyper sources are kept near their object-centered preference
to preserve collision headroom for the short input pre-arm.

## Adaptive global margin

The overlay's safety value is a floor. Once that floor yields a route, the runtime searches upward
for the largest feasible global margin, capped at 10 px and quantized to quarter-pixel candidates.
This gives ordinary maps additional robustness while allowing genuinely saturated maps to fall
back toward the requested floor.

The search runs once during candidate preparation. No route optimization occurs in the timing-
critical loop.

## Song-clock qualification

During Catch construction the client can expose a convincing transient sequence such as
`-1020 -> 0 -> -1020`. A single range check would arm on that false zero. The baseline requires at
least three advancing samples across at least 50 ms of real time before sending its first key
transition.

Once trusted, the clock has explicit boundaries:

- pause releases held keys and records a suspended state;
- a backward jump while suspended terminates the session;
- an unchanged post-start clock for 750 ms without a pause flag terminates the session;
- a score, manager, mode, replay, or foreground change terminates the session; and
- completion is declared one second after the final object time.

These gates prevent stale input from surviving a lifecycle transition.

## Low-chatter feedback controller

The route is precomputed. Each live tick performs only bounded work:

1. read song clock and actual catcher X;
2. binary-search the active control phase and next waypoint;
3. compare actual X with route reference and target;
4. select idle, walk left/right, or dash left/right; and
5. emit only changed key states.

The reference is sampled 1.5 ms ahead. The configured deadband is reduced when a waypoint has
little collision clearance. During planned idle slack, the controller moves only when the
remaining error would require more than approximately `0.42 px/ms`. During final approach it
chooses Dash only when required speed exceeds approximately `0.46 px/ms`. Small overshoot releases
direction instead of immediately reversing it; reversal is reserved for final approach.

That policy is intentionally quiet. Earlier one-millisecond pulse-width modulation produced more
input transitions and visible shaking without better live catches.

## The chained-hyper guard

For a non-chained hyper source, the controller starts holding the outgoing direction during the
last 12 ms before source time. Without that pre-arm, the first useful input sample can arrive one
frame too late.

A chained source is different: it is simultaneously the target of the previous hyper. Reversing
12 ms early while the incoming multiplier is still active can move tens of pixels and miss the
source itself. The accepted baseline therefore applies pre-arm only when

```csharp
next.DepartsByHyperDash && !next.ArrivedByHyperDash
```

and holds the incoming direction through a chained collision. On the next sample, the outgoing
target becomes the active constraint and provides the new direction. This rule depends only on
the runtime object graph, so it generalizes beyond the map that exposed the failure.

## Verification boundary

The baseline has three layers of repeatable evidence:

1. portable parser/converter/planner synthetic self-tests;
2. exact net40 planner tests and four-style comparison across locally owned native Catch maps; and
3. live stop telemetry based on the game's own `caught` fields.

At restoration, the corpus produced eight maps, 32 style builds, 35,168 aggregate constraints, and
6,180 hyper links. The two retained live observations were:

```text
The End: F 1259/1259, T 266/266, total 1525/1525
TAG IV:  F 1278/1280, T 107/107, path completed
```

Those results establish behavior on the fingerprinted client and maps; they are not a universal
FC guarantee. Windows input scheduling, focus changes, unusual frame stalls, and future client
builds remain outside the mathematical route proof.
