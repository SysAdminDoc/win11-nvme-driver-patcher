# NVMe Driver Patcher

## Overview
GUI + CLI tool to enable the experimental Windows Server 2025 Native NVMe driver (nvmedisk.sys) on Windows 11.

## Tech Stack
- PowerShell 5.1+, WinForms GUI
- Single monolithic script (~3500 lines): `NVMe_Driver_Patcher.ps1`
- `$script:` scope for cross-function state

## Current Version: v3.3.0

## Architecture
- Async preflight: `[PowerShell]::Create()` with `InitialSessionState` containing function definitions via `SessionStateFunctionEntry`, polled by `System.Windows.Forms.Timer`
- Background runspace gets no-op Write-Log and SilentMode=true Config to avoid UI thread references
- Results marshalled via hashtable: Checks, BuildDetails, CachedDrives, CachedHealth, DriverInfo, NativeNVMeStatus, BypassIOStatus
- `New-CardPanel` helper creates rounded dark panels; `Set-RoundedCorners` applies GraphicsPath Region
- GDI+ progress ring with custom Paint handler for arc drawing
- Dark/Light theme auto-detection from Windows registry

## Build / Run
```powershell
# GUI mode (auto-elevates)
.\NVMe_Driver_Patcher.ps1

# Silent mode
.\NVMe_Driver_Patcher.ps1 -Silent -Apply -NoRestart
.\NVMe_Driver_Patcher.ps1 -Silent -Status
.\NVMe_Driver_Patcher.ps1 -Silent -Remove
```

## Key Functions
- `Import-Configuration` / `Save-Configuration` - JSON config persistence
- `Invoke-PreflightChecks` - Async system scan (drives, BitLocker, drivers, build)
- `Install-NVMePatch` / `Uninstall-NVMePatch` - Core patch operations
- `Update-StatusDisplay` - Refreshes all UI elements to match current patch state
- `Test-PatchStatus` - Registry key verification
- `Export-SystemDiagnostics` - Full system report
- `New-VerificationScript` - Post-reboot check script generation

## Gotchas
- WinForms (not WPF) - different theming from MavenWinUtil
- No emoji/unicode in PowerShell (CLAUDE.md global rule)
- Auto-elevate via ShellExecute "runas"
- `$script:Config` hashtable mixes constants, prefs, and runtime state (tech debt)
- 5 confirmation dialogs before Apply in GUI mode (BitLocker, 3rd-party, build, DirectStorage, final)

## Version History
- v3.3.0: PSScriptAnalyzer verb fixes, version removed from filename, async preflight, AutoScroll, dark scrollbar, loading bar, footer fix, FormClosing cleanup
- v3.2.0: GDI region leak fix, resizable form, tray icon, progress ring, health badges, context menu, status pulse, before/after comparison
- v3.1.0: 12 code quality fixes (silent mode exit, toast timing, registry CRLF, CIM dedup, mutex fix)
- v3.0.0: Server 2025 key, nvmedisk.sys detection, BypassIO check, build detection
