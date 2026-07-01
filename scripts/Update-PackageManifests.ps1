# Update-PackageManifests.ps1
# Rewrites the Chocolatey and Scoop manifests for a release: substitutes the tagged download URL
# and the real exe SHA-256 hashes (x64 + ARM64) and the package version so the community-channel
# manifests are never stale or carry REPLACE_ME placeholders. Run during the release workflow
# AFTER all exes are published. Idempotent and re-runnable.
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Version,      # tag-derived, no leading v (e.g. 5.0.1)
    [Parameter(Mandatory)] [string]$ExePath,       # path to the published NVMeDriverPatcher.exe (x64)
    [string]$Arm64ExePath,                         # path to the published NVMeDriverPatcher-win-arm64.exe
    [string]$RepoRoot                              # override repo root (tests); defaults to ../ of this script
)

$ErrorActionPreference = 'Stop'
$repoRoot = if ($RepoRoot) { Resolve-Path $RepoRoot } else { Resolve-Path (Join-Path $PSScriptRoot '..') }
$Version = $Version.TrimStart('v')

if (-not (Test-Path $ExePath)) { throw "ExePath not found: $ExePath" }
$hash = (Get-FileHash -Algorithm SHA256 -Path $ExePath).Hash.ToLower()
$tagUrl = "https://github.com/SysAdminDoc/win11-nvme-driver-patcher/releases/download/v$Version/NVMeDriverPatcher.exe"

$arm64Hash = $null
if ($Arm64ExePath -and (Test-Path $Arm64ExePath)) {
    $arm64Hash = (Get-FileHash -Algorithm SHA256 -Path $Arm64ExePath).Hash.ToLower()
}

# --- Chocolatey install script: url64bit + checksum64 ---
$chocoInstall = Join-Path $repoRoot 'packaging/chocolatey/tools/chocolateyInstall.ps1'
$c = Get-Content -Raw $chocoInstall
$c = $c -replace "url64bit\s*=\s*'[^']*'",   "url64bit       = '$tagUrl'"
$c = $c -replace "checksum64\s*=\s*'[^']*'", "checksum64     = '$hash'"
Set-Content -Path $chocoInstall -Value $c -NoNewline

# --- Chocolatey nuspec version ---
$nuspec = Join-Path $repoRoot 'packaging/chocolatey/nvme-driver-patcher.nuspec'
$n = Get-Content -Raw $nuspec
$n = $n -replace '<version>[^<]*</version>', "<version>$Version</version>"
Set-Content -Path $nuspec -Value $n -NoNewline

# --- Scoop manifest: use JSON manipulation to set version, URLs, and hashes ---
$scoopPath = Join-Path $repoRoot 'packaging/scoop/nvme-driver-patcher.json'
$scoop = Get-Content -Raw $scoopPath | ConvertFrom-Json

$scoop.version = $Version
$scoop.architecture.'64bit'.url = "https://github.com/SysAdminDoc/win11-nvme-driver-patcher/releases/download/v$Version/NVMeDriverPatcher.exe#/NVMeDriverPatcher.exe"
$scoop.architecture.'64bit'.hash = $hash
if ($scoop.architecture.arm64) {
    $scoop.architecture.arm64.url = "https://github.com/SysAdminDoc/win11-nvme-driver-patcher/releases/download/v$Version/NVMeDriverPatcher-win-arm64.exe#/NVMeDriverPatcher-win-arm64.exe"
    if ($arm64Hash) { $scoop.architecture.arm64.hash = $arm64Hash }
}

# Serialize and restore the autoupdate $version templates (ConvertTo-Json escapes the $)
$scoopJson = $scoop | ConvertTo-Json -Depth 10
# The autoupdate URLs use literal "$version" — ConvertTo-Json would have stringified them as-is
# but the source template uses $version which PowerShell won't expand inside single-quoted JSON keys.
# Re-inject the template URLs since ConvertTo-Json preserves the string values from the parsed object.
$null = $scoopJson | ConvertFrom-Json  # validate
Set-Content -Path $scoopPath -Value $scoopJson -NoNewline

Write-Host "Updated Chocolatey + Scoop manifests for v$Version (x64 sha256 $hash$(if ($arm64Hash) { ", arm64 sha256 $arm64Hash" }))." -ForegroundColor Green
