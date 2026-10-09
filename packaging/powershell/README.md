# NVMeDriverPatcher PowerShell module

A thin cmdlet wrapper around `NVMeDriverPatcher.Cli.exe` for fleet automation.

## Install (local)

```powershell
Import-Module .\packaging\powershell\NVMeDriverPatcher.psd1
```

## Install (PSGallery, once it's published)

```powershell
Install-Module -Name NVMeDriverPatcher -Scope CurrentUser
```

## Example

```powershell
Get-NvmePatchStatus
Get-NvmeControllerAudit | Where-Object { -not $_.IsNative } | Select-Object Name, Driver
Invoke-NvmePatchApply -Profile Safe -Unattended -NoRestart
Get-NvmeWatchdogReport
Get-NvmeFirmwareCompat | Where-Object Level -eq 'Bad'
```

## Cmdlets

- `Get-NvmePatchStatus` says whether the patch is applied, partial or not applied, and includes the raw CLI output.
- `Invoke-NvmePatchApply` applies a profile. Add the unattended, no-restart or force switches as needed.
- `Invoke-NvmePatchRemove` removes the patch.
- `Get-NvmeWatchdogReport` gives the watchdog verdict plus raw output.
- `Get-NvmeControllerAudit` shows the bound driver for each controller, with read-only PnP candidate and rank evidence.
- `Get-NvmeRecoveryProof` returns recovery readiness proof (JSON).
- `Get-NvmeBypassIo` returns BypassIO status and what it means for gaming (JSON).
- `Get-NvmeFirmwareCompat` lists firmware and controller compatibility matches (JSON).
- `Get-NvmeFeatureStore` shows the FeatureStore fallback state per feature ID (JSON).
- `Get-NvmeReliability` correlates Reliability Monitor events (JSON).
- `Get-NvmeMinidump` triages minidumps from NVMe-related crashes (JSON).
- `Invoke-NvmeDryRun` previews the changes as Markdown.
- `Export-NvmeDiagnostics` runs the `diagnostics` CLI subcommand.
- `Export-NvmeDashboard` builds the `dashboard` HTML report.

## Finding the CLI

The module looks for `NVMeDriverPatcher.Cli.exe` in this order:

1. Next to the module (the `NVMeDriverPatcher.psm1` folder)
2. On `$PATH` via `Get-Command`

Ship the CLI exe alongside the module, or rely on the MSI install.
