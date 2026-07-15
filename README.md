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

taiko/
  TaikoBeatmap/            .NET 8 native Mode:1 parser and reference planner
  InProcess/               CLR v4 Player-input agent, overlay, tests, scripts
  artifacts/inprocess/    core net40 loader/plugin binaries and checksums
  reverse/analysis/       curated format, runtime, and judgement research
  reverse/scripts/        repeatable IL extraction and IDA annotation
  docs/                    installation and operator documentation
  BLOG.md                 full research article

catch/
  CatchPlanner/           .NET 8 Mode:2 parser and reference planner
  InProcess/              CLR v4 Player-input agent, overlay, tests, scripts
  RuntimePlannerValidation/ cross-check between portable and runtime models
  artifacts/inprocess/    core net40 loader/plugin binaries and checksums
  reverse/analysis/       conversion, runtime, and generalization research
  docs/                    installation and operator documentation
  BLOG.md                 full research article

reverse/
  analysis/               cross-ruleset client integrity research
  artifacts/              hash-locked metadata and IL evidence
  scripts/                read-only security-surface extractor
  BLOG.md                 long-form anti-cheat architecture article
```

## Client integrity and anti-cheat architecture

The cross-ruleset study reconstructs the trust and consistency pipeline in one pinned osu!stable
build. It covers Authenticode startup/update validation, obfuscation and VM boundaries, normal-play
evidence collection, redundant clocks and score state, Flashlight/window/movement signals, local
validity and finish gates, periodic telemetry, and the boundary between a client submission attempt
and a server verdict.

The work is static and descriptive. It contains no bypass, patch, forged-payload, or evasion
procedure. Start with the [cross-ruleset notebook](reverse/README.md), use the evidence-first
[technical report](reverse/analysis/client-integrity-and-anti-cheat.md), and read
[The Score Has More Than One Clock](reverse/BLOG.md) for the complete research narrative.

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

## Taiko

The Taiko module follows the same evidence-first workflow but recovers a substantially different
ruleset: Don/Kat colour, strong two-hand circles, drumroll duration and cadence, spinner demand,
and strict 300/100 windows. Its runtime experiment deliberately bypasses the built-in Auto replay
generator. A selectable in-process agent plans strikes, humanizes them with a miss-safe correlated
timing model, resolves the user's current four Taiko bindings, and sends real scan-code down/up
events while osu! remains in normal Player mode.

The final installed build completed a 658-object Oni map with all 1,534 physical transitions and
no skipped input; the last two complete observations recorded 19–28 ms maximum scheduler
lateness. Start with the
[Taiko module README](taiko/README.md), continue through the
[reverse-engineering index](taiko/reverse/README.md), use the dedicated
[installation and operation manual](taiko/docs/INSTALLATION_AND_USAGE.md), and read
[Four Drums, No Replay](taiko/BLOG.md) for the complete evidence chain, source excerpts,
diagrams, equations, and engineering analysis.

The shared post-play audit is documented separately in
[Score validity is not submission](taiko/reverse/analysis/submission-path.md). It records the
client-side validity, finish, login, and worker gates plus an opt-in read-only A/B diagnostic for
Mania and Taiko. The broader trust and integrity context lives in the top-level
[client-integrity report](reverse/analysis/client-integrity-and-anti-cheat.md).

## Catch

The Catch module turns the ruleset into a one-dimensional viability problem. A portable Mode 2
converter reconstructs fruits, droplets, tiny droplets, bananas, slider paths, catcher geometry,
and hyperdash links. The live experiment goes one step closer to the authoritative state: it reads
osu!stable's already-converted runtime object list and actual catcher width, computes a
forward/backward reachable tube, and follows a smooth trajectory inside that tube through the
user's configured Left, Right, and Dash bindings.

No replay frames or built-in Auto list are created. The controller runs in normal Player mode,
observes the song clock and catcher position, and emits real scan-code transitions. Generality is
protected by model invariants, deterministic synthetic tests, an exact net40 planner check, four
independent path styles, and native-map corpus validation; there are no map-name, timestamp, or
object-ID exceptions in the runtime algorithm.

Start with the [Catch module README](catch/README.md), use the
[installation and operation manual](catch/docs/INSTALLATION_AND_USAGE.md), and read
[Catching Rain Without a Replay](catch/BLOG.md) for the complete conversion mathematics,
managed-runtime analysis, viability derivation, controller design, and validation evidence.

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
