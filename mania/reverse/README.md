# Curated reverse-engineering material

This directory records the reproducible analysis for one fingerprinted osu!stable executable.

```text
analysis/
  mania-auto.md                 native Auto replay-frame reconstruction
  inprocess-loader.md           CLR bootstrap and historical replay-list proof
  live-agent.md                 normal Player-input runtime path
  mania-judgement-windows.md    OD/mod-aware mania hit windows

scripts/
  decompile-osu.sh              pinned ILSpy project export
```

The complete ILSpy project is intentionally ignored and is not part of the public repository.
Run the script against your own executable when reproducing the analysis:

```bash
./reverse/scripts/decompile-osu.sh /mnt/c/Games/osu/osu!.exe
```

## Analysed sample

| Property | Value |
|---|---|
| File/Product version | `1.3.3.8` |
| Architecture/runtime | PE32 managed, CLR `v4.0.30319` |
| SHA-256 | `6e182c10d1813209d12753dbc70b3a5bba00fef4ecf64bc42051870e6dfe4b7d` |
| ILSpy CLI | `9.1.0.7988` |

The target is obfuscated. The notes preserve opaque names only when they are useful as local
cross-references and derive semantics from call sites, enum values, signatures, field flow, and
independent behavioral parity rather than from names.
