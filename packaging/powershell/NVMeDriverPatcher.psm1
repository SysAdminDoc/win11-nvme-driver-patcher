#Requires -Version 5.1
<#
.SYNOPSIS
  PowerShell wrapper for NVMeDriverPatcher.Cli.exe.

.DESCRIPTION
  Locates the CLI exe (either next to the module or via Get-Command), invokes the
  requested subcommand, captures stdout/stderr, and returns a typed PSCustomObject
  the caller can pipeline. Each cmdlet parses the CLI's plain-text output defensively —
  upstream changes to CLI messaging should not break the module even if the parsed
  fields go null.

.NOTES
  No AI attribution — this module is plain-English PowerShell. Every function is a thin
  wrapper around one CLI subcommand; the heavy lifting stays in the C# CLI exe.
#>

$script:CliExeCandidates = @(
    (Join-Path $PSScriptRoot 'NVMeDriverPatcher.Cli.exe'),
    'NVMeDriverPatcher.Cli.exe'
)

function Get-CliPath {
    foreach ($c in $script:CliExeCandidates) {
        if (Test-Path $c) { return (Resolve-Path $c).Path }
    }
    $cmd = Get-Command -Name 'NVMeDriverPatcher.Cli.exe' -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    throw "NVMeDriverPatcher.Cli.exe not found. Install via winget / MSI, or drop it next to this module."
}

function Invoke-Cli {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $Command,
        [string[]] $Arguments = @()
    )
    $cli = Get-CliPath
    $stdout = & $cli $Command @Arguments 2>&1
    [PSCustomObject]@{
        ExitCode = $LASTEXITCODE
        Output   = $stdout
        Raw      = ($stdout -join "`n")
    }
}

function Get-NvmePatchStatus {
    [CmdletBinding()]
    param()
    $r = Invoke-Cli -Command 'status'
    [PSCustomObject]@{
        Applied    = ($r.Raw -match 'Status:\s*APPLIED')
        Partial    = ($r.Raw -match 'Status:\s*PARTIAL')
        NotApplied = ($r.Raw -match 'Status:\s*NOT APPLIED')
        ExitCode   = $r.ExitCode
        Raw        = $r.Raw
    }
}

function Invoke-NvmePatchApply {
    [CmdletBinding()]
    param(
        [ValidateSet('Safe','Full')] [string] $Profile = 'Safe',
        [switch] $NoRestart,
        [switch] $Unattended,
        [switch] $Force
    )
    $argsList = @()
    if ($Profile -eq 'Safe') { $argsList += '--safe' } else { $argsList += '--full' }
    if ($NoRestart) { $argsList += '--no-restart' }
    if ($Unattended) { $argsList += '--unattended' }
    if ($Force) { $argsList += '--force' }
    Invoke-Cli -Command 'apply' -Arguments $argsList
}

function Invoke-NvmePatchRemove {
    [CmdletBinding()]
    param([switch] $NoRestart)
    $argsList = @()
    if ($NoRestart) { $argsList += '--no-restart' }
    Invoke-Cli -Command 'remove' -Arguments $argsList
}

function Get-NvmeWatchdogReport {
    [CmdletBinding()] param()
    $r = Invoke-Cli -Command 'watchdog'
    $verdict = if ($r.Raw -match 'Verdict:\s*(\w+)') { $Matches[1] } else { 'Unknown' }
    [PSCustomObject]@{
        Verdict  = $verdict
        ExitCode = $r.ExitCode
        Raw      = $r.Raw
    }
}

function Get-NvmeControllerAudit {
    [CmdletBinding()] param()
    $r = Invoke-Cli -Command 'controllers'
    $controllers = foreach ($line in $r.Output) {
        if ($line -match '^\s*\[(NATIVE|LEGACY)\]\s+(.+?)\s+driver=(\S+)\s+id=(.+)$') {
            [PSCustomObject]@{
                IsNative   = ($Matches[1] -eq 'NATIVE')
                Name       = $Matches[2].Trim()
                Driver     = $Matches[3]
                InstanceId = $Matches[4].Trim()
            }
        }
    }
    [PSCustomObject]@{
        ExitCode    = $r.ExitCode
        Controllers = $controllers
    }
}

function Invoke-NvmeDryRun {
    [CmdletBinding()] param()
    Invoke-Cli -Command 'dry-run'
}

function Export-NvmeDiagnostics {
    [CmdletBinding()] param()
    Invoke-Cli -Command 'diagnostics'
}

function Export-NvmeDashboard {
    [CmdletBinding()] param()
    Invoke-Cli -Command 'dashboard'
}

Export-ModuleMember -Function Get-NvmePatchStatus, Invoke-NvmePatchApply, Invoke-NvmePatchRemove,
    Get-NvmeWatchdogReport, Get-NvmeControllerAudit, Invoke-NvmeDryRun,
    Export-NvmeDiagnostics, Export-NvmeDashboard
