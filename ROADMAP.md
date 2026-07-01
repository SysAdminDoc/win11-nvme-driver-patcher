# NVMe Driver Patcher — Roadmap

Living document — **incomplete work only**. Shipped items are deleted (git history + [CHANGELOG.md](CHANGELOG.md) are the record). Blocked items live in [Roadmap_Blocked.md](Roadmap_Blocked.md). Current ship: **v5.0.0**.

**Scope rule:** every item must improve the core function — enabling, disabling, verifying, or rolling back Microsoft's native NVMe driver swap on Windows 11. No external integrations, no general-purpose storage tools, no theme/UI-locale polish. If an idea drifts into "separate tool that happens to live in the same exe," it doesn't belong here. Priority is by user impact / regret cost, not effort; S/M/L/XL are rough effort estimates.

---

## Research-Driven Additions

### P2 — Safety and UX

### P2 — Safety and compat (research pass 4, 2026-06-20)

### P3 — Quality and distribution (research pass 4, 2026-06-20)



### P3 - Dependency watch

- [ ] P3 - Add an explicit OpenTK/GLFW native-dependency watch gate
  Why: `dotnet list package --outdated --include-transitive` reports OpenTK/GLFW transitive updates through charting, while direct project code does not use OpenGL.
  Evidence: NuGet outdated scan; `src/NVMeDriverPatcher/NVMeDriverPatcher.csproj`; OpenTK NuGet/release pages.
  Touches: `src/NVMeDriverPatcher/NVMeDriverPatcher.csproj`, `tests/NVMeDriverPatcher.Tests/ChartingSmokeTests.cs`, `CLAUDE.md`.
  Acceptance: Dependency notes explain why OpenTK is transitive, charting smoke tests remain the upgrade gate, and any future direct pin/upgrade keeps SkiaSharp/OpenTK native assets ABI-compatible.
  Complexity: S


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
