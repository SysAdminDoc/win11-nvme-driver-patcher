# WiX v4 MSI build

Produces a per-machine MSI installer for NVMe Driver Patcher with four features:

- **Main** — GUI exe, CLI exe, shipped `compat.json`, icon, Start Menu shortcut
- **TrayAgent** — non-admin status tray (drops `NVMeDriverPatcher.Tray.exe`)
- **AdmxTemplates** — ADMX + ADML into the install dir. To activate the policies, install them into the local policy store with the CLI (no manual copy needed):

  ```powershell
  # Install local machine templates (ADMX -> PolicyDefinitions, ADML -> PolicyDefinitions\<lang>)
  NVMeDriverPatcher.Cli policy-install

  # Domain admins: target the Central Store instead
  NVMeDriverPatcher.Cli policy-install --central-store="\\contoso.com\SYSVOL\contoso.com\Policies\PolicyDefinitions"

  # Remove them again (rollback)
  NVMeDriverPatcher.Cli policy-uninstall
  ```

  `policy-install` copies the bundled `admx\` templates beside the exe; pass `--source=<dir>` to point at a different template set. Both commands need an elevated shell. Central Store deployment makes the templates available to every Group Policy editor in the domain; local install only affects the current machine.
- **WatchdogService** — opt-in (Level 2, NOT installed by default): drops `NVMeDriverPatcher.Watchdog.exe` and registers/starts the `NVMeDriverPatcherWatchdog` LocalSystem service; removed cleanly on uninstall. Select via the installer feature tree or `msiexec /i NVMeDriverPatcher.msi ADDLOCAL=WatchdogService`

## Prereqs

WiX is pinned in the repo tool manifest (`.config/dotnet-tools.json`, currently 5.0.2 — WiX 7.x
requires Open Source Maintenance Fee acceptance). Restore it and add the matching extensions:

```powershell
dotnet tool restore
dotnet wix extension add WixToolset.UI.wixext/5.0.2
dotnet wix extension add WixToolset.Util.wixext/5.0.2
```

## Build

Use the local release builder (`scripts/Build-ReleaseArtifacts.ps1`) which publishes x64 and ARM64
binaries, builds the MSI, and generates checksums in a single pass:

```powershell
.\scripts\Build-ReleaseArtifacts.ps1 -Version 5.0.0
```

Or build the MSI manually:

```powershell
# 1. Publish the four projects to a staging folder
dotnet publish src\NVMeDriverPatcher\NVMeDriverPatcher.csproj -c Release -r win-x64 --self-contained -o build\publish
dotnet publish src\NVMeDriverPatcher.Cli\NVMeDriverPatcher.Cli.csproj -c Release -r win-x64 --self-contained -o build\publish
dotnet publish src\NVMeDriverPatcher.Tray\NVMeDriverPatcher.Tray.csproj -c Release -r win-x64 --self-contained -o build\publish
dotnet publish src\NVMeDriverPatcher.Watchdog\NVMeDriverPatcher.Watchdog.csproj -c Release -r win-x64 --self-contained -o build\publish

# 2. Copy the app icon next to the published exes (wxs references it there)
Copy-Item src\NVMeDriverPatcher\nvme.ico build\publish\icon.ico -Force

# 3. Build the MSI
wix build packaging\wix\NVMeDriverPatcher.wxs `
  -d PublishDir="$(Resolve-Path build\publish)" `
  -d ProjectRoot="$(Resolve-Path .)" `
  -ext WixToolset.UI.wixext `
  -ext WixToolset.Util.wixext `
  -out build\NVMeDriverPatcher-<version>.msi
```

## Signing

```powershell
# After building, sign with an EV cert (SmartScreen friendly) or a standard OV cert.
signtool sign /sha1 <cert thumbprint> /tr http://timestamp.sectigo.com /td sha256 /fd sha256 build\NVMeDriverPatcher-<version>.msi
```
