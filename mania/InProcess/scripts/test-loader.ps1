param(
    [Parameter(Mandatory = $true)]
    [string] $ArtifactDirectory
)

$ErrorActionPreference = 'Stop'
$artifact = (Resolve-Path $ArtifactDirectory).Path
$temp = Join-Path $env:TEMP ("LocalManiaAuto-LoaderTest-" + $PID)
$resultLog = Join-Path $artifact 'appdomain-loader-test.log'

try {
    New-Item -ItemType Directory -Force -Path $temp | Out-Null
    Copy-Item (Join-Path $artifact 'LocalManiaAuto.Loader.dll') $temp
    Copy-Item (Join-Path $artifact 'LocalManiaAuto.Plugin.dll') $temp
    Copy-Item (Join-Path $artifact 'LocalManiaAuto.TestHost.exe') $temp

    $env:APPDOMAIN_MANAGER_ASM = 'LocalManiaAuto.Loader, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null'
    $env:APPDOMAIN_MANAGER_TYPE = 'LocalManiaAuto.Loader.LocalManiaAutoDomainManager'
    $env:MANIA_AUTO_PLUGIN = Join-Path $temp 'LocalManiaAuto.Plugin.dll'
    $env:MANIA_AUTO_LOG = Join-Path $temp 'loader-test.log'
    $env:MANIA_AUTO_ENABLED = '0'

    & (Join-Path $temp 'LocalManiaAuto.TestHost.exe')
    if ($LASTEXITCODE -ne 0) {
        throw "TestHost exited with code $LASTEXITCODE"
    }

    $log = Get-Content $env:MANIA_AUTO_LOG -Raw
    if ($log -notmatch 'plugin started:' -or $log -notmatch 'Entry.Start in AppDomain DefaultDomain') {
        throw "Expected loader/plugin evidence was not found in log:`n$log"
    }

    Set-Content -Path $resultLog -Value $log -Encoding UTF8
    Write-Output $log
    Write-Output "APPDOMAIN LOADER TEST: PASS"
}
finally {
    Remove-Item Env:APPDOMAIN_MANAGER_ASM -ErrorAction SilentlyContinue
    Remove-Item Env:APPDOMAIN_MANAGER_TYPE -ErrorAction SilentlyContinue
    Remove-Item Env:MANIA_AUTO_PLUGIN -ErrorAction SilentlyContinue
    Remove-Item Env:MANIA_AUTO_LOG -ErrorAction SilentlyContinue
    Remove-Item Env:MANIA_AUTO_ENABLED -ErrorAction SilentlyContinue
    Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
}
