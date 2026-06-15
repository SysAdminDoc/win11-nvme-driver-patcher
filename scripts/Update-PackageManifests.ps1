# Update-PackageManifests.ps1
# Rewrites the Chocolatey and Scoop manifests for a release: substitutes the tagged download URL
# and the real GUI exe SHA-256 (and the package version) so the community-channel manifests are
# never stale or carry the REPLACE_ME_WITH_RELEASE_SHA256 placeholder. Run during the release
# workflow AFTER the GUI exe is published. Idempotent and re-runnable.
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Version,   # tag-derived, no leading v (e.g. 5.0.1)
    [Parameter(Mandatory)] [string]$ExePath,    # path to the published NVMeDriverPatcher.exe (for the hash)
    [string]$RepoRoot                           # override repo root (tests); defaults to ../ of this script
)

$ErrorActionPreference = 'Stop'
$repoRoot = if ($RepoRoot) { Resolve-Path $RepoRoot } else { Resolve-Path (Join-Path $PSScriptRoot '..') }
$Version = $Version.TrimStart('v')

if (-not (Test-Path $ExePath)) { throw "ExePath not found: $ExePath" }
$hash = (Get-FileHash -Algorithm SHA256 -Path $ExePath).Hash.ToLower()
$tagUrl = "https://github.com/SysAdminDoc/win11-nvme-driver-patcher/releases/download/v$Version/NVMeDriverPatcher.exe"

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

# --- Scoop manifest: version + concrete-version url + hash (leave the autoupdate $version template alone) ---
$scoopPath = Join-Path $repoRoot 'packaging/scoop/nvme-driver-patcher.json'
$s = Get-Content -Raw $scoopPath
$s = $s -replace '"version":\s*"[^"]*"', "`"version`": `"$Version`""
$s = $s -replace '"hash":\s*"[^"]*"',    "`"hash`": `"$hash`""
# Only the concrete numeric-version URL — the autoupdate block keeps its literal v$version token.
$s = $s -replace 'download/v[0-9][^/]*/NVMeDriverPatcher\.exe', "download/v$Version/NVMeDriverPatcher.exe"
# Fail loudly if the rewrite produced invalid JSON rather than shipping a broken manifest.
$null = $s | ConvertFrom-Json
Set-Content -Path $scoopPath -Value $s -NoNewline

Write-Host "Updated Chocolatey + Scoop manifests for v$Version (sha256 $hash)." -ForegroundColor Green
