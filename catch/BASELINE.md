# Active Catch baseline

The active Catch implementation is the exact source state reconstructed from the original local
patch stream through 2026-07-14 23:57 SGT. This is the early low-chatter version remembered as the
"few misses" baseline, verified from osu!'s own `caught` fields:

```text
The End: fruit 1259/1259, tiny droplets 266/266, total 1525/1525
TAG IV: fruit 1278/1280, tiny droplets 107/107, path completed
```

This snapshot includes the pre-roll clock qualification, low-chatter phase controller, runtime
`caught` telemetry, adaptive global safety margin, locally inset catch windows, and the chained-
hyper collision guard. It predates the subsequent deadline, frame-quantizer, typed-accessor, and
turnaround experiments.

## Verification at restoration

```text
CATCH NET40 PLANNER: PASS
objects=7, constraints=7, phases=20, hyper=1

RUNTIME CATCH CORPUS: PASS
maps=8, style-builds=32, aggregate-constraints=35168, aggregate-hyper-links=6180
```

The default artifacts were rebuilt from that source for publication. Their current hashes are:

```text
b7919182b396e7c6129bbcc86bf8762c33a18f82c96ac61e4febed595c1cc213  LocalCatchAgent.Loader.dll
0769bf8b43bfb726c54f7624acd06f1b6db1f34422d80aa4e9a5b49ac0c53d49  LocalCatchAgent.Plugin.dll
```

PE timestamps make hashes change after a rebuild; behavioral provenance is the source snapshot,
not the timestamp-bearing binary hash. Both the earlier 23:53 source and the later implementation
are preserved under `catch/experiments/`, which is intentionally ignored by Git.
