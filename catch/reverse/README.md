# Catch runtime reverse-engineering index

This directory records only the stable runtime facts needed by the Catch planner and live
Player-input experiment. The supported executable is identified by SHA-256 before any private
metadata token is used; names are obfuscated and are not treated as durable identifiers.

- [`analysis/stable-runtime-dynamics.md`](analysis/stable-runtime-dynamics.md) — recovered
  collision, movement, hyperdash, clock, and update semantics, plus their controller
  consequences.
- [`scripts/inspect-managed-type.ps1`](scripts/inspect-managed-type.ps1) — reflection-only type
  hierarchy and metadata-token inspection for the fingerprinted executable. It loads metadata;
  it does not start the game.

The executable fingerprint and live anchors are also verified by
`LocalCatchAgent.MetadataProbe.exe`, built from
[`../InProcess/TestHost/MetadataProbeProgram.cs`](../InProcess/TestHost/MetadataProbeProgram.cs).

For the full argument connecting those anchors to conversion, interval viability, input, and live
evidence, read [`../BLOG.md`](../BLOG.md). Build, installation, overlay controls, and log diagnosis
are documented in [`../docs/INSTALLATION_AND_USAGE.md`](../docs/INSTALLATION_AND_USAGE.md). The
accepted rollback boundary and retained observations are frozen in
[`../BASELINE.md`](../BASELINE.md).
