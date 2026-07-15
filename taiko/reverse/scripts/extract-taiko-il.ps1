param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$OsuPath,

    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'

$SupportedSha256 = '6e182c10d1813209d12753dbc70b3a5bba00fef4ecf64bc42051870e6dfe4b7d'
$Targets = @(
    @{ Label = 'taiko-auto-generator'; Token = 0x06001EF7 },
    @{ Label = 'player-input-recorder'; Token = 0x0600229B },
    @{ Label = 'four-button-state-packer'; Token = 0x060011AF },
    @{ Label = 'binding-initializer'; Token = 0x06002C55 },
    @{ Label = 'taiko-circle-accepts-press'; Token = 0x060020E8 },
    @{ Label = 'taiko-circle-resolve-judgement'; Token = 0x060020EB },
    @{ Label = 'difficulty-range'; Token = 0x060028B3 },
    @{ Label = 'taiko-drumroll-native-interval'; Token = 0x06004257 },
    @{ Label = 'taiko-spinner-constructor'; Token = 0x06001D6D },
    @{ Label = 'score-submission-state-getter'; Token = 0x06002B4D },
    @{ Label = 'score-invalidator'; Token = 0x06002B5A },
    @{ Label = 'score-submit-entry'; Token = 0x06002B5C },
    @{ Label = 'score-submit-worker'; Token = 0x06002B6C },
    @{ Label = 'logged-in-predicate'; Token = 0x0600469B }
)

function Get-Sha256Hex([byte[]]$Bytes) {
    $algorithm = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($algorithm.ComputeHash($Bytes))).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $algorithm.Dispose()
    }
}

function Get-FileSha256([string]$Path) {
    $stream = [System.IO.File]::OpenRead($Path)
    $algorithm = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($algorithm.ComputeHash($stream))).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $algorithm.Dispose()
        $stream.Dispose()
    }
}

function Convert-ToEscapedName([string]$Value) {
    $builder = New-Object System.Text.StringBuilder
    foreach ($character in $Value.ToCharArray()) {
        $code = [int]$character
        if ($code -ge 0x20 -and $code -le 0x7e) {
            [void]$builder.Append($character)
        }
        else {
            [void]$builder.Append(('\u{0:x4}' -f $code))
        }
    }
    return $builder.ToString()
}

$fullPath = [System.IO.Path]::GetFullPath($OsuPath)
if (-not [System.IO.File]::Exists($fullPath)) {
    throw "osu!.exe was not found: $fullPath"
}

$fileHash = Get-FileSha256 $fullPath
if ($fileHash -ne $SupportedSha256) {
    throw "unsupported osu!.exe sha256=$fileHash"
}

$gameDirectory = [System.IO.Path]::GetDirectoryName($fullPath)
$resolver = [System.ResolveEventHandler] {
    param($Sender, $EventArgs)

    $assemblyName = New-Object System.Reflection.AssemblyName($EventArgs.Name)
    $candidate = [System.IO.Path]::Combine($gameDirectory, $assemblyName.Name + '.dll')
    if ([System.IO.File]::Exists($candidate)) {
        return [System.Reflection.Assembly]::ReflectionOnlyLoadFrom($candidate)
    }
    try {
        return [System.Reflection.Assembly]::ReflectionOnlyLoad($EventArgs.Name)
    }
    catch {
        return $null
    }
}

[System.AppDomain]::CurrentDomain.add_ReflectionOnlyAssemblyResolve($resolver)
try {
    $assembly = [System.Reflection.Assembly]::ReflectionOnlyLoadFrom($fullPath)
    $module = $assembly.ManifestModule
    $methods = foreach ($target in $Targets) {
        $method = $module.ResolveMethod([int]$target.Token)
        if ($null -eq $method) {
            throw ('metadata token 0x{0:x8} did not resolve' -f $target.Token)
        }
        $body = $method.GetMethodBody()
        if ($null -eq $body) {
            throw ('metadata token 0x{0:x8} has no managed body' -f $target.Token)
        }
        [byte[]]$il = $body.GetILAsByteArray()
        [pscustomobject]@{
            label = $target.Label
            token = ('0x{0:x8}' -f $target.Token)
            declaringType = Convert-ToEscapedName $method.DeclaringType.FullName
            method = Convert-ToEscapedName $method.Name
            ilBytes = $il.Length
            ilSha256 = Get-Sha256Hex $il
        }
    }

    $document = [ordered]@{
        schema = 1
        executable = [ordered]@{
            fileName = [System.IO.Path]::GetFileName($fullPath)
            sha256 = $fileHash
            assemblyVersion = $assembly.GetName().Version.ToString()
        }
        methods = @($methods)
    }
    $json = $document | ConvertTo-Json -Depth 6
    if ([string]::IsNullOrWhiteSpace($OutputPath)) {
        $json
    }
    else {
        $destination = [System.IO.Path]::GetFullPath($OutputPath)
        [System.IO.File]::WriteAllText($destination, $json + [Environment]::NewLine)
        Write-Host "wrote $destination"
    }
}
finally {
    [System.AppDomain]::CurrentDomain.remove_ReflectionOnlyAssemblyResolve($resolver)
}
