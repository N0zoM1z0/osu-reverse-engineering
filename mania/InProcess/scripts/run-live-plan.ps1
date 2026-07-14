param(
    [Parameter(Mandatory = $true)]
    [string] $ArtifactDirectory,

    [Parameter(Mandatory = $true)]
    [string] $Beatmap,

    [ValidateRange(1, 100)]
    [int] $TapMilliseconds = 8,

    [switch] $All
)

$ErrorActionPreference = 'Stop'
$executable = Join-Path $ArtifactDirectory 'LocalManiaAuto.LivePlanTest.exe'
if (-not (Test-Path $executable)) {
    throw "Missing $executable. Run InProcess/scripts/build-net40.sh first."
}

if ($All) {
    & $executable $Beatmap $TapMilliseconds '--all'
}
else {
    & $executable $Beatmap $TapMilliseconds
}
exit $LASTEXITCODE
