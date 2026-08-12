# Test-NuGetAuditGate.ps1
# Disposable regression proof for the repository-wide NuGet audit gate. It creates a local
# package whose dependency is a known-vulnerable Newtonsoft.Json version, then requires restore
# to fail with the NU1900-NU1904 audit family. Nothing is written under the repository.
[CmdletBinding()]
param(
    [string]$DotnetPath = 'dotnet.exe'
)

$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) ('nvme-nuget-audit-' + [Guid]::NewGuid().ToString('N'))
$source = Join-Path $root 'source'
$seed = Join-Path $root 'seed'
$consumer = Join-Path $root 'consumer'
$config = Join-Path $root 'NuGet.Config'
$restoreOutput = Join-Path $root 'restore.log'

function Invoke-Dotnet {
    param([string[]]$Arguments)
    $output = & $DotnetPath @Arguments 2>&1
    $exitCode = $LASTEXITCODE
    foreach ($line in @($output)) { Write-Host $line }
    return $exitCode
}

try {
    New-Item -ItemType Directory -Path $source -Force | Out-Null
    $exitCode = Invoke-Dotnet @('new', 'classlib', '--framework', 'netstandard2.0', '--output', $seed, '--no-restore')
    if ($exitCode -ne 0) { throw "dotnet new seed failed with exit code $exitCode." }

    $seedProject = (Get-ChildItem -LiteralPath $seed -Filter *.csproj | Select-Object -First 1).FullName
    $exitCode = Invoke-Dotnet @('add', $seedProject, 'package', 'Newtonsoft.Json', '--version', '12.0.1', '--no-restore')
    if ($exitCode -ne 0) { throw "dotnet add seed dependency failed with exit code $exitCode." }
    $exitCode = Invoke-Dotnet @('restore', $seedProject, '-p:NuGetAudit=false')
    if ($exitCode -ne 0) { throw "seed restore failed with exit code $exitCode." }
    $exitCode = Invoke-Dotnet @('pack', $seedProject, '--no-restore', '-c', 'Release', '-o', $source,
        '-p:PackageId=NVMeAuditSeed', '-p:PackageVersion=1.0.0')
    if ($exitCode -ne 0) { throw "seed pack failed with exit code $exitCode." }

    $exitCode = Invoke-Dotnet @('new', 'classlib', '--framework', 'net10.0', '--output', $consumer, '--no-restore')
    if ($exitCode -ne 0) { throw "dotnet new consumer failed with exit code $exitCode." }
    $consumerProject = (Get-ChildItem -LiteralPath $consumer -Filter *.csproj | Select-Object -First 1).FullName

    $exitCode = Invoke-Dotnet @('add', $consumerProject, 'package', 'NVMeAuditSeed', '--version', '1.0.0',
        '--source', $source, '--no-restore')
    if ($exitCode -ne 0) { throw "dotnet add consumer dependency failed with exit code $exitCode." }

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

    $exitCode = & $DotnetPath restore $consumerProject --force-evaluate --configfile $config `
        '-p:NuGetAuditMode=all' '-p:NuGetAuditLevel=low' `
        '-p:WarningsAsErrors=NU1900%3BNU1901%3BNU1902%3BNU1903%3BNU1904' 2>&1 | Tee-Object -FilePath $restoreOutput
    $restoreExitCode = $LASTEXITCODE
    $output = Get-Content -Raw -LiteralPath $restoreOutput
    if ($restoreExitCode -eq 0) {
        throw 'Seeded vulnerable transitive restore unexpectedly succeeded.'
    }
    if ($output -notmatch 'NU190[0-4]') {
        throw "Restore failed, but did not report a NuGet audit diagnostic: $output"
    }

    Write-Host 'NuGet audit gate regression proof passed: seeded vulnerable transitive restore failed with NU190x.'
    exit 0
}
finally {
    if (Test-Path -LiteralPath $root) {
        Remove-Item -LiteralPath $root -Recurse -Force
    }
}
