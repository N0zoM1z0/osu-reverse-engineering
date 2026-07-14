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
$loaderSource = Join-Path $ArtifactDirectory 'LocalManiaAuto.Loader.dll'
$pluginSource = Join-Path $ArtifactDirectory 'LocalManiaAuto.Plugin.dll'
$pluginDirectory = Join-Path $OsuDirectory 'LocalManiaAuto'

if (-not (Test-Path $osu)) { throw "Missing $osu" }
if ((Get-FileHash $osu -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expectedSha256) {
    throw 'osu!.exe fingerprint differs from the analysed build; refusing to install.'
}
if (-not (Test-Path $loaderSource) -or -not (Test-Path $pluginSource)) {
    throw 'Build artifacts are missing. Run InProcess/scripts/build-net40.sh in WSL first.'
}

New-Item -ItemType Directory -Force -Path $pluginDirectory | Out-Null
Copy-Item $loaderSource (Join-Path $OsuDirectory 'LocalManiaAuto.Loader.dll') -Force
Copy-Item $loaderSource (Join-Path $pluginDirectory 'LocalManiaAuto.Loader.dll') -Force
Copy-Item $pluginSource (Join-Path $pluginDirectory 'LocalManiaAuto.Plugin.dll') -Force
Write-Output "Installed LocalManiaAuto loader/plugin into $OsuDirectory"
Write-Output 'No osu! binary or configuration file was modified.'
