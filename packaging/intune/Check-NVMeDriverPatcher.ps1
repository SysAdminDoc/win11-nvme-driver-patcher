#Requires -Version 5.1
<#
.SYNOPSIS
  Intune remediation detection script for NVMe Driver Patcher.

.DESCRIPTION
  Pair with Remediate-NVMeDriverPatcher.ps1 under Devices > Scripts and remediations.
  Exit 0 means there's nothing to fix. Exit 1 tells Intune to run the remediation.

    Patch applied, or Windows already on the native driver: exit 0
    The build policy refuses apply on this Windows build:   exit 0, with the reason printed
    Patch missing or partial on a build that allows it:     exit 1
    CLI missing or its status unreadable:                    exit 1, with the cause printed

  Run it as SYSTEM in 64-bit PowerShell ("Run this script using the logged-on credentials": No,
  "Run script in 64-bit PowerShell": Yes). Every CLI command needs administrator rights.

.PARAMETER CliPath
  Path to NVMeDriverPatcher.Cli.exe. Intune never passes it; the script reads the MSI's
  InstallLocation instead.
#>
param([string]$CliPath)

$ErrorActionPreference = 'Continue'

function Get-CliPath {
    param([string]$Override)
    if ($Override) {
        if (Test-Path -LiteralPath $Override -PathType Leaf) { return $Override }
        return $null
    }
    # The CLI runs elevated, so it's resolved from the MSI's InstallLocation and never from PATH or
    # the current directory. The 64-bit registry view keeps a 32-bit host from reading WOW6432Node.
    $folder = $null
    try {
        $hklm = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
            [Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry64)
        $key = $hklm.OpenSubKey('SOFTWARE\SysAdminDoc\NVMeDriverPatcher')
        if ($key) { $folder = $key.GetValue('InstallLocation'); $key.Dispose() }
        $hklm.Dispose()
    } catch { }
    if ([string]::IsNullOrWhiteSpace($folder)) {
        $programFiles = if ($env:ProgramW6432) { $env:ProgramW6432 } else { $env:ProgramFiles }
        $folder = Join-Path $programFiles 'NVMe Driver Patcher'
    }
    $exe = Join-Path $folder 'NVMeDriverPatcher.Cli.exe'
    if (Test-Path -LiteralPath $exe -PathType Leaf) { return $exe }
    return $null
}

$cli = Get-CliPath $CliPath
if (-not $cli) {
    Write-Output 'NVMe Driver Patcher CLI not found. Deploy the MSI to this device first.'
    exit 1
}

$raw = (& $cli status --json) | Out-String
$start = $raw.IndexOf('{')
$end = $raw.LastIndexOf('}')
if ($start -lt 0 -or $end -le $start) {
    Write-Output "Couldn't read status from the CLI: $($raw.Trim())"
    exit 1
}
try {
    $status = ($raw.Substring($start, $end - $start + 1) | ConvertFrom-Json).data
} catch {
    Write-Output "Couldn't parse the CLI status: $($_.Exception.Message)"
    exit 1
}

if ($status.nativeActive) {
    Write-Output 'Compliant: the native NVMe driver is active.'
    exit 0
}
if ($status.applied) {
    Write-Output 'Compliant: patch applied. The native driver loads after the next restart.'
    exit 0
}
if (-not $status.applyAllowed) {
    Write-Output "Not applicable on this Windows build: $($status.applyBlockedReason)"
    exit 0
}
Write-Output "Not compliant: patch is $($status.status) ($($status.componentsApplied) of $($status.componentsTotal) components)."
exit 1
