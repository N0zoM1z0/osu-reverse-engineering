# Drumroll arbitration near Taiko circles

## Symptom

A CLEAN-ish HR/DT run on *Time Dilation* recorded all planned combo key edges, yet seven circles
around the long yellow drumroll became misses. This ruled out the short-pulse sampling failure:
the physical edges existed in osu!'s own replay stream and their offsets were inside the recovered
circle judgement windows.

The failure came from a different boundary. The planner copied the native drumroll cadence
literally and emitted those optional Don strikes even when a normal circle was close enough to
accept them as Player input.

## Recovering judgement time from `.osg`

The matching local `.osg` file starts with two little-endian 32-bit integers:

```text
int32 clientVersion
int32 recordCount
```

For the pinned stable client, every following score snapshot is 29 bytes:

```text
int32  songTime
byte   stateMarker
uint16 count300, count100, count50, countGeki, countKatu, countMiss
int32  score
uint16 maximumCombo
uint16 combo
byte[4] target-specific tail
```

The observed file was exactly `8 + 932 * 29 = 27,036` bytes. Comparing each snapshot's six
cumulative counters with its predecessor gives the exact map-clock time at which a judgement was
recorded. [`analyze-score-graph.py`](../scripts/analyze-score-graph.py) performs that extraction
without reading an account value or making a network request.

The relevant sequence was:

| Drumroll strike | Next circle | Lead | `.osg` miss time |
| ---: | ---: | ---: | ---: |
| 12,227 | 12,304 | 77 ms | 12,231 |
| 12,304 | 12,382 | 78 ms | 12,309 |
| 12,382 | 12,460 | 78 ms | 12,383 |
| 12,460 | 12,538 | 78 ms | 12,463 |
| 12,537 | 12,615 | 78 ms | 12,540 |
| 12,693 | 12,771 | 78 ms | 12,704 |
| 12,848 | 12,926 | 78 ms | 12,854 |

The last row is particularly useful: the bonus strike is still inside the drumroll, while the
circle is just after its computed end. Protecting only geometrically overlapping circles would
therefore be insufficient.

## Why a 78 ms lead is dangerous

Circle scoring under HR on this OD 6 map uses `OD' = min(10, 1.4 * 6) = 8.4`, producing
`W300 = 29 ms` and `W100 = 72 ms`. The drumroll strikes are outside both scoring windows, but the
Player press-acceptance region is broader:

$$
A(OD') = \left\lfloor
\operatorname{DifficultyRange}(OD', 200, 150, 100)
\right\rfloor = 116\text{ ms}.
$$

An optional drumroll input 78 ms before a circle therefore reaches that circle. A wrong-colour
Don can fail a Kat immediately; a same-colour Don is accepted outside the 100 window and also
resolves as a miss. The later correctly coloured key edge cannot repair an object which the
earlier bonus input has already consumed.

This explains all seven consecutive misses without invoking a map-specific offset, scheduler
stall, or missing key transition. The other judgements in the score were kept separate from this
claim; they were not needed to derive the drumroll correction.

## Arbitration rule

Let `b` be a planned drumroll strike, `r_c` the beatmap reference time of circle `c`, and `d_c` the
last physical down used for that circle after humanization and strong-note hand splitting. The
planner suppresses `b` when

$$
\exists c:\quad
|b-r_c| \le A(OD')
\;\land\;
b \le d_c.
$$

There is one safe exception: if the drumroll and circle request the same physical key at the same
time, the transition builder can coalesce them and retain the circle's combo metadata. A bonus
strike which occurs after the circle's actual planned down is also safe with respect to that
circle, although it is still tested against later circles.

The implementation sorts circle references once and uses a lower-bound search for each drumroll
strike. Only circles in the local `2A` neighborhood are inspected. This makes the policy depend on
the recovered ruleset window and the current mods, not on a title, difficulty name, fixed BPM, or
the particular 78 ms spacing that exposed it.

The guard runs twice in the runtime planner:

1. after native strikes are generated, so obviously unsafe bonus inputs never enter the
   humanizer;
2. after bonus timing variation, so a small stochastic shift cannot move a previously safe tick
   across the acceptance boundary.

Hand alternation is computed before suppression. Removing an optional strike therefore does not
silently change which hand the following combo circle receives.

## Regression result

For the failing HR/DT map, the source plan retained all early drumroll cadence and the safe exact
same-key strike at 12,771 ms, while suppressing eight unsafe drumroll strikes. The semantic strike
count changed from 964 to 956; the yellow bar was not removed.

The synthetic tests cover both a circle inside the acceptance neighborhood and the first circle
after a drumroll end. The 26-map local corpus then produced:

```text
TAIKO IN-PROCESS CORPUS TEST: PASS
maps=26, objects=18073, strikes=19611, batches=42139,
predicted-100=216, predicted-miss=0, clipped-pulses=11,
hrdt-batches=39065, hrdt-clipped-pulses=1, hrdt-predicted-miss=0
```

This is an offline structural regression, not a claim about a live score. Its useful guarantee is
more precise: every remaining bonus transition has legal key-state alternation, and no retained
drumroll strike can reach an unresolved combo circle under the recovered press-acceptance rule.

## Live validation

The rebuilt plugin was then installed and exercised on the same map with the HR/DT family
(`mods=0x459`) in the ordinary HUMAN profile. Plan preparation reported the expected two-stage
arbitration:

```text
strikes=955, batches=2088, clock-rate=1.50x, pulse=30ms-real/45ms-map
suppressed 8 drumroll strikes inside the 116ms Taiko circle press-acceptance guard
suppressed 1 drumroll strikes inside the 116ms Taiko circle press-acceptance guard
```

The first line of suppression came from native planning; the second caught one bonus tick whose
humanized timing crossed the same boundary. Execution completed with `skipped=0`.

The score graph supplies the decisive before/after comparison. Before the change, the drumroll
neighborhood contained these non-300 judgements:

```text
12231 miss, 12309 miss, 12383 miss, 12463 miss,
12540 miss, 12704 miss, 12854 miss
```

After the change, that event list was empty. The new graph also contained 25 consecutive drumroll
bonus-score snapshots from 10,291 through 12,153 ms, followed by successful circle judgements.
Thus both parts of the visible symptom changed: the yellow bar itself registered strikes, and the
notes inside and immediately after it no longer broke.

The complete score was `896x300 / 17x100 / 1xmiss`. The remaining miss occurred much later at
104,237 ms on a strong Don whose reconstructed physical downs were planned at 104,166 and
104,170 ms. Replay sampling observed both at 104,237 ms after the run's isolated scheduler-lateness
peak; it is more than ninety seconds away from the only drumroll and outside this guard's scope.
Keeping that miss explicit matters: the live run validates the arbitration bug, not a universal
full-combo claim.
