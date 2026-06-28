# NVMe Driver Patcher — Roadmap

Living document — **incomplete work only**. Shipped items are deleted (git history + [CHANGELOG.md](CHANGELOG.md) are the record). Blocked items live in [Roadmap_Blocked.md](Roadmap_Blocked.md). Current ship: **v5.0.0**.

**Scope rule:** every item must improve the core function — enabling, disabling, verifying, or rolling back Microsoft's native NVMe driver swap on Windows 11. No external integrations, no general-purpose storage tools, no theme/UI-locale polish. If an idea drifts into "separate tool that happens to live in the same exe," it doesn't belong here. Priority is by user impact / regret cost, not effort; S/M/L/XL are rough effort estimates.

---

## Research-Driven Additions

### P2 — Safety and UX

### P2 — Safety and compat (research pass 4, 2026-06-20)

### P3 — Quality and distribution (research pass 4, 2026-06-20)

### P0 - Safety and truthfulness

- [ ] P0 - Sync public compatibility claims with build rules
  Why: `README.md` says 25H2 / 26200+ has full support while `windows_build_rules.json` marks 26200.8524+ and newer trains as no known enablement path.
  Evidence: `README.md`; `src/NVMeDriverPatcher.Core/windows_build_rules.json`; ViVe issue #164.
  Touches: `README.md`, `src/NVMeDriverPatcher.Core/Services/DocsService.cs`, `tests/NVMeDriverPatcher.Tests/DocsServiceTests.cs`, `tests/NVMeDriverPatcher.Tests/WindowsBuildRulesServiceTests.cs`.
  Acceptance: README and offline docs distinguish 24H2 registry/fallback paths, 25H2 pre-8524 fallback, 26200.8524+ verify/rollback-only, and 26300+ Feature Flags-page guidance; a test fails if README support text contradicts bundled build rules.
  Complexity: S

- [ ] P0 - Make native FeatureStore writer the default fallback path
  Why: The app already has tested in-process Rtl FeatureStore writes/resets, but GUI and CLI fallback still download ViVeTool first and may run with weak archive integrity.
  Evidence: `src/NVMeDriverPatcher.Core/Services/FeatureStoreWriterService.cs`; `src/NVMeDriverPatcher.Core/Services/ViVeToolService.cs`; `src/NVMeDriverPatcher/ViewModels/MainViewModel.Commands.cs`.
  Touches: `src/NVMeDriverPatcher/ViewModels/MainViewModel.Commands.cs`, `src/NVMeDriverPatcher.Cli/Program.cs`, `src/NVMeDriverPatcher.Core/Services/FeatureStoreWriterService.cs`, `tests/NVMeDriverPatcher.Tests/FeatureStoreWriterServiceTests.cs`.
  Acceptance: GUI "fallback" and CLI `fallback` first call `FeatureStoreWriterService.WriteOverrides` with both-store verification and no network; ViVeTool is offered only after native write failure, with the integrity level shown in logs/JSON.
  Complexity: M

### P1 - Recovery and supply-chain hardening

- [ ] P1 - Fix GitHub release sidecar lookup before CDN redirect
  Why: `VerifiedDownloader` checks `.sha256` at the final redirected asset URI, but release sidecars are sibling GitHub release assets; this can silently degrade updater verification to Authenticode only.
  Evidence: `src/NVMeDriverPatcher.Core/Services/VerifiedDownloader.cs`; `src/NVMeDriverPatcher.Core/Services/AutoUpdaterService.cs`; `packaging/release-artifacts.json`.
  Touches: `src/NVMeDriverPatcher.Core/Services/VerifiedDownloader.cs`, `src/NVMeDriverPatcher.Core/Services/AutoUpdaterService.cs`, `tests/NVMeDriverPatcher.Tests/VerifiedDownloaderTests.cs`, `tests/NVMeDriverPatcher.Tests/AutoUpdaterServiceTests.cs`.
  Acceptance: A redirect-chain test proves the downloader requests `<original-browser-download-url>.sha256` and accepts a matching sidecar before falling back to final-URI sidecar/Authenticode; README updater claims match observed verification.
  Complexity: M

- [ ] P1 - Detect and warn on custom-INF/test-signing native NVMe workarounds
  Why: The 26200.8524+ community workaround uses a test-signed custom INF and `pnputil /add-driver /install`, which changes driver-store state outside this tool's rollback model.
  Evidence: ViVe issue #164; Microsoft TESTSIGNING docs; Microsoft PnPUtil docs.
  Touches: `src/NVMeDriverPatcher.Core/Services/PreflightService.cs`, `src/NVMeDriverPatcher.Core/Services/PerControllerAuditService.cs`, `src/NVMeDriverPatcher.Core/Services/DiagnosticsService.cs`, `src/NVMeDriverPatcher.Core/Services/DocsService.cs`, `tests/NVMeDriverPatcher.Tests/PreflightServiceTests.cs`.
  Acceptance: Preflight/diagnostics flag BCD TESTSIGNING, non-Microsoft `nvmedisk`/`NvmeDisk` OEM INF bindings, and `SCSI\DiskNVMe____` custom matches; warning explains that the app will not automate the route and gives `pnputil /enum-drivers /files` evidence collection/removal guidance.
  Complexity: M

- [ ] P1 - Add guarded WinRE stornvme injection execution
  Why: The repo can preview DISM commands for injecting `stornvme.inf` into WinRE, but users still have to perform the risky recovery hardening manually.
  Evidence: `src/NVMeDriverPatcher.Core/Services/WinReDriverInjectionService.cs`; Microsoft DISM offline-driver docs; Microsoft REAgentC docs.
  Touches: `src/NVMeDriverPatcher.Core/Services/WinReDriverInjectionService.cs`, `src/NVMeDriverPatcher.Cli/Program.cs`, `src/NVMeDriverPatcher.Core/Services/RecoveryProofGateService.cs`, `tests/NVMeDriverPatcher.Tests/WinReDriverInjectionServiceTests.cs`.
  Acceptance: `winre-inject --apply` backs up `winre.wim`, mounts to an app-owned temp dir, adds `stornvme.inf`, commits or discards cleanly, runs DISM cleanup on failure, logs checksums, and leaves preview mode as the default.
  Complexity: L

- [ ] P1 - Add a fallback-active recovery gate before reboot
  Why: `RecoveryKitService` correctly states WinRE cannot reset FeatureStore fallback IDs, so fallback apply needs stronger proof and automatic reset behavior before the first reboot.
  Evidence: `src/NVMeDriverPatcher.Core/Services/RecoveryKitService.cs`; `src/NVMeDriverPatcher.Core/Services/FeatureStoreWriterService.cs`.
  Touches: `src/NVMeDriverPatcher.Core/Services/RecoveryProofGateService.cs`, `src/NVMeDriverPatcher.Core/Services/PatchVerificationService.cs`, `src/NVMeDriverPatcher.Core/Services/FeatureStoreWriterService.cs`, `src/NVMeDriverPatcher/ViewModels/MainViewModel.Commands.cs`, `src/NVMeDriverPatcher.Cli/Program.cs`.
  Acceptance: Fallback apply refuses or strongly blocks when recovery proof fails, records pending fallback state, and on the next successful boot automatically resets FeatureStore IDs when binding fails or watchdog severity crosses revert thresholds.
  Complexity: M

### P2 - Distribution and dependency hygiene

- [ ] P2 - Add ARM64 entries to package-manager manifests
  Why: Release artifacts now require signed ARM64 portable builds, but winget and Scoop manifests still publish only x64 GUI URLs.
  Evidence: `packaging/release-artifacts.json`; `packaging/winget/SysAdminDoc.NVMeDriverPatcher.yaml`; `packaging/scoop/nvme-driver-patcher.json`; winget and Scoop manifest docs.
  Touches: `packaging/winget/SysAdminDoc.NVMeDriverPatcher.yaml`, `packaging/scoop/nvme-driver-patcher.json`, `scripts/Build-ReleaseArtifacts.ps1`, `scripts/Update-PackageManifests.ps1`, `scripts/Validate-ReleaseAssets.ps1`, `tests/NVMeDriverPatcher.Tests/PackageManifestsScriptTests.cs`.
  Acceptance: Generated winget manifest has x64 and arm64 installers with matching hashes; Scoop manifest has `64bit` and `arm64` architecture blocks/autoupdate URLs; release validation fails on missing or stale ARM64 hashes.
  Complexity: M

### P3 - Dependency watch

- [ ] P3 - Add an explicit OpenTK/GLFW native-dependency watch gate
  Why: `dotnet list package --outdated --include-transitive` reports OpenTK/GLFW transitive updates through charting, while direct project code does not use OpenGL.
  Evidence: NuGet outdated scan; `src/NVMeDriverPatcher/NVMeDriverPatcher.csproj`; OpenTK NuGet/release pages.
  Touches: `src/NVMeDriverPatcher/NVMeDriverPatcher.csproj`, `tests/NVMeDriverPatcher.Tests/ChartingSmokeTests.cs`, `CLAUDE.md`.
  Acceptance: Dependency notes explain why OpenTK is transitive, charting smoke tests remain the upgrade gate, and any future direct pin/upgrade keeps SkiaSharp/OpenTK native assets ABI-compatible.
  Complexity: S
