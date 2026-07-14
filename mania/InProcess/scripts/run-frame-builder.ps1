param(
    [Parameter(Mandatory = $true)]
    [string] $ArtifactDirectory,

    [Parameter(Mandatory = $true)]
    [string] $Beatmap,

    [switch] $All
)

$ErrorActionPreference = 'Stop'
$artifact = (Get-Item -LiteralPath $ArtifactDirectory).FullName
$map = (Get-Item -LiteralPath $Beatmap).FullName
$executable = Join-Path $artifact 'LocalManiaAuto.FrameBuilderTest.exe'

if ($All) {
    & $executable $map '--all'
}
else {
    & $executable $map
}

exit $LASTEXITCODE
