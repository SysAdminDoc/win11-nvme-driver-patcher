# NVMe Driver Patcher — Roadmap

Living document — **incomplete work only**. Shipped items are deleted (git history + [CHANGELOG.md](CHANGELOG.md) are the record). Blocked items live in [Roadmap_Blocked.md](Roadmap_Blocked.md). Current ship: **v5.0.0**.

**Scope rule:** every item must improve the core function — enabling, disabling, verifying, or rolling back Microsoft's native NVMe driver swap on Windows 11. No external integrations, no general-purpose storage tools, no theme/UI-locale polish. If an idea drifts into "separate tool that happens to live in the same exe," it doesn't belong here. Priority is by user impact / regret cost, not effort; S/M/L/XL are rough effort estimates.

---

## Research-Driven Additions

### P2 — Safety and UX

### P2 — Safety and compat (research pass 4, 2026-06-20)

### P3 — Quality and distribution (research pass 4, 2026-06-20)

### P1 - Recovery and supply-chain hardening

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

### P1 - Security and data safety

- [ ] P1 - Raise the bundled SQLite security floor or prove the compiled surface is safe
  Why: The app runs elevated and bundles native SQLite 3.50.4.5 while SQLite's CVE page now lists FTS5-related fixes in 3.53.2; the current regression test only pins the older 3.50.2 floor.
  Evidence: `src/NVMeDriverPatcher.Core/NVMeDriverPatcher.Core.csproj`; `tests/NVMeDriverPatcher.Tests/SqliteVersionTests.cs`; `https://sqlite.org/cves.html`; `https://github.com/ericsink/SQLitePCL.raw`.
  Touches: `src/NVMeDriverPatcher.Core/NVMeDriverPatcher.Core.csproj`, `tests/NVMeDriverPatcher.Tests/SqliteVersionTests.cs`, `src/NVMeDriverPatcher.Core/Data/AppDbContext.cs`, release validation notes.
  Acceptance: `sqlite_version()` is at least 3.53.2, or tests prove FTS5 is unavailable and SQLite defensive mode is enabled for every app DB connection; downgrade tests fail below the accepted floor and telemetry/support-bundle DB smoke tests still pass.
  Complexity: M

### P2 - Truth and distribution guardrails

- [ ] P2 - Add source provenance and recency gates to build-rule and compatibility data
  Why: Microsoft and community native-NVMe behavior changes by build train; `windows_build_rules.json` and `compat.json` need source URLs/review timestamps so public guidance cannot silently drift.
  Evidence: `src/NVMeDriverPatcher.Core/windows_build_rules.json`; `src/NVMeDriverPatcher.Core/compat.json`; ViVe issue #164; Microsoft native NVMe and DISM docs.
  Touches: `src/NVMeDriverPatcher.Core/windows_build_rules.json`, `src/NVMeDriverPatcher.Core/compat.json`, schema tests, `DocsService`, README compatibility table.
  Acceptance: Data schemas include source URL and last-reviewed fields where applicable; tests fail when required source metadata is missing or stale; CLI/diagnostics expose the matched rule source in JSON output.
  Complexity: M

- [ ] P2 - Refresh packaging/operator docs and add drift checks
  Why: Packaging docs still describe direct x64 staging and dependency-bot-era updates, the PowerShell README omits exported JSON cmdlets, and Intune docs still say x64-only despite ARM64 diagnostic artifacts.
  Evidence: `packaging/wix/README.md`; `packaging/powershell/README.md`; `packaging/powershell/NVMeDriverPatcher.psd1`; `packaging/intune/README.md`; `scripts/Build-ReleaseArtifacts.ps1`.
  Touches: `packaging/wix/README.md`, `packaging/powershell/README.md`, `packaging/intune/README.md`, README packaging section, package/readme drift tests.
  Acceptance: Existing packaging docs point operators to the local release builder, remove dependency-bot/CI language, document x64 versus ARM64 diagnostic channels accurately, and a test compares exported PowerShell cmdlets against the module README list.
  Complexity: M

### P3 - Test toolchain hygiene

- [ ] P3 - Refresh test infrastructure package pins without changing runtime packages
  Why: The local suite is the release trust gate, and `dotnet list package --outdated --include-transitive` shows newer Microsoft.NET.Test.Sdk, test platform, xUnit analyzer, and Newtonsoft.Json test dependencies.
  Evidence: local NuGet outdated scan; `tests/NVMeDriverPatcher.Tests/NVMeDriverPatcher.Tests.csproj`; `https://www.nuget.org/packages/Microsoft.NET.Test.Sdk/18.7.0`.
  Touches: `tests/NVMeDriverPatcher.Tests/NVMeDriverPatcher.Tests.csproj`, test runner configuration, package audit notes.
  Acceptance: Test-only package pins are current where compatible, the suite still runs locally on .NET 10, the xUnit v2 runner decision is preserved or explicitly migrated, and no runtime project package changes are included in the same commit.
  Complexity: S
