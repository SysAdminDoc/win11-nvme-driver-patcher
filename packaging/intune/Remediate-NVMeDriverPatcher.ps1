#Requires -Version 5.1
<#
.SYNOPSIS
  Intune remediation script for NVMe Driver Patcher.

.DESCRIPTION
  Pair with Check-NVMeDriverPatcher.ps1 under Devices > Scripts and remediations. Runs the CLI's
  unattended apply and passes its exit code back to Intune: 0 is success, anything else failed.

  It never passes --force or --force-unsupported-build. The CLI refuses apply on a Windows build
  with no known enablement path, on a failed critical safety check, and when the recovery files
  aren't in place, and a fleet remediation honors all three. The device isn't restarted unless
  $restartAfterApply below is set to $true. The native driver loads on the next restart.

  Run it as SYSTEM in 64-bit PowerShell, same as the check script.

.PARAMETER CliPath
  Path to NVMeDriverPatcher.Cli.exe. Intune never passes it; the script reads the MSI's
  InstallLocation instead.
#>
param([string]$CliPath)

# Set to $true to restart right after a successful apply instead of at the next planned restart.
$restartAfterApply = $false

$ErrorActionPreference = 'Continue'

function Get-CliPath {
    param([string]$Override)
    if ($Override) {
        if (Test-Path -LiteralPath $Override -PathType Leaf) { return $Override }
        return $null
    }
    # Same lookup as the check script: InstallLocation from the 64-bit registry view, never PATH.
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

$cliArgs = @('apply', '--unattended')
if (-not $restartAfterApply) { $cliArgs += '--no-restart' }

$output = (& $cli @cliArgs) | Out-String
$code = $LASTEXITCODE

# Intune keeps about 2,048 characters of output, so keep the end, where the result is.
$lines = @($output -split "`r?`n" | Where-Object { $_.Trim() })
$lines | Select-Object -Last 20 | ForEach-Object { Write-Output $_ }
if ($code -eq 0 -and -not $restartAfterApply) {
    Write-Output 'Applied. The native driver loads after the next restart.'
}
exit $code
