param(
    [string] $OsuDirectory = 'C:\Games\osu!'
)

$ErrorActionPreference = 'Stop'
if (Get-Process -Name 'osu!' -ErrorAction SilentlyContinue) {
    throw 'Close osu! normally before uninstalling.'
}

Remove-Item (Join-Path $OsuDirectory 'LocalCatchAgent.Loader.dll') -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $OsuDirectory 'Launch osu! with Catch Agent.bat') -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $OsuDirectory 'LocalCatchAgent') -Recurse -Force -ErrorAction SilentlyContinue
Write-Output 'LocalCatchAgent files removed. osu!.exe and osu! configuration were untouched.'
