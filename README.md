# osu! Reverse Engineering

Reverse-engineering notes, reproducible experiments, and original tooling for selected osu!
rulesets. The repository is organized by ruleset so later work can live beside mania without
mixing incompatible parsers, runtime targets, or validation evidence.

This repository contains only original source code, small synthetic test data, documentation, and
build artifacts produced from that source. It does not contain osu! binaries, complete decompiler
output, user configuration, replay files, logs, locally installed beatmaps, or extracted game
assets. Use the tools only with software and beatmaps you are allowed to inspect.

## Contents

```text
mania/
  ManiaAuto/              readable .NET 8 parser and reference models
  InProcess/              CLR v4 loader, live Player-mode agent, tests, scripts
  artifacts/inprocess/    core net40 loader/plugin binaries and checksums
  reverse/analysis/       curated reverse-engineering notes
  reverse/scripts/        reproducible ILSpy entry point
  docs/                    installation and operator documentation
  BLOG.md                 full research article
```

## Mania

The mania module began by reproducing osu!stable's native Auto replay-frame generator, then moved
to a different architecture: an in-process agent that remains in normal Player mode, reads the
gameplay song clock, and emits real scan-code key transitions through the ordinary input path.

The current v0.5.0 research build includes:

- native Mode 3 `.osu` parsing and lane mapping;
- exact Auto frame generation for taps, long notes, chords, and overlapping lane state;
- a hash-locked CLR v4 `AppDomainManager` bootstrap for one analysed osu!stable build;
- metadata-token and IL-shape validation before runtime fields are used;
- a normal-input executor driven by the internal song clock;
- an owned, no-activate WinForms control overlay;
- correlated timing, rush bursts, frame cadence, fatigue, finger trouble, and controlled 200/100
  mixtures based on recovered mania judgement windows;
- deterministic synthetic tests and full-map parity scripts.

Start with the [mania module README](mania/README.md). The
[installation and usage manual](mania/docs/INSTALLATION_AND_USAGE.md) covers the complete operating
path, while [Clockwork Fingers](mania/BLOG.md) presents the reverse engineering, runtime design,
source excerpts, diagrams, and mathematical model as a research article.

## Repository policy

- No game executable or proprietary dependency is distributed here.
- No complete ILSpy/decompiler tree is committed; only compact findings and pseudocode are kept.
- No user-specific paths, usernames, process IDs, account data, logs, or beatmap collections are
  part of the public tree.
- Runtime metadata tokens are valid only for the documented executable fingerprint. A mismatch is
  a hard failure, not a compatibility guess.

## License

Original code and documentation in this repository are available under the [MIT License](LICENSE).
Third-party product names and behavior described in the research remain the property of their
respective owners.
