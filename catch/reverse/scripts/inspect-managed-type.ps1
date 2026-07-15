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

function Convert-ToEscapedName([string]$Value) {
    if ($null -eq $Value) { return $null }
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
    try { return [System.Reflection.Assembly]::ReflectionOnlyLoad($EventArgs.Name) }
    catch { return $null }
}

[System.AppDomain]::CurrentDomain.add_ReflectionOnlyAssemblyResolve($resolver)
try {
    $assembly = [System.Reflection.Assembly]::ReflectionOnlyLoadFrom($fullPath)
    $type = $assembly.GetType($TypeName, $false, $false)
    if ($null -eq $type) {
        $matches = @($assembly.GetTypes() | Where-Object {
            $_.FullName -like ('*' + $TypeName + '*')
        })
        if ($matches.Count -gt 1) {
            $topLevelMatches = @($matches | Where-Object { $_.FullName -notlike '*+*' })
            if ($topLevelMatches.Count -eq 1) { $matches = $topLevelMatches }
        }
        if ($matches.Count -ne 1) {
            $names = @($matches | ForEach-Object { Convert-ToEscapedName $_.FullName })
            throw "type query matched $($matches.Count) types: $($names -join ', ')"
        }
        $type = $matches[0]
    }

    $flags = [System.Reflection.BindingFlags]'DeclaredOnly,Instance,Static,Public,NonPublic'
    $hierarchy = New-Object System.Collections.Generic.List[object]
    $current = $type
    while ($null -ne $current) {
        $fields = foreach ($field in $current.GetFields($flags)) {
            [pscustomobject]@{
                token = ('0x{0:x8}' -f $field.MetadataToken)
                name = (Convert-ToEscapedName $field.Name)
                fieldType = (Convert-ToEscapedName $field.FieldType.FullName)
                isStatic = $field.IsStatic
                visibility = if ($field.IsPublic) { 'public' } elseif ($field.IsFamily) { 'family' } elseif ($field.IsAssembly) { 'assembly' } else { 'private' }
            }
        }
        $methods = foreach ($method in @($current.GetConstructors($flags)) + @($current.GetMethods($flags))) {
            [pscustomobject]@{
                token = ('0x{0:x8}' -f $method.MetadataToken)
                name = (Convert-ToEscapedName $method.Name)
                returnType = if ($method -is [System.Reflection.MethodInfo]) { Convert-ToEscapedName $method.ReturnType.FullName } else { $null }
                parameterTypes = @($method.GetParameters() | ForEach-Object { Convert-ToEscapedName $_.ParameterType.FullName })
                isStatic = $method.IsStatic
            }
        }
        $hierarchy.Add([pscustomobject]@{
            token = ('0x{0:x8}' -f $current.MetadataToken)
            type = (Convert-ToEscapedName $current.FullName)
            fields = @($fields | Sort-Object token)
            methods = @($methods | Sort-Object token)
        })
        $current = $current.BaseType
    }

    [ordered]@{
        executableSha256 = $fileHash
        requestedType = (Convert-ToEscapedName $type.FullName)
        hierarchy = $hierarchy.ToArray()
    } | ConvertTo-Json -Depth 10
}
finally {
    [System.AppDomain]::CurrentDomain.remove_ReflectionOnlyAssemblyResolve($resolver)
}
