# Intune / SCCM deployment

Thin deployment assets for fleet installs. The primary installer is the WiX MSI
(`packaging/wix/`); these files wire it into MDM tooling.

## Intune Win32 app

1. Wrap the MSI with `IntuneWinAppUtil.exe` to produce `NVMeDriverPatcher-4.x.y.intunewin`.
2. Upload to Intune → **Apps** → **Windows** → **Line-of-business app** (or Win32).
3. **Install command**: `msiexec /i NVMeDriverPatcher-4.x.y.msi /qn`
4. **Uninstall command**: `msiexec /x {9C2E3F01-6B91-4C1A-8F4D-09F7D8B4A3C2} /qn`
5. **Detection**: paste `Detect-NVMeDriverPatcher.ps1` into "Use a custom detection script."
6. **Requirements**: Windows 11 23H2 (10.0.22631) or later, x64.
7. **Return codes**: `0` success, `1707` success, `1641`/`3010` success (restart required).

## SCCM / MEMCM

Create an Application with:

- **Deployment type**: Windows Installer (MSI)
- **Install command**: `msiexec /i NVMeDriverPatcher-4.x.y.msi /qn ALLUSERS=1`
- **Uninstall**: auto-populated from MSI product code
- **Detection method**: `Product Code = {9C2E3F01-6B91-4C1A-8F4D-09F7D8B4A3C2}` OR
  `Detect-NVMeDriverPatcher.ps1` as a script-based detection clause

## Group Policy

ADMX + ADML live in `packaging/admx/`. Copy them into:

- `C:\Windows\PolicyDefinitions\` (or `\\domain\SYSVOL\<domain>\Policies\PolicyDefinitions\`)
- `en-US\` subfolder for the ADML

Policies land under `HKLM\SOFTWARE\Policies\SysAdminDoc\NVMeDriverPatcher` and override
per-user `config.json`.
