# WiX v4 MSI build

Produces a per-machine MSI installer for NVMe Driver Patcher with three features:

- **Main** — GUI exe, CLI exe, shipped `compat.json`, icon, Start Menu shortcut
- **TrayAgent** — non-admin status tray (drops `NVMeDriverPatcher.Tray.exe`)
- **AdmxTemplates** — ADMX + ADML into the install dir (manual copy to `C:\Windows\PolicyDefinitions` is still required — see ADMX docs)

## Prereqs

```powershell
dotnet tool install --global wix
wix extension add WixToolset.UI.wixext
wix extension add WixToolset.Util.wixext
```

## Build

```powershell
# 1. Publish the three projects to a staging folder
dotnet publish src\NVMeDriverPatcher\NVMeDriverPatcher.csproj -c Release -r win-x64 --self-contained -o build\publish
dotnet publish src\NVMeDriverPatcher.Cli\NVMeDriverPatcher.Cli.csproj -c Release -r win-x64 --self-contained -o build\publish
dotnet publish src\NVMeDriverPatcher.Tray\NVMeDriverPatcher.Tray.csproj -c Release -r win-x64 --self-contained -o build\publish

# 2. Copy the icon next to the published exes (wxs references it there)
Copy-Item icon.ico build\publish\ -Force

# 3. Build the MSI
wix build packaging\wix\NVMeDriverPatcher.wxs `
  -d PublishDir="$(Resolve-Path build\publish)" `
  -d ProjectRoot="$(Resolve-Path .)" `
  -ext WixToolset.UI.wixext `
  -ext WixToolset.Util.wixext `
  -out build\NVMeDriverPatcher-4.5.0.msi
```

## Signing

```powershell
# After building, sign with an EV cert (SmartScreen friendly) or a standard OV cert.
signtool sign /sha1 <cert thumbprint> /tr http://timestamp.sectigo.com /td sha256 /fd sha256 build\NVMeDriverPatcher-4.5.0.msi
```
