#Requires -Version 5.1
<#
.SYNOPSIS
  PowerShell wrapper for NVMeDriverPatcher.Cli.exe.

.DESCRIPTION
  Locates the CLI exe next to the module or at the install location the MSI recorded,
  invokes the requested subcommand with --json, parses the versioned JSON envelope, and
  returns a typed PSCustomObject the caller can pipeline. Read commands use the CliJson
  contract; mutation commands (apply, remove) return raw text.

.NOTES
  Every function is a thin wrapper around one CLI subcommand; the heavy lifting
  stays in the C# CLI exe.

  The CLI carries a requireAdministrator manifest and this module is written to be used
  from an elevated session, so resolution is deliberately narrow: no bare relative
  candidate and no Get-Command / $PATH fallback, because either would let a planted
  NVMeDriverPatcher.Cli.exe in the caller's current directory run elevated. Only fully
  qualified paths from trusted locations are accepted, and each is rejected if its
  directory grants write access to a non-administrative principal.
#>

$script:CliExeName = 'NVMeDriverPatcher.Cli.exe'
$script:InstallLocationKey = 'HKLM:\Software\SysAdminDoc\NVMeDriverPatcher'

# Principals that are already trusted to write into a privileged program directory.
# Anything else holding a write/modify/full right makes the directory plantable.
$script:TrustedWriterSids = @(
    'S-1-5-18',     # LOCAL SYSTEM
    'S-1-5-32-544', # BUILTIN\Administrators
    'S-1-3-0',      # CREATOR OWNER
    'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464' # NT SERVICE\TrustedInstaller
)

# Only genuine write bits, spelled out numerically. The composite FileSystemRights values
# (FullControl 0x1F01FF, Modify 0x301BF) also carry the standard rights READ_CONTROL and
# SYNCHRONIZE, which every read-only ACE has too -- masking with them matches
# "ReadAndExecute, Synchronize" and would report Program Files as writable by BUILTIN\Users.
$script:PlantableRights =
    0x00000002 -bor  # WriteData / CreateFiles
    0x00000004 -bor  # AppendData / CreateDirectories
    0x00000010 -bor  # WriteExtendedAttributes
    0x00000040 -bor  # DeleteSubdirectoriesAndFiles
    0x00000100 -bor  # WriteAttributes
    0x00010000 -bor  # Delete
    0x00040000 -bor  # ChangePermissions (WRITE_DAC)
    0x00080000 -bor  # TakeOwnership (WRITE_OWNER)
    0x10000000 -bor  # GENERIC_ALL
    0x40000000       # GENERIC_WRITE

# Returns the first non-administrative identity that can write into $Directory, or $null
# when only trusted principals can. A directory a standard user can write is a directory
# where the exe we are about to run elevated can be replaced.
function Get-NonAdminWriter {
    [CmdletBinding()]
    param([Parameter(Mandatory)] [string] $Directory)

    $acl = Get-Acl -LiteralPath $Directory -ErrorAction Stop
    foreach ($ace in $acl.Access) {
        if ($ace.AccessControlType -ne [System.Security.AccessControl.AccessControlType]::Allow) { continue }
        # Cast through int: FileSystemRights values above 0x7FFFFFFF surface as negative ints and
        # sign-extend when PowerShell promotes them for -band.
        if (((([int] $ace.FileSystemRights) -band $script:PlantableRights) -band 0xFFFFFFFF) -eq 0) { continue }

        $identity = $ace.IdentityReference
        try {
            $sid = $identity.Translate([System.Security.Principal.SecurityIdentifier]).Value
        } catch {
            # An identity that cannot be translated cannot be proven trusted.
            return $identity.Value
        }
        if ($script:TrustedWriterSids -notcontains $sid) { return $identity.Value }
    }
    return $null
}

# Ordered, fully-qualified candidates only. $PSScriptRoot first (the module ships beside the
# exe), then the install location the MSI recorded in HKLM, then the default per-machine
# install directory.
function Get-CliPathCandidate {
    [CmdletBinding()] param()

    if ($PSScriptRoot) { Join-Path $PSScriptRoot $script:CliExeName }

    $recorded = (Get-ItemProperty -Path $script:InstallLocationKey -Name 'InstallLocation' -ErrorAction SilentlyContinue).InstallLocation
    if ($recorded) { Join-Path $recorded $script:CliExeName }

    $programFiles = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
    if ($programFiles) { Join-Path (Join-Path $programFiles 'NVMe Driver Patcher') $script:CliExeName }
}

function Get-CliPath {
    [CmdletBinding()] param()

    $rejected = @()
    foreach ($candidate in (Get-CliPathCandidate)) {
        if (-not [System.IO.Path]::IsPathRooted($candidate)) { continue }
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { continue }

        $resolved = (Resolve-Path -LiteralPath $candidate).Path
        $directory = Split-Path -Path $resolved -Parent

        $writer = $null
        try {
            $writer = Get-NonAdminWriter -Directory $directory
        } catch {
            $rejected += "$resolved (its directory ACL could not be read: $($_.Exception.Message))"
            continue
        }

        if ($writer) {
            $rejected += "$resolved (directory is writable by '$writer')"
            continue
        }
        return $resolved
    }

    if ($rejected.Count -gt 0) {
        throw ("$($script:CliExeName) was found but refused because it could be replaced by a " +
               "non-administrator: $($rejected -join '; '). Install via winget / MSI into Program Files.")
    }
    throw "$($script:CliExeName) not found. Install via winget / MSI, or place it next to this module in a directory only administrators can write."
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
            DriverVersion  = $c.boundDriverVersion
            InstanceId     = $c.instanceId
            InfName        = $c.infName
            DriverProvider = $c.driverProvider
            DeviceClass    = $c.deviceClass
            CandidateProbeSucceeded = [bool]$c.driverCandidateProbeSucceeded
            CandidateProbeError     = $c.driverCandidateProbeError
            DriverCandidates        = $c.driverCandidates
        }
    }
    [PSCustomObject]@{
        ExitCode    = $j.ExitCode
        NativeCount = $d.nativeCount
        LegacyCount = $d.legacyCount
        ObservedAtUtc = $d.observedAtUtc
        Controllers = $controllers
    }
}

function Get-NvmeRecoveryProof {
    [CmdletBinding()] param()
    $j = Invoke-CliJson -Command 'recovery-proof'
    $d = $j.Envelope.data
    [PSCustomObject]@{
        AllPassed   = [bool]$d.allPassed
        PassedCount = $d.passedCount
        TotalCount  = $d.totalCount
        Items       = $d.items
        ExitCode    = $j.ExitCode
    }
}

function Get-NvmeBypassIo {
    [CmdletBinding()] param()
    $j = Invoke-CliJson -Command 'bypassio'
    $d = $j.Envelope.data
    [PSCustomObject]@{
        Supported   = [bool]$d.supported
        StorageType = $d.storageType
        DriverCompat = $d.driverCompat
        BlockedBy   = $d.blockedBy
        Warning     = $d.warning
        ExitCode    = $j.ExitCode
    }
}

function Get-NvmeFirmwareCompat {
    [CmdletBinding()] param()
    $j = Invoke-CliJson -Command 'firmware'
    $d = $j.Envelope.data
    [PSCustomObject]@{
        SchemaVersion = $d.schemaVersion
        Updated       = $d.updated
        EntryCount    = $d.entryCount
        Entries       = $d.entries
        ExitCode      = $j.ExitCode
    }
}

function Get-NvmeFeatureStore {
    [CmdletBinding()] param()
    $j = Invoke-CliJson -Command 'featurestore'
    $d = $j.Envelope.data
    [PSCustomObject]@{
        HasFallbackEvidence = [bool]$d.hasFallbackEvidence
        Configurations      = $d.configurations
        ExitCode            = $j.ExitCode
    }
}

function Get-NvmeReliability {
    [CmdletBinding()] param()
    $j = Invoke-CliJson -Command 'reliability'
    $d = $j.Envelope.data
    [PSCustomObject]@{
        DataAvailable    = [bool]$d.dataAvailable
        PrePatchAverage  = $d.prePatchAverage
        PostPatchAverage = $d.postPatchAverage
        Delta            = $d.delta
        Summary          = $d.summary
        Series           = $d.series
        ExitCode         = $j.ExitCode
    }
}

function Get-NvmeMinidump {
    [CmdletBinding()] param()
    $j = Invoke-CliJson -Command 'minidump'
    $d = $j.Envelope.data
    [PSCustomObject]@{
        TotalFound     = $d.totalFound
        NewerThanPatch = $d.newerThanPatch
        NVMeRelated    = $d.nvMeRelated
        ScanCompleted  = [bool]$d.scanCompleted
        Summary        = $d.summary
        Dumps          = $d.dumps
        ExitCode       = $j.ExitCode
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
    Get-NvmeWatchdogReport, Get-NvmeControllerAudit, Get-NvmeRecoveryProof, Get-NvmeBypassIo,
    Get-NvmeFirmwareCompat, Get-NvmeFeatureStore, Get-NvmeReliability, Get-NvmeMinidump,
    Invoke-NvmeDryRun, Export-NvmeDiagnostics, Export-NvmeDashboard
