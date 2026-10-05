# Test-NuGetAuditGate.ps1
# Disposable regression proof for the repository-wide NuGet audit gate. It creates a local
# package whose dependency is a known-vulnerable Newtonsoft.Json version, then requires restore
# to fail with the NU1900-NU1904 audit family.
#
# The probe projects live under <repo>\obj (gitignored) so MSBuild imports the repository's own
# Directory.Build.props, and the consumer restore passes no audit settings of its own. A probe in
# %TEMP% with explicit audit flags only proved that NuGet honors those flags; it kept passing if
# the gate were deleted from Directory.Build.props. The probe folder is removed on exit, and every
# dotnet call runs without a window and with a time limit.
[CmdletBinding()]
param(
    [string]$DotnetPath = 'dotnet.exe',
    [ValidateRange(30, 3600)]
    [int]$TimeoutSeconds = 300
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$buildProps = Join-Path $repoRoot 'Directory.Build.props'
if (-not (Test-Path -LiteralPath $buildProps -PathType Leaf)) {
    throw "Directory.Build.props was not found at '$buildProps'."
}

$objRoot = Join-Path $repoRoot 'obj'
$objExisted = Test-Path -LiteralPath $objRoot
$gateRoot = Join-Path $objRoot 'nuget-audit-gate'
$root = Join-Path $gateRoot ([Guid]::NewGuid().ToString('N'))
$source = Join-Path $root 'source'
$seed = Join-Path $root 'seed'
$consumer = Join-Path $root 'consumer'
$config = Join-Path $root 'NuGet.Config'

function ConvertTo-ProcessArgument {
    param([string]$Value)
    if ($Value -and $Value -notmatch '[\s"]') { return $Value }
    $escaped = $Value -replace '(\\*)"', '$1$1\"'
    $escaped = $escaped -replace '(\\+)$', '$1$1'
    return '"' + $escaped + '"'
}

function Invoke-Dotnet {
    param([string[]]$Arguments)

    $info = New-Object System.Diagnostics.ProcessStartInfo
    $info.FileName = $DotnetPath
    $info.Arguments = (@($Arguments | ForEach-Object { ConvertTo-ProcessArgument $_ })) -join ' '
    $info.WorkingDirectory = $root
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.EnvironmentVariables['DOTNET_NOLOGO'] = '1'
    $info.EnvironmentVariables['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'
    $info.EnvironmentVariables['MSBUILDDISABLENODEREUSE'] = '1'

    $process = [System.Diagnostics.Process]::Start($info)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            & (Join-Path $env:WINDIR 'System32\taskkill.exe') /PID $process.Id /T /F 2>&1 | Out-Null
            throw "dotnet $($Arguments[0]) did not finish within $TimeoutSeconds seconds."
        }
        $process.WaitForExit()
        $text = $stdout.Result + $stderr.Result
        foreach ($line in ($text -split "`r?`n")) {
            if ($line) { Write-Host $line }
        }
        return [pscustomobject]@{ ExitCode = $process.ExitCode; Output = $text }
    }
    finally {
        $process.Dispose()
    }
}

try {
    New-Item -ItemType Directory -Path $source -Force | Out-Null

    $result = Invoke-Dotnet @('new', 'classlib', '--framework', 'netstandard2.0', '--output', $seed, '--no-restore')
    if ($result.ExitCode -ne 0) { throw "dotnet new seed failed with exit code $($result.ExitCode)." }
    $seedProject = (Get-ChildItem -LiteralPath $seed -Filter *.csproj | Select-Object -First 1).FullName

    $result = Invoke-Dotnet @('add', $seedProject, 'package', 'Newtonsoft.Json', '--version', '12.0.1', '--no-restore')
    if ($result.ExitCode -ne 0) { throw "dotnet add seed dependency failed with exit code $($result.ExitCode)." }
    # The seed depends on the vulnerable version directly and inherits the gate too, so its own
    # restore has to opt out. Only the consumer restore below is the proof.
    $result = Invoke-Dotnet @('restore', $seedProject, '--disable-build-servers', '-p:NuGetAudit=false')
    if ($result.ExitCode -ne 0) { throw "seed restore failed with exit code $($result.ExitCode)." }
    $result = Invoke-Dotnet @('pack', $seedProject, '--no-restore', '--disable-build-servers', '-c', 'Release',
        '-o', $source, '-p:PackageId=NVMeAuditSeed', '-p:PackageVersion=1.0.0')
    if ($result.ExitCode -ne 0) { throw "seed pack failed with exit code $($result.ExitCode)." }

    $result = Invoke-Dotnet @('new', 'classlib', '--framework', 'net10.0', '--output', $consumer, '--no-restore')
    if ($result.ExitCode -ne 0) { throw "dotnet new consumer failed with exit code $($result.ExitCode)." }
    $consumerProject = (Get-ChildItem -LiteralPath $consumer -Filter *.csproj | Select-Object -First 1).FullName

    $result = Invoke-Dotnet @('add', $consumerProject, 'package', 'NVMeAuditSeed', '--version', '1.0.0',
        '--source', $source, '--no-restore')
    if ($result.ExitCode -ne 0) { throw "dotnet add consumer dependency failed with exit code $($result.ExitCode)." }

    @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="audit-local" value="$source" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
"@ | Set-Content -LiteralPath $config -Encoding UTF8

    $result = Invoke-Dotnet @('restore', $consumerProject, '--force-evaluate', '--disable-build-servers',
        '--configfile', $config)
    if ($result.ExitCode -eq 0) {
        throw 'Seeded vulnerable transitive restore unexpectedly succeeded. Directory.Build.props no longer fails restore on NU1900-NU1904.'
    }
    if ($result.Output -notmatch 'error\s+NU190[0-4]') {
        throw "Restore failed, but not with a NuGet audit error: $($result.Output)"
    }

    Write-Host 'NuGet audit gate regression proof passed: Directory.Build.props failed the seeded vulnerable transitive restore with NU190x.'
    exit 0
}
finally {
    if (Test-Path -LiteralPath $root) {
        Remove-Item -LiteralPath $root -Recurse -Force
    }
    if ((Test-Path -LiteralPath $gateRoot) -and -not (Get-ChildItem -LiteralPath $gateRoot -Force)) {
        Remove-Item -LiteralPath $gateRoot -Force
    }
    if (-not $objExisted -and (Test-Path -LiteralPath $objRoot) -and -not (Get-ChildItem -LiteralPath $objRoot -Force)) {
        Remove-Item -LiteralPath $objRoot -Force
    }
}
