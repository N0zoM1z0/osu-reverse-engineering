@echo off
setlocal

if not "%~1"=="" (
    echo [ERROR] Unknown option: %~1
    exit /b 1
)

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0LocalCatchAgent\launch-osu.ps1" -OsuPath "%~dp0osu!.exe"
if errorlevel 1 pause
