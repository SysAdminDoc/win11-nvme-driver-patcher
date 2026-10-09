# WiX v4 MSI build

Produces a per-machine MSI installer for NVMe Driver Patcher with four features:

- **Main**: GUI exe, CLI exe, the shipped data files (`compat.json`, `windows_build_rules.json`, `feature_ids.json`), icon, Start Menu shortcut. The exes also carry built-in copies of those files, so a bare exe download still has them.
- **TrayAgent**: non-admin status tray (drops `NVMeDriverPatcher.Tray.exe`)
- **AdmxTemplates**: ADMX + ADML into the install dir. To activate the policies, install them into the local policy store with the CLI (no manual copy needed):

  ```powershell
  # Install local machine templates (ADMX -> PolicyDefinitions, ADML -> PolicyDefinitions\<lang>)
  NVMeDriverPatcher.Cli policy-install

  # Domain admins: target the Central Store instead
  NVMeDriverPatcher.Cli policy-install --central-store="\\contoso.com\SYSVOL\contoso.com\Policies\PolicyDefinitions"

  # Remove them again (rollback)
  NVMeDriverPatcher.Cli policy-uninstall
  ```

  `policy-install` copies the bundled `admx\` templates beside the exe; pass `--source=<dir>` to point at a different template set. Both commands need an elevated shell. Central Store deployment makes the templates available to every Group Policy editor in the domain; local install only affects the current machine.
- **WatchdogService**: opt-in (Level 2, NOT installed by default): drops `NVMeDriverPatcher.Watchdog.exe` and registers/starts the `NVMeDriverPatcherWatchdog` service as **NT AUTHORITY\LocalService** (least privilege, matching the wxs `ServiceInstall` Account); removed cleanly on uninstall. The installer UI has no feature tree, so pick it on the command line. A fresh install takes `msiexec /i NVMeDriverPatcher.msi ADDLOCAL=ALL` (naming only `WatchdogService` there would leave out the app itself), and an existing install adds it with `ADDLOCAL=WatchdogService`.

The MSI installs only under Program Files. There's no folder picker, and an `INSTALLFOLDER` on the command line has to be a folder inside Program Files or the install stops with a message saying so. The install folder gets an admin-only DACL, but that protects the files, not the path to them. Outside Program Files a standard user can usually rename a parent folder, move the real one aside and put their own programs at the same path, and the elevated app, the SYSTEM custom action and the watchdog service would run them. `scripts/Test-InstallFolderAcl.ps1` checks both halves on a real install.

The installer's license/info page and product-facing strings come from `packaging\wix\License.rtf` (no placeholder text, see issue #12) and the `packaging\wix\en-US.wxl` string contract.

## Prereqs

WiX is pinned in the repo tool manifest (`.config/dotnet-tools.json`, currently 5.0.2, because WiX 7.x
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
.\scripts\Build-ReleaseArtifacts.ps1 -Version 5.1.0
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

# 3. Build the MSI (‑loc supplies the en-US string contract; License.rtf resolves beside the wxs)
wix build packaging\wix\NVMeDriverPatcher.wxs `
  -d PublishDir="$(Resolve-Path build\publish)" `
  -d ProjectRoot="$(Resolve-Path .)" `
  -loc packaging\wix\en-US.wxl `
  -ext WixToolset.UI.wixext `
  -ext WixToolset.Util.wixext `
  -out build\NVMeDriverPatcher-<version>.msi
```

## Signing

Not applicable. This project ships **unsigned**. Do not code-sign the MSI or its payload. If
SmartScreen warns, choose "More info → Run anyway"; installation does not require a signature.
