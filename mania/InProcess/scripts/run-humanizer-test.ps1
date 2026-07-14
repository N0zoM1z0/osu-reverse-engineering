param(
    [Parameter(Mandatory = $true)]
    [string] $ArtifactDirectory,

    [string] $Beatmap = ''
)

$ErrorActionPreference = 'Stop'
$executable = Join-Path $ArtifactDirectory 'LocalManiaAuto.HumanizerTest.exe'
if (-not (Test-Path $executable)) {
    throw "Missing $executable. Run InProcess/scripts/build-net40.sh first."
}

if ([string]::IsNullOrWhiteSpace($Beatmap)) {
    & $executable
}
else {
    & $executable $Beatmap
}
exit $LASTEXITCODE
