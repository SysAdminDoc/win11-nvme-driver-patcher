#Requires -Version 5.1
<#
.SYNOPSIS
  Intune Win32 detection script for NVMe Driver Patcher.

.DESCRIPTION
  Intune treats stdout + exit code 0 as "detected" when used as a custom detection script.
  We return both: print the exe path if present, exit 0; print nothing + exit 1 otherwise.

  Detection criteria (any of):
    - NVMeDriverPatcher.exe present in default install dir
    - registry key created by the MSI present under
      HKLM\SOFTWARE\SysAdminDoc\NVMeDriverPatcher (EventLogSourceRegistered=1)

.NOTES
  Paste this file into Intune's "Detection script" field for the Win32 app.
  https://learn.microsoft.com/mem/intune/apps/apps-win32-add
#>

$ErrorActionPreference = 'Stop'
$minVersion = [Version]'4.6.0'

$defaultExe = Join-Path ${env:ProgramFiles} 'NVMe Driver Patcher\NVMeDriverPatcher.exe'
$altExe     = Join-Path ${env:ProgramFiles(x86)} 'NVMe Driver Patcher\NVMeDriverPatcher.exe'
$regKey     = 'HKLM:\SOFTWARE\SysAdminDoc\NVMeDriverPatcher'

$exe = $null
if (Test-Path $defaultExe) { $exe = $defaultExe }
elseif (Test-Path $altExe) { $exe = $altExe }

if ($exe) {
    try {
        $fv = (Get-Item $exe).VersionInfo.FileVersion
        $parsed = [Version]::new()
        if ([Version]::TryParse($fv, [ref]$parsed) -and $parsed -ge $minVersion) {
            Write-Output "Detected: $exe (version $fv)"
            exit 0
        }
    } catch { }
}

if (Test-Path $regKey) {
    try {
        $flag = (Get-ItemProperty -Path $regKey -Name EventLogSourceRegistered -ErrorAction Stop).EventLogSourceRegistered
        if ($flag -eq 1) {
            Write-Output "Detected (registry): $regKey"
            exit 0
        }
    } catch { }
}

# Absent or too old. Exit non-zero with no stdout — Intune treats this as "not installed".
exit 1
