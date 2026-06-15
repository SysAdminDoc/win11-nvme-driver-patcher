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

# Runs a CLI subcommand with --json and parses the versioned envelope. Falls back to a $null
# Data object (and surfaces the raw text) if the CLI returned non-JSON for any reason.
function Invoke-CliJson {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $Command,
        [string[]] $Arguments = @()
    )
    $r = Invoke-Cli -Command $Command -Arguments (@('--json') + $Arguments)
    $data = $null
    try { $data = $r.Raw | ConvertFrom-Json } catch { }
    [PSCustomObject]@{ ExitCode = $r.ExitCode; Envelope = $data; Raw = $r.Raw }
}

function Get-NvmePatchStatus {
    [CmdletBinding()]
    param()
    $j = Invoke-CliJson -Command 'status'
    $d = $j.Envelope.data
    [PSCustomObject]@{
        Applied           = [bool]$d.applied
        Partial           = [bool]$d.partial
        NotApplied        = (-not ($d.applied -or $d.partial))
        ComponentsApplied = $d.componentsApplied
        ComponentsTotal   = $d.componentsTotal
        AppliedKeys       = $d.appliedKeys
        NativeActive      = [bool]$d.nativeActive
        ActiveDriver      = $d.activeDriver
        EnablementSource  = $d.enablementSource
        BuildRuleId       = $d.buildRuleId
        ExitCode          = $j.ExitCode
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
    $j = Invoke-CliJson -Command 'watchdog'
    $d = $j.Envelope.data
    [PSCustomObject]@{
        Verdict     = if ($d) { $d.verdict } else { 'Unknown' }
        TotalEvents = $d.totalEvents
        BugChecks   = $d.bugChecks
        Summary     = $d.summary
        EventCounts = $d.eventCounts
        ExitCode    = $j.ExitCode
    }
}

function Get-NvmeControllerAudit {
    [CmdletBinding()] param()
    $j = Invoke-CliJson -Command 'controllers'
    $d = $j.Envelope.data
    $controllers = foreach ($c in $d.controllers) {
        [PSCustomObject]@{
            IsNative       = [bool]$c.isNative
            Name           = $c.friendlyName
            Driver         = $c.boundDriver
            InstanceId     = $c.instanceId
            InfName        = $c.infName
            DriverProvider = $c.driverProvider
            DeviceClass    = $c.deviceClass
        }
    }
    [PSCustomObject]@{
        ExitCode    = $j.ExitCode
        NativeCount = $d.nativeCount
        LegacyCount = $d.legacyCount
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
