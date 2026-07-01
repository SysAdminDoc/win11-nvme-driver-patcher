# Validate-ReleaseAssets.ps1
# Pre-release artifact gate. Run AFTER all build/publish/checksum steps and BEFORE the
# GitHub release is created. Reads packaging/release-artifacts.json (the artifact contract),
# substitutes {version}, and fails (exit 1) when:
#   - any required artifact is missing
#   - any checksummed artifact lacks its per-asset .sha256 sidecar in publish/
#   - SHA256SUMS.txt omits a checksummed artifact or carries a stale hash
#   - the generated winget manifest's InstallerUrl tag/version or InstallerSha256 disagrees
#     with the actual GUI exe and release version
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Version,   # tag-derived, no leading v (e.g. 4.6.2)
    [string]$RepoRoot,                          # override repo root (tests); defaults to ../ of this script
    [switch]$ExpectSigned                       # when set, every sign:true artifact must carry a Valid Authenticode signature
)

$ErrorActionPreference = 'Stop'
$repoRoot = if ($RepoRoot) { Resolve-Path $RepoRoot } else { Resolve-Path (Join-Path $PSScriptRoot '..') }
$Version = $Version.TrimStart('v')

$contract = Get-Content -Raw (Join-Path $repoRoot 'packaging/release-artifacts.json') | ConvertFrom-Json
$failures = New-Object System.Collections.Generic.List[string]

$sumsPath = Join-Path $repoRoot 'publish/SHA256SUMS.txt'
$sums = @{}
if (Test-Path $sumsPath) {
    foreach ($line in Get-Content $sumsPath) {
        if ($line -match '^([0-9a-f]{64})\s+(.+)$') { $sums[$Matches[2].Trim()] = $Matches[1] }
    }
}

function Get-Sha256Hex {
    param([Parameter(Mandatory)] [string]$Path)

    $stream = [System.IO.File]::OpenRead($Path)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = $sha.ComputeHash($stream)
        return -join ($bytes | ForEach-Object { $_.ToString('x2') })
    }
    finally {
        $sha.Dispose()
        $stream.Dispose()
    }
}

foreach ($a in $contract.artifacts) {
    $rel = $a.path -replace '\{version\}', $Version
    $full = Join-Path $repoRoot $rel

    if (-not (Test-Path $full)) {
        if ($a.required) { $failures.Add("required artifact missing: $rel (id=$($a.id))") }
        else { Write-Host "  optional artifact absent (ok): $rel" }
        continue
    }

    $leaf = Split-Path $rel -Leaf
    $actualHash = Get-Sha256Hex $full

    if ($a.checksum) {
        # Per-asset sidecar — the in-app auto-updater requires it.
        $sidecar = Join-Path $repoRoot "publish/$leaf.sha256"
        if (-not (Test-Path $sidecar)) {
            $failures.Add("missing .sha256 sidecar for $leaf (id=$($a.id))")
        } elseif ((Get-Content -Raw $sidecar) -notmatch [regex]::Escape($actualHash)) {
            $failures.Add("stale sidecar hash for $leaf")
        }
        # Combined manifest coverage.
        if (-not $sums.ContainsKey($leaf)) {
            $failures.Add("SHA256SUMS.txt missing entry for $leaf")
        } elseif ($sums[$leaf] -ne $actualHash) {
            $failures.Add("SHA256SUMS.txt stale hash for $leaf")
        }
    }

    # Authenticode gate: when the release was built with signing secrets (-ExpectSigned), every
    # artifact the contract marks sign:true must actually carry a Valid signature. This catches a
    # silently-skipped signing step (the env/if gate bug) instead of shipping unsigned binaries
    # that weaken SmartScreen and break the auto-updater's signature fallback.
    if ($a.sign -and $ExpectSigned) {
        $sig = Get-AuthenticodeSignature -FilePath $full
        if ($sig.Status -ne 'Valid') {
            $failures.Add("expected Authenticode signature missing/invalid (status=$($sig.Status)): $leaf (id=$($a.id))")
        }
    }

    if ($a.id -eq 'winget-manifest') {
        $yaml = Get-Content -Raw $full
        if ($yaml -notmatch "PackageVersion:\s*$([regex]::Escape($Version))\b") {
            $failures.Add("winget manifest PackageVersion is not $Version")
        }
        if ($yaml -notmatch "InstallerUrl:\s*\S*/download/v$([regex]::Escape($Version))/NVMeDriverPatcher\.exe") {
            $failures.Add("winget manifest InstallerUrl (x64) does not point at tag v$Version")
        }
        if ($yaml -notmatch "Architecture:\s*arm64") {
            $failures.Add("winget manifest missing arm64 Architecture entry")
        }
        if ($yaml -notmatch "InstallerUrl:\s*\S*/download/v$([regex]::Escape($Version))/NVMeDriverPatcher-win-arm64\.exe") {
            $failures.Add("winget manifest InstallerUrl (arm64) does not point at tag v$Version")
        }
        $guiPath = Join-Path $repoRoot 'publish/gui/NVMeDriverPatcher.exe'
        if (Test-Path $guiPath) {
            $guiHash = (Get-Sha256Hex $guiPath).ToUpper()
            if ($yaml -notmatch [regex]::Escape($guiHash)) {
                $failures.Add("winget manifest InstallerSha256 (x64) does not match publish/gui/NVMeDriverPatcher.exe")
            }
        }
        $arm64Path = Join-Path $repoRoot 'publish/NVMeDriverPatcher-win-arm64.exe'
        if (Test-Path $arm64Path) {
            $arm64Hash = (Get-Sha256Hex $arm64Path).ToUpper()
            if ($yaml -notmatch [regex]::Escape($arm64Hash)) {
                $failures.Add("winget manifest InstallerSha256 (arm64) does not match publish/NVMeDriverPatcher-win-arm64.exe")
            }
        }
    }

    if ($a.id -eq 'scoop-manifest') {
        $guiPath = Join-Path $repoRoot 'publish/gui/NVMeDriverPatcher.exe'
        $arm64Path = Join-Path $repoRoot 'publish/NVMeDriverPatcher-win-arm64.exe'
        try { $scoop = Get-Content -Raw $full | ConvertFrom-Json } catch { $scoop = $null }
        if ($null -eq $scoop) {
            $failures.Add("scoop manifest is not valid JSON")
        } else {
            if ($scoop.version -ne $Version) { $failures.Add("scoop manifest version is '$($scoop.version)', expected $Version") }
            $arch = $scoop.architecture.'64bit'
            if ($arch.url -notmatch "/releases/download/v$([regex]::Escape($Version))/NVMeDriverPatcher\.exe") {
                $failures.Add("scoop manifest 64bit url does not point at tag v$Version")
            }
            if ($arch.hash -match 'REPLACE_ME') { $failures.Add("scoop manifest 64bit still has a REPLACE_ME hash placeholder") }
            if (Test-Path $guiPath) {
                $guiHashLower = Get-Sha256Hex $guiPath
                if ($arch.hash -ne $guiHashLower) {
                    $failures.Add("scoop manifest 64bit hash does not match publish/gui/NVMeDriverPatcher.exe")
                }
            }
            $arm64Arch = $scoop.architecture.arm64
            if ($null -eq $arm64Arch) {
                $failures.Add("scoop manifest missing arm64 architecture block")
            } else {
                if ($arm64Arch.url -notmatch "/releases/download/v$([regex]::Escape($Version))/NVMeDriverPatcher-win-arm64\.exe") {
                    $failures.Add("scoop manifest arm64 url does not point at tag v$Version")
                }
                if ($arm64Arch.hash -match 'REPLACE_ME') { $failures.Add("scoop manifest arm64 still has a REPLACE_ME hash placeholder") }
                if (Test-Path $arm64Path) {
                    $arm64HashLower = Get-Sha256Hex $arm64Path
                    if ($arm64Arch.hash -ne $arm64HashLower) {
                        $failures.Add("scoop manifest arm64 hash does not match publish/NVMeDriverPatcher-win-arm64.exe")
                    }
                }
            }
            if ($null -eq $scoop.autoupdate.architecture.arm64) {
                $failures.Add("scoop manifest missing arm64 autoupdate block")
            }
        }
    }
}

if ($failures.Count -gt 0) {
    Write-Host "Release artifact contract violations (version $Version):" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  FAIL $_" -ForegroundColor Red }
    exit 1
}

Write-Host "All release artifacts satisfy the contract for $Version." -ForegroundColor Green
exit 0
