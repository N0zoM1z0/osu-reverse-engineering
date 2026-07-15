param(
    [string] $OsuPath = 'C:\Games\osu!\osu!.exe',
    [switch] $Enabled
)

$ErrorActionPreference = 'Stop'
$expectedSha256 = '6e182c10d1813209d12753dbc70b3a5bba00fef4ecf64bc42051870e6dfe4b7d'
$loaderAssembly = 'LocalCatchAgent.Loader, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null'
$loaderType = 'LocalCatchAgent.Loader.LocalCatchAgentDomainManager'

$osu = Get-Item $OsuPath
$osuDirectory = $osu.Directory.FullName
$loader = Join-Path $osuDirectory 'LocalCatchAgent.Loader.dll'
$stagedLoader = Join-Path $osuDirectory 'LocalCatchAgent\LocalCatchAgent.Loader.dll'
$plugin = Join-Path $osuDirectory 'LocalCatchAgent\LocalCatchAgent.Plugin.dll'
$log = Join-Path $osuDirectory 'LocalCatchAgent\LocalCatchAgent.log'

if ((Get-FileHash $osu.FullName -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expectedSha256) {
    throw 'osu!.exe fingerprint differs from the analysed build; refusing to launch the plugin.'
}
if (-not (Test-Path $stagedLoader) -or -not (Test-Path $plugin)) {
    throw 'Catch plugin is not installed beside osu!.exe. Run install.ps1 first.'
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
    'CATCH_AGENT_PLUGIN' = $plugin
    'CATCH_AGENT_LOG' = $log
    'CATCH_AGENT_ENABLED' = $(if ($Enabled) { '1' } else { '0' })
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

Write-Output "Started osu! PID $($process.Id) with LocalCatchAgent."
Write-Output "Log: $log"
Write-Output 'Ctrl+Alt+F7: settings; Ctrl+Alt+F8: Player/Agent toggle.'
Write-Output 'Architecture: normal Catch Player mode + configured Left/Right/Dash + real key state; no replay list.'
