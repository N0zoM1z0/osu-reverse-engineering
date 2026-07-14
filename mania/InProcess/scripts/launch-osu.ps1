param(
    [string] $OsuPath = 'C:\Games\osu!\osu!.exe',
    [bool] $Enabled = $false,
    [string] $Keys = '',
    [ValidateRange(1, 100)]
    [int] $TapMilliseconds = 8,
    [ValidateRange(-5000, 5000)]
    [int] $OffsetMilliseconds = 0,
    [ValidateRange(10, 1000)]
    [int] $MaximumLatenessMilliseconds = 80,
    [ValidateRange(100, 5000)]
    [int] $ClockStallMilliseconds = 250
)

$ErrorActionPreference = 'Stop'
$expectedSha256 = '6e182c10d1813209d12753dbc70b3a5bba00fef4ecf64bc42051870e6dfe4b7d'
$loaderAssembly = 'LocalManiaAuto.Loader, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null'
$loaderType = 'LocalManiaAuto.Loader.LocalManiaAutoDomainManager'

$osu = Get-Item $OsuPath
$osuDirectory = $osu.Directory.FullName
$loader = Join-Path $osuDirectory 'LocalManiaAuto.Loader.dll'
$stagedLoader = Join-Path $osuDirectory 'LocalManiaAuto\LocalManiaAuto.Loader.dll'
$plugin = Join-Path $osuDirectory 'LocalManiaAuto\LocalManiaAuto.Plugin.dll'
$log = Join-Path $osuDirectory 'LocalManiaAuto\LocalManiaAuto.log'

if ((Get-FileHash $osu.FullName -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expectedSha256) {
    throw 'osu!.exe fingerprint differs from the analysed build; refusing to launch the plugin.'
}
if (-not (Test-Path $stagedLoader) -or -not (Test-Path $plugin)) {
    throw 'Plugin is not installed beside osu!.exe. Run install.ps1 first.'
}
if (Get-Process -Name 'osu!' -ErrorAction SilentlyContinue) {
    throw 'osu! is already running. Close it normally first; this launcher will not terminate it for you.'
}

# osu! may clean unknown DLLs from its root after startup. Keep the durable copy
# in the plugin subdirectory and restore the CLR bootstrap immediately before launch.
Copy-Item $stagedLoader $loader -Force
if ((Get-FileHash $loader -Algorithm SHA256).Hash -ne
    (Get-FileHash $stagedLoader -Algorithm SHA256).Hash) {
    throw 'The restored AppDomainManager loader failed its hash check.'
}

$childEnvironment = [ordered]@{
    'APPDOMAIN_MANAGER_ASM' = $loaderAssembly
    'APPDOMAIN_MANAGER_TYPE' = $loaderType
    'MANIA_AUTO_PLUGIN' = $plugin
    'MANIA_AUTO_LOG' = $log
    'MANIA_AGENT_ENABLED' = $(if ($Enabled) { '1' } else { '0' })
    'MANIA_AGENT_KEYS' = $Keys
    'MANIA_AGENT_TAP_MS' = $TapMilliseconds.ToString()
    'MANIA_AGENT_OFFSET_MS' = $OffsetMilliseconds.ToString()
    'MANIA_AGENT_MAX_LATE_MS' = $MaximumLatenessMilliseconds.ToString()
    'MANIA_AGENT_CLOCK_STALL_MS' = $ClockStallMilliseconds.ToString()
}
$savedEnvironment = @{}

try {
    foreach ($entry in $childEnvironment.GetEnumerator()) {
        $savedEnvironment[$entry.Key] = [Environment]::GetEnvironmentVariable($entry.Key, 'Process')
        [Environment]::SetEnvironmentVariable($entry.Key, [string] $entry.Value, 'Process')
    }

    # ShellExecute keeps the long-running GUI child detached from a WSL caller's
    # stdout/stderr handles. The temporary process environment is inherited by
    # this child only and is restored immediately below.
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

Write-Output "Started osu! PID $($process.Id) with LocalManiaAgent in its default AppDomain."
Write-Output "Log: $log"
Write-Output 'Overlay settings: Ctrl+Alt+F7; quick Player/Agent toggle: Ctrl+Alt+F8'
Write-Output 'Architecture: normal Player mode + internal song clock + real key down/up; no Auto/replay list.'
