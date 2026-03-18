# NVMe Driver Patcher

## Overview
GUI + CLI tool to enable the experimental Windows Server 2025 Native NVMe driver (nvmedisk.sys) on Windows 11.

## Tech Stack
- PowerShell 5.1+, WinForms GUI
- Single monolithic script (~4040 lines): `NVMe_Driver_Patcher.ps1`
- `$script:` scope for cross-function state
- GitHub Actions CI/CD: `.github/workflows/release.yml` (tag push or workflow_dispatch)

## Current Version: v3.6.0

## Architecture
- Async preflight: `[PowerShell]::Create()` + `InitialSessionState` + Timer polling
- VeraCrypt = hard block (cannot override); BitLocker = auto-suspend
- Rollback on partial failure; consolidated single confirmation dialog
- GitHub update check via releases API (5s timeout)
- DiskSpd benchmark: auto-downloads from GitHub, runs 4K random r/w, saves JSON history, shows before/after delta
- Post-reboot detection: saves patch state to JSON, compares on next launch, shows verification if driver activated

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
- `Test-VeraCryptSystemEncryption` / `Get-IncompatibleSoftware` - Safety gates
- `Install-NVMePatch` / `Uninstall-NVMePatch` - Core ops with rollback + BitLocker suspend
- `Invoke-StorageBenchmark` / `Start-GUIBenchmark` - DiskSpd 4K random benchmark
- `Save-BenchmarkResults` / `Show-BenchmarkComparison` - JSON history + delta display
- `Test-PatchAppliedSinceLastRun` - Post-reboot verification
- `Test-UpdateAvailable` - GitHub version check

## Version History
- v3.6.0: DiskSpd benchmark, enhanced SMART tooltips, post-reboot verification, GitHub Actions CI/CD
- v3.5.0: VeraCrypt hard block, BitLocker auto-suspend, incompatible software detection, firmware display
- v3.4.0: GitHub update check, consolidated dialogs, rollback, skip warnings
- v3.3.0: PSScriptAnalyzer verb fixes, filename cleanup, async preflight
- v3.2.0: GDI leak fix, resizable form, tray icon, progress ring, health badges
