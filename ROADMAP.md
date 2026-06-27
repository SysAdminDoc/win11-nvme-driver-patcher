# NVMe Driver Patcher — Roadmap

Living document — **incomplete work only**. Shipped items are deleted (git history + [CHANGELOG.md](CHANGELOG.md) are the record). Blocked items live in [Roadmap_Blocked.md](Roadmap_Blocked.md). Current ship: **v5.0.0**.

**Scope rule:** every item must improve the core function — enabling, disabling, verifying, or rolling back Microsoft's native NVMe driver swap on Windows 11. No external integrations, no general-purpose storage tools, no theme/UI-locale polish. If an idea drifts into "separate tool that happens to live in the same exe," it doesn't belong here. Priority is by user impact / regret cost, not effort; S/M/L/XL are rough effort estimates.

---

## Research-Driven Additions

### P2 — Safety and UX

### P2 — Safety and compat (research pass 4, 2026-06-20)

- [ ] P2 — Add Phison E18/E26 power-loss warning to preflight
  Why: nvmedisk.sys can acknowledge writes before data physically reaches NAND. On Phison E18/E26 controllers specifically, power loss during writes causes RAW partition corruption ("Phantom Ack"). This is a firmware-level behavior the patcher can't fix, but users with these controllers need a UPS/power-protection advisory.
  Evidence: GigXP technical analysis of MFT corruption mechanism; Overclock.net reports on Phison E18 instability.
  Touches: `src/NVMeDriverPatcher.Core/Services/FirmwareCompatService.cs` or `PreflightService.cs` — when compat.json matches a Phison E18/E26 controller, surface a "power protection recommended" advisory alongside the existing compat level. `compat.json` entries for Phison should carry a `powerLossRisk` flag or note.
  Acceptance: Preflight surfaces an advisory when a Phison E18/E26 controller is detected, recommending UPS/power protection; advisory does not block patching; test verifies the advisory triggers on Phison controller match.
  Complexity: S

- [ ] P2 — Enrich Event ID 129 watchdog guidance
  Why: Storport Event ID 129 ("Reset to device") indicates command timeout / controller saturation — a sign the drive is struggling under the native driver. The watchdog already watches for it, but the user-facing verdict text does not explain what ID 129 means or that it specifically warrants immediate revert consideration.
  Evidence: GigXP documents Event ID 129 as a "command saturation" signal requiring immediate revert; current `EventLogWatchdogService.BuildDetail()` counts the events but does not explain their significance.
  Touches: `src/NVMeDriverPatcher.Core/Services/EventLogWatchdogService.cs` — enrich `BuildDetail` and `BuildSummary` with ID-129-specific guidance when those events are present. CLI `watchdog` output should name the event type. `DocsService` watchdog topic should mention it.
  Acceptance: When watchdog detects Storport ID 129 events, the summary text includes "command timeout (Storport 129)" with a recommendation to consider revert; existing watchdog test fixtures updated.
  Complexity: S

### P3 — Quality and distribution (research pass 4, 2026-06-20)

- [ ] P3 — Monitor SkiaSharp for libpng >= 1.6.55 and bump when available
  Why: SkiaSharp 3.119.4 bundles libpng 1.6.54. CVE-2026-25646 (out-of-bounds read in `png_set_quantize()`) and CVE-2026-33416 (use-after-free) affect libpng < 1.6.55. Attack surface is low (requires crafted PNG in charting engine) but should be tracked.
  Evidence: SentinelOne CVE-2026-25646; Powder Keg CVE-2026-33416; mono/SkiaSharp#3426.
  Touches: `src/NVMeDriverPatcher/NVMeDriverPatcher.csproj` — bump all SkiaSharp packages when a version bundling libpng >= 1.6.55 ships. Run `ChartingSmokeTests` before and after per dependency update checklist.
  Acceptance: All SkiaSharp packages updated to a version bundling libpng >= 1.6.55; `ChartingSmokeTests` pass; csproj comment updated with CVE references.
  Complexity: S (when available)

- [ ] P3 — Add `win-arm64` to release build matrix
  Why: Windows on ARM (Snapdragon X Elite/Plus) is a growing market. nvmedisk.sys is x64-only today, so ARM64 builds provide diagnostic/status/monitoring value only — but the build change is low effort and future-proofs for when Microsoft ships an ARM64 variant. ViVeTool already ships split-arch assets (v0.3.4+).
  Evidence: ARM64 WDK support confirmed (Microsoft Learn); Surface Pro 11 / Snapdragon X laptops use PCIe NVMe; no ARM64 nvmedisk.sys exists yet.
  Touches: `.github/workflows/release.yml` — add `win-arm64` publish steps for GUI, CLI, Tray, Watchdog alongside existing `win-x64`. MSI may need a separate ARM64 build or a dual-arch approach. `packaging/release-artifacts.json` — add ARM64 entries.
  Acceptance: Release workflow produces ARM64 self-contained exe artifacts with SHA-256 sidecars; ARM64 exe launches and shows "status" on an ARM64 machine (x64 emulation is fallback); release notes mention ARM64 as diagnostic-only until Microsoft ships the driver.
  Complexity: M
