# New-ArtifactManifest.ps1
# Emits the same ARTIFACT-MANIFEST.json v1 contract as GeneratedArtifactManifestService for
# build-time payloads that exist before the application is installed.
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$PayloadRoot,
    [Parameter(Mandatory)] [string]$PayloadType,
    [Parameter(Mandatory)] [string]$ToolVersion
)

$ErrorActionPreference = 'Stop'
$manifestName = 'ARTIFACT-MANIFEST.json'
$root = (Resolve-Path -LiteralPath $PayloadRoot).Path
if (-not (Test-Path -LiteralPath $root -PathType Container)) {
    throw "PayloadRoot must be an existing directory: $PayloadRoot"
}
if ([string]::IsNullOrWhiteSpace($PayloadType) -or $PayloadType.Length -gt 100) {
    throw 'PayloadType must be between 1 and 100 characters.'
}
if ([string]::IsNullOrWhiteSpace($ToolVersion)) { throw 'ToolVersion is required.' }

function Get-Role {
    param([Parameter(Mandatory)] [string]$RelativePath)
    $name = [IO.Path]::GetFileName($RelativePath)
    $extension = [IO.Path]::GetExtension($name)
    if ($extension -ieq '.msi') { return 'installer' }
    if ($name -like 'Detect-*.ps1') { return 'detection-script' }
    if ($extension -ieq '.ps1') { return 'deployment-script' }
    if ($extension -ieq '.cmd' -or $extension -ieq '.bat') { return 'deployment-script' }
    return 'deployment-payload'
}

function Get-Sha256Hex {
    param([Parameter(Mandatory)] [string]$Path)
    $stream = [IO.File]::OpenRead($Path)
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        return -join ($sha.ComputeHash($stream) | ForEach-Object { $_.ToString('x2') })
    }
    finally {
        $sha.Dispose()
        $stream.Dispose()
    }
}

$prefix = $root.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
$records = New-Object System.Collections.Generic.List[object]
$files = Get-ChildItem -LiteralPath $root -Recurse -File |
    Where-Object { $_.Name -ine $manifestName } |
    Sort-Object FullName

foreach ($file in $files) {
    if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Refusing to manifest reparse-point file: $($file.FullName)"
    }
    if (-not $file.FullName.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Payload file escaped root: $($file.FullName)"
    }
    $relative = $file.FullName.Substring($prefix.Length).Replace('\', '/')
    if ([string]::IsNullOrWhiteSpace($relative) -or $relative.Split('/') -contains '..') {
        throw "Unsafe payload path: $relative"
    }
    $records.Add([ordered]@{
        relativePath = $relative
        role         = Get-Role $relative
        byteLength   = [long]$file.Length
        sha256       = Get-Sha256Hex $file.FullName
        required     = $true
    })
}

$duplicates = $records | Group-Object { $_.relativePath.ToLowerInvariant() } | Where-Object Count -gt 1
if ($duplicates) { throw "Payload contains duplicate case-insensitive paths: $($duplicates.Name -join ', ')" }

$manifest = [ordered]@{
    schemaVersion  = 1
    toolVersion    = $ToolVersion
    payloadType    = $PayloadType
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    files          = $records.ToArray()
}

$finalPath = Join-Path $root $manifestName
$tempPath = Join-Path $root ("{0}.{1}.tmp" -f $manifestName, [Guid]::NewGuid().ToString('N'))
$backupPath = $finalPath + '.replace-backup'
$json = ($manifest | ConvertTo-Json -Depth 6) + [Environment]::NewLine
$bytes = [Text.UTF8Encoding]::new($false).GetBytes($json)

try {
    $stream = [IO.FileStream]::new(
        $tempPath,
        [IO.FileMode]::CreateNew,
        [IO.FileAccess]::Write,
        [IO.FileShare]::None,
        4096,
        [IO.FileOptions]::WriteThrough)
    try {
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Flush($true)
    }
    finally { $stream.Dispose() }

    $parsed = Get-Content -LiteralPath $tempPath -Raw | ConvertFrom-Json
    if ($parsed.schemaVersion -ne 1 -or $parsed.payloadType -ne $PayloadType -or
        @($parsed.files).Count -ne $records.Count) {
        throw 'Generated artifact manifest failed its publication validation.'
    }

    if (Test-Path -LiteralPath $finalPath) {
        if (Test-Path -LiteralPath $backupPath) { Remove-Item -LiteralPath $backupPath -Force }
        [IO.File]::Replace($tempPath, $finalPath, $backupPath)
        if (Test-Path -LiteralPath $backupPath) { Remove-Item -LiteralPath $backupPath -Force }
    }
    else {
        [IO.File]::Move($tempPath, $finalPath)
    }
}
finally {
    if (Test-Path -LiteralPath $tempPath) { Remove-Item -LiteralPath $tempPath -Force }
    if (Test-Path -LiteralPath $backupPath) { Remove-Item -LiteralPath $backupPath -Force }
}

Write-Output $finalPath
