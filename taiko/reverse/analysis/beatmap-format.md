# Native Taiko beatmap structure

Native osu!taiko maps use `Mode:1`. The file remains the normal sectioned `.osu` format; Taiko
semantics are carried mostly by hit-object type bits, hit-sound bits, timing points, and difficulty
values.

## Sections used by the planner

| Section | Fields used |
| --- | --- |
| Header | `osu file format vN` |
| `[General]` | `Mode` |
| `[Metadata]` | artist, title, creator, difficulty name |
| `[Difficulty]` | `OverallDifficulty`, `SliderMultiplier`, `SliderTickRate` |
| `[TimingPoints]` | offset, beat length, inherited flag |
| `[HitObjects]` | time, type, hit sound, slider repeat/length, spinner end |

The implementation intentionally accepts only native `Mode:1`; conversion from standard maps is
a separate runtime process and would introduce another layer of rules.

## Circle colour and strength

For a circle, colour and strength come from the `hitSound` bitmask:

| Bit | Name | Taiko meaning |
| ---: | --- | --- |
| `2` | Whistle | rim / Kat |
| `4` | Finish | strong / big note |
| `8` | Clap | rim / Kat |

Thus:

$$
\operatorname{Kat} = ((h \mathbin{\&} (2\,|\,8)) \ne 0),
\qquad
\operatorname{Strong} = ((h \mathbin{\&} 4) \ne 0).
$$

Don uses either inner key; Kat uses either outer key. A strong circle requires both hands of the
same colour, with the second hand accepted only inside the recovered 30 ms completion interval.

## Timing points and slider velocity

An uninherited timing point supplies a positive beat length `B`. An inherited point has a negative
beat length and supplies slider-velocity multiplier

$$
SV = \operatorname{clamp}\!\left(\frac{-100}{B_{inherited}}, 0.1, 10\right).
$$

When a new uninherited point is encountered, inherited velocity resets to one until a later green
line changes it.

## Taiko drumroll duration

A Taiko drumroll is encoded as a slider, but this stable build applies a Taiko-specific `1.4`
length conversion before constructing the runtime object. For pixel length `L`, repeat count `R`,
slider multiplier `SM`, active beat length `B`, and inherited velocity `SV`, the runtime end is

$$
t_{end} = t_{start} +
\left\lfloor
\frac{1.4\,L\,R\,B}{100\,SM\,SV}
\right\rfloor.
$$

The floor is operationally important: the manager casts the duration to an integer before adding
the object start. The first parser prototype used the ordinary slider formula and was therefore
short by 40%; corpus parity exposed the discrepancy.

## Native drumroll cadence

For a local Player score, the runtime starts with a beat subdivision:

$$
\Delta_0 =
\begin{cases}
B/(8SV), & format < 8,\\
B/6, & format \ge 8 \land SliderTickRate \in \{1.5,3,6\},\\
B/8, & \text{otherwise}.
\end{cases}
$$

It then octave-folds the interval into a comfortable range:

$$
\Delta \leftarrow 2\Delta \quad \text{while } \Delta < 60,
\qquad
\Delta \leftarrow \Delta/2 \quad \text{while } \Delta > 120.
$$

The Auto implementation accumulates this as a `double` and truncates each emitted frame time. The
physical planner mirrors that detail, which produces patterns such as `2500, 2562, 2625, 2687…`
for a 62.5 ms interval.

## Spinner count

The common spinner first derives a rate from OD:

$$
r(OD) = \operatorname{DifficultyRange}(OD, 3, 5, 7.5).
$$

The Taiko subclass then computes, in the same cast order as the managed code,

$$
n_0 = \left\lfloor \frac{d}{1000}r(OD) \right\rfloor,
\qquad
n = \left\lfloor \max(1, 1.65n_0) \right\rfloor.
$$

Double Time multiplies `n` by `0.75`; Half Time multiplies it by `1.5`, with integer truncation and
a minimum of one after each operation. Auto schedules `n+1` strikes, cycling inner-left,
outer-left, inner-right, outer-right. The live agent uses the same count as a conservative Player
policy.
