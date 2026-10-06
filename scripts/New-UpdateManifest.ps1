# New-UpdateManifest.ps1
# Writes publish/update-manifest.json (version, the oldest install that may take it, an expiry,
# and the SHA-256 of every checksummed release asset) and signs its exact bytes with the offline
# ECDSA P-256 key into publish/update-manifest.json.sig (base64, IEEE P1363). The app trusts the
# public halves listed in src/NVMeDriverPatcher.Core/Services/UpdateManifestService.cs, and
# Validate-ReleaseAssets.ps1 checks the signature against them. Runs on Windows PowerShell 5.1.
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Version,
    [string]$RepoRoot,
    # Unencrypted PKCS#8 PEM. Defaults to NVME_PATCHER_UPDATE_KEY, then the owner's key folder.
    [string]$KeyPath,
    [string]$MinimumVersion = '5.0.0',
    [ValidateRange(30, 730)] [int]$ValidDays = 365
)

$ErrorActionPreference = 'Stop'
$repoRoot = if ($RepoRoot) { (Resolve-Path $RepoRoot).Path } else { (Resolve-Path (Join-Path $PSScriptRoot '..')).Path }
$Version = $Version.TrimStart('v')
foreach ($v in @($Version, $MinimumVersion)) {
    if ($v -notmatch '^\d+\.\d+\.\d+$') { throw "'$v' isn't a three-part version." }
}

if (-not $KeyPath) { $KeyPath = $env:NVME_PATCHER_UPDATE_KEY }
if (-not $KeyPath) { $KeyPath = Join-Path $env:USERPROFILE '.nvme-patcher-update-keys\update-manifest-primary.pem' }
if (-not (Test-Path -LiteralPath $KeyPath -PathType Leaf)) {
    throw "Update-manifest signing key not found at '$KeyPath'. Set NVME_PATCHER_UPDATE_KEY or pass -KeyPath."
}

$publishRoot = Join-Path $repoRoot 'publish'
$contract = Get-Content -Raw (Join-Path $repoRoot 'packaging/release-artifacts.json') | ConvertFrom-Json
$assets = [ordered]@{}
foreach ($artifact in $contract.artifacts) {
    if (-not $artifact.checksum) { continue }
    $rel = $artifact.path -replace '\{version\}', $Version
    $full = Join-Path $repoRoot $rel
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
        if ($artifact.required) { throw "Required checksummed artifact missing: $rel" }
        continue
    }
    $leaf = Split-Path $rel -Leaf
    if ($leaf -match '["\\]') { throw "Asset name can't go into the manifest as written: $leaf" }
    $assets[$leaf] = (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash.ToLowerInvariant()
}
if ($assets.Count -eq 0) { throw 'No checksummed artifacts to put in the update manifest.' }

$invariant = [Globalization.CultureInfo]::InvariantCulture
$issued = [DateTime]::UtcNow
$lines = New-Object System.Collections.Generic.List[string]
$lines.Add('{')
$lines.Add('  "schema": 1,')
$lines.Add('  "product": "NVMeDriverPatcher",')
$lines.Add("  `"version`": `"$Version`",")
$lines.Add("  `"minimumVersion`": `"$MinimumVersion`",")
$lines.Add("  `"issuedUtc`": `"$($issued.ToString('yyyy-MM-ddTHH:mm:ssZ', $invariant))`",")
$lines.Add("  `"expiresUtc`": `"$($issued.AddDays($ValidDays).ToString('yyyy-MM-ddTHH:mm:ssZ', $invariant))`",")
$lines.Add('  "assets": {')
$names = @($assets.Keys)
for ($i = 0; $i -lt $names.Count; $i++) {
    $separator = if ($i -lt $names.Count - 1) { ',' } else { '' }
    $lines.Add("    `"$($names[$i])`": `"$($assets[$names[$i]])`"$separator")
}
$lines.Add('  }')
$lines.Add('}')
$manifestBytes = (New-Object System.Text.UTF8Encoding($false)).GetBytes(($lines -join "`n") + "`n")

$pem = Get-Content -Raw -LiteralPath $KeyPath
if ($pem -notmatch '-----BEGIN PRIVATE KEY-----') {
    throw "The key at '$KeyPath' isn't an unencrypted PKCS#8 PEM (BEGIN PRIVATE KEY)."
}
$der = [Convert]::FromBase64String(($pem -replace '-----[^-]+-----', '' -replace '\s', ''))
$cngKey = [System.Security.Cryptography.CngKey]::Import($der, [System.Security.Cryptography.CngKeyBlobFormat]::Pkcs8PrivateBlob)
$signer = $null
try {
    $signer = New-Object System.Security.Cryptography.ECDsaCng($cngKey)
    if ($signer.KeySize -ne 256) { throw "The key at '$KeyPath' isn't a P-256 key." }
    $signature = $signer.SignData($manifestBytes, [System.Security.Cryptography.HashAlgorithmName]::SHA256)
}
finally {
    if ($signer) { $signer.Dispose() }
    $cngKey.Dispose()
}

New-Item -ItemType Directory -Force -Path $publishRoot | Out-Null
$manifestPath = Join-Path $publishRoot 'update-manifest.json'
[IO.File]::WriteAllBytes($manifestPath, $manifestBytes)
[IO.File]::WriteAllText("$manifestPath.sig", [Convert]::ToBase64String($signature) + "`n", [Text.Encoding]::ASCII)
Write-Host "Signed update manifest for $Version ($($assets.Count) assets, expires in $ValidDays days)."
