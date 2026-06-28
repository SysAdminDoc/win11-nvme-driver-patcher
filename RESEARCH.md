# Research - NVMe Driver Patcher

## Executive Summary
NVMe Driver Patcher v5.0.0 is a Windows 11 native-NVMe enablement and recovery product, not a generic storage tweaker. Its strongest current shape is the safety envelope around a risky unsupported client-driver path: build-rule awareness, registry and FeatureStore paths, recovery kits, watchdog/minidump/reliability evidence, package artifacts, opt-in telemetry, and broad local tests. Highest-value direction: keep every enablement route reversible, locally verifiable, and honest as Microsoft changes the client gate. Top opportunities in priority order: complete the existing P0 native FeatureStore-first fallback item; fix existing release sidecar lookup; detect custom-INF/test-signing workarounds; add the fallback-active recovery gate; raise or defensively prove the bundled SQLite native-library security floor; execute guarded WinRE driver injection; add ARM64 package-manager parity; add source provenance/recency gates for build rules and compatibility data; refresh packaging/operator docs with drift checks; keep low-risk test-toolchain updates current. Confidence: Verified unless labeled otherwise.

## Product Map
- Core workflows: scan readiness, choose Safe/Full/fallback route, apply registry or FeatureStore state, reboot, verify driver binding, monitor watchdog/reliability/minidumps, rollback, export recovery and support artifacts.
- User personas: Windows storage enthusiasts, workstation and homelab admins, fleet operators using CLI/PowerShell, support engineers diagnosing failed storage-stack swaps.
- Platforms and distribution: Windows 11 24H2/25H2 x64 enablement, Windows 11 ARM64 diagnostic/status builds until ARM64 `nvmedisk.sys` ships, Windows Server 2025 as the official native-NVMe reference, portable EXEs, MSI, winget/Scoop/Chocolatey manifests, PowerShell module ZIP.
- Key integrations and data flows: `windows_build_rules.json`, `compat.json`, `%ProgramData%` config/state, SQLite WAL DB, Windows Event Log, WMI/CIM storage classes, Rtl feature-configuration APIs, optional ViVeTool cache, DiskSpd cache, GitHub release assets and `.sha256` sidecars.

## Competitive Landscape
- ViVeTool: generic Windows Feature Store tool with active native-NVMe issue signal. Learn from its Rtl behavior and community IDs; avoid making a third-party binary the normal path when `FeatureStoreWriterService` exists locally.
- 1LUC1D4710N/nvme-performance-script and RedirectCorvin/Windows-native-NVMe-support-Enabler: simple script/batch approaches for registry or feature switches. Learn from inspectable artifacts; avoid their weak recovery, SafeBoot, proof, telemetry, and rollback coverage.
- GEAnalyticsLabs/native-nvme: small Python GUI emphasizing reversible registry edits, state, recovery assets, and resume. Learn from its restart/checkpoint framing; avoid reimplementing this repo's already richer .NET safety model.
- Broad Windows tweak tools (Winhance, WinUtil, Win11Debloat, Sophia Script): high adoption for admin convenience but little native-NVMe-specific depth, and Winhance rejected a native-NVMe request. Learn distribution clarity; avoid diluting the product into a tweak suite.
- Vendor SSD utilities (Samsung Magician, WD Dashboard, Crucial Storage Executive, Solidigm/SK hynix tools): own firmware and device-health workflows but may lose visibility or support assumptions under `nvmedisk.sys`. Learn to guide disable-update-reenable verification; avoid becoming a firmware updater.
- PnPUtil/DISM/DriverStore tools and docs: adjacent driver-management mechanisms explain custom-INF and WinRE paths. Learn detection, evidence collection, and rollback language; avoid automating test-signed production storage-driver packages.
- Package channels (winget, Scoop, Chocolatey, PowerShell Gallery, Intune): table-stakes for sysadmin adoption. Learn architecture-specific manifests and checksum discipline; avoid stale x64-only or old dependency-bot-era documentation after ARM64 artifacts ship.

## Security, Privacy, and Reliability
- Existing P0 remains right: GUI/CLI fallback should first use in-process `FeatureStoreWriterService` and only offer ViVeTool after native failure. Evidence: active `ROADMAP.md`, `src/NVMeDriverPatcher.Core/Services/FeatureStoreWriterService.cs`, `src/NVMeDriverPatcher.Core/Services/ViVeToolService.cs`.
- Updater trust needs the existing P1 fix: `VerifiedDownloader` must try the original GitHub release asset `.sha256` before any redirected CDN URI because release sidecars are sibling assets. Evidence: `src/NVMeDriverPatcher.Core/Services/VerifiedDownloader.cs`, `packaging/release-artifacts.json`.
- Custom-INF/test-signing detection belongs in preflight, not automation. ViVe issue #164 documents a 26200.8524+ custom INF route; Microsoft TESTSIGNING and PnPUtil docs confirm this changes kernel-mode driver/package state outside this tool's normal rollback model.
- SQLite native-library posture needs a new guard. The repo pins `SQLitePCLRaw.bundle_e_sqlite3` 3.0.3 / `SourceGear.sqlite3` 3.50.4.5 and tests only the CVE-2025-6965 floor of 3.50.2; SQLite's CVE page now lists FTS5-related fixes in 3.53.2. The app does not expose arbitrary SQL input, but it runs elevated and ships a bundled native SQLite library, so the security floor should move or be explicitly proved safe.
- Recovery limitation is accurately documented: registry recovery kits cannot reset FeatureStore/ViVeTool fallback state offline. The existing fallback-active gate and guarded WinRE injection items are the right next reliability moves.
- Privacy posture is sound: compatibility telemetry is opt-in, schema-validated, anonymized, GPO-controllable, and not backed by a default hosted service. Keep telemetry advisory until reviewed; do not auto-update `compat.json` from raw submissions.
- Package and docs drift is now the biggest non-runtime trust issue: `packaging/wix/README.md` still describes direct x64 staging and dependency-bot PRs; `packaging/powershell/README.md` omits six exported JSON cmdlets; `packaging/intune/README.md` still says x64-only requirements while release scripts build ARM64 diagnostic assets.

## Architecture Assessment
- The Core boundary is good: build rules, compatibility, recovery, FeatureStore, downloads, package validation, and tests live outside WPF. Keep new behavior in Core and keep GUI/CLI as thin presentation layers.
- `FeatureStoreWriterService` is the correct root abstraction for fallback: it already verifies both Persistent and Boot stores and rolls back Persistent state if Boot store write fails.
- `RecoveryKitService` should stay registry-only; `WinReDriverInjectionService` is the right separate boundary for DISM/WinRE execution with preview as default.
- `windows_build_rules.json` and `compat.json` are product-critical data files. They should carry source URLs/review timestamps and have tests that fail when evidence age or schema drift makes public guidance stale.
- Package scripts are centralized enough to add guards: `scripts/Build-ReleaseArtifacts.ps1`, `scripts/Update-PackageManifests.ps1`, `scripts/Validate-ReleaseAssets.ps1`, and package tests can enforce manifest/docs parity without adding CI.
- UI accessibility is not a current top risk: the WPF app has light/dark/high-contrast dictionaries, AutomationProperties, polite/assertive live regions, tooltips, and chart smoke tests. Keep focused testing, but do not spend roadmap capacity on broad theme/i18n/mobile/plugin work without concrete failures.
- Test coverage is broad; net-new tests should target sidecar URL selection, native fallback defaulting, custom-INF detection, fallback recovery gating, SQLite native version/compile options, data-source recency, and package-doc drift.

## Rejected Ideas
- Automate custom-INF/test-signing enablement. Source: ViVe issue #164, Microsoft TESTSIGNING, PnPUtil. Reason: installs nonstandard kernel storage-driver packages and can require Secure Boot/test-signing changes outside the rollback model.
- Bundle ViVeTool as a shipped asset. Source: ViVeTool and current `ViVeToolService`. Reason: increases third-party binary responsibility when the native Rtl path is already implemented.
- Auto-promote raw compatibility telemetry into `compat.json`. Source: telemetry receiver and `compat.json`. Reason: unsigned community data must remain reviewed advisory input.
- Full SSD firmware updater. Source: Samsung/WD/Crucial/Solidigm tools. Reason: vendor utilities own firmware delivery; this product should guide verification around driver swaps.
- General driver-store cleanup UI. Source: DriverStoreExplorer/PnPUtil. Reason: useful but not required to enable, verify, or roll back native NVMe.
- Broad benchmark lab suite. Source: StorageReview and DiskSpd patterns. Reason: built-in before/after evidence is enough for this product; extensive benchmarking would distract from recovery truth.
- Full i18n/l10n, mobile app, plugin ecosystem, and multi-user server mode. Source: local product model. Reason: local elevated storage-stack changes benefit more from deterministic CLI/GUI/recovery artifacts than new surfaces.
- NVMe-oF management. Source: Windows Server NVMe material and `compat.json` advisory. Reason: separate server/fabric capability, not the Windows 11 PCIe client driver swap.

## Sources
Official platform and Windows:
- https://techcommunity.microsoft.com/blog/windowsservernewsandbestpractices/announcing-native-nvme-in-windows-server-2025-ushering-in-a-new-era-of-storage-p/4477353
- https://learn.microsoft.com/en-us/windows-hardware/drivers/ifs/bypassio
- https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/reagentc-command-line-options?view=windows-11
- https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/add-and-remove-drivers-to-an-offline-windows-image?view=windows-11
- https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/dism-driver-servicing-command-line-options-s14?view=windows-11
- https://learn.microsoft.com/en-us/windows-hardware/drivers/install/the-testsigning-boot-configuration-option
- https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/pnputil-command-syntax

Native NVMe and community signal:
- https://github.com/thebookisclosed/ViVe
- https://github.com/thebookisclosed/ViVe/issues/164
- https://github.com/1LUC1D4710N/nvme-performance-script
- https://github.com/GEAnalyticsLabs/native-nvme
- https://github.com/RedirectCorvin/Windows-native-NVMe-support-Enabler
- https://github.com/memstechtips/Winhance/issues/495
- https://www.tomshardware.com/software/windows/microsoft-blocks-the-registry-hack-trick-that-unlocked-native-nvme-performance-on-windows-11
- https://hothardware.com/news/microsoft-kills-nvme-registry-trick-but-theres-a-workaround
- https://www.windowscentral.com/microsoft/windows-11/microsoft-shuts-down-windows-11-registry-hack-native-nvme-ssd-support
- https://www.elevenforum.com/t/windows-11-25h2-nvmedisk-sys-driver-support.46678/page-2
- https://forum.level1techs.com/t/microsoft-nvme-driver-update-lulz/243399

Distribution and dependencies:
- https://learn.microsoft.com/en-us/windows/package-manager/package/manifest
- https://github.com/microsoft/winget-pkgs/blob/master/doc/manifest/schema/1.10.0/installer.md
- https://github.com/ScoopInstaller/Scoop/wiki/App-Manifests
- https://docs.chocolatey.org/en-us/create/functions/install-chocolateypackage/
- https://learn.microsoft.com/en-us/intune/intune-service/apps/apps-win32-add
- https://learn.microsoft.com/en-us/powershell/module/powershellget/publish-module?view=powershellget-2.x
- https://sqlite.org/cves.html
- https://sqlite.org/releaselog/3_53_2.html
- https://github.com/ericsink/SQLitePCL.raw
- https://www.nuget.org/packages/Microsoft.NET.Test.Sdk/18.7.0

## Open Questions
- Needs live validation: On Windows 11 26200.8524+ systems with a custom-INF workaround already installed, what exact PnP/driver-store evidence distinguishes that state from any future official Microsoft binding?
- Needs live validation: Does in-process `FeatureStoreWriterService.WriteOverrides` match ViVeTool's successful binding rate on 24H2 post-block and 25H2 pre-8524 machines?
- Needs live validation: Can a DISM-applied `stornvme.inf` inside a real machine WinRE image boot and see the system volume on hardware that fails after native NVMe enablement?
