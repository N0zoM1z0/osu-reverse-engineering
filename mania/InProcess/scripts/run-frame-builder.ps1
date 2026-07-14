param(
    [Parameter(Mandatory = $true)]
    [string] $ArtifactDirectory,

    [Parameter(Mandatory = $true)]
    [string] $Beatmap,

    [switch] $All
)

$ErrorActionPreference = 'Stop'
$artifact = (Resolve-Path -LiteralPath $ArtifactDirectory).Path
$map = (Resolve-Path -LiteralPath $Beatmap).Path
$executable = Join-Path $artifact 'LocalManiaAuto.FrameBuilderTest.exe'

if ($All) {
    & $executable $map '--all'
}
else {
    & $executable $map
}

exit $LASTEXITCODE
