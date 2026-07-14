param(
    [Parameter(Mandatory = $true)]
    [string] $ArtifactDirectory,

    [string] $OsuPath = 'C:\Games\osu!\osu!.exe'
)

$ErrorActionPreference = 'Stop'
$artifact = (Resolve-Path -LiteralPath $ArtifactDirectory).Path
$osu = (Resolve-Path -LiteralPath $OsuPath).Path
$temp = Join-Path $env:TEMP ("LocalManiaAuto-MetadataProbe-" + $PID)
$exitCode = 1

try {
    New-Item -ItemType Directory -Force -Path $temp | Out-Null
    Copy-Item (Join-Path $artifact 'LocalManiaAuto.MetadataProbe.exe') $temp
    Copy-Item (Join-Path $artifact 'LocalManiaAuto.Plugin.dll') $temp

    $probe = Join-Path $temp 'LocalManiaAuto.MetadataProbe.exe'
    $plugin = Join-Path $temp 'LocalManiaAuto.Plugin.dll'
    & $probe $osu $plugin
    $exitCode = $LASTEXITCODE
}
finally {
    Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
}

exit $exitCode
