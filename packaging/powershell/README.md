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
```

## Cmdlets

- `Get-NvmePatchStatus`            — patch applied/partial/not-applied + raw CLI output
- `Invoke-NvmePatchApply`          — apply with profile + unattended/no-restart/force switches
- `Invoke-NvmePatchRemove`         — remove the patch
- `Get-NvmeWatchdogReport`         — verdict + raw output
- `Get-NvmeControllerAudit`        — parsed per-controller list with IsNative flag
- `Invoke-NvmeDryRun`              — Markdown change preview
- `Export-NvmeDiagnostics`         — invoke the `diagnostics` CLI subcommand
- `Export-NvmeDashboard`           — invoke the `dashboard` HTML report

## Finding the CLI

The module looks for `NVMeDriverPatcher.Cli.exe` in this order:

1. Next to the module (`NVMeDriverPatcher.psm1` folder)
2. On `$PATH` via `Get-Command`

Ship the CLI exe alongside the module or rely on the winget / MSI install.
