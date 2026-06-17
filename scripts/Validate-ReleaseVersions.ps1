# Validate-ReleaseVersions.ps1
# Fails (exit 1) when any version-bearing surface drifts from the canonical version in
# Directory.Build.props. Run locally or in CI; pass -ReleaseVersion in the release workflow
# to additionally assert the tag matches the repo state being released.
[CmdletBinding()]
param(
    # Optional tag-derived version ("4.6.2", no leading v). When supplied, it must equal
    # the canonical VersionPrefix — prevents tagging a commit whose metadata lags the tag.
    [string]$ReleaseVersion,
    [string]$RepoRoot
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
} else {
    $repoRoot = (Resolve-Path $RepoRoot).Path
}

function Get-Canonical {
    $props = Join-Path $repoRoot 'Directory.Build.props'
    if (-not (Test-Path $props)) { throw "Directory.Build.props not found at repo root." }
    $xml = [xml](Get-Content -Raw $props)
    $v = $xml.Project.PropertyGroup.VersionPrefix | Where-Object { $_ } | Select-Object -First 1
    if (-not $v) { throw "VersionPrefix not found in Directory.Build.props." }
    return $v.Trim()
}

$canonical = Get-Canonical
$failures = New-Object System.Collections.Generic.List[string]

function Check {
    param([string]$Surface, [string]$Actual, [string]$Expected)
    if ($Actual -ne $Expected) {
        $failures.Add(("{0}: expected '{1}', found '{2}'" -f $Surface, $Expected, $Actual))
    }
}

# 1. PowerShell module manifest
$psd1Path = Join-Path $repoRoot 'packaging/powershell/NVMeDriverPatcher.psd1'
$psd1 = Get-Content -Raw $psd1Path
if ($psd1 -match "ModuleVersion\s*=\s*'([^']+)'") {
    Check "packaging/powershell/NVMeDriverPatcher.psd1 ModuleVersion" $Matches[1] $canonical
} else { $failures.Add("psd1: ModuleVersion line not found") }

# 2. winget manifest — PackageVersion and the tag segment of InstallerUrl
$wingetPath = Join-Path $repoRoot 'packaging/winget/SysAdminDoc.NVMeDriverPatcher.yaml'
$winget = Get-Content -Raw $wingetPath
if ($winget -match "PackageVersion:\s*(\S+)") {
    Check "winget PackageVersion" $Matches[1] $canonical
} else { $failures.Add("winget: PackageVersion line not found") }
if ($winget -match "InstallerUrl:\s*\S*/download/v([0-9][^/]*)/") {
    Check "winget InstallerUrl tag segment" $Matches[1] $canonical
} else { $failures.Add("winget: InstallerUrl tag segment not found") }

# 3. WiX MSI package version (4-part)
$wxsPath = Join-Path $repoRoot 'packaging/wix/NVMeDriverPatcher.wxs'
$wxs = Get-Content -Raw $wxsPath
if ($wxs -cmatch 'Version="([0-9]+\.[0-9]+\.[0-9]+\.[0-9]+)"') {
    Check "packaging/wix/NVMeDriverPatcher.wxs Version" $Matches[1] "$canonical.0"
} else { $failures.Add("wxs: Version attribute not found") }

# 4. Intune detection script minimum version — must not lag the release
$intunePath = Join-Path $repoRoot 'packaging/intune/Detect-NVMeDriverPatcher.ps1'
$intune = Get-Content -Raw $intunePath
if ($intune -match "\`$minVersion\s*=\s*\[Version\]'([^']+)'") {
    Check "packaging/intune/Detect-NVMeDriverPatcher.ps1 minVersion" $Matches[1] $canonical
} else { $failures.Add("intune: minVersion line not found") }

# 5. AppConfig last-resort fallback literal
$appConfigPath = Join-Path $repoRoot 'src/NVMeDriverPatcher.Core/Models/AppConfig.cs'
$appConfig = Get-Content -Raw $appConfigPath
if ($appConfig -match 'FallbackVersionLiteral\s*=\s*"([^"]+)"') {
    Check "AppConfig.cs FallbackVersionLiteral" $Matches[1] $canonical
} else { $failures.Add("AppConfig.cs: FallbackVersionLiteral not found") }

# 6. Chocolatey nuspec
$nuspecPath = Join-Path $repoRoot 'packaging/chocolatey/nvme-driver-patcher.nuspec'
if (Test-Path $nuspecPath) {
    $nuspec = [xml](Get-Content -Raw $nuspecPath)
    $nuspecVersion = $nuspec.package.metadata.version
    if ($nuspecVersion) { Check "packaging/chocolatey nuspec version" $nuspecVersion $canonical }
    else { $failures.Add("chocolatey nuspec: version element not found") }
}

# 7. Scoop manifest
$scoopPath = Join-Path $repoRoot 'packaging/scoop/nvme-driver-patcher.json'
if (Test-Path $scoopPath) {
    $scoop = Get-Content -Raw $scoopPath | ConvertFrom-Json
    if ($scoop.version) { Check "packaging/scoop manifest version" $scoop.version $canonical }
    else { $failures.Add("scoop manifest: version field not found") }
}

# 8. Optional: tag-derived release version must match repo state
if ($ReleaseVersion) {
    Check "release tag version" $ReleaseVersion.TrimStart('v') $canonical
}

# 9. Packaging markdown artifact examples must not lag the canonical package version.
# Use NVMeDriverPatcher-<version>.msi in docs that should survive routine bumps.
$canonicalMajor = ([Version]$canonical).Major
$artifactVersionPattern = 'NVMeDriverPatcher-(?<version><version>|[0-9]+\.[0-9]+\.[0-9]+|[0-9]+\.x\.y)(?=\.(?:exe|intunewin|msi|sha256|zip)\b)'
$packagingDocsRoot = Join-Path $repoRoot 'packaging'
if (Test-Path $packagingDocsRoot) {
    foreach ($doc in Get-ChildItem -LiteralPath $packagingDocsRoot -Filter '*.md' -Recurse -File) {
        $text = Get-Content -Raw $doc.FullName
        # Defensive relative path: Resolve-Path can return a provider-qualified root on UNC shares,
        # making it longer than $doc.FullName and crashing a naive Substring. Fall back to the leaf.
        $relativeDoc = if ($doc.FullName.StartsWith($repoRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
            $doc.FullName.Substring($repoRoot.Length).TrimStart([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
        } else {
            $doc.Name
        }

        foreach ($match in [regex]::Matches($text, $artifactVersionPattern)) {
            $versionToken = $match.Groups['version'].Value
            if ($versionToken -eq '<version>') {
                continue
            }

            if ($versionToken -match '^([0-9]+)\.x\.y$') {
                $placeholderMajor = [int]$Matches[1]
                if ($placeholderMajor -ne $canonicalMajor) {
                    $failures.Add(("{0}: artifact placeholder '{1}' should be '{2}.x.y' or '<version>'" -f $relativeDoc, $versionToken, $canonicalMajor))
                }
                continue
            }

            if ($versionToken -ne $canonical) {
                $failures.Add(("{0}: artifact example '{1}' should use '{2}' or '<version>'" -f $relativeDoc, $versionToken, $canonical))
            }
        }
    }
}

# 10. Narrative docs must not lag the canonical version. Each is guarded by Test-Path so the
# check is a no-op when the file is absent (e.g. minimal fixtures, or gitignored CLAUDE.md in CI).
function Check-Narrative {
    param([string]$RelPath, [string]$Pattern, [string]$Surface)
    $full = Join-Path $repoRoot $RelPath
    if (-not (Test-Path $full)) { return }
    $text = Get-Content -Raw $full
    if ($text -match $Pattern) {
        Check $Surface $Matches[1] $canonical
    }
}
# README version badge: ![Version](.../Version-5.0.0-blue)
Check-Narrative 'README.md' 'Version-([0-9]+\.[0-9]+\.[0-9]+)-' 'README.md version badge'
# ROADMAP intro: "Current ship: **v5.0.0**"
Check-Narrative 'ROADMAP.md' 'Current ship:\s*\*\*v?([0-9]+\.[0-9]+\.[0-9]+)\*\*' 'ROADMAP.md current-ship version'
# CLAUDE.md status line: "Version: v5.0.0" (gitignored locally; validated only when present)
Check-Narrative 'CLAUDE.md' 'Version:\s*v?([0-9]+\.[0-9]+\.[0-9]+)' 'CLAUDE.md status version'

if ($failures.Count -gt 0) {
    Write-Host "Version drift detected (canonical = $canonical):" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  FAIL $_" -ForegroundColor Red }
    exit 1
}

Write-Host "All version surfaces match canonical $canonical." -ForegroundColor Green
exit 0
