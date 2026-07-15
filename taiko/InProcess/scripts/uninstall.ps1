param(
    [string] $OsuDirectory = 'C:\Games\osu!'
)

$ErrorActionPreference = 'Stop'
if (Get-Process -Name 'osu!' -ErrorAction SilentlyContinue) {
    throw 'Close osu! normally before uninstalling.'
}

Remove-Item (Join-Path $OsuDirectory 'LocalTaikoAgent.Loader.dll') -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $OsuDirectory 'Launch osu! with Taiko Agent.bat') -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $OsuDirectory 'LocalTaikoAgent') -Recurse -Force -ErrorAction SilentlyContinue
Write-Output 'LocalTaikoAgent files removed. osu!.exe and osu! configuration were untouched.'
