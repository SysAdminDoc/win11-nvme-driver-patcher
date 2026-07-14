# NVMeDriverPatcher PowerShell module

Thin cmdlet wrapper around `NVMeDriverPatcher.Cli.exe` for fleet automation.

## Install (local)

```powershell
Import-Module .\packaging\powershell\NVMeDriverPatcher.psd1
```

## Install (PSGallery — once published)

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

- `Get-NvmePatchStatus`            — patch applied/partial/not-applied + raw CLI output
- `Invoke-NvmePatchApply`          — apply with profile + unattended/no-restart/force switches
- `Invoke-NvmePatchRemove`         — remove the patch
- `Get-NvmeWatchdogReport`         — verdict + raw output
- `Get-NvmeControllerAudit`        — bound driver plus read-only PnP candidate/rank evidence per controller
- `Get-NvmeRecoveryProof`          — recovery readiness proof (JSON)
- `Get-NvmeBypassIo`               — BypassIO status and gaming-impact guidance (JSON)
- `Get-NvmeFirmwareCompat`         — firmware/controller compatibility matches (JSON)
- `Get-NvmeFeatureStore`           — FeatureStore fallback state per feature ID (JSON)
- `Get-NvmeReliability`            — Reliability Monitor correlation (JSON)
- `Get-NvmeMinidump`               — minidump triage for NVMe-related crashes (JSON)
- `Invoke-NvmeDryRun`              — Markdown change preview
- `Export-NvmeDiagnostics`         — invoke the `diagnostics` CLI subcommand
- `Export-NvmeDashboard`           — invoke the `dashboard` HTML report

## Finding the CLI

The module looks for `NVMeDriverPatcher.Cli.exe` in this order:

1. Next to the module (`NVMeDriverPatcher.psm1` folder)
2. On `$PATH` via `Get-Command`

Ship the CLI exe alongside the module or rely on the winget / MSI install.
