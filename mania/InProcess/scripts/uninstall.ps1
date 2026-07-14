param(
    [string] $OsuDirectory = 'C:\Games\osu!'
)

$ErrorActionPreference = 'Stop'
if (Get-Process -Name 'osu!' -ErrorAction SilentlyContinue) {
    throw 'Close osu! normally before uninstalling.'
}

$loader = Join-Path $OsuDirectory 'LocalManiaAuto.Loader.dll'
$pluginDirectory = Join-Path $OsuDirectory 'LocalManiaAuto'
Remove-Item $loader -Force -ErrorAction SilentlyContinue
Remove-Item $pluginDirectory -Recurse -Force -ErrorAction SilentlyContinue
Write-Output 'LocalManiaAuto files removed. osu!.exe and osu! configuration were untouched.'
