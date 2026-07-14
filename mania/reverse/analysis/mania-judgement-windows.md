# osu!stable mania judgement windows

These findings are locked to `osu!.exe` 1.3.3.8 with SHA-256
`6e182c10d1813209d12753dbc70b3a5bba00fef4ecf64bc42051870e6dfe4b7d`.
Do not assume the same metadata or formulas apply to another stable build or to lazer.

## Base formulas

The mania gameplay manager computes:

```text
d = clamp(10 - OverallDifficulty, 0, 10)
```

Before timing-mod adjustment, the maximum absolute hit errors are:

| Judgement | Maximum `abs(hitTime - noteTime)` |
|---|---:|
| 320 | `16` ms |
| 300 | `34 + 3d` ms |
| 200 | `67 + 3d` ms |
| 100 | `97 + 3d` ms |
| 50 | `121 + 3d` ms |
| miss | greater than the 50 window |

At OD 8, the five limits are `16 / 40 / 73 / 103 / 127 ms`. The comparisons use `<=`, so a value
exactly on a boundary remains in that judgement.

## Mod adjustment

Each window is transformed in this order and truncated to an integer:

```text
if HR:      value /= 1.4
else if EZ: value *= 1.4

if DT:      value *= 1.5
else if HT: value *= 0.75

window = (int)value
```

The DT/HT multipliers above describe the internal behavior of this specific stable binary. The live
agent reads the same gameplay clock used by judgement, so it does not separately rescale event
timestamps by playback rate.

## Result-flag cross-check

The mania note handler takes the absolute timing difference and compares the five fields in order.
The resulting flags were cross-checked against the base scoring switch:

| Result flag | Judgement |
|---:|---:|
| `268435456` | 320 |
| `134217728` | 300 |
| `67108864` | 200 |
| `33554432` | 100 |
| `16777216` | 50 |

This cross-reference matters because obfuscated field names alone do not reveal which threshold is
which.

## Use in v0.5.0

`LivePlanBuilder.cs` parses `[Difficulty] OverallDifficulty`. `Humanizer.cs` applies the same formula
and the currently selected EZ/HR/DT/HT bits.

The timing model has two layers. Its central distribution is calibrated using
`UR = 10 * population standard deviation(ms)`. User-controlled 200 and 100 events are then sampled
inside the safe interior of their respective bands instead of relying on an unbounded Gaussian
tail. A final physical-lane pass projects both down and up times into the 100 window with a frame-
cadence guard while preserving `down < up < nextDown`.

Displayed 320/300/200/100 counts are predicted press grades based on realized press offset. Long
notes also involve hold/release behavior, so final LN scoring remains the game's decision. Release
times are guarded too, but a press-only statistic is not presented as a complete LN result.

Synthetic tests cover NM, EZ, HR, DT, HT, and HR+DT and require zero generated 50s and misses. The
same constraint is applied to every full-map humanizer profile test.
