@echo off
setlocal

set "SUBMISSION_DIAGNOSTICS_ARGUMENT="
if /I "%~1"=="--diagnostics" (
    set "SUBMISSION_DIAGNOSTICS_ARGUMENT=-SubmissionDiagnostics"
    shift
)
if not "%~1"=="" (
    echo [ERROR] Unknown option: %~1
    echo         Supported option: --diagnostics
    exit /b 1
)

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0LocalTaikoAgent\launch-osu.ps1" -OsuPath "%~dp0osu!.exe" %SUBMISSION_DIAGNOSTICS_ARGUMENT%
if errorlevel 1 pause
