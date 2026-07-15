# LocalCatchAgent stable baseline

This directory contains the active 2026-07-14 23:57 SGT Catch baseline. It runs inside the pinned
osu!stable process, waits for a normal Catch Player session, builds a complete viable path from the
runtime-converted fruit list, and drives the configured Left, Right, and Dash bindings through
ordinary scan-code input. It does not create an Auto or replay frame list.

## Build and test

From WSL at the repository root:

```bash
catch/InProcess/scripts/build-net40.sh
catch/artifacts/inprocess/net40/LocalCatchAgent.PlannerTest.exe
dotnet run --project catch/RuntimePlannerValidation -c Release -- '/path/to/osu!/Songs'
```

The build uses the Windows x86 .NET Framework 4 compiler. The runtime planner test should report:

```text
CATCH NET40 PLANNER: PASS
objects=7, constraints=7, phases=20, hyper=1
```

## Install and launch

Close osu!, then install from PowerShell:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\catch\InProcess\scripts\install.ps1 `
  -OsuDirectory 'C:\Games\osu!'
```

Double-click `Launch osu! with Catch Agent.bat` in the osu! directory. That shortcut starts in
Player mode; `Ctrl+Alt+F8` toggles the Agent and `Ctrl+Alt+F7` shows or hides its settings overlay.
Launching `osu!.exe` directly remains plugin-free.

The loader is hash-locked to the analysed x86 osu!stable executable. If the executable or private
metadata layout changes, installation or startup fails closed instead of guessing new offsets.
