# NVMe Driver Patcher — Roadmap

Living document — **incomplete work only**. Shipped items are deleted (git history + [CHANGELOG.md](CHANGELOG.md) are the record). Blocked items live in [Roadmap_Blocked.md](Roadmap_Blocked.md). Current ship: **v5.0.0**.

**Scope rule:** every item must improve the core function — enabling, disabling, verifying, or rolling back Microsoft's native NVMe driver swap on Windows 11. No external integrations, no general-purpose storage tools, no theme/UI-locale polish. If an idea drifts into "separate tool that happens to live in the same exe," it doesn't belong here. Priority is by user impact / regret cost, not effort; S/M/L/XL are rough effort estimates.

---

## Research-Driven Additions

### P2 — Safety and UX

### P2 — Safety and compat (research pass 4, 2026-06-20)

### P3 — Quality and distribution (research pass 4, 2026-06-20)

- [ ] P3 — Add `win-arm64` to release build matrix
  Why: Windows on ARM (Snapdragon X Elite/Plus) is a growing market. nvmedisk.sys is x64-only today, so ARM64 builds provide diagnostic/status/monitoring value only — but the build change is low effort and future-proofs for when Microsoft ships an ARM64 variant. ViVeTool already ships split-arch assets (v0.3.4+).
  Evidence: ARM64 WDK support confirmed (Microsoft Learn); Surface Pro 11 / Snapdragon X laptops use PCIe NVMe; no ARM64 nvmedisk.sys exists yet.
  Touches: `.github/workflows/release.yml` — add `win-arm64` publish steps for GUI, CLI, Tray, Watchdog alongside existing `win-x64`. MSI may need a separate ARM64 build or a dual-arch approach. `packaging/release-artifacts.json` — add ARM64 entries.
  Acceptance: Release workflow produces ARM64 self-contained exe artifacts with SHA-256 sidecars; ARM64 exe launches and shows "status" on an ARM64 machine (x64 emulation is fallback); release notes mention ARM64 as diagnostic-only until Microsoft ships the driver.
  Complexity: M
