# Research - NVMe Driver Patcher

## Executive Summary
NVMe Driver Patcher v5.0.0 is already the most complete Windows 11 native-NVMe enablement tool found: it has GUI, CLI, tray/watchdog, recovery kit, compat DB, build-rule intelligence, telemetry, packaging, and broad tests. The highest-value direction is not feature sprawl; it is making every enablement path reversible, evidence-backed, and honest as Microsoft changes the client driver gate. Priority opportunities: 1) sync public docs with `windows_build_rules.json` so 26200.8524+ is not advertised as fully supported; 2) make the in-process `FeatureStoreWriterService` the default fallback instead of downloading ViVeTool; 3) harden update sidecar lookup so GitHub release `.sha256` assets are checked before CDN redirects; 4) warn against custom-INF/test-signing workarounds now circulating for 26200+; 5) add ARM64 package-manager manifest entries to match shipped ARM64 assets; 6) turn the WinRE stornvme injection plan into a guarded executable workflow; 7) keep OpenTK/GLFW transitive updates on watch, with no urgent bump while Skia chart smoke coverage is green. Confidence: Verified unless labeled otherwise.

## Product Map
- Core workflows: preflight safety scan, apply Safe/Full registry profile, apply FeatureStore/ViVeTool fallback when registry overrides are blocked, reboot, verify driver binding, monitor watchdog/reliability/minidumps, roll back, export recovery kit/support bundle.
- User personas: storage enthusiasts testing performance, homelab and workstation admins, fleet operators needing scriptable status, recovery-focused troubleshooters after a failed storage-stack change.
- Platforms and distribution: Windows 11 24H2/25H2 x64 for enablement, Windows 11 ARM64 diagnostic/status builds, Windows Server 2025 reference behavior, portable EXEs, MSI, winget/Scoop/Chocolatey manifests, PowerShell module ZIP, deprecated PS1.
- Key integrations and data flows: `windows_build_rules.json`, `compat.json`, `config.json`, `watchdog.json`, SQLite WAL DB, Windows Event Log/System channel, WMI/CIM storage classes, Rtl feature-configuration APIs, ViVeTool cache, DiskSpd cache, GitHub releases and `.sha256` sidecars.

## Competitive Landscape
- ViVeTool: generic hidden-feature toggle with the community NVMe fallback IDs and issue #164 documenting the 26200.8524+ `GenNvmeDisk` failure. Learn from its Rtl feature-configuration behavior; avoid making a third-party download the first-choice path when this repo already has `FeatureStoreWriterService`.
- 1LUC1D4710N/nvme-performance-script: small registry-only PowerShell helper. Learn from simple inspectable registry artifacts; avoid its missing SafeBoot, BitLocker, post-reboot verification, fallback, and recovery coverage.
- Winhance, WinUtil, Win11Debloat, Sophia-Script: broad Windows tweak tools that do not handle native NVMe, and Winhance rejected an NVMe request. Learn that this patch deserves a narrow, safety-first product instead of a general tweak suite.
- Vendor SSD tools: Samsung Magician, WD Dashboard, Crucial Storage Executive, Solidigm/SK hynix tools own firmware delivery but can lose visibility under `nvmedisk.sys`. Learn to guide disable-update-reenable flows; avoid becoming a firmware updater.
- PnPUtil/DISM/DriverStore workflows: adjacent driver-management tools and docs explain the custom-INF route. Learn detection and recovery diagnostics; avoid automating test-signed storage-driver packages on production systems.
- Windows Package Manager/Scoop/Chocolatey/PowerShell Gallery: distribution channels fit sysadmin adoption. Learn architecture-specific manifests and checksum discipline; avoid publishing x64-only package metadata after shipping ARM64 assets.
- StorageReview/Tom's Hardware/PCWorld/HotHardware coverage: validates performance opportunity and compatibility risks. Learn to keep benchmark and BypassIO warnings concrete; avoid promising gains on unsupported builds or gaming/DirectStorage workloads.

## Security, Privacy, and Reliability
- README compatibility bug: `README.md` claims `25H2 | 26200+ | Full support, best performance`, while `src/NVMeDriverPatcher.Core/windows_build_rules.json` marks 26200.8524+ and newer trains as `none-known`. This is user-risking misinformation because a user may apply changes on a build the code already knows cannot bind.
- Fallback supply-chain risk: `MainViewModel.Commands.cs` and `Program.cs` still route the normal `fallback` UX through `ViVeToolService`, which accepts upstream ViVeTool zips with "weak" integrity when no `.sha256` exists. `FeatureStoreWriterService` already writes and resets the same Rtl feature state in-process with tests.
- Updater sidecar bug: `VerifiedDownloader.DownloadAsync` follows redirects, then calls `TryFetchSidecarHashAsync` on the final CDN URI. GitHub release sidecars are sibling release assets such as `NVMeDriverPatcher.exe.sha256`, not guaranteed to exist at the signed CDN redirect path. Authenticode fallback keeps EXE staging safer, but the README's SHA-256 claim can silently degrade.
- Custom-INF hazard: ViVeTool issue #164 documents a test-signed custom INF using `SCSI\DiskNVMe____` plus `pnputil /add-driver /install` to force binding on 26200.8524+. Microsoft docs confirm TESTSIGNING is for loading test-signed kernel-mode code and PnPUtil installs/removes driver packages. This repo should detect and warn, not automate it.
- Recovery limitation: `RecoveryKitService` correctly tells users the WinRE kit cannot undo FeatureStore/ViVeTool fallback state because Rtl reset needs a running Windows kernel. That makes the fallback path higher risk than registry-only apply and argues for stronger preflight and automatic reset behavior around fallback.
- Package-channel mismatch: `packaging/release-artifacts.json` and build scripts require `*-win-arm64.exe` assets, but winget/Scoop manifests and validators only model x64 GUI downloads. ARM64 users may get emulated x64 assets from package managers despite native diagnostic builds existing.
- Dependency state: `dotnet list package --vulnerable --include-transitive` is clean. Direct SkiaSharp pins are current at 4.148.0 with libpng 1.6.58. Outdated scan shows only transitive OpenTK/GLFW packages from the LiveCharts/Skia chart stack; no direct project use found, so this is a watch item rather than a forced upgrade.
- Privacy posture remains strong: compat telemetry is opt-in and anonymized, the Cloudflare Worker reference validates schema version/anonId and avoids storing user-agent headers. Continue to keep telemetry as a user-supplied endpoint, not a default service.

## Architecture Assessment
- The Core split is working: safety, recovery, FeatureStore, build rules, package validation, and tests live outside WPF. Keep new logic in Core and keep GUI/CLI thin.
- `FeatureStoreWriterService` is mature enough to promote: it has read/write/reset paths, both-store verification, rollback on Boot-store write failure, and tests for ViVeTool-equivalent enable/reset payloads.
- `VerifiedDownloader` is the right abstraction but needs redirect-aware sidecar tests: preserve per-hop host/scheme validation while resolving sidecars from the original release asset URL as well as any final URL.
- `RecoveryKitService` is intentionally registry-only. The next practical recovery improvement is the existing `WinReDriverInjectionService` plan: Microsoft supports adding INF drivers to offline Windows/WinPE images with DISM, so a guarded `--apply` flow can make WinRE more likely to see NVMe storage after a bad driver switch.
- Package scripts are centralized but x64-biased: `scripts/Build-ReleaseArtifacts.ps1`, `scripts/Update-PackageManifests.ps1`, `scripts/Validate-ReleaseAssets.ps1`, and `PackageManifestsScriptTests.cs` need ARM64 manifest/hash awareness.
- Test coverage is broad; add focused tests for README/build-rule compatibility strings, updater sidecar URL selection, native fallback default flow, test-signing/custom-INF detection, and ARM64 package manifest generation.

## Rejected Ideas
- Automate custom-INF/test-signing enablement. Source: ViVe issue #164 plus Microsoft TESTSIGNING/PnPUtil docs. Reason: it installs a test-signed storage driver package, can require Secure Boot changes, and is harder to recover than feature flags.
- Ship ViVeTool bundled in this repo. Source: current `ViVeToolService` comments and supply-chain model. Reason: it makes this project responsible for a third-party binary it does not sign.
- Auto-update `compat.json` directly from crowdsourced telemetry. Source: existing telemetry receiver and data-file provenance. Reason: unsigned community data must remain advisory until reviewed.
- Full SSD firmware updater. Source: Samsung/WD/Crucial/Solidigm tool landscape. Reason: vendor tools own firmware packages; this project should disable, guide, re-enable, and verify.
- General driver-store cleanup UI. Source: PnPUtil/DriverStoreExplorer class of tools. Reason: not tied to enabling, disabling, verifying, or rolling back native NVMe.
- Broad benchmark suite. Source: DiskSpd and StorageReview benchmark patterns. Reason: built-in before/after DiskSpd is enough to validate this specific patch.
- Broad UI accessibility/i18n/theme polish. Source: local UI/test scan. Reason: no current evidence of failures affecting enable, verify, or rollback; keep existing accessibility smoke coverage as the gate.
- Mobile app, plugin ecosystem, and multi-user server mode. Source: product shape and security model. Reason: local admin storage-stack recovery does not benefit enough to justify new trust surfaces.
- NVMe-oF management. Source: Microsoft Server Insider NVMe-oF material. Reason: separate server/fabric stack, not the local PCIe client driver swap.

## Sources
Official platform and Windows:
- https://techcommunity.microsoft.com/blog/windowsservernewsandbestpractices/announcing-native-nvme-in-windows-server-2025-ushering-in-a-new-era-of-storage-p/4477353
- https://learn.microsoft.com/en-us/windows-hardware/drivers/storage/bypassio
- https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/reagentc-command-line-options?view=windows-11
- https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/add-and-remove-drivers-to-an-offline-windows-image?view=windows-11
- https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/dism-driver-servicing-command-line-options-s14?view=windows-11
- https://learn.microsoft.com/en-us/windows-hardware/drivers/install/the-testsigning-boot-configuration-option
- https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/pnputil-command-syntax

Native NVMe and community signal:
- https://github.com/thebookisclosed/ViVe/issues/164
- https://www.tomshardware.com/software/windows/microsoft-blocks-the-registry-hack-trick-that-unlocked-native-nvme-performance-on-windows-11
- https://hothardware.com/news/microsoft-kills-nvme-registry-trick-but-theres-a-workaround
- https://www.windowscentral.com/microsoft/windows-11/microsoft-shuts-down-windows-11-registry-hack-native-nvme-ssd-support
- https://www.elevenforum.com/t/windows-11-25h2-nvmedisk-sys-driver-support.46678/page-2
- https://forum.level1techs.com/t/microsoft-nvme-driver-update-lulz/243399
- https://winraid.level1techs.com/c/system-performance/ahci-nvme-performance/18
- https://www.reddit.com/r/Windows11/comments/1ptq4w1/have_you_tried_win11s_25h2s_new_nvme_driver_yet/

Competitors and adjacent tools:
- https://github.com/thebookisclosed/ViVe
- https://github.com/1LUC1D4710N/nvme-performance-script
- https://github.com/memstechtips/Winhance/issues/495
- https://github.com/ChrisTitusTech/winutil

Distribution and dependencies:
- https://github.com/microsoft/winget-pkgs/blob/master/doc/manifest/schema/1.5.0/installer.md
- https://github.com/ScoopInstaller/Scoop/wiki/App-Manifests
- https://docs.chocolatey.org/en-us/create/functions/install-chocolateypackage/
- https://learn.microsoft.com/en-us/powershell/module/powershellget/publish-module?view=powershellget-2.x
- https://devblogs.microsoft.com/dotnet/dotnet-and-dotnet-framework-june-2026-servicing-updates/
- https://github.com/ericsink/SQLitePCL.raw/issues/662
- https://github.com/mono/SkiaSharp/releases
- https://www.nuget.org/packages/opentk/

## Open Questions
- Needs live validation: On build 26200.8524+ with a custom-INF workaround already installed, what exact PnP/driver-store evidence distinguishes it from official future Microsoft binding?
- Needs live validation: Does in-process `FeatureStoreWriterService.WriteOverrides` produce the same successful binding rate as ViVeTool on 24H2 post-block and 25H2 pre-8524 machines?
- Needs live validation: Can a DISM-applied `stornvme.inf` inside the machine's real WinRE image boot and see the system volume on systems that fail after native NVMe enablement?
