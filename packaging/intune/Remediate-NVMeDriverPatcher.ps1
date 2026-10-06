#Requires -Version 5.1
<#
.SYNOPSIS
  Intune remediation script for NVMe Driver Patcher.

.DESCRIPTION
  Pair with Check-NVMeDriverPatcher.ps1 under Devices > Scripts and remediations. Refreshes the
  recovery kit, runs the CLI's unattended apply and passes its exit code back to Intune: 0 is
  success, anything else failed.

  It never passes --force or --force-unsupported-build. The CLI refuses apply on a Windows build
  with no known enablement path, on a failed critical safety check, and when its recovery checks
  don't pass (BitLocker recovery, System Restore on the system drive, SafeBoot entries that need
  upgrade-safeboot first). A fleet remediation honors all of them and prints the CLI's reason.
  The device isn't restarted unless $restartAfterApply below is set to $true. The native driver
  loads on the next restart.

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

function Invoke-Cli {
    param([string[]]$Arguments)
    # A CLI that never starts leaves $LASTEXITCODE alone, so clear it first or a stale 0 reads
    # as success.
    $global:LASTEXITCODE = $null
    # The CLI writes UTF-8 when its output is captured; decode it that way. Intune runs this
    # without a console, which can refuse the change, and then the output reads as before.
    $previousEncoding = $null
    try {
        $previousEncoding = [Console]::OutputEncoding
        [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
    } catch { $previousEncoding = $null }
    try {
        $text = (& $cli @Arguments) | Out-String
        $code = if ($null -eq $LASTEXITCODE) { 1 } else { $LASTEXITCODE }
    } catch {
        return @{ Code = 1; Text = "Couldn't run the CLI: $($_.Exception.Message)" }
    } finally {
        if ($null -ne $previousEncoding) { try { [Console]::OutputEncoding = $previousEncoding } catch { } }
    }
    return @{ Code = $code; Text = $text }
}

function Write-Tail {
    param([string]$Text)
    # Intune keeps about 2,048 characters of output, so keep the end, where the result is.
    @($Text -split "`r?`n" | Where-Object { $_.Trim() }) |
        Select-Object -Last 20 | ForEach-Object { Write-Output $_ }
}

# apply refuses to run without a recovery kit from the last 30 days, so refresh it first. It
# lands in the tool's working folder and replaces the previous kit.
$kit = Invoke-Cli @('recovery-kit')
if ($kit.Code -ne 0) {
    Write-Tail $kit.Text
    Write-Output "Couldn't create the recovery kit (exit $($kit.Code)), so apply wasn't attempted."
    exit 1
}
Write-Output 'Recovery kit refreshed.'

$cliArgs = @('apply', '--unattended')
if (-not $restartAfterApply) { $cliArgs += '--no-restart' }

$apply = Invoke-Cli $cliArgs
Write-Tail $apply.Text
if ($apply.Code -eq 0 -and -not $restartAfterApply) {
    Write-Output 'Applied. The native driver loads after the next restart.'
}
exit $apply.Code
