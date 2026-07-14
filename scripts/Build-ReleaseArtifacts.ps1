# Build-ReleaseArtifacts.ps1
# Local release builder: publishes the installable x64 assets plus diagnostic/status-only
# win-arm64 portable assets, then writes contract-compatible SHA-256 sidecars.
[CmdletBinding()]
param(
    [string]$Version,
    [string]$Configuration = 'Release',
    [string]$RepoRoot,
    [switch]$NoClean,
    [switch]$SkipMsi
)

$ErrorActionPreference = 'Stop'
$repoRoot = if ($RepoRoot) { (Resolve-Path $RepoRoot).Path } else { (Resolve-Path (Join-Path $PSScriptRoot '..')).Path }

if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$props = Get-Content -Raw (Join-Path $repoRoot 'Directory.Build.props')
    $Version = [string]$props.Project.PropertyGroup.VersionPrefix
}
$Version = $Version.TrimStart('v')

$publishRoot = Join-Path $repoRoot 'publish'
if (-not $NoClean -and (Test-Path -LiteralPath $publishRoot)) {
    $resolved = (Resolve-Path -LiteralPath $publishRoot).Path
    if (-not $resolved.StartsWith($repoRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to delete publish path outside repo: $resolved"
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null

function Invoke-Checked {
    param([Parameter(Mandatory)] [string]$FilePath, [string[]]$Arguments = @())
    Write-Host ("+ {0} {1}" -f $FilePath, ($Arguments -join ' '))
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$FilePath exited with $LASTEXITCODE" }
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

Invoke-Checked dotnet @('restore', (Join-Path $repoRoot 'NVMeDriverPatcher.sln'))

$matrix = @(
    @{ Id = 'gui'; Project = 'src/NVMeDriverPatcher/NVMeDriverPatcher.csproj'; Runtime = 'win-x64'; Output = 'publish/gui'; Exe = 'NVMeDriverPatcher.exe' },
    @{ Id = 'cli'; Project = 'src/NVMeDriverPatcher.Cli/NVMeDriverPatcher.Cli.csproj'; Runtime = 'win-x64'; Output = 'publish/cli'; Exe = 'NVMeDriverPatcher.Cli.exe' },
    @{ Id = 'tray'; Project = 'src/NVMeDriverPatcher.Tray/NVMeDriverPatcher.Tray.csproj'; Runtime = 'win-x64'; Output = 'publish/tray'; Exe = 'NVMeDriverPatcher.Tray.exe' },
    @{ Id = 'watchdog'; Project = 'src/NVMeDriverPatcher.Watchdog/NVMeDriverPatcher.Watchdog.csproj'; Runtime = 'win-x64'; Output = 'publish/watchdog'; Exe = 'NVMeDriverPatcher.Watchdog.exe' },
    @{ Id = 'gui-arm64'; Project = 'src/NVMeDriverPatcher/NVMeDriverPatcher.csproj'; Runtime = 'win-arm64'; Output = 'publish/arm64/gui'; Exe = 'NVMeDriverPatcher.exe'; Asset = 'publish/NVMeDriverPatcher-win-arm64.exe' },
    @{ Id = 'cli-arm64'; Project = 'src/NVMeDriverPatcher.Cli/NVMeDriverPatcher.Cli.csproj'; Runtime = 'win-arm64'; Output = 'publish/arm64/cli'; Exe = 'NVMeDriverPatcher.Cli.exe'; Asset = 'publish/NVMeDriverPatcher.Cli-win-arm64.exe' },
    @{ Id = 'tray-arm64'; Project = 'src/NVMeDriverPatcher.Tray/NVMeDriverPatcher.Tray.csproj'; Runtime = 'win-arm64'; Output = 'publish/arm64/tray'; Exe = 'NVMeDriverPatcher.Tray.exe'; Asset = 'publish/NVMeDriverPatcher.Tray-win-arm64.exe' },
    @{ Id = 'watchdog-arm64'; Project = 'src/NVMeDriverPatcher.Watchdog/NVMeDriverPatcher.Watchdog.csproj'; Runtime = 'win-arm64'; Output = 'publish/arm64/watchdog'; Exe = 'NVMeDriverPatcher.Watchdog.exe'; Asset = 'publish/NVMeDriverPatcher.Watchdog-win-arm64.exe' }
)

foreach ($entry in $matrix) {
    $project = Join-Path $repoRoot $entry.Project
    $output = Join-Path $repoRoot $entry.Output
    Invoke-Checked dotnet @(
        'publish',
        $project,
        '-c',
        $Configuration,
        '-r',
        $entry.Runtime,
        '--self-contained',
        'true',
        '-p:PublishSingleFile=true',
        '--no-restore',
        '-o',
        $output
    )

    if ($entry.ContainsKey('Asset')) {
        $source = Join-Path $output $entry.Exe
        $asset = Join-Path $repoRoot $entry.Asset
        Copy-Item -LiteralPath $source -Destination $asset -Force
    }
}

if (-not $SkipMsi) {
    $input = Join-Path $publishRoot 'msi-input'
    if (Test-Path -LiteralPath $input) { Remove-Item -LiteralPath $input -Recurse -Force }
    New-Item -ItemType Directory -Path $input | Out-Null
    foreach ($folder in @('gui', 'cli', 'tray', 'watchdog')) {
        Copy-Item -Path (Join-Path $publishRoot "$folder/*") -Destination $input -Recurse -Force
    }
    Copy-Item -LiteralPath (Join-Path $repoRoot 'src/NVMeDriverPatcher/nvme.ico') -Destination (Join-Path $input 'icon.ico') -Force

    $wix = Join-Path $env:USERPROFILE '.dotnet/tools/wix.exe'
    if (-not (Test-Path -LiteralPath $wix)) { $wix = 'wix' }
    Invoke-Checked $wix @(
        'build',
        (Join-Path $repoRoot 'packaging/wix/NVMeDriverPatcher.wxs'),
        '-d',
        "PublishDir=$input",
        '-d',
        "ProjectRoot=$repoRoot",
        '-loc',
        (Join-Path $repoRoot 'packaging/wix/en-US.wxl'),
        '-bindpath',
        (Join-Path $repoRoot 'packaging/wix'),
        '-ext',
        'WixToolset.UI.wixext',
        '-ext',
        'WixToolset.Util.wixext',
        '-out',
        (Join-Path $publishRoot "NVMeDriverPatcher-$Version.msi")
    )

    $intuneStage = Join-Path $publishRoot 'intune-input'
    New-Item -ItemType Directory -Path $intuneStage -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $publishRoot "NVMeDriverPatcher-$Version.msi") -Destination $intuneStage -Force
    Copy-Item -LiteralPath (Join-Path $repoRoot 'packaging/intune/Detect-NVMeDriverPatcher.ps1') -Destination $intuneStage -Force
    Invoke-Checked powershell.exe @(
        '-NoProfile',
        '-ExecutionPolicy',
        'Bypass',
        '-File',
        (Join-Path $repoRoot 'scripts/New-ArtifactManifest.ps1'),
        '-PayloadRoot',
        $intuneStage,
        '-PayloadType',
        'intune-source',
        '-ToolVersion',
        $Version
    )
    $intuneZip = Join-Path $publishRoot "NVMeDriverPatcher.Intune-$Version.zip"
    $intuneZipTemp = Join-Path $publishRoot ("NVMeDriverPatcher.Intune-{0}.{1}.tmp.zip" -f $Version, [Guid]::NewGuid().ToString('N'))
    $intuneZipBackup = $intuneZip + '.replace-backup'
    try {
        Compress-Archive -Path (Join-Path $intuneStage '*') -DestinationPath $intuneZipTemp -Force
        if (Test-Path -LiteralPath $intuneZip) {
            if (Test-Path -LiteralPath $intuneZipBackup) { Remove-Item -LiteralPath $intuneZipBackup -Force }
            [IO.File]::Replace($intuneZipTemp, $intuneZip, $intuneZipBackup)
            Remove-Item -LiteralPath $intuneZipBackup -Force
        }
        else { [IO.File]::Move($intuneZipTemp, $intuneZip) }
    }
    finally {
        if (Test-Path -LiteralPath $intuneZipTemp) { Remove-Item -LiteralPath $intuneZipTemp -Force }
        if (Test-Path -LiteralPath $intuneZipBackup) { Remove-Item -LiteralPath $intuneZipBackup -Force }
    }
}

$moduleZip = Join-Path $publishRoot "NVMeDriverPatcher.PowerShell-$Version.zip"
if (Test-Path -LiteralPath $moduleZip) { Remove-Item -LiteralPath $moduleZip -Force }
Compress-Archive -Path (Join-Path $repoRoot 'packaging/powershell/*') -DestinationPath $moduleZip -Force

$manifestGenerator = Join-Path $repoRoot 'scripts/Update-PackageManifests.ps1'
Invoke-Checked powershell.exe @(
    '-NoProfile',
    '-ExecutionPolicy',
    'Bypass',
    '-File',
    $manifestGenerator,
    '-Version',
    $Version,
    '-ExePath',
    (Join-Path $publishRoot 'gui/NVMeDriverPatcher.exe'),
    '-Arm64ExePath',
    (Join-Path $publishRoot 'NVMeDriverPatcher-win-arm64.exe'),
    '-RepoRoot',
    $repoRoot,
    '-OutputRoot',
    $publishRoot
)
Invoke-Checked winget.exe @('validate', '--manifest', (Join-Path $publishRoot 'winget'))

$chocoStage = Join-Path $publishRoot 'chocolatey-package'
$nuspec = Join-Path $chocoStage 'nvme-driver-patcher.nuspec'

$nuget = Join-Path $env:USERPROFILE 'repos/nuget.exe'
if (-not (Test-Path -LiteralPath $nuget)) { $nuget = 'nuget.exe' }
Invoke-Checked $nuget @(
    'pack',
    $nuspec,
    '-OutputDirectory',
    $publishRoot,
    '-BasePath',
    $chocoStage,
    '-NoDefaultExcludes',
    '-NoPackageAnalysis'
)

$contract = Get-Content -Raw (Join-Path $repoRoot 'packaging/release-artifacts.json') | ConvertFrom-Json
$sumLines = New-Object System.Collections.Generic.List[string]
foreach ($artifact in $contract.artifacts) {
    if (-not $artifact.checksum) { continue }

    $rel = $artifact.path -replace '\{version\}', $Version
    $full = Join-Path $repoRoot $rel
    if (-not (Test-Path -LiteralPath $full)) {
        Write-Warning "Skipping checksum for missing artifact: $rel"
        continue
    }

    $leaf = Split-Path $rel -Leaf
    $hash = Get-Sha256Hex $full
    $line = "$hash  $leaf"
    $sumLines.Add($line)
    Set-Content -LiteralPath (Join-Path $publishRoot "$leaf.sha256") -Value $line -Encoding ASCII
}
Set-Content -LiteralPath (Join-Path $publishRoot 'SHA256SUMS.txt') -Value $sumLines -Encoding ASCII

Write-Host "Built release artifacts for v$Version. ARM64 portable assets are diagnostic/status-only until Microsoft ships ARM64 nvmedisk.sys." -ForegroundColor Green
