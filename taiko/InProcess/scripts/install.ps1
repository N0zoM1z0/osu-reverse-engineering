param(
    [string] $OsuDirectory = 'C:\Games\osu!',
    [string] $ArtifactDirectory = ''
)

$ErrorActionPreference = 'Stop'
$expectedSha256 = '6e182c10d1813209d12753dbc70b3a5bba00fef4ecf64bc42051870e6dfe4b7d'
if ([string]::IsNullOrWhiteSpace($ArtifactDirectory)) {
    $ArtifactDirectory = Join-Path $PSScriptRoot '..\..\artifacts\inprocess\net40'
}

$osu = Join-Path $OsuDirectory 'osu!.exe'
$loaderSource = Join-Path $ArtifactDirectory 'LocalTaikoAgent.Loader.dll'
$pluginSource = Join-Path $ArtifactDirectory 'LocalTaikoAgent.Plugin.dll'
$pluginDirectory = Join-Path $OsuDirectory 'LocalTaikoAgent'
$launchSource = Join-Path $PSScriptRoot 'launch-osu.ps1'
$batchSource = Join-Path $PSScriptRoot 'launch-taiko-agent.bat'

if (-not (Test-Path $osu)) { throw "Missing $osu" }
if ((Get-FileHash $osu -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expectedSha256) {
    throw 'osu!.exe fingerprint differs from the analysed build; refusing to install.'
}
if (-not (Test-Path $loaderSource) -or -not (Test-Path $pluginSource)) {
    throw 'Build artifacts are missing. Run taiko/InProcess/scripts/build-net40.sh first.'
}

New-Item -ItemType Directory -Force -Path $pluginDirectory | Out-Null
Copy-Item $loaderSource (Join-Path $OsuDirectory 'LocalTaikoAgent.Loader.dll') -Force
Copy-Item $loaderSource (Join-Path $pluginDirectory 'LocalTaikoAgent.Loader.dll') -Force
Copy-Item $pluginSource (Join-Path $pluginDirectory 'LocalTaikoAgent.Plugin.dll') -Force
Copy-Item $launchSource (Join-Path $pluginDirectory 'launch-osu.ps1') -Force
Copy-Item $batchSource (Join-Path $OsuDirectory 'Launch osu! with Taiko Agent.bat') -Force

Write-Output "Installed LocalTaikoAgent into $OsuDirectory"
Write-Output 'No osu! executable or configuration file was modified.'
Write-Output 'Launch with: Launch osu! with Taiko Agent.bat'
