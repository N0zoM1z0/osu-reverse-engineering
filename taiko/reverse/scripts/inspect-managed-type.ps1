param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$OsuPath,

    [Parameter(Mandatory = $true, Position = 1)]
    [string]$TypeName
)

$ErrorActionPreference = 'Stop'
$SupportedSha256 = '6e182c10d1813209d12753dbc70b3a5bba00fef4ecf64bc42051870e6dfe4b7d'

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

function Get-Sha256Hex([byte[]]$Bytes) {
    $algorithm = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($algorithm.ComputeHash($Bytes))).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $algorithm.Dispose()
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
    $type = $assembly.GetType($TypeName, $false, $false)
    if ($null -eq $type) {
        throw "managed type was not found: $TypeName"
    }

    $flags = [System.Reflection.BindingFlags]'DeclaredOnly,Instance,Static,Public,NonPublic'
    $members = @($type.GetConstructors($flags)) + @($type.GetMethods($flags))
    $rows = foreach ($method in $members) {
        $body = $method.GetMethodBody()
        [byte[]]$il = if ($null -eq $body) { @() } else { $body.GetILAsByteArray() }
        [pscustomobject]@{
            memberKind = if ($method -is [System.Reflection.ConstructorInfo]) { 'constructor' } else { 'method' }
            token = ('0x{0:x8}' -f $method.MetadataToken)
            name = Convert-ToEscapedName $method.Name
            returnType = if ($method -is [System.Reflection.MethodInfo]) {
                Convert-ToEscapedName $method.ReturnType.FullName
            } else {
                $null
            }
            parameterTypes = @($method.GetParameters() | ForEach-Object {
                Convert-ToEscapedName $_.ParameterType.FullName
            })
            isStatic = $method.IsStatic
            ilBytes = $il.Length
            ilSha256 = if ($il.Length -eq 0) { $null } else { Get-Sha256Hex $il }
        }
    }

    [ordered]@{
        executableSha256 = $fileHash
        type = Convert-ToEscapedName $type.FullName
        methods = @($rows | Sort-Object token)
    } | ConvertTo-Json -Depth 8
}
finally {
    [System.AppDomain]::CurrentDomain.remove_ReflectionOnlyAssemblyResolve($resolver)
}
