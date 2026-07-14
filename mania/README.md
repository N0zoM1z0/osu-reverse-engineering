# osu!mania Research Module

This module contains two independent implementations and the reverse-engineering notes that tie
them to one hash-locked osu!stable build.

`ManiaAuto/` is a readable .NET 8 reference tool. It parses native Mode 3 beatmaps, reconstructs the
built-in Auto replay-frame model, exports a real-input event timeline, and includes a Windows
foreground-input prototype. `InProcess/` targets .NET Framework 4 and contains the default-AppDomain
loader, live Player-mode agent, owned overlay, humanizer, metadata probes, and parity test hosts.

## Target

```text
Product:     osu!stable
Version:     1.3.3.8
Architecture: PE32 / x86 managed CLR v4
SHA-256:     6e182c10d1813209d12753dbc70b3a5bba00fef4ecf64bc42051870e6dfe4b7d
```

Metadata tokens in the in-process plugin are not portable API identifiers. The plugin verifies the
complete executable hash, member shapes, declaring/return types, and critical IL before it arms.

## Layout

```text
ManiaAuto/
  Beatmap.cs              native Mode 3 parser
  ReplayFrames.cs         built-in Auto frame model
  LiveTimeline.cs         physical key transition planner
  WindowsPlayback.cs      foreground SendInput prototype
  TestData/               synthetic, redistributable test map

InProcess/
  Loader/                 CLR AppDomainManager bootstrap
  Plugin/                 live agent, planner, overlay, humanizer
  TestHost/               net40 parity and metadata probes
  scripts/                build, install, launch, and verification scripts

reverse/
  analysis/               curated findings; no full decompiler dump
  scripts/                reproducible ILSpy command

artifacts/inprocess/net40/
  LocalManiaAuto.Loader.dll
  LocalManiaAuto.Plugin.dll
  SHA256SUMS
```

## Beatmap and Auto model

For native mania, `[General] Mode` must be `3` and integer `[Difficulty] CircleSize` is the key
count. A hit object's lane is:

```text
lane = clamp(floor(x * keyCount / 512), 0, keyCount - 1)
```

The recovered stable Auto model stores the aggregate mania key mask in replay-frame `x`:

- lane `n` uses bit `1 << n`;
- tap: set at `startTime`, clear at `startTime + 1`;
- long note: set at `startTime`, clear at `endTime - 1`;
- simultaneous changes merge into one frame;
- a long-note bit is propagated through intermediate frames.

Build and inspect the readable implementation:

```bash
dotnet build ManiaAuto/ManiaAuto.csproj -c Release
dotnet run --project ManiaAuto -- self-test
dotnet run --project ManiaAuto -- inspect ManiaAuto/TestData/minimal-4k.osu
dotnet run --project ManiaAuto -- frames ManiaAuto/TestData/minimal-4k.osu
dotnet run --project ManiaAuto -- events ManiaAuto/TestData/minimal-4k.osu --tap-ms 8
```

## In-process live agent

The active v0.5.0 plugin does not use Auto or a replay list as its input source. It remains in
normal `Play + OsuMania`, reads the internal gameplay clock every worker tick, and sends real
scan-code down/up events with Win32 `SendInput`. The normal mania columns, judgement logic,
combo/health system, and replay recorder observe those states exactly as they observe keyboard
input.

The launcher starts in `PLAYER / SELF`, so loading the plugin does not immediately send input.

- `Ctrl+Alt+F7`: expand or collapse the settings panel.
- `Ctrl+Alt+F8`: switch between Player/self and Agent.
- `Ctrl+Alt+Up/Down`: select a setting.
- `Ctrl+Alt+Left/Right/Enter`: change it.

The twelve rows control style, base UR, global timing bias, rush bursts, 200 and 100 mixtures,
density boost, frame cadence, fatigue, finger trouble, and repeatable versus fresh variation.

## Build and verify

The net40 build script is designed for WSL with Windows .NET Framework available at the mounted
Windows path. Override `CSC_NET40` if necessary.

```bash
./InProcess/scripts/build-net40.sh

powershell.exe -NoProfile -ExecutionPolicy Bypass -File \
  "$(wslpath -w InProcess/scripts/test-loader.ps1)" \
  -ArtifactDirectory "$(wslpath -w artifacts/inprocess/net40)"

powershell.exe -NoProfile -ExecutionPolicy Bypass -File \
  "$(wslpath -w InProcess/scripts/test-metadata.ps1)" \
  -ArtifactDirectory "$(wslpath -w artifacts/inprocess/net40)" \
  -OsuPath 'C:\Games\osu!\osu!.exe'
```

Full-map scripts take the caller's own Songs directory explicitly:

```bash
./InProcess/scripts/verify-frame-parity.sh /mnt/c/Games/osu/Songs
./InProcess/scripts/verify-event-parity.sh /mnt/c/Games/osu/Songs
./InProcess/scripts/verify-humanizer.sh /mnt/c/Games/osu/Songs
```

The development corpus contained 13 native Mode 3 maps. The final run passed all 13 native Auto
frame comparisons, all 13 live-event comparisons, and 13 maps times four humanizer profiles. The
humanizer suite also covers NM, EZ, HR, DT, HT, and HR+DT window transforms with no generated 50 or
miss.

## Install and launch

Close osu! normally before replacing an already loaded plugin. In PowerShell:

```powershell
.\InProcess\scripts\install.ps1 -OsuDirectory 'C:\Games\osu!'
.\InProcess\scripts\launch-osu.ps1 -OsuPath 'C:\Games\osu!\osu!.exe'
```

The loader environment exists only in the child process created by the launcher. A normal shortcut
launch does not load the plugin. Remove installed loader/plugin files with:

```powershell
.\InProcess\scripts\uninstall.ps1 -OsuDirectory 'C:\Games\osu!'
```

## Documentation

- [End-to-end engineering blog](BLOG.md)
- [Recovered native Auto algorithm](reverse/analysis/mania-auto.md)
- [CLR loader and historical replay-list proof](reverse/analysis/inprocess-loader.md)
- [Normal Player-input live agent](reverse/analysis/live-agent.md)
- [Stable mania judgement windows](reverse/analysis/mania-judgement-windows.md)
