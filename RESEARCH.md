# Research — NVMe Driver Patcher

Last updated: 2026-06-14 (second pass). Confidence labels: Verified, Likely, Needs live validation.
This pass re-verified the prior pass's open findings against current source, audited the
recovery/import services that earlier passes had not opened (WinPE builder, WinRE/BCD probe,
recovery-proof gate, SafeBoot upgrade, config import/export, clean-data), and refreshed external
signals. It also brought ROADMAP.md back into hygiene compliance (≈700 lines of completed tiers,
research logs, and continuation state removed; all open items preserved).

## Executive Summary

NVMe Driver Patcher (v5.0.0, .NET 10 LTS) is a Windows 11 safety tool for enabling, verifying,
monitoring, and rolling back Microsoft's native NVMe driver path (`nvmedisk.sys`). It ships
GUI/CLI/tray/watchdog surfaces with MSI, winget, Chocolatey, Scoop, and PowerShell-module
distribution, ~65 services, a 42-command CLI, build-rules JSON for per-build enablement
intelligence, a compat JSON for firmware/controller risk, and an opt-in Cloudflare Worker
telemetry receiver. The project is mature and unusually current with the upstream situation —
it already encodes build-aware ViVeTool fallback ID sets and the post-KB5079391 service-name
SafeBoot entries.

The dominant theme this pass: the **recovery and verification surfaces — the project's entire
reason to exist over the one-line enable scripts — contain correctness bugs that make them
silently lie about readiness**. A WinPE recovery stick is built with its boot announcement in a
location WinPE never reads; the WinRE-readiness probe returns "not enabled" on every non-English
Windows; the recovery-proof gate reports a restore point will be created when System Restore may
be off; the SafeBoot-upgrade verify gate can report success when nothing was written. None of
these throw — they pass, and the user trusts a recovery path that isn't there.

Top opportunities in priority order (Verified unless noted):

1. **WinPE `startnet.cmd` written to the wrong path** — the boot-time recovery announcement never runs (P2, **net-new**).
2. **WinRE/BCD probe is locale-gated** — non-English Windows is told WinRE is disabled when it isn't (P2, **net-new**).
3. **Telemetry client/receiver schema drift** — fleet summaries read `unknown/unknown/Other` (P1, carried).
4. **FeatureStore Boot-store verification gap** — native writes hit Runtime+Boot but only Runtime is verified (P1, carried).
5. **FeatureStore/ViVeTool cleanup absent from rollback & recovery kit** — fallback users can't fully revert (P1, carried).
6. **SkiaSharp/libpng CVE coverage** — bundled native libpng < 1.6.51 (P1, carried).
7. **Recovery-proof System Restore check is a weak proxy** — `RPSessionInterval` ≠ "protection on" (P3, **net-new**, Likely).
8. **SafeBoot-upgrade verify gate can false-pass** — gated on `GuidEntriesPresent` not the write result (P3, **net-new**).
9. **Config-import accepts unvalidated bundles** — no `SchemaVersion` check, `RestartDelay`/`PatchProfile` unclamped (P3, **net-new**).
10. **Missing preflight guardrails** — pending-reboot, disk-space, Modern-Standby/APST (P2/P3, carried).

## Product Map

- **Core workflows:** Preflight safety check → enable native NVMe (registry override, native FeatureStore, or ViVeTool fallback) → reboot → verify driver binding → monitor stability (watchdog) → roll back if unstable.
- **User personas:** Windows 11 storage enthusiasts, homelab operators, imaging/fleet admins, storage-performance testers, recovery-focused troubleshooters.
- **Platforms:** Windows 11 24H2+ (x64 only); Windows Server 2025 is the officially-supported origin of the driver. Self-contained single-file .NET 10 executables.
- **Distribution:** GitHub releases (GUI/CLI/tray/watchdog EXEs + MSI + legacy PS1), winget, Chocolatey, Scoop, PowerShell module ZIP.
- **Key data flows:** `windows_build_rules.json` (build-gated enablement + fallback ID selection), `FallbackFeatureCatalog` (per-build ViVeTool ID sets), `compat.json` (firmware/controller risk), `config.json`, `watchdog.json`, `drive_scope.json`, `maintenance_window.json`, SQLite DB (benchmark/telemetry/BypassIO history), optional Cloudflare Worker.

## Competitive Landscape

**Microsoft Windows Server native NVMe (and the client situation):** Official proof of up to +80% IOPS / -45% CPU. As of June 2026 Microsoft has committed native NVMe to the 25H2/26H2 client cycles with **no firm date**, and is adding a built-in "Feature flags" page (Insider 26300+) that may eventually expose this officially. KB5079391 (2026-03-26, re-issued as emergency KB5086672 on 03-31) neutered the registry-override route and tightened Safe Mode storage-driver resolution. The project must keep distinguishing official server support from client experimentation and keep its build ruleset current. (Verified — Tom's Hardware, Windows Central, ElevenForum, June 2026.)

**ViVe (thebookisclosed):** Reference for FeatureStore IDs and Runtime/Boot store behavior. The fallback ID landscape is build-dependent: pre-26200 builds use `60786016 + 48433719`; 26200.x (before .8524) use `55369237 + 48433719 + 49453572`; 26200.8524+ expose no `GenNvmeDisk`-compatible ID so no route works (ViVe #164). Already encoded in `FallbackFeatureCatalog.cs` + `windows_build_rules.json`.

**1LUC1D4710N/nvme-performance-script, GEAnalyticsLabs/native-nvme:** Lightweight enable-only scripts; no safety nets. This project's moat is safety/recovery depth — which is exactly why the recovery-surface bugs above are existential, not cosmetic.

**VeraCrypt #1640:** Remains the definitive reason to keep system encryption a hard block.

**DriverStoreExplorer:** Architecture reference for backend probing (PnPUtil/DISM/native API). Broad driver-store cleanup stays out of scope.

**Debloaters (NTLite, Winhance, Chris Titus WinUtil, O&O ShutUp10):** Their users are this tool's audience. Community reports suggest debloated systems can break native NVMe binding by disabling required scheduled tasks (Needs live validation). Detect-and-warn fits scope; becoming an optimizer does not.

## Security, Privacy, and Reliability

### Net-new findings this pass (Verified unless noted)

- (Verified) **WinPE recovery announcement is dead.** `WinPERecoveryBuilderService.BuildAsync` writes `startnet.cmd` to `<tree>\media\sources\startnet.cmd` (WinPERecoveryBuilderService.cs:138). WinPE executes `startnet.cmd` from inside `boot.wim` at `\Windows\System32\startnet.cmd`, never from the media's `sources` folder — so the boot-time "here's how to remove the patch" message never appears on the exact can't-boot path the stick exists for. Fix requires `Dism /Mount-Image` of `boot.wim`, writing into `\Windows\System32`, then commit/unmount. (Minor adjacent: `result.WimPath` is only set inside the recovery-kit copy block, line 132, so a kit-less build returns `WimPath=null`.)
- (Verified) **WinRE/BCD probe is locale-gated.** `WinReBcdPrepService.Probe` matches the English literals "Windows RE status : Enabled" and the BCD-identifier label (WinReBcdPrepService.cs:23-25). `reagentc /info` is localized, so on any non-English Windows `WinReEnabled` is false and `DeviceGuid` is never extracted → the tool reports "WinRE not currently enabled — recovery-from-WinRE path will NOT work" even when WinRE is fully provisioned. Parse the exit code / structural GUID line instead of localized labels.
- (Likely) **Recovery-proof System Restore check is a weak proxy.** `RecoveryProofGateService.EvaluateRestorePointCapability` treats `RPSessionInterval != 0` as "System Restore enabled" (RecoveryProofGateService.cs:147-148). `RPSessionInterval` governs scheduled-checkpoint cadence and can be present/non-zero while System Protection on the system drive is OFF — so the gate can report "a restore point will be created" when `CreateRestorePoint` will silently no-op. Verify per-drive protection (e.g. `Get-ComputerRestorePoint`/WMI `SystemRestoreConfig`) or attempt and confirm a checkpoint.
- (Verified) **SafeBoot-upgrade verify gate can false-pass.** `SafeBootUpgradeService.UpgradeEntries` returns failure only when `after.GuidEntriesPresent && !after.ServiceEntriesComplete` (SafeBootUpgradeService.cs:97). If the service-name writes silently no-op (`CreateSubKey` returns null) on a machine with no GUID entries, the gate skips the failure branch and reports "in place." The check should assert `after.ServiceEntriesComplete` directly.
- (Verified) **Config-import accepts unvalidated bundles.** `ConfigImportExportService.Import` never checks `Bundle.SchemaVersion` and copies `RestartDelay`/`PatchProfile` without bounds/`Enum.IsDefined` validation (ConfigImportExportService.cs:47-58). A hostile/foreign bundle can set a negative `RestartDelay` (flows into a `shutdown /r /t` argument) or an undefined `PatchProfile`. Validate `SchemaVersion`, clamp `RestartDelay`, and `Enum.IsDefined` the profile before persisting.
- (Verified, hardening) **CleanData subtree guard is asymmetric.** `CleanDataService.IsSafeCleanRoot` refuses paths *under* the Windows directory but only refuses the *exact* ProgramFiles/ProgramFilesX86/UserProfile/MyDocuments dirs, not their subtrees (CleanDataService.cs:138-145). Blast radius is limited to the app's own known subpaths (`tools\`, `etl\`, pattern-matched files), so this is defense-in-depth, not an active data-loss bug — apply the `StartsWith(prot + sep)` subtree refusal to all protected roots.
- (Verified, doc) **Version-string drift.** SSOT `Directory.Build.props` = 5.0.0 and the README badge = 5.0.0, but `CLAUDE.md` status and the ROADMAP intro still said v4.6.0 (ROADMAP corrected this pass; CLAUDE.md left to the maintainer per the two-file output rule).

### Carried open findings (re-confirmed against current source)

- (Verified) **MaintenanceWindowService overnight/day-boundary.** `IsInWindow` checks `ActiveDays.Contains(local.DayOfWeek)` on the current instant, then accepts `hour < EndHour` for overnight windows; the early-morning tail lands on the wrong calendar day (MaintenanceWindowService.cs:65-81). Note: `MaintenanceWindowServiceTests.cs` exists but does not cover the Fri/Sat/Sun/Mon boundary.
- (Verified) **FeatureStore Boot-store verification gap.** Writes hit Runtime+Boot (FeatureStoreWriterService.cs:156) but verification reads Runtime only (line 181).
- (Verified) **Telemetry client/receiver schema drift.** Client serializes `controllers[]`+`verification`; Worker reads `controller`/`firmware`/`verificationResult` (cloudflare-worker.js:89,91) → `unknown/unknown`/`Other`.
- (Verified) **RecoveryKit hardcoded IDs.** `.reg`/`.bat` embed literal IDs + SafeBoot GUID (RecoveryKitService.cs:53-62,137-158); only `GenerateVerificationScript` (line 270) uses `AppConfig`.
- (Verified) **CPU sanitizer stepping leak / telemetry HTTP / CORS `*` / single-page KV list** (CompatTelemetryService.cs:157-158,217-223; cloudflare-worker.js:76,83,120,131).
- (Verified) **AutoRevert swallows fatal exceptions** (AutoRevertService.cs:66, no OOM filter).
- (Verified) **DriveService robustness** — `TestLaptopChassis` `is ushort[]` (DriveService.cs:636); uncompiled driver-scan regex (184-185).
- (Verified) **MainViewModel** — `HH:mm:ss` log timestamp (248); dead `Task.WhenAny` (611-619); non-atomic autosave (1344).
- (Verified absent) **Preflight gaps** — pending-reboot, working-dir disk-space, Modern Standby/`CsEnabled`+APST.

### Verified safe / already handled

- (Verified) Build-aware ViVeTool fallback IDs (`FallbackFeatureCatalog.cs`, `windows_build_rules.json`) including the 26200.x set and the 26200.8524+ no-route state.
- (Verified) Post-KB5079391 service-name SafeBoot entries written by `PatchService.Install`.
- (Verified) `VerifiedDownloader`/`AutoUpdaterService` — host allowlist, manual redirect handling, size caps, SHA-256 sidecar or Authenticode, exact asset selection, atomic promote, fail-closed.
- (Verified) `ConfigService`/`BackupIntegrityService` — atomic temp-then-rename, retry, corrupt-file quarantine.
- (Verified) `TuningService` — every write validated against bounds, read-back verified, flushed (BSOD prevention intact).
- (Verified) `HtmlDashboardService` — XSS-safe; all attacker-influenced strings pass through `WebEscape` (escapes `& < > "`) into double-quoted attributes; enum/int/DateTime sinks non-injectable; atomic write.
- (Verified) `dotnet list package --vulnerable --include-transitive` clean in CI (bundled native libpng inside SkiaSharp is NOT covered by NuGet advisories — see ROADMAP libpng item).

## Architecture Assessment

- **Service boundaries are strong** (~65 focused service classes). The dominant residual risk is cross-surface contract drift (telemetry client↔receiver, FeatureStore write↔verify) and — newly highlighted — **recovery-surface correctness**: WinPE/WinRE/restore-point/SafeBoot gates that pass without delivering the recovery capability they claim.
- **Recovery story is incomplete for the fallback path.** Registry/SafeBoot rollback is solid, but ViVeTool/FeatureStore-applied IDs have no offline (WinRE) or in-Windows undo in the recovery kit; and the recovery-readiness gates above can report green falsely. This is the highest-leverage area because recovery depth is the project's competitive moat.
- **`MainWindow.xaml`** ~2,500 lines — decomposition remains a maintainability item.
- **Test suite** covers high-risk pure functions well; pure-logic gaps remain (`MaintenanceWindowService.IsInWindow` boundary cases, `SafeBootUpgradeService` verify logic, `RecoveryProofGateService`, `ConfigImportExportService` validation, `CompatTelemetryService` report shape).
- **Doc/roadmap hygiene** — ROADMAP.md had accumulated ~700 lines of completed tiers, research logs, and continuation state in violation of AGENTS.md; pruned this pass to incomplete work only. Version strings drifted across CLAUDE.md/ROADMAP vs the SSOT.
- **Release pipeline is mature** (version SSOT, validation scripts, sidecars, conditional Authenticode, watchdog published, MSI required). Remaining gaps: Chocolatey/Scoop manifest URL/hash automation and PowerShell-module artifact-contract validation.

## Rejected Ideas

- **Native INF / test-signing workarounds for ViVe #164:** Rejected — must not instruct users to tamper with storage-driver signatures (ViVe #164).
- **General driver-store cleanup:** Rejected — DriverStoreExplorer owns it; scope rule limits this project to enable/disable/verify/rollback.
- **Optimizer/debloat functionality:** Rejected — weakens the safety story; only NVMe-binding-relevant prerequisites belong in preflight.
- **Cloud dashboard / account system:** Rejected — workflow is local, admin-level, recovery-sensitive.
- **Treating registry-write success as patch success:** Rejected — current builds accept writes without binding `nvmedisk.sys`.
- **ARM64 / Windows-on-ARM, Linux port, GUI i18n (UI translation):** Rejected — negligible audience overlap / out of scope. (Note: this does NOT excuse locale-*correctness* bugs like the WinRE probe, which must work regardless of UI language.)
- **Auto-crowd-update of compat.json from telemetry:** Rejected until a signed trust model and a hardened receiver exist.
- **Single-quote escaping in HtmlDashboardService:** Rejected — no single-quoted attributes are emitted, so `WebEscape` omitting `'` is harmless (Verified).

## Sources

Official platform and Windows:
- https://techcommunity.microsoft.com/blog/windowsservernewsandbestpractices/announcing-native-nvme-in-windows-server-2025-ushering-in-a-new-era-of-storage-p/4477353
- https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/winpe-mount-and-customize
- https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/wpeinit-and-startnet-cmd-using-winpe-startup-scripts
- https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/windows-recovery-environment-windows-re--technical-reference
- https://learn.microsoft.com/en-us/windows-hardware/drivers/storage/bypassio
- https://learn.microsoft.com/en-us/lifecycle/products/microsoft-net-and-net-core

Native NVMe / block / fallback ecosystem (June 2026 refresh):
- https://github.com/thebookisclosed/ViVe/issues/164
- https://github.com/thebookisclosed/ViVe/wiki/Which-features-can-ViVeTool-toggle%3F
- https://www.tomshardware.com/software/windows/microsoft-blocks-the-registry-hack-trick-that-unlocked-native-nvme-performance-on-windows-11
- https://www.windowscentral.com/microsoft/windows-11/microsoft-shuts-down-windows-11-registry-hack-native-nvme-ssd-support
- https://www.elevenforum.com/t/windows-11-25h2-nvmedisk-sys-driver-support.46678/
- https://www.guru3d.com/story/microsoft-disables-nvme-registry-tweak-in-windows-11-insider-builds/
- https://www.overclock.net/threads/enable-native-nvme-driver-in-windows-11-24h2-25h2-with-last-update.1818467/
- https://www.heise.de/en/news/SSD-Turbo-NVMe-driver-quietly-switched-back-11273631.html
- https://github.com/veracrypt/VeraCrypt/issues/1640

Competitors / adjacent tools:
- https://github.com/1LUC1D4710N/nvme-performance-script
- https://github.com/GEAnalyticsLabs/native-nvme
- https://github.com/lostindark/DriverStoreExplorer
- https://github.com/ken-yossy/nvmetool-win

Dependencies and security:
- https://github.com/ArtifexSoftware/libpng/security
- https://www.nuget.org/packages/SkiaSharp
- https://developers.cloudflare.com/kv/api/list-keys/
- https://developers.cloudflare.com/workers/runtime-apis/bindings/rate-limit/

## Open Questions

1. Needs live validation: which 25H2/26x00 build ranges still show flags-enabled-but-not-bound after the latest servicing, and is the 26200.8524+ "no route" boundary still accurate?
2. Needs design decision: for the overnight maintenance-window fix, evaluate active-day against the window's start day (recommended) vs. requiring both bounding days.
3. Needs live validation: do debloat tools (NTLite, WinUtil, Winhance) break native NVMe binding by disabling specific scheduled tasks, and which ones?
4. Needs design decision: should the recovery kit's FeatureStore/ViVeTool cleanup run only in-Windows, or can a WinRE-safe offline FeatureStore reset be produced?
5. Needs live validation: for the WinPE `startnet.cmd` fix, confirm the DISM mount/commit roundtrip is acceptable build-time cost, or whether `winpeshl.ini` is a lighter injection point.
6. Needs design decision: what is the authoritative, locale-independent signal that System Restore will actually create a checkpoint on the system drive (per-drive protection query vs. attempt-and-verify)?
