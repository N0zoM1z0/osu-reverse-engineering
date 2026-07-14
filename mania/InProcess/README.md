# In-process Player-mode mania agent

This directory contains the .NET Framework 4 implementation loaded into the analysed osu!stable
process. `APPDOMAIN_MANAGER_ASM` and `APPDOMAIN_MANAGER_TYPE` ask CLR v4 to instantiate
`LocalManiaAutoDomainManager` in the default AppDomain before normal managed startup. The loader
then resolves `LocalManiaAuto.Plugin.dll` from a subdirectory and invokes its entry point.

No game executable is patched. The environment variables are scoped to the child created by
`launch-osu.ps1`; an ordinary launch remains unchanged.

## Active architecture

The active v0.5.0 build is a normal Player-input agent, not the historical replay injector:

- require the exact executable SHA-256 and validated managed metadata shapes;
- require global `Play`, ruleset `OsuMania`, no replay source, and no automation mod;
- parse the current native `.osu` file and build lane down/up transitions;
- read the internal song clock every worker tick;
- emit due transitions as x86-compatible Win32 `SendInput` scan-code events;
- expose a user-controlled, no-activate owned overlay;
- release every tracked key when control is disabled or a runtime gate fails.

`ReplayInjector.cs` and `NativeFrameBuilder.cs` remain as historical/research evidence, but
`ReplayInjector.cs` is excluded from `LocalManiaAuto.Plugin.dll` by the build script.

## Humanization model

The humanizer builds a timeline once per score; runtime execution still happens tick by tick. It
combines:

- standardized core timing with `base UR = 10 * population sigma(ms)`;
- an AR(1) process with correlation based on inter-note time;
- chord-shared error, lane bias, chord roll, and density-scaled independent noise;
- persistent three-to-seven-group early rush bursts;
- optional fatigue, jammed presses, and sticky releases;
- native, 240, 120, or 60 Hz frame quantization with phase wander and rare hitches;
- explicitly controlled 200 and 100 tail mixtures with a stronger dense-section multiplier for
  100s.

OverallDifficulty and selected timing mods are converted to the exact recovered stable mania
windows. Explicit low-grade samples stay inside their intended band, and a final per-lane
feasibility pass projects every press and release into at least the 100 safety window. No style has
an intentional miss setting.

## Overlay controls

- `Ctrl+Alt+F7`: open or close the panel.
- `Ctrl+Alt+F8`: Player/self versus Agent.
- `Ctrl+Alt+Up/Down`: select one of twelve rows.
- `Ctrl+Alt+Left/Right/Enter`: adjust the selected value.

Default HUMAN settings are base UR 65, -4 ms timing bias, 20% rush mix, 1.0% 200 mix, 0.2% 100
mix, 125% dense boost, 240 Hz cadence, fatigue off, 1% finger trouble, and a fresh seed per play.

## Build

```bash
./InProcess/scripts/build-net40.sh
```

Generated files go to `artifacts/inprocess/net40/`. See the module-level
[README](../README.md) for the research overview. The dedicated
[installation and usage manual](../docs/INSTALLATION_AND_USAGE.md) covers target verification,
artifact checks, loader/metadata probes, installation, key configuration, overlay operation,
troubleshooting, full-map parity, and removal.
