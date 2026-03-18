# NVMe Driver Patcher

## Overview
GUI + CLI tool to enable the experimental Windows Server 2025 Native NVMe driver (nvmedisk.sys) on Windows 11.

## Tech Stack
- PowerShell 5.1+, WinForms GUI
- Single monolithic script (~3800 lines): `NVMe_Driver_Patcher.ps1`
- `$script:` scope for cross-function state

## Current Version: v3.5.0

## Architecture
- Async preflight: `[PowerShell]::Create()` with `InitialSessionState` + `SessionStateFunctionEntry`, polled by `System.Windows.Forms.Timer`
- Background runspace gets no-op Write-Log and SilentMode=true to avoid UI thread refs
- Results marshalled via hashtable
- Rollback on partial failure undoes all applied registry keys
- GitHub update check via releases API (5s timeout, best-effort, non-blocking)
- Single consolidated confirmation dialog replaces old 5-dialog chain
- VeraCrypt detection is a hard block (cannot be overridden, even with -Force)
- BitLocker auto-suspension via Suspend-BitLocker before patching

## Build / Run
```powershell
.\NVMe_Driver_Patcher.ps1                          # GUI mode (auto-elevates)
.\NVMe_Driver_Patcher.ps1 -Silent -Apply -NoRestart # Silent mode
.\NVMe_Driver_Patcher.ps1 -Silent -Status           # Check status
.\NVMe_Driver_Patcher.ps1 -Silent -Remove            # Remove patch
```

## Key Functions
- `Import-Configuration` / `Save-Configuration` - JSON config persistence
- `Invoke-PreflightChecks` - 10 async system checks
- `Test-VeraCryptSystemEncryption` - Hard block detection
- `Get-IncompatibleSoftware` - Acronis, Macrium, VirtualBox, VeraCrypt detection
- `Install-NVMePatch` / `Uninstall-NVMePatch` - Core patch ops with rollback + BitLocker suspend
- `Update-StatusDisplay` - Refreshes all UI elements
- `Test-UpdateAvailable` - GitHub releases API version check
- `Show-ConfirmDialog` - Single consolidated confirmation with all warnings
- `Export-SystemDiagnostics` / `New-VerificationScript` - Reporting

## Gotchas
- WinForms (not WPF) - different theming from MavenWinUtil
- No emoji/unicode in PowerShell (CLAUDE.md global rule)
- SafeBoot GUID: `{75416E63-5912-4DFA-AE8F-3EFACCAFFB14}` (was wrong in README before v3.4.0)
- `$script:Config` hashtable mixes constants, prefs, and runtime state (tech debt)

## Version History
- v3.5.0: VeraCrypt hard block, incompatible software detection, BitLocker auto-suspend, firmware display, updated benchmarks/README with sources
- v3.4.0: GitHub update check, consolidated dialogs, rollback, skip warnings, SafeBoot GUID fix
- v3.3.0: PSScriptAnalyzer verb fixes, filename cleanup, async preflight, AutoScroll
- v3.2.0: GDI leak fix, resizable form, tray icon, progress ring, health badges
- v3.1.0: 12 code quality fixes
- v3.0.0: Server 2025 key, nvmedisk.sys detection, BypassIO check
