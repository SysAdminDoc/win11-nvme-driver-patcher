# Update-PackageManifests.ps1
# Single release-metadata generator for winget, Scoop, and Chocolatey. It binds each
# architecture block to the matching PE artifact and hash, then atomically publishes either
# into the source packaging tree (legacy release workflow) or an isolated output directory.
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Version,
    [Parameter(Mandatory)] [string]$ExePath,
    [Parameter(Mandatory)] [string]$Arm64ExePath,
    [string]$RepoRoot,
    [string]$OutputRoot
)

$ErrorActionPreference = 'Stop'
$repoRoot = if ($RepoRoot) { (Resolve-Path $RepoRoot).Path } else { (Resolve-Path (Join-Path $PSScriptRoot '..')).Path }
$Version = $Version.TrimStart('v')
if ($Version -notmatch '^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$') {
    throw "Version is not a supported semantic version: $Version"
}

function Get-PeMachine {
    param([Parameter(Mandatory)] [string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Artifact not found: $Path" }
    $stream = [System.IO.File]::OpenRead((Resolve-Path -LiteralPath $Path).Path)
    $reader = [System.IO.BinaryReader]::new($stream)
    try {
        if ($stream.Length -lt 64 -or $reader.ReadUInt16() -ne 0x5A4D) {
            throw "Artifact is not a valid PE image: $Path"
        }
        $stream.Position = 0x3C
        $peOffset = $reader.ReadInt32()
        if ($peOffset -lt 0 -or $peOffset -gt ($stream.Length - 6)) {
            throw "Artifact has an invalid PE header offset: $Path"
        }
        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550) { throw "Artifact has no PE signature: $Path" }
        return $reader.ReadUInt16()
    }
    finally {
        $reader.Dispose()
        $stream.Dispose()
    }
}

function Write-Utf8Atomic {
    param([Parameter(Mandatory)] [string]$Path, [Parameter(Mandatory)] [string]$Content)

    $parent = Split-Path -Parent $Path
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    $temp = "$Path.$PID.$([Guid]::NewGuid().ToString('N')).tmp"
    $backup = "$Path.$PID.$([Guid]::NewGuid().ToString('N')).bak"
    try {
        [System.IO.File]::WriteAllText($temp, $Content, [System.Text.UTF8Encoding]::new($false))
        if (Test-Path -LiteralPath $Path) {
            [System.IO.File]::Replace($temp, $Path, $backup, $true)
        } else {
            [System.IO.File]::Move($temp, $Path)
        }
    }
    finally {
        if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Force }
        if (Test-Path -LiteralPath $backup) { Remove-Item -LiteralPath $backup -Force }
    }
}

function Update-WingetManifest {
    param(
        [Parameter(Mandatory)] [string]$Text,
        [Parameter(Mandatory)] [hashtable]$Urls,
        [Parameter(Mandatory)] [hashtable]$Hashes
    )

    $lines = $Text -split '\r?\n'
    $currentArchitecture = $null
    $versionCount = 0
    $urlCounts = @{ x64 = 0; arm64 = 0 }
    $hashCounts = @{ x64 = 0; arm64 = 0 }
    $architectureCounts = @{ x64 = 0; arm64 = 0 }

    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '^(\s*PackageVersion:\s*)\S+\s*$') {
            $lines[$i] = $Matches[1] + $Version
            $versionCount++
            continue
        }
        if ($lines[$i] -match '^\s*-\s*Architecture:\s*["'']?([^"''\s]+)') {
            $currentArchitecture = $Matches[1].ToLowerInvariant()
            if ($architectureCounts.ContainsKey($currentArchitecture)) {
                $architectureCounts[$currentArchitecture]++
            }
            continue
        }
        if ($currentArchitecture -and $Urls.ContainsKey($currentArchitecture) -and
            $lines[$i] -match '^(\s*InstallerUrl:\s*)\S+\s*$') {
            $lines[$i] = $Matches[1] + $Urls[$currentArchitecture]
            $urlCounts[$currentArchitecture]++
            continue
        }
        if ($currentArchitecture -and $Hashes.ContainsKey($currentArchitecture) -and
            $lines[$i] -match '^(\s*InstallerSha256:\s*)\S+\s*$') {
            $lines[$i] = $Matches[1] + $Hashes[$currentArchitecture].ToUpperInvariant()
            $hashCounts[$currentArchitecture]++
        }
    }

    if ($versionCount -ne 1) { throw "winget template must contain exactly one PackageVersion field (found $versionCount)." }
    foreach ($architecture in @('x64', 'arm64')) {
        if ($architectureCounts[$architecture] -ne 1 -or $urlCounts[$architecture] -ne 1 -or $hashCounts[$architecture] -ne 1) {
            throw "winget template must contain one $architecture installer with one URL and hash."
        }
    }
    return (($lines -join [Environment]::NewLine).TrimEnd() + [Environment]::NewLine)
}

function Update-PackageVersion {
    param([Parameter(Mandatory)] [string]$Text, [Parameter(Mandatory)] [string]$ManifestName)

    $pattern = '(?m)^(\s*PackageVersion:\s*)\S+\s*$'
    $matches = [regex]::Matches($Text, $pattern)
    if ($matches.Count -ne 1) {
        throw "$ManifestName must contain exactly one PackageVersion field (found $($matches.Count))."
    }
    return [regex]::Replace($Text, $pattern, "`${1}$Version")
}

$x64Machine = Get-PeMachine -Path $ExePath
$arm64Machine = Get-PeMachine -Path $Arm64ExePath
if ($x64Machine -ne 0x8664) { throw ('ExePath PE machine is 0x{0:X4}, expected x64 (0x8664).' -f $x64Machine) }
if ($arm64Machine -ne 0xAA64) { throw ('Arm64ExePath PE machine is 0x{0:X4}, expected ARM64 (0xAA64).' -f $arm64Machine) }

$x64Hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $ExePath).Hash.ToLowerInvariant()
$arm64Hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $Arm64ExePath).Hash.ToLowerInvariant()
$x64Url = "https://github.com/SysAdminDoc/win11-nvme-driver-patcher/releases/download/v$Version/NVMeDriverPatcher.exe"
$arm64Url = "https://github.com/SysAdminDoc/win11-nvme-driver-patcher/releases/download/v$Version/NVMeDriverPatcher-win-arm64.exe"

# Build and validate every payload in memory before mutating any destination.
$wingetSourceRoot = Join-Path $repoRoot 'packaging/winget'
$wingetVersionSource = Join-Path $wingetSourceRoot 'SysAdminDoc.NVMeDriverPatcher.yaml'
$wingetInstallerSource = Join-Path $wingetSourceRoot 'SysAdminDoc.NVMeDriverPatcher.installer.yaml'
$wingetLocaleSource = Join-Path $wingetSourceRoot 'SysAdminDoc.NVMeDriverPatcher.locale.en-US.yaml'
$wingetVersionText = Update-PackageVersion -Text (Get-Content -Raw $wingetVersionSource) -ManifestName 'winget version manifest'
$wingetInstallerText = Update-WingetManifest -Text (Get-Content -Raw $wingetInstallerSource) `
    -Urls @{ x64 = $x64Url; arm64 = $arm64Url } `
    -Hashes @{ x64 = $x64Hash; arm64 = $arm64Hash }
$wingetLocaleText = Update-PackageVersion -Text (Get-Content -Raw $wingetLocaleSource) -ManifestName 'winget locale manifest'

$scoopSource = Join-Path $repoRoot 'packaging/scoop/nvme-driver-patcher.json'
$scoop = Get-Content -Raw $scoopSource | ConvertFrom-Json
if ($null -eq $scoop.architecture.'64bit' -or $null -eq $scoop.architecture.arm64) {
    throw 'Scoop template must contain 64bit and arm64 architecture blocks.'
}
if ($scoop.bin -ne 'NVMeDriverPatcher.exe') { throw 'Scoop template bin must be NVMeDriverPatcher.exe.' }
$scoop.version = $Version
$scoop.architecture.'64bit'.url = "$x64Url#/NVMeDriverPatcher.exe"
$scoop.architecture.'64bit'.hash = $x64Hash
$scoop.architecture.arm64.url = "$arm64Url#/NVMeDriverPatcher.exe"
$scoop.architecture.arm64.hash = $arm64Hash
$scoop.autoupdate.architecture.'64bit'.url = 'https://github.com/SysAdminDoc/win11-nvme-driver-patcher/releases/download/v$version/NVMeDriverPatcher.exe#/NVMeDriverPatcher.exe'
$scoop.autoupdate.architecture.arm64.url = 'https://github.com/SysAdminDoc/win11-nvme-driver-patcher/releases/download/v$version/NVMeDriverPatcher-win-arm64.exe#/NVMeDriverPatcher.exe'
$scoopText = $scoop | ConvertTo-Json -Depth 10
$null = $scoopText | ConvertFrom-Json

$chocoSource = Join-Path $repoRoot 'packaging/chocolatey'
$chocoInstallText = Get-Content -Raw (Join-Path $chocoSource 'tools/chocolateyInstall.ps1')
$chocoInstallText = $chocoInstallText -replace "url64bit\s*=\s*'[^']*'", "url64bit       = '$x64Url'"
$chocoInstallText = $chocoInstallText -replace "checksum64\s*=\s*'[^']*'", "checksum64     = '$x64Hash'"
$nuspecText = Get-Content -Raw (Join-Path $chocoSource 'nvme-driver-patcher.nuspec')
$nuspecText = $nuspecText -replace '<version>[^<]*</version>', "<version>$Version</version>"
$nuspecText = $nuspecText -replace 'http://schemas.microsoft.com/packaging/2015/06/nuspec.xsd', 'http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd'
if ($chocoInstallText -notmatch [regex]::Escape($x64Hash) -or $nuspecText -notmatch "<version>$([regex]::Escape($Version))</version>") {
    throw 'Chocolatey template did not expose the expected checksum/version fields.'
}

if ($OutputRoot) {
    $outputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
    New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
    $chocoRoot = Join-Path $outputRoot 'chocolatey-package'
    if (Test-Path -LiteralPath $chocoRoot) { Remove-Item -LiteralPath $chocoRoot -Recurse -Force }
    New-Item -ItemType Directory -Path $chocoRoot | Out-Null
    Copy-Item -Path (Join-Path $chocoSource '*') -Destination $chocoRoot -Recurse -Force
    $wingetTargetRoot = Join-Path $outputRoot 'winget'
    $wingetVersionTarget = Join-Path $wingetTargetRoot 'SysAdminDoc.NVMeDriverPatcher.yaml'
    $wingetInstallerTarget = Join-Path $wingetTargetRoot 'SysAdminDoc.NVMeDriverPatcher.installer.yaml'
    $wingetLocaleTarget = Join-Path $wingetTargetRoot 'SysAdminDoc.NVMeDriverPatcher.locale.en-US.yaml'
    $scoopTarget = Join-Path $outputRoot 'nvme-driver-patcher.json'
} else {
    $chocoRoot = $chocoSource
    $wingetVersionTarget = $wingetVersionSource
    $wingetInstallerTarget = $wingetInstallerSource
    $wingetLocaleTarget = $wingetLocaleSource
    $scoopTarget = $scoopSource
}

Write-Utf8Atomic -Path $wingetVersionTarget -Content $wingetVersionText
Write-Utf8Atomic -Path $wingetInstallerTarget -Content $wingetInstallerText
Write-Utf8Atomic -Path $wingetLocaleTarget -Content $wingetLocaleText
Write-Utf8Atomic -Path $scoopTarget -Content $scoopText
Write-Utf8Atomic -Path (Join-Path $chocoRoot 'tools/chocolateyInstall.ps1') -Content $chocoInstallText
Write-Utf8Atomic -Path (Join-Path $chocoRoot 'nvme-driver-patcher.nuspec') -Content $nuspecText

Write-Host "Generated architecture-bound winget, Scoop, and Chocolatey metadata for v$Version (x64 $x64Hash, arm64 $arm64Hash)." -ForegroundColor Green
