# Physical input sampling under DT

## Symptom

The planner could report zero predicted misses and the executor could report `skipped=0`, yet a
CLEAN Taiko run still produced tens of misses under HR/DT. The ordinary hit-time hypothesis did
not fit the evidence: successful presses remained tightly centred, and even the largest logged
`SendInput` lateness was usually inside the recovered 100 window.

The missing variable was pulse observability. `skipped=0` means that Windows accepted every
transition the executor chose to emit. It does not prove that osu!'s input update observed a
pressed state between that key's down and up transitions.

## Read-only evidence

The investigation correlated three local sources:

1. the plugin plan and scheduler summary;
2. aggregate Taiko judgements from `scores.db`;
3. the corresponding `.osr` frame stream under `Data/r`.

The replay parser consumes and discards the player-name field. No replay, score, or account value
is committed or transmitted.

| Run | Result | Planned key downs | Recorded key downs | Required edges unmatched |
|---|---:|---:|---:|---:|
| Inner Oni, no mods, CLEAN | 1412×300, 0 miss | 1576 | 1576 | 0 |
| Inner Oni, HR/DT family, CLEAN | 1364×300, 3×100, 45 miss | 1575 | 1554 | 47 |
| Time Dilation, HR/DT family, CLEAN | 829×300, 11×100, 74 miss | 1053 | 1004 | 70 |

For the HR/DT Inner Oni run, 41 circles lost every planned edge; the final score contained 45
misses. Successfully recorded required edges were only `+7 ms` from the object at the median. The
planned edges which disappeared had a `21 ms` median wait to the next replay input frame. The
replay frame-gap distribution was `23 ms` at p50 and `30 ms` at p99, in map-clock units.

This sharply separates timing error from edge loss:

- widening `W100` would not create a missing key-down;
- changing the humanizer's mean would move the surviving edges, which were already centred;
- increasing a pulse's observable lifetime directly addresses the failed condition.

## Sampling model

For a physical key pulse with down `d`, release `u`, and input-frame times `f_k`, a down state can
only be sampled when

$$
\exists k:\quad d \le f_k \le u.
$$

A repeated press on the same key also needs a sampled released state before its next down. The
old planner used an eight-millisecond pulse measured in map time. At clock rate `r`, its physical
duration was

$$
p_{real}=\frac{8}{r}.
$$

That is about `5.3 ms` under DT (`r=1.5`), far below the observed input-frame interval. The fact
that many short pulses still survived does not make the policy sound; their survival depended on
thread and frame phase.

## Rate-invariant pulse policy

The runtime option is now explicitly a physical duration. It is converted once when a score is
prepared:

$$
p_{map}=\left\lceil r\,p_{real}\right\rceil,
\qquad
r=\begin{cases}
1.5 & \text{DT or NC},\\
0.75 & \text{HT},\\
1 & \text{otherwise}.
\end{cases}
$$

With the `30 ms` default this gives `30 map-ms` normally, `45 map-ms` under DT/NC, and `23 map-ms`
under HT. The per-key planner still clips a release before the next down, so a dense pattern can
override the nominal hold rather than producing an illegal overlapping key state.

The executor adds a second, narrower safeguard for stalls. If one worker pass finds both a down
and its release overdue, the release is deferred until the down has existed for at most `20 ms`
of wall time. That guard is also capped by the planner's intended physical pulse duration. A pulse
which was deliberately shortened for a dense same-key sequence is therefore not stretched back
across its next down.

## Offline regression

The net40 corpus test now checks both ordinary HUMAN plans and CLEAN HRDT plans. One local corpus
snapshot covered 26 native Taiko maps and 18,073 objects:

```text
maps=26, objects=18073, strikes=19611, batches=42139,
predicted-100=216, predicted-miss=0, clipped-pulses=11,
hrdt-batches=39065, hrdt-clipped-pulses=1, hrdt-predicted-miss=0
```

Every per-key stream still alternated down/up and finished released. Only one HRDT pulse required
density clipping across the whole corpus after unsafe drumroll/circle inputs were arbitrated.
Sampling the new `45 map-ms` DT plan on the two failing
replays' historical frame grids reduced the model's exposed circles to zero. This is an offline
counterfactual, not a claim of a post-fix live FC; the installed build must still be exercised in
the real client.

## Reproduce the diagnosis

Run the privacy-safe analyzer against a local replay and its native Taiko beatmap:

```bash
python3 taiko/reverse/scripts/analyze-input-sampling.py \
  '/path/to/local-run.osr' \
  '/path/to/native-taiko-map.osu'
```

Useful output includes replay frame gaps, matched edge offsets, missing required edges, and a
frame-grid exposure comparison between the historical `8 map-ms` pulse and the new rate-adjusted
physical pulse. Humanized runs may require a wider `--edge-window`; CLEAN runs provide the most
direct per-hand comparison.

## Boundary of the result

This fix targets input-state observability. It does not alter OD, judgement windows, beatmap
timestamps, selected mods, score validity, or submission state. Extremely dense same-key patterns
can still be physically undersampled when neither a pressed nor released state lasts for one game
input update; that is a hand-assignment and cadence limit, not a reason to make every pulse longer.
