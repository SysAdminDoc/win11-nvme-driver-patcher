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

& (Join-Path $PSScriptRoot 'Validate-LegacyPowerShellBoundary.ps1') `
    -ScriptPath (Join-Path $repoRoot 'NVMe_Driver_Patcher.ps1')

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

function Get-StreamSha256Hex {
    param([Parameter(Mandatory)] [System.IO.Stream]$Stream)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        return -join ($sha.ComputeHash($Stream) | ForEach-Object { $_.ToString('x2') })
    }
    finally { $sha.Dispose() }
}

function Get-PeMachine {
    param([Parameter(Mandatory)] [string]$Path)

    $stream = [System.IO.File]::OpenRead($Path)
    $reader = [System.IO.BinaryReader]::new($stream)
    try {
        if ($stream.Length -lt 64 -or $reader.ReadUInt16() -ne 0x5A4D) { throw 'missing MZ header' }
        $stream.Position = 0x3C
        $peOffset = $reader.ReadInt32()
        if ($peOffset -lt 0 -or $peOffset -gt ($stream.Length - 6)) { throw 'invalid PE header offset' }
        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550) { throw 'missing PE signature' }
        return $reader.ReadUInt16()
    }
    finally {
        $reader.Dispose()
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

    if ($a.runtime -in @('win-x64', 'win-arm64')) {
        $expectedMachine = if ($a.runtime -eq 'win-x64') { 0x8664 } else { 0xAA64 }
        try {
            $actualMachine = Get-PeMachine $full
            if ($actualMachine -ne $expectedMachine) {
                $failures.Add(('PE architecture mismatch for {0}: runtime {1} expects 0x{2:X4}, found 0x{3:X4}' -f
                    $leaf, $a.runtime, $expectedMachine, $actualMachine))
            }
        }
        catch {
            $failures.Add("PE architecture unreadable for $leaf (runtime $($a.runtime)): $($_.Exception.Message)")
        }
    }

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

    if ($a.id -like 'winget-*-manifest') {
        $yaml = Get-Content -Raw $full
        if ($yaml -notmatch "PackageVersion:\s*$([regex]::Escape($Version))\b") {
            $failures.Add("$($a.id) PackageVersion is not $Version")
        }
    }

    if ($a.id -eq 'intune-source') {
        try {
            Add-Type -AssemblyName System.IO.Compression.FileSystem
            $zip = [System.IO.Compression.ZipFile]::OpenRead($full)
            try {
                $manifestEntries = @($zip.Entries | Where-Object { $_.FullName -ieq 'ARTIFACT-MANIFEST.json' })
                if ($manifestEntries.Count -ne 1) {
                    $failures.Add("Intune source ZIP must contain exactly one ARTIFACT-MANIFEST.json; found $($manifestEntries.Count)")
                }
                else {
                    $reader = [IO.StreamReader]::new($manifestEntries[0].Open(), [Text.Encoding]::UTF8, $true)
                    try { $payloadManifest = $reader.ReadToEnd() | ConvertFrom-Json }
                    finally { $reader.Dispose() }

                    if ($payloadManifest.schemaVersion -ne 1) { $failures.Add('Intune source manifest schemaVersion must be 1') }
                    if ($payloadManifest.toolVersion -ne $Version) { $failures.Add("Intune source manifest toolVersion is '$($payloadManifest.toolVersion)', expected $Version") }
                    if ($payloadManifest.payloadType -ne 'intune-source') { $failures.Add("Intune source manifest payloadType must be intune-source") }

                    $records = @{}
                    foreach ($record in @($payloadManifest.files)) {
                        $key = ([string]$record.relativePath).ToLowerInvariant()
                        if (-not $key -or $key.Contains('..') -or $key.Contains('\')) {
                            $failures.Add("Intune source manifest contains unsafe path '$($record.relativePath)'")
                        }
                        elseif ($records.ContainsKey($key)) {
                            $failures.Add("Intune source manifest contains duplicate path '$($record.relativePath)'")
                        }
                        else { $records[$key] = $record }
                    }

                    $actual = @{}
                    foreach ($entry in $zip.Entries | Where-Object { $_.Name -and $_.FullName -ine 'ARTIFACT-MANIFEST.json' }) {
                        $key = $entry.FullName.ToLowerInvariant()
                        if ($actual.ContainsKey($key)) {
                            $failures.Add("Intune source ZIP contains duplicate path '$($entry.FullName)'")
                            continue
                        }
                        $actual[$key] = $entry
                    }
                    foreach ($key in $records.Keys) {
                        if (-not $actual.ContainsKey($key)) {
                            $failures.Add("Intune source ZIP is missing required manifest file '$($records[$key].relativePath)'")
                            continue
                        }
                        $entry = $actual[$key]
                        $record = $records[$key]
                        if ([long]$record.byteLength -ne [long]$entry.Length) {
                            $failures.Add("Intune source length mismatch for '$($entry.FullName)'")
                            continue
                        }
                        $stream = $entry.Open()
                        try { $entryHash = Get-StreamSha256Hex $stream }
                        finally { $stream.Dispose() }
                        if ($entryHash -ne [string]$record.sha256) {
                            $failures.Add("Intune source SHA-256 mismatch for '$($entry.FullName)'")
                        }
                        $actual.Remove($key)
                    }
                    foreach ($key in $actual.Keys) {
                        $failures.Add("Intune source ZIP contains unexpected file '$($actual[$key].FullName)'")
                    }

                    $msiName = "NVMeDriverPatcher-$Version.msi"
                    $msiRecord = $records[$msiName.ToLowerInvariant()]
                    if ($null -eq $msiRecord -or $msiRecord.role -ne 'installer') {
                        $failures.Add("Intune source manifest must declare $msiName with installer role")
                    }
                    $detectRecord = $records['detect-nvmedriverpatcher.ps1']
                    if ($null -eq $detectRecord -or $detectRecord.role -ne 'detection-script') {
                        $failures.Add('Intune source manifest must declare Detect-NVMeDriverPatcher.ps1 with detection-script role')
                    }
                }
            }
            finally { $zip.Dispose() }
        }
        catch {
            $failures.Add("Intune source ZIP integrity validation failed: $($_.Exception.Message)")
        }
    }

    if ($a.id -eq 'winget-installer-manifest') {
        $yaml = Get-Content -Raw $full
        $installers = @{}
        $currentArchitecture = $null
        foreach ($line in $yaml -split '\r?\n') {
            if ($line -match '^\s*-\s*Architecture:\s*["'']?([^"''\s]+)') {
                $currentArchitecture = $Matches[1].ToLowerInvariant()
                if ($installers.ContainsKey($currentArchitecture)) {
                    $failures.Add("winget manifest contains duplicate $currentArchitecture installer blocks")
                    $currentArchitecture = $null
                } else {
                    $installers[$currentArchitecture] = @{ Url = $null; Hash = $null }
                }
            } elseif ($currentArchitecture -and $line -match '^\s*InstallerUrl:\s*(\S+)') {
                $installers[$currentArchitecture].Url = $Matches[1]
            } elseif ($currentArchitecture -and $line -match '^\s*InstallerSha256:\s*(\S+)') {
                $installers[$currentArchitecture].Hash = $Matches[1]
            }
        }
        $guiPath = Join-Path $repoRoot 'publish/gui/NVMeDriverPatcher.exe'
        $arm64Path = Join-Path $repoRoot 'publish/NVMeDriverPatcher-win-arm64.exe'
        foreach ($binding in @(
            @{ Architecture = 'x64'; File = $guiPath; Asset = 'NVMeDriverPatcher.exe' },
            @{ Architecture = 'arm64'; File = $arm64Path; Asset = 'NVMeDriverPatcher-win-arm64.exe' }
        )) {
            $architecture = $binding.Architecture
            if (-not $installers.ContainsKey($architecture)) {
                $failures.Add("winget manifest missing $architecture Architecture entry")
                continue
            }
            $record = $installers[$architecture]
            $expectedUrl = "https://github.com/SysAdminDoc/win11-nvme-driver-patcher/releases/download/v$Version/$($binding.Asset)"
            if ($record.Url -ne $expectedUrl) {
                $failures.Add("winget manifest InstallerUrl ($architecture) is '$($record.Url)', expected '$expectedUrl'")
            }
            if (Test-Path $binding.File) {
                $expectedHash = (Get-Sha256Hex $binding.File).ToUpperInvariant()
                if ($record.Hash -ne $expectedHash) {
                    $failures.Add("winget manifest InstallerSha256 ($architecture) does not match $($binding.Asset)")
                }
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
            if ($arch.url -ne "https://github.com/SysAdminDoc/win11-nvme-driver-patcher/releases/download/v$Version/NVMeDriverPatcher.exe#/NVMeDriverPatcher.exe") {
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
                if ($arm64Arch.url -ne "https://github.com/SysAdminDoc/win11-nvme-driver-patcher/releases/download/v$Version/NVMeDriverPatcher-win-arm64.exe#/NVMeDriverPatcher.exe") {
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
            } elseif ($scoop.autoupdate.architecture.arm64.url -notmatch 'NVMeDriverPatcher-win-arm64\.exe#/NVMeDriverPatcher\.exe$') {
                $failures.Add("scoop manifest arm64 autoupdate must preserve the ARM64 asset and common executable filename")
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
