# Intune / SCCM deployment

Thin deployment assets for fleet installs. The primary installer is the WiX MSI
(`packaging/wix/`); these files wire it into MDM tooling.

## Intune Win32 app

1. Wrap the MSI with `IntuneWinAppUtil.exe` to produce `NVMeDriverPatcher-<version>.intunewin`.
2. Upload to Intune → **Apps** → **Windows** → **Line-of-business app** (or Win32).
3. **Install command**: `msiexec /i NVMeDriverPatcher-<version>.msi /qn`
4. **Uninstall command**: `msiexec /x {9C2E3F01-6B91-4C1A-8F4D-09F7D8B4A3C2} /qn`
5. **Detection**: paste `Detect-NVMeDriverPatcher.ps1` into "Use a custom detection script."
6. **Requirements**: Windows 11 23H2 (10.0.22631) or later. x64 for full enablement; ARM64 portable builds are available for diagnostics/status only (until Microsoft ships an ARM64 `nvmedisk.sys`).
7. **Return codes**: `0` success, `1707` success, `1641`/`3010` success (restart required).

## Intune remediation

The Win32 app above installs the tool. To keep the patch applied across a fleet, add a remediation that uses the CLI the MSI installed:

1. In Intune, open **Devices** → **Scripts and remediations** → **Create**.
2. **Detection script file**: `Check-NVMeDriverPatcher.ps1`. **Remediation script file**: `Remediate-NVMeDriverPatcher.ps1`.
3. **Run this script using the logged-on credentials**: No. **Run script in 64-bit PowerShell**: Yes. Both scripts need SYSTEM rights.
4. Assign it to the same devices as the Win32 app, on whatever schedule suits you. Daily is plenty.

The check script reads `NVMeDriverPatcher.Cli.exe status --json` and exits 1 only when the patch is missing or partial on a Windows build where apply is allowed. A device that's already patched, or already running the native driver, exits 0. So does a build with no known enablement path or with stale build rules, and the reason shows up in the script output column, so those devices read as "nothing to fix" instead of failing remediation on every run.

The remediation script runs `apply --unattended --no-restart` and hands the CLI's exit code back to Intune. It never passes `--force` or `--force-unsupported-build`, so the CLI's own refusals still stand: a blocked build, a failed critical safety check, or recovery files that aren't in place all end the run with exit 1 and the CLI's reason in the output. The native driver loads at the device's next restart. If you'd rather restart right away, set `$restartAfterApply = $true` near the top of the remediation script before uploading it.

`Detect-NVMeDriverPatcher.ps1` stays the Win32 app's detection script. It only answers "is the tool installed", which is a different question from the remediation check's "is the patch in place".

## SCCM / MEMCM

Create an Application with:

- **Deployment type**: Windows Installer (MSI)
- **Install command**: `msiexec /i NVMeDriverPatcher-<version>.msi /qn ALLUSERS=1`
- **Uninstall**: auto-populated from MSI product code
- **Detection method**: `Product Code = {9C2E3F01-6B91-4C1A-8F4D-09F7D8B4A3C2}` OR
  `Detect-NVMeDriverPatcher.ps1` as a script-based detection clause

## Group Policy

ADMX + ADML live in `packaging/admx/`. Copy them into:

- `C:\Windows\PolicyDefinitions\` (or `\\domain\SYSVOL\<domain>\Policies\PolicyDefinitions\`)
- `en-US\` subfolder for the ADML

Policies land under `HKLM\SOFTWARE\Policies\SysAdminDoc\NVMeDriverPatcher` and override
per-user `config.json`.
