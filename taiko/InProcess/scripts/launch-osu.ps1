param(
    [string] $OsuPath = 'C:\Games\osu!\osu!.exe',
    [bool] $Enabled = $false,
    [ValidateRange(1, 100)]
    [int] $TapMilliseconds = 8,
    [ValidateRange(-5000, 5000)]
    [int] $OffsetMilliseconds = 0,
    [ValidateRange(10, 1000)]
    [int] $MaximumLatenessMilliseconds = 70,
    [ValidateRange(100, 5000)]
    [int] $ClockStallMilliseconds = 250,
    [switch] $SubmissionDiagnostics
)

$ErrorActionPreference = 'Stop'
$expectedSha256 = '6e182c10d1813209d12753dbc70b3a5bba00fef4ecf64bc42051870e6dfe4b7d'
$loaderAssembly = 'LocalTaikoAgent.Loader, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null'
$loaderType = 'LocalTaikoAgent.Loader.LocalTaikoAgentDomainManager'

$osu = Get-Item $OsuPath
$osuDirectory = $osu.Directory.FullName
$loader = Join-Path $osuDirectory 'LocalTaikoAgent.Loader.dll'
$stagedLoader = Join-Path $osuDirectory 'LocalTaikoAgent\LocalTaikoAgent.Loader.dll'
$plugin = Join-Path $osuDirectory 'LocalTaikoAgent\LocalTaikoAgent.Plugin.dll'
$log = Join-Path $osuDirectory 'LocalTaikoAgent\LocalTaikoAgent.log'

if ((Get-FileHash $osu.FullName -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expectedSha256) {
    throw 'osu!.exe fingerprint differs from the analysed build; refusing to launch the plugin.'
}
if (-not (Test-Path $stagedLoader) -or -not (Test-Path $plugin)) {
    throw 'Taiko plugin is not installed beside osu!.exe. Run install.ps1 first.'
}
if (Get-Process -Name 'osu!' -ErrorAction SilentlyContinue) {
    throw 'osu! is already running. Close it normally first; this launcher will not terminate it.'
}

Copy-Item $stagedLoader $loader -Force
if ((Get-FileHash $loader -Algorithm SHA256).Hash -ne
    (Get-FileHash $stagedLoader -Algorithm SHA256).Hash) {
    throw 'The restored AppDomainManager loader failed its hash check.'
}

$childEnvironment = [ordered]@{
    'APPDOMAIN_MANAGER_ASM' = $loaderAssembly
    'APPDOMAIN_MANAGER_TYPE' = $loaderType
    'TAIKO_AGENT_PLUGIN' = $plugin
    'TAIKO_AGENT_LOG' = $log
    'TAIKO_AGENT_ENABLED' = $(if ($Enabled) { '1' } else { '0' })
    'TAIKO_AGENT_TAP_MS' = $TapMilliseconds.ToString()
    'TAIKO_AGENT_OFFSET_MS' = $OffsetMilliseconds.ToString()
    'TAIKO_AGENT_MAX_LATE_MS' = $MaximumLatenessMilliseconds.ToString()
    'TAIKO_AGENT_CLOCK_STALL_MS' = $ClockStallMilliseconds.ToString()
    'TAIKO_SUBMISSION_DIAGNOSTICS' = $(if ($SubmissionDiagnostics) { '1' } else { '0' })
}
$savedEnvironment = @{}

try {
    foreach ($entry in $childEnvironment.GetEnumerator()) {
        $savedEnvironment[$entry.Key] = [Environment]::GetEnvironmentVariable($entry.Key, 'Process')
        [Environment]::SetEnvironmentVariable($entry.Key, [string] $entry.Value, 'Process')
    }
    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = $osu.FullName
    $start.WorkingDirectory = $osuDirectory
    $start.UseShellExecute = $true
    $process = [System.Diagnostics.Process]::Start($start)
}
finally {
    foreach ($entry in $savedEnvironment.GetEnumerator()) {
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
    }
}

Write-Output "Started osu! PID $($process.Id) with LocalTaikoAgent."
Write-Output "Log: $log"
Write-Output 'Ctrl+Alt+F7: settings; Ctrl+Alt+F8: Player/Agent toggle.'
Write-Output 'Architecture: normal Player mode + current bindings + real key down/up; no replay list.'
if ($SubmissionDiagnostics) {
    Write-Output 'Read-only submission diagnostics: enabled (validity/login boolean/state only).'
}
