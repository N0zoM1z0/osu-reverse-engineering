# Cross-ruleset reverse-engineering notebook

This directory contains evidence that applies to osu!stable as a client rather than to one
ruleset. The first study is a static reconstruction of the pinned client's trust, integrity,
telemetry, score-validity, and submission boundaries.

## Scope

| Property | Value |
| --- | --- |
| Product version | `1.3.3.8` |
| Architecture | PE32 / x86 / CLR |
| SHA-256 | `6e182c10d1813209d12753dbc70b3a5bba00fef4ecf64bc42051870e6dfe4b7d` |
| Analysis style | static managed metadata and IL, with read-only signature inspection |

The findings are about this exact executable. They are not a claim about every historical or
future osu!stable build, osu!(lazer), Bancho's current server policy, or native code that is not
recoverable from the managed assembly.

## Contents

- [Client integrity and anti-cheat architecture](analysis/client-integrity-and-anti-cheat.md) is
  the evidence-first technical report.
- [The Score Has More Than One Clock](BLOG.md) is the long-form research article, with diagrams,
  equations, reconstructed pseudocode, and engineering interpretation.
- [`artifacts/security-target-manifest.json`](artifacts/security-target-manifest.json) records the
  target fingerprint, selected metadata tokens, raw-IL hashes, Authenticode identity, native-import
  summary, and negative API queries.
- [`scripts/extract-security-surface.ps1`](scripts/extract-security-surface.ps1) regenerates that
  manifest without executing the target.

## Reproduce the metadata evidence

Run the extractor from Windows PowerShell:

```powershell
.\extract-security-surface.ps1 C:\path\to\osu!.exe
```

To write the compact public manifest:

```powershell
.\extract-security-surface.ps1 C:\path\to\osu!.exe `
    -OutputPath .\security-target-manifest.json
```

`-IncludeAllNativeImports` adds every declared P/Invoke to a local report. The committed artifact
keeps only a module-count summary and the imports relevant to the analysis; bundled audio, video,
and graphics interop otherwise dominates the file.

The script hard-fails on an executable hash mismatch. It uses `ReflectionOnlyLoad`, does not invoke
target methods, does not attach to a running process, and performs no network activity. A full
decompiler tree remains local and ignored; proprietary binaries and decompiled source are not
committed.

## Research boundary

This work describes where trust is established, which consistency signals are visible, and where
static evidence stops. It intentionally omits bypass recipes, binary patches, suppression hooks,
forged payloads, and server-evasion strategies. Exact tokens and IL hashes are included as
reproducibility anchors, not as modification instructions.
