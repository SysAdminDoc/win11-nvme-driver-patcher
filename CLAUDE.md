# NVMe Driver Patcher

## Overview
GUI + CLI tool to enable the experimental Windows Server 2025 Native NVMe driver (nvmedisk.sys) on Windows 11.

## Tech Stack
- PowerShell 5.1+, WinForms GUI
- Single monolithic script (~4040 lines): `NVMe_Driver_Patcher.ps1`
- `$script:` scope for cross-function state
- GitHub Actions CI/CD: `.github/workflows/release.yml`

## Current Version: v3.3.0

## Architecture
- Async preflight: `[PowerShell]::Create()` + `InitialSessionState` + Timer polling
- VeraCrypt = hard block (cannot override); BitLocker = auto-suspend
- Rollback on partial failure; consolidated single confirmation dialog
- DiskSpd benchmark: auto-downloads, runs 4K random r/w, JSON history, before/after delta
- Post-reboot detection: saves state to JSON, auto-verifies on next launch
- GitHub update check via releases API (5s timeout)
- SafeBoot GUID: `{75416E63-5912-4DFA-AE8F-3EFACCAFFB14}`

## Version History
- v3.3.0: Major update -- VeraCrypt block, BitLocker suspend, compat detection, DiskSpd benchmark, SMART tooltips, post-reboot verify, rollback, consolidated dialogs, update check, CI/CD, PSScriptAnalyzer fixes, async preflight
- v3.2.0: GDI leak fix, resizable form, tray icon, progress ring, health badges
- v3.1.0: 12 code quality fixes
- v3.0.0: Server 2025 key, nvmedisk.sys detection, BypassIO check

## Known Bugs (from audit)
- DiskSpd output parsing regex expects pipe-delimited "total:" line but DiskSpd uses space-delimited -- benchmarks return 0s
- DiskSpd blocks GUI for ~60s (runs synchronously on UI thread)
- CheckBox FlatStyle.Standard leaves white glyph on dark theme
- Several GDI Region objects not disposed on control recreation
- $response.body.Length in Test-UpdateAvailable can null-deref
- Benchmark JSON can corrupt if only 1 entry (PS 5.1 ConvertFrom-Json scalar issue)
- Locale-dependent decimal separator breaks DiskSpd number parsing
