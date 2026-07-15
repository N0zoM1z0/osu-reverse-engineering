param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$OsuPath,

    [string]$OutputPath,

    [switch]$IncludeAllNativeImports
)

# Read-only metadata extractor for the pinned osu!stable research target.
#
# This script never invokes a target method, starts osu!, attaches to a process,
# decodes protected strings, changes a score, or performs network activity. It
# uses ReflectionOnlyLoad to fingerprint selected managed bodies and inventory
# statically declared native imports.

$ErrorActionPreference = 'Stop'

$SupportedSha256 = '6e182c10d1813209d12753dbc70b3a5bba00fef4ecf64bc42051870e6dfe4b7d'

$MethodTargets = @(
    @{ Layer = 'executable-trust'; Label = 'startup-entry'; Token = 0x060023B9 },
    @{ Layer = 'executable-trust'; Label = 'authenticode-file-validator'; Token = 0x060035AD },
    @{ Layer = 'executable-trust'; Label = 'update-directory-signature-scan'; Token = 0x06000D79 },

    @{ Layer = 'gameplay-evidence'; Label = 'player-initialization'; Token = 0x06002237 },
    @{ Layer = 'gameplay-evidence'; Label = 'player-finish-and-submit-gates'; Token = 0x06002267 },
    @{ Layer = 'gameplay-evidence'; Label = 'player-main-update-and-clock-guards'; Token = 0x0600226B },
    @{ Layer = 'gameplay-evidence'; Label = 'player-redundant-score-check-a'; Token = 0x06002276 },
    @{ Layer = 'gameplay-evidence'; Label = 'player-redundant-score-check-b'; Token = 0x06002279 },
    @{ Layer = 'gameplay-evidence'; Label = 'player-input-and-replay-recorder'; Token = 0x0600229B },
    @{ Layer = 'gameplay-evidence'; Label = 'player-finish-eligibility-a'; Token = 0x060022A1 },
    @{ Layer = 'gameplay-evidence'; Label = 'player-finish-eligibility-b'; Token = 0x060022A2 },
    @{ Layer = 'gameplay-evidence'; Label = 'player-runtime-integrity-aggregate'; Token = 0x060022A6 },
    @{ Layer = 'gameplay-evidence'; Label = 'player-movement-vector-monitor'; Token = 0x060022A9 },
    @{ Layer = 'gameplay-evidence'; Label = 'guarded-process-snapshot-callback'; Token = 0x060022CE },

    @{ Layer = 'score-envelope'; Label = 'score-checksum'; Token = 0x06002B43 },
    @{ Layer = 'score-envelope'; Label = 'replay-frame-serialization'; Token = 0x06002B46 },
    @{ Layer = 'score-envelope'; Label = 'score-summary-and-integrity-signal-serialization'; Token = 0x06002B52 },
    @{ Layer = 'score-envelope'; Label = 'comprehensive-score-checksum'; Token = 0x06002B53 },
    @{ Layer = 'score-envelope'; Label = 'score-invalidator'; Token = 0x06002B5A },
    @{ Layer = 'score-envelope'; Label = 'periodic-score-sample-serialization'; Token = 0x06002B5B },
    @{ Layer = 'submission-boundary'; Label = 'score-submit-entry'; Token = 0x06002B5C },
    @{ Layer = 'submission-boundary'; Label = 'score-submit-worker'; Token = 0x06002B6C },
    @{ Layer = 'submission-boundary'; Label = 'logged-in-predicate'; Token = 0x0600469B },

    @{ Layer = 'telemetry-boundary'; Label = 'periodic-integrity-coordinator'; Token = 0x06004867 },
    @{ Layer = 'telemetry-boundary'; Label = 'telemetry-event-dispatch'; Token = 0x0600486A },
    @{ Layer = 'telemetry-boundary'; Label = 'telemetry-stage-dispatch'; Token = 0x06004872 },
    @{ Layer = 'process-observation'; Label = 'background-app-compatibility-scanner'; Token = 0x0600284C },

    @{ Layer = 'code-protection'; Label = 'vm-dispatcher-accessor'; Token = 0x0600275A },
    @{ Layer = 'code-protection'; Label = 'protected-string-wrapper'; Token = 0x060049B0 },
    @{ Layer = 'code-protection'; Label = 'protected-string-decoder'; Token = 0x060049B1 }
)

$FieldTargets = @(
    @{ Layer = 'score-envelope'; Label = 'global-current-score'; Token = 0x040013C3 },
    @{ Layer = 'score-envelope'; Label = 'score-validity'; Token = 0x04001990 },
    @{
        Layer = 'gameplay-evidence'
        Label = 'integrity-signal-flags'
        TypeName = '#=zOzP1rPg3E8uUj1uRyHI8VvJbIxgp'
        FieldName = '#=zQ1pk95Y='
    }
)

$NegativeNativeApiQueries = @(
    'IsDebuggerPresent',
    'CheckRemoteDebuggerPresent',
    'NtQueryInformationProcess',
    'ReadProcessMemory',
    'WriteProcessMemory',
    'VirtualQuery',
    'CreateToolhelp32Snapshot',
    'Process32First',
    'Process32Next',
    'GetMessageExtraInfo'
)

$SelectedNativeApiQueries = @(
    'WinVerifyTrust',
    'EnumWindows',
    'GetWindowText',
    'GetClassName',
    'GetWindowThreadProcessId',
    'GetLayeredWindowAttributes',
    'SetWindowsHookExA',
    'QueryPerformanceCounter',
    'QueryPerformanceFrequency',
    'GetModuleHandle',
    'GetModuleHandleA',
    'GetModuleHandleW',
    'GetProcAddress'
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

function Get-LoadableTypes([System.Reflection.Assembly]$Assembly) {
    try {
        return @($Assembly.GetTypes())
    }
    catch [System.Reflection.ReflectionTypeLoadException] {
        return @($_.Exception.Types | Where-Object { $null -ne $_ })
    }
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
    $bindingFlags = [System.Reflection.BindingFlags]'DeclaredOnly,Instance,Static,Public,NonPublic'

    $methods = foreach ($target in $MethodTargets) {
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
            layer = $target.Layer
            label = $target.Label
            token = ('0x{0:x8}' -f $target.Token)
            declaringType = Convert-ToEscapedName $method.DeclaringType.FullName
            method = Convert-ToEscapedName $method.Name
            isStatic = $method.IsStatic
            parameterTypes = @($method.GetParameters() | ForEach-Object {
                Convert-ToEscapedName $_.ParameterType.FullName
            })
            ilBytes = $il.Length
            ilSha256 = Get-Sha256Hex $il
        }
    }

    $fields = foreach ($target in $FieldTargets) {
        if ($target.ContainsKey('Token')) {
            $field = $module.ResolveField([int]$target.Token)
        }
        else {
            $declaringType = $assembly.GetType($target.TypeName, $false, $false)
            if ($null -eq $declaringType) {
                throw "managed type was not found: $($target.TypeName)"
            }
            $field = $declaringType.GetField($target.FieldName, $bindingFlags)
        }
        if ($null -eq $field) {
            throw "field did not resolve: $($target.Label)"
        }
        [pscustomobject]@{
            layer = $target.Layer
            label = $target.Label
            token = ('0x{0:x8}' -f $field.MetadataToken)
            declaringType = Convert-ToEscapedName $field.DeclaringType.FullName
            field = Convert-ToEscapedName $field.Name
            fieldType = Convert-ToEscapedName $field.FieldType.FullName
            isStatic = $field.IsStatic
        }
    }

    $nativeImports = foreach ($type in Get-LoadableTypes $assembly) {
        foreach ($method in $type.GetMethods($bindingFlags)) {
            if (($method.Attributes -band [System.Reflection.MethodAttributes]::PinvokeImpl) -eq 0) {
                continue
            }

            $attribute = @($method.GetCustomAttributesData() | Where-Object {
                $_.AttributeType.FullName -eq 'System.Runtime.InteropServices.DllImportAttribute'
            } | Select-Object -First 1)
            if ($attribute.Count -eq 0) {
                continue
            }

            $moduleName = [string]$attribute[0].ConstructorArguments[0].Value
            $entryPoint = $method.Name
            $setLastError = $false
            foreach ($argument in $attribute[0].NamedArguments) {
                if ($argument.MemberName -eq 'EntryPoint' -and $null -ne $argument.TypedValue.Value) {
                    $entryPoint = [string]$argument.TypedValue.Value
                }
                elseif ($argument.MemberName -eq 'SetLastError') {
                    $setLastError = [bool]$argument.TypedValue.Value
                }
            }

            [pscustomobject]@{
                module = $moduleName.ToLowerInvariant()
                entryPoint = $entryPoint
                token = ('0x{0:x8}' -f $method.MetadataToken)
                declaringType = Convert-ToEscapedName $method.DeclaringType.FullName
                method = Convert-ToEscapedName $method.Name
                setLastError = $setLastError
            }
        }
    }
    $nativeImports = @($nativeImports | Sort-Object module, entryPoint, token)

    $nativeImportSummary = @($nativeImports | Group-Object module | Sort-Object Name | ForEach-Object {
        [pscustomobject]@{
            module = $_.Name
            declarations = $_.Count
        }
    })

    $negativeNativeApiResults = foreach ($query in $NegativeNativeApiQueries) {
        [pscustomobject]@{
            entryPoint = $query
            declared = @($nativeImports | Where-Object { $_.entryPoint -eq $query }).Count -gt 0
        }
    }

    $selectedNativeImports = @($nativeImports | Where-Object {
        $SelectedNativeApiQueries -contains $_.entryPoint
    })

    $signature = Get-AuthenticodeSignature -FilePath $fullPath
    $signerSubject = $null
    if ($null -ne $signature.SignerCertificate) {
        $signerSubject = $signature.SignerCertificate.Subject
    }
    $versionInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($fullPath)

    $document = [ordered]@{
        schema = 1
        generatedBy = 'reverse/scripts/extract-security-surface.ps1'
        safety = [ordered]@{
            reflectionOnly = $true
            invokesTargetMethods = $false
            attachesToProcess = $false
            performsNetworkActivity = $false
        }
        executable = [ordered]@{
            fileName = [System.IO.Path]::GetFileName($fullPath)
            productVersion = $versionInfo.ProductVersion
            assemblyVersion = $assembly.GetName().Version.ToString()
            architecture = 'PE32 / x86 / CLR'
            sha256 = $fileHash
            moduleVersionId = $module.ModuleVersionId.ToString()
            authenticodeStatus = $signature.Status.ToString()
            signerSubject = $signerSubject
        }
        methods = @($methods)
        fields = @($fields)
        nativeImportSummary = $nativeImportSummary
        selectedNativeImports = $selectedNativeImports
        negativeNativeApiQueries = @($negativeNativeApiResults)
    }
    if ($IncludeAllNativeImports) {
        $document.nativeImports = $nativeImports
    }

    # ConvertTo-Json follows the host platform's line endings. Normalize the
    # public artifact so a Windows regeneration is byte-stable in Git/WSL.
    $json = ($document | ConvertTo-Json -Depth 8) -replace "`r`n", "`n"
    if ([string]::IsNullOrWhiteSpace($OutputPath)) {
        $json
    }
    else {
        $destination = [System.IO.Path]::GetFullPath($OutputPath)
        [System.IO.File]::WriteAllText($destination, $json + "`n")
        Write-Host "wrote $destination"
    }
}
finally {
    [System.AppDomain]::CurrentDomain.remove_ReflectionOnlyAssemblyResolve($resolver)
}
