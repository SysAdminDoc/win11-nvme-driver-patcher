# NVMe Driver Patcher — Roadmap

Living document. Current ship: **v4.6.0** (2026-04-19). See [CHANGELOG.md](CHANGELOG.md) for what's landed.

v4.6.0 lights up the GUI Diagnostics+ tab, adds nine "live with the patch" services
(accessibility / maintenance window / clean-data / HTML dashboard / firmware nudge /
Safe-Mode verify / docs / AppLocker-SRP guardrail / real-time watchdog service), a full
packaging pipeline (PSGallery module, Intune/SCCM detection, JSON schemas, issue
templates), and Authenticode signing hooks in the release workflow. All published
ROADMAP items are now shipped — future work is follow-up polish + scope-rule-compliant
new ideas as they surface.

v4.5.1 closes the last three open ROADMAP items from the published list (§1.5 recovery-kit
persistent CTA — `RecoveryKitFreshnessService`; §3.1 native FeatureStore writer — stubbed
with a stable contract + fallback-evidence probe; §3.3 WinRE BCD prep — probe-only via
`WinReBcdPrepService` with `EnableWinRe` helper). Driver-injection into the WinRE image
remains future work. Test coverage added for 12 v4.4/v4.5 service surfaces; PR-gated CI
workflow added.

## ✅ v4.5.0 closed ROADMAP items
Tier 1 §1.1 (silent/unattended CLI), §1.2 (tuning profile import/export JSON), §1.3
(`compare-benchmarks` CLI), §1.4 (config schema migration), plus Tier 2 §2.1 (per-controller
audit), §2.2 (live event-log tail), §2.3 (expanded StorNVMe tuning), §2.4 (MSFT_PhysicalDisk +
StorageReliabilityCounter telemetry), §2.5 (BypassIO per-volume inspector), §2.6 (auto-benchmark
regression compare), §2.7 (portable mode), §2.8 (IOCTL surface), §2.9 (config import/export), and
Tier 3 §3.2 (APST inspector + override). Only §1.5 (recovery-kit persistent hero CTA), §3.1
(native FeatureStore writer), and §3.3 (WinRE BCD preparation) remain open from the published
list.

**Scope rule:** everything on this list must improve the core function — enabling, disabling, verifying, or rolling back Microsoft's native NVMe driver swap on Windows 11. No external integrations, no general-purpose storage tools, no theme/locale polish. If an idea drifts into "separate tool that happens to live in the same exe," it doesn't belong here.

Priority tiers are by **user impact / regret cost**, not effort. S/M/L are rough effort estimates.

---

## Tier 0 — Critical (addresses the Microsoft override block) — ✅ COMPLETE

All four items shipped. 0.1, 0.3, 0.4 in v4.2.0; 0.2 in v4.3.0. Kept here as history.

### 0.1 Detect + handle the Microsoft block (Feb/Mar 2026 Insider neutering) — ✅ v4.2.0
- **Context:** Microsoft silently neutered the three `FeatureManagement\Overrides` keys (`735209102`, `1853569164`, `156965516`) on recent Insider builds. `NVMeDriverPatcher` writes them successfully, reboot succeeds, but `nvmedisk.sys` never binds. From the user's POV the app *lies*.
- **Fix:** `PatchVerificationService` runs on next startup after a patch was flagged. If `nvmedisk.sys` is NOT bound, surface an honest "Patch Written But Inactive" status and route the user to the ViVeTool fallback (0.2).

### 0.2 ViVeTool fallback path (IDs 60786016 + 48433719) — ✅ v4.3.0
- **Context:** Community moved to ViVeTool, which writes to `FeatureStore` instead of `FeatureManagement\Overrides`.
- **Fix:** `ViVeToolService` downloads ViVeTool from its official GitHub release, caches in `tools/`, shells out. Still open: native `FeatureStore` writer (§3.1) to drop the external dependency.

### 0.3 BypassIO / DirectStorage regression warning — ✅ v4.2.0
- `nvmedisk.sys` refuses BypassIO. Pre-patch confirmation now elevates this from `[i]` to `[!]` when the system drive currently honors BypassIO.

### 0.4 Safer default: primary-key-only mode — ✅ v4.2.0
- Community BSOD reports cluster on `156965516` + `1853569164`. Default is now Safe Mode (primary key only). Full Mode is opt-in.

---

## Tier 1 — Ship next (v4.4+)

Each of these either improves how the patch is deployed, or improves confidence that it worked. All S-effort except where noted.

### 1.1 Silent / unattended CLI mode — DEFERRED TO v4.4.1
- `--unattended` + `--unattended-delay=<seconds>` + `--log-to-file=<path>` for imaging workflows. No prompts, auto-reboot, non-zero exit on any preflight blocker unless `--force`.
- **Where:** `Program.cs`, threads through `PatchService.Install(unattended: true, ...)`.

### 1.2 Tuning profile import/export (JSON) — DEFERRED TO v4.4.1
- `TuningService` emits/ingests named profiles. GUI "Export Profile" / "Import Profile" on Tuning tab; CLI `export-tuning-profile` / `import-tuning-profile`.

### 1.3 Benchmark diff CLI (`compare-benchmarks`) — DEFERRED TO v4.4.1
- `NVMeDriverPatcher.Cli compare-benchmarks <before.json> <after.json> --threshold=15` → exit 0 within threshold, exit 1 on regression.

### 1.4 Config schema migration — DEFERRED TO v4.4.1
- `AppConfig.ConfigVersion` is already persisted (v4.2). Wire a migration table so future breaking changes don't silently drop user settings.

### 1.5 Recovery-kit CTA on hero card — DEFERRED TO v4.4.1
- Today the freshness warning only fires inside Apply-Patch confirmation. Surface a persistent badge: "Recovery kit: stale (42 days) — Generate Now" on the main hero card.

---

## ✅ Tier 1.5 — SHIPPED in v4.4.0 (2026-04-19)

Fourteen capabilities across stability correlation, enterprise deployment, and non-admin ambient status. All passed the scope rule — improvements to enable/disable/verify/rollback.

### 1.6 Event-log watchdog with auto-revert — ✅ v4.4.0
- `EventLogWatchdogService` armed on Install, disarmed on Uninstall. Storport 129 / disk 51+153 / BugCheck 1001 / Kernel-Power 41 inside the configurable window produce `Healthy`/`Warning`/`Unstable`/`Completed`. `Unstable` + `AutoRevertEnabled` signals the next-boot auto-revert path.

### 1.7 Reliability Monitor correlation — ✅ v4.4.0
- `ReliabilityService` pulls `Win32_ReliabilityStabilityMetrics`, overlays the patch timestamp, reports pre/post averages + delta.

### 1.8 Minidump triage — ✅ v4.4.0
- `MinidumpTriageService` scans `C:\Windows\Minidump` (+ LiveKernelReports) for dumps newer than the patch, flags references to nvmedisk/stornvme/storport/disk.

### 1.9 Firmware + controller compat JSON — ✅ v4.4.0
- Shipped `compat.json` + `FirmwareCompatService`. User-editable copy in `%LocalAppData%\NVMePatcher\` takes precedence over the bundled default.

### 1.10 Per-drive scope exclusions — ✅ v4.4.0
- `PerDriveScopeService` + `drive_scope.json`. Serial or model-pattern exclusions. Pure decision function; UI renders "N drives — M excluded by scope."

### 1.11 Dry-run preview — ✅ v4.4.0
- `DryRunService.PlanInstall` produces a Markdown changeset with blockers, warnings, and every registry write. CLI `apply --dry-run` + ViewModel `PreviewDryRunCommand`.

### 1.12 ETW storage trace — ✅ v4.4.0
- `EtwTraceService` wraps `wpr.exe` for 60s pre/post captures using the inbox `GeneralProfile.Storage`.

### 1.13 WinPE recovery USB builder — ✅ v4.4.0
- `WinPERecoveryBuilderService` auto-detects the ADK, wraps copype + MakeWinPEMedia, pre-stages the Recovery Kit, writes a custom `startnet.cmd` that announces the recovery path on boot. Closes ROADMAP §3.3.

### 1.14 Opt-in compat telemetry — ✅ v4.4.0
- `CompatTelemetryService` builds an anonymized report. Local-save by default; `--endpoint=<url>` submits via HTTPS. No serials/machine names/drive letters/user names.

### 1.15 Driver Verifier harness — ✅ v4.4.0
- `DriverVerifierService` narrowly wraps verifier.exe for `/standard /driver nvmedisk.sys stornvme.sys disk.sys`. Dev/tester mode only.

### 1.16 GPO / ADMX templates — ✅ v4.4.0
- `packaging/admx/` ADMX + ADML. `HKLM\SOFTWARE\Policies\SysAdminDoc\NVMeDriverPatcher` overlay via `GpoPolicyService` in both GUI and CLI.

### 1.17 winget manifest — ✅ v4.4.0
- `packaging/winget/SysAdminDoc.NVMeDriverPatcher.yaml` — portable-installer manifest v1.6.0.

### 1.18 Non-admin status tray — ✅ v4.4.0
- `NVMeDriverPatcher.Tray` new project. Ships without an admin manifest; shells the main exe via `runas` on demand.

### 1.19 Rotating logs — ✅ v4.4.0
- `LogRotationService` — 5MB × 5 retention on `crash.log`, `activity.log`, `watchdog.log`, `diagnostics.log`.

---

## Tier 2 — Platform depth (v4.5 – v4.7)

### 2.1 PatchVerificationService: per-controller driver audit — M
- Beyond 0.1's "did nvmedisk bind?" — enumerate every NVMe controller, report bound driver and queue depth in effect per controller. Surface per-controller status so users with multiple NVMe drives can see which ones migrated and which didn't.
- **Why this is a core improvement:** Server 2025 supports per-controller scoping; client patches currently swap globally with no per-device visibility.

### 2.2 Live Event Log tail (Storport / nvmedisk / disk) — M
- Background `EventLogWatcher` on `System` channel filtered for Storport ID 129 (timeouts), nvmedisk init/unload, disk ID 51 (paging errors). Show the last 20 events in a Diagnostics sub-panel.
- **Why this is a core improvement:** post-patch the user needs to see "driver initialized OK" within seconds of login without opening Event Viewer. Also catches post-patch regressions (ID 129 spike) early.

### 2.3 Expanded StorNVMe tuning surface — M
- Current `TuningProfile` covers 6 params. Add `AsyncEventNotificationEnabled`, `PowerStateTransitionLatency`, `ThermalMgmtEnabled`, APST threshold overrides, per-drive `NoLowPowerTransitions`.
- **Why this is a core improvement:** tuning is already an app surface — its current coverage is the subset publicly documented in 2024. More has surfaced since.

### 2.4 MSFT_PhysicalDisk / StorageReliabilityCounter telemetry — M
- Add `MSFT_PhysicalDisk` (predictive failure, media wear), `MSFT_StorageReliabilityCounter` (error trending) to the Telemetry tab.
- **Why this is a core improvement:** correlates the driver swap with drive health trends. Lets the user answer "did the patch cause this new error?" with data instead of vibes.

### 2.5 BypassIO per-volume inspector panel — M
- After 0.3's pre-patch warning, give users a dedicated Diagnostics panel showing BypassIO state per volume, re-checked after each reboot.
- **Why this is a core improvement:** the BypassIO/DirectStorage regression is the patch's biggest tradeoff; users who dual-boot patched/unpatched (e.g. work vs gaming) need visibility.

### 2.6 Scheduled auto-benchmark with regression alerting — M
- Opt-in Task Scheduler job (weekly / nightly) runs a quick benchmark, stores in SQLite, toasts if > N% regression from rolling baseline.
- **Why this is a core improvement:** regression detection for the patch effect over time. Windows Update can quietly alter driver behavior; this catches it.

### 2.7 Portable mode (`--portable`) — S
- Redirect `AppConfig.GetWorkingDir()` to the executable folder when `--portable` is passed or `portable.flag` sits beside the exe.
- **Why this is a core improvement:** enables USB-stick field deployments of the patcher without installer/install state.

### 2.8 IOCTL surface expansion — L
- `IOCTL_STORAGE_PROTOCOL_COMMAND` for raw NVMe admin commands (full Identify, controller limits, firmware version), `IOCTL_STORAGE_PREDICT_FAILURE`, `IOCTL_DISK_GET_DRIVE_GEOMETRY_EX`.
- **Why this is a core improvement:** feeds 2.1 (per-controller audit) and 2.4 (telemetry) with data that WMI doesn't expose.

### 2.9 Config import/export — S
- CLI `export-config` / `import-config`, GUI "Share Settings" button. Separate from the support-bundle ZIP.
- **Why this is a core improvement:** enables fleet cloning of preflight/toast/tuning preferences for sysadmins patching many machines.

---

## Tier 3 — Strategic (v5.0+)

Higher effort or higher risk; all still within the scope rule.

### 3.1 Native FeatureStore writer — L
- Reverse-engineer ViVeTool's protobuf-ish `FeatureStore` format and implement in C#. Drops the bundled-tool download from 0.2, keeps single-exe ship story.
- **Why this is a core improvement:** the ViVeTool dependency is a permanent exit point for users behind corporate firewalls that block `github.com` downloads.

### 3.2 APST / power-state inspector + override — M
- Inspector showing per-PS transition latencies, entry/exit power, and a user override with safety bounds. `IdlePowerTimeout` / `StandbyPowerTimeout` are already exposed; APST itself isn't.
- **Why this is a core improvement:** APST is the patch's biggest laptop tradeoff. Today the app warns about it; this lets the user measure and tune it.

### 3.3 WinRE / BCD preparation — L
- Ensure `stornvme` is in WinRE's BCD default load order; generate a Safe-Mode-bootable CLI diagnostic script. Beyond the current SafeBoot registry keys.
- **Why this is a core improvement:** real fallback confidence. The recovery kit today assumes the user can boot; this handles the case where they can't.

---

## Research log (2026-04-17)

Sources that drove the current priority list:

- **Tom's Hardware (Mar 2026)** — Microsoft blocks NVMe registry hack; new IDs `60786016` + `48433719` via ViVeTool
- **Windows Central (Mar 2026)** — block confirmed; MS committed native NVMe to 25H2/26H2 client with no firm date
- **Neowin (Mar 2026)** — 24H2/25H2 hack silently neutered
- **HotHardware (Mar 2026)** — ViVeTool workaround, new flag IDs
- **gamegpu.com (Mar 2026)** — post-block unlock works again via ViVeTool
- **Tom's Hardware (Dec 2025)** — 85% random-write gain, 80% IOPS gain, 45% CPU reduction under heavy load; sequential near-unchanged
- **gigxp.com / windowsforum.com (Feb 2026)** — BypassIO veto + DirectStorage regression reports
- **Overclock.net / windowsforum.com (Jan–Mar 2026)** — BSODs correlated with `156965516` + `1853569164`; `735209102` alone is the safer path
- **MS Tech Community (Nov 2024)** — original Server 2025 announcement
- **GitHub: giosci1994/feature-overrides-registry, 1LUC1D4710N/nvme-performance-script** — competitor projects; lack BitLocker/VeraCrypt/restore-point safety net this project has

### Competitive positioning (as of Apr 2026)

**This project is the only NVMe enabler with:** Safe/Full mode split with safer default, BitLocker auto-suspend, VeraCrypt detection/block, system restore-point creation, registry backup + rollback, preflight safety, recovery kit, support-bundle ZIP, post-reboot verification, automated ViVeTool fallback, WPF dark UI, C# native.

**Gaps vs community practice (all in Tier 2+):** per-controller patch scoping (2.1), live Event Log visibility after patch (2.2), expanded StorNVMe tuning surface (2.3), regression detection over time (2.6).

---

## Autonomous Roadmap Expansion - 2026-06-06

This section resumes the nonstop roadmap loop from local repository inspection, current external research, and verification attempts on 2026-06-06. It does not replace the historical tiers above; it adds current implementation-ready work now that the published v4.4-v4.6 surfaces have largely landed.

### Cycle 1: Repository Comprehension

**Current summary:** NVMe Driver Patcher is a mature Windows 11 safety tool for enabling, verifying, diagnosing, and rolling back the Windows Server 2025 native NVMe stack (`nvmedisk.sys`) on client Windows builds. The repo contains a WPF GUI, separate elevated CLI, non-admin tray agent, optional watchdog service, legacy PowerShell script, packaging assets, CI/release workflows, and a broad test suite.

**Likely target users:**

- Advanced Windows users and homelab operators experimenting with native NVMe performance.
- Sysadmins packaging the patcher for small fleets, imaging labs, or managed Windows 11 endpoints.
- Storage/performance testers comparing `stornvme.sys` versus `nvmedisk.sys`.
- Support/debug users who need recovery kits, event correlation, and safe rollback.

**Core user jobs:**

- Determine whether a Windows 11 machine is eligible and safe to patch.
- Apply the safest supported native NVMe enablement path, then reboot and verify the driver actually bound.
- Detect Microsoft feature-override blocks and use the ViVeTool fallback only when appropriate.
- Preserve a rollback path before changing storage-driver state.
- Compare pre/post performance, stability, BypassIO/DirectStorage impact, and compatibility signals.
- Package the tool for repeatable CLI, tray, MSI/winget/PowerShell/Intune workflows.

**Local evidence reviewed:**

- `README.md` documents GUI/CLI, safety checks, benchmarks, compatibility issues, recovery kit, and current user-facing claims.
- `CLAUDE.md` describes v4.6.0 architecture, key services, status, and project scope rule.
- `ROADMAP.md` contains earlier tiers plus OSS and implementation research rounds.
- `NVMeDriverPatcher.sln` includes GUI, CLI, tray, watchdog, and tests.
- `src/NVMeDriverPatcher/NVMeDriverPatcher.csproj` targets `net9.0-windows` and is currently versioned `4.6.1`.
- `src/NVMeDriverPatcher.Cli/Program.cs` exposes a large operational command surface.
- `.github/workflows/ci.yml` and `.github/workflows/release.yml` define Windows CI and release packaging.
- `tests/NVMeDriverPatcher.Tests` contains 37 test files and 176 `[Fact]` / `[Theory]` entries.
- Git history HEAD: `910d3ef merge: integrate origin/main (HEAD-favored, unrelated-histories from AI-scrub rewrite)`; recent history includes a v4.7.0 bump and revert plus v4.6.1 SafeBoot fix.

**Current strengths:**

- Strong safety posture: VeraCrypt block, BitLocker handling, restore/backup/recovery paths, dry-run preview, post-reboot verification, watchdog, minidump triage, firmware compatibility, per-drive scope, and guardrails.
- Multi-surface delivery: WPF GUI, CLI, tray, optional watchdog service, PowerShell wrapper, winget manifest, WiX MSI, Intune/SCCM detection, ADMX policy templates, and release workflow.
- Good implementation separation at the service layer: many focused services under `src/NVMeDriverPatcher/Services`.
- Test coverage exists for many high-risk surfaces, including config migration, ViVeTool, downloader, recovery kit, telemetry, watchdog, FeatureStore stub contract, and service classifiers.
- The app tells the truth about the Microsoft post-block state instead of claiming registry writes imply success.

**Current weaknesses and drift:**

- Version drift is visible across shipping surfaces: GUI csproj is `4.6.1`, while CLI/tray/watchdog csproj files, WiX, winget, PowerShell module, README, CLAUDE.md, and the top of this roadmap still reference `4.6.0`.
- The optional real-time watchdog service exists as `src/NVMeDriverPatcher.Watchdog`, but `.github/workflows/release.yml` publishes only GUI, CLI, and tray, and `packaging/wix/NVMeDriverPatcher.wxs` installs only GUI, CLI, tray, `compat.json`, icon, and ADMX files. The shipped v4.6 story likely misses the watchdog binary unless manual packaging covers it elsewhere.
- `.github/workflows/release.yml` stages `icon.ico`, but `rg --files` did not find a root `icon.ico`; the repo has `icon.png` and `src/NVMeDriverPatcher/nvme.ico`. This can make MSI staging fail before the best-effort WiX step.
- CLI command implementation supports more commands than `PrintUsage()` and README document. Hidden but implemented commands include `guardrails`, `controllers`, `tail`, `physical-disks`, `bypassio`, `apst`, `identify`, config/tuning import/export, `compare-benchmarks`, `compat-checksum`, backup verification, scheduled tasks, portable mode, update check, WinRE, FeatureStore, clean-data, dashboard, firmware nudge, Safe Mode verify, accessibility, and maintenance-window.
- `FeatureStoreWriterService.WriteOverrides()` remains an intentional stub, so ViVeTool is still the only active post-block writer path.
- `MainWindow.xaml` is about 2,500 lines, combining shell, overview, tabs, workspace, recovery, diagnostics, settings, and activity rail. This increases UI-change risk and slows targeted QA.
- Local verification has changed since repo notes: `dotnet --info` shows .NET SDK 10.0.300 and no .NET 9 SDK. `dotnet restore NVMeDriverPatcher.sln --verbosity minimal` succeeds, but `dotnet test NVMeDriverPatcher.sln -c Debug --verbosity minimal` and `dotnet build NVMeDriverPatcher.sln -c Debug --no-restore --verbosity minimal /m:1` both timed out without useful compiler output after 120-180 seconds. Re-running through `cmd pushd` to avoid raw UNC current-directory issues also timed out. Timed-out `dotnet` / MSBuild worker processes were stopped.
- Commands invoked from a raw UNC working directory can fall back through `cmd.exe` to `C:\Windows` when child tooling does not support UNC paths. This was observed during command inspection output and should be handled in contributor runbooks even if it is not the full build-hang cause.

### Cycle 2: Current Feature Inventory

| Area | Existing Feature | Evidence | Maturity | Notes |
|---|---|---|---|---|
| Core patching | Safe/Full profile feature flag writes, optional server key, SafeBoot GUID and service-name entries | `src/NVMeDriverPatcher/Models/AppConfig.cs`, `src/NVMeDriverPatcher/Services/PatchService.cs`, `src/NVMeDriverPatcher/Services/RegistryService.cs` | High | Core value is well covered; version and docs drift now matter more than missing basics. |
| Verification | Post-reboot state tracking and override-block detection | `PatchVerificationService.cs`, CLI `status`, ViewModel flow | High | Should evolve into release-channel-specific explanations as Microsoft changes client gating. |
| Post-block fallback | ViVeTool download/cache/apply and FeatureStore evidence probe | `ViVeToolService.cs`, `FeatureStoreWriterService.cs`, tests | Medium | Active path depends on external ViVeTool; native writer is still a research/spec item. |
| Safety preflight | Windows build, NVMe presence, BitLocker, VeraCrypt, driver status, software compatibility, guardrails | `PreflightService.cs`, `SystemGuardrailsService.cs`, `DriveService.cs` | High | Add explicit "why blocked" education and exportable decision report. |
| Recovery | Recovery kit, verification script, WinPE builder, backup integrity, freshness | `RecoveryKitService.cs`, `WinPERecoveryBuilderService.cs`, `BackupIntegrityService.cs`, `RecoveryKitFreshnessService.cs` | High | Turn recovery readiness into a first-class gate and visual proof checklist. |
| Diagnostics | Watchdog, event tail, reliability, minidump triage, ETW, BypassIO, APST, physical disk telemetry, controller audit, HTML dashboard | `Services/*Watchdog*`, `EventLogTailService.cs`, `ReliabilityService.cs`, `MinidumpTriageService.cs`, `BypassIoInspectorService.cs`, `ApstInspectorService.cs`, `HtmlDashboardService.cs` | High | GUI and CLI discoverability lag behind service breadth. |
| CLI | 40+ operational commands in `Program.cs` | `src/NVMeDriverPatcher.Cli/Program.cs` | Medium | Needs command metadata, generated help, README parity, and command tests for every route. |
| GUI | WPF dark/light/high-contrast shell, overview cards, workspace tabs, activity rail, settings, telemetry, recovery, diagnostics | `Views/MainWindow.xaml`, `Themes/*.xaml`, `ViewModels/MainViewModel*.cs` | Medium | Feature-rich, but monolithic XAML should be split around tabs/workspaces. |
| Data and config | EF Core SQLite, config migration, schemas, import/export, WAL mode | `Data/AppDbContext.cs`, `ConfigService.cs`, `ConfigMigrationService.cs`, `packaging/schemas/*.json` | Medium | Add schema validation into CI and include generated config docs. |
| Tests | 37 test files, 176 facts/theories | `tests/NVMeDriverPatcher.Tests` | Medium | Local restore succeeds; local build/test hang needs triage before declaring green on this machine. |

### Cycle 3: External Research Signals

| Competitor / Source | Type | Relevant Features | UX Ideas | Technical Ideas | Notes | Confidence |
|---|---|---|---|---|---|---|
| Microsoft Tech Community - Native NVMe in Windows Server 2025 - https://techcommunity.microsoft.com/blog/windowsservernewsandbestpractices/announcing-native-nvme-in-windows-server-2025-ushering-in-a-new-era-of-storage-p/4477353 | Official platform source | Server 2025 Native NVMe is GA but opt-in after the October cumulative update; Microsoft cites up to about 80% more IOPS and about 45% fewer CPU cycles per I/O for 4K random read workloads. | Keep positioning around "official server feature, experimental client enablement"; show server/client distinction clearly in app copy. | Add a Windows release/build ruleset file that can be updated as Microsoft changes client behavior. | Official article is about Server 2025, not supported client enablement. | High |
| Tom's Hardware, 2026-03-23 - https://www.tomshardware.com/software/windows/microsoft-blocks-the-registry-hack-trick-that-unlocked-native-nvme-performance-on-windows-11 | News / market signal | Registry overrides stopped working in recent Insider builds; ViVeTool fallback IDs are `60786016` and `48433719`; BitLocker and vendor SSD tools remain risks. | Post-patch status should distinguish "keys written", "driver bound", "fallback evidence found", and "blocked by current build". | Build an updateable compatibility/rules file keyed by Windows build/channel. | Confirms existing product direction; needs continued monitoring because the workaround can change. | High |
| Windows Central, 2026-03-24 - https://www.windowscentral.com/microsoft/windows-11/microsoft-shuts-down-windows-11-registry-hack-native-nvme-ssd-support | News / mainstream user framing | Client rollout timing for 25H2/26H2 remains unclear; third-party tools and backup software can be confused by disk identity/presentation changes. | Add a "Who should wait?" decision panel for non-enthusiast users, gamers, and backup-heavy systems. | Expand preflight to capture backup software and vendor tool versions for support bundles. | Useful for wording risk and user education. | Medium |
| Microsoft Learn - BypassIO in storage drivers - https://learn.microsoft.com/en-us/windows-hardware/drivers/storage/bypassio | Official technical doc | BypassIO supports DirectStorage, is client-only, NVMe-only, NTFS-only, read-only today, and unsupported drivers can veto the optimized path. | Add a gaming/DirectStorage impact panel with per-volume status and plain-language tradeoff. | Store per-volume BypassIO history before and after patch so users can see whether native NVMe changed their gaming path. | Directly validates the project's BypassIO warning priority. | High |
| Microsoft Learn - PnPUtil - https://learn.microsoft.com/windows-hardware/drivers/devtest/pnputil | Official technical doc | Inbox tool for driver package add/install/delete/enumerate; present in Windows Vista and later. | Explain when the app uses native APIs, WMI, registry, or inbox tools. | Introduce backend capability probes and explicit fallback order where driver/package enumeration expands. | Useful for avoiding DevCon-style deprecated tooling. | High |
| DriverStoreExplorer - https://github.com/lostindark/DriverStoreExplorer | Open-source inspiration | Driver store GUI with Native API / DISM / PnPUtil backend auto-detection, online/offline image mode, device association, search/filter/export, warnings. | Borrow device association, sortable/filterable driver views, and "old/unused driver" explanation patterns. | Add an `IDriverBackend` abstraction and offline mounted-image mode for WinPE/Intune imaging workflows. | Adjacent tool; keep every borrowed pattern tied to enable/disable/verify/rollback. | Medium |
| thebookisclosed/ViVe - https://github.com/thebookisclosed/ViVe | Open-source dependency | C# library and console app for Windows feature control APIs; used by fallback path. | Show the fallback dependency, version, cache path, and trust signal in diagnostics. | Cache release metadata, validate GitHub host, export exact version and integrity signal into support bundles. | Upstream does not guarantee this specific NVMe use case remains stable. | High |

### Cycle 4: User Pain Points and Market Signals

| Source | Pain Point | Evidence | Opportunity | Priority |
|---|---|---|---|---|
| Local repo drift | Users and packagers cannot tell whether the current release is v4.6.0, v4.6.1, or reverted v4.7.0. | `src/NVMeDriverPatcher.csproj` is `4.6.1`; several other ship surfaces still say `4.6.0`; recent git history includes v4.7.0 bump/reverts. | Create one release-version source of truth and add CI checks for docs, manifests, package files, and generated release notes. | P0 |
| Local release pipeline | The published v4.6 watchdog-service claim may not ship a watchdog binary. | Release workflow publishes GUI/CLI/tray only; WiX has no watchdog component. | Add Watchdog publish, signing, checksums, release upload, MSI component, and install/service docs. | P0 |
| Local release pipeline | MSI staging references a missing `icon.ico`. | `rg --files` found no root `icon.ico`; release workflow uses `Copy-Item icon.ico`. | Replace with `src/NVMeDriverPatcher/nvme.ico` or add a tracked root icon and a release preflight that fails fast. | P0 |
| Local verification | Restore succeeds but build/test hang locally with .NET 10 SDK and no .NET 9 SDK. | `dotnet restore` passed; `dotnet test` and `dotnet build` timed out; worker processes required cleanup. | Add `global.json`, local runbook, build-timeout diagnostics, and CI parity notes for SDK 9 versus SDK 10. | P0 |
| External news | Registry overrides are unstable on client builds. | Tom's Hardware and Windows Central report Microsoft changed/blocked the registry trick on Insider builds. | Add a build/channel ruleset and a release-monitor task so UI copy and fallback routing stay current. | P1 |
| External docs | BypassIO is a DirectStorage performance path and driver vetoes degrade gaming experience. | Microsoft Learn BypassIO doc. | Make DirectStorage/BypassIO a first-class decision workflow with per-volume history and gamer-safe recommendations. | P1 |
| Local CLI/docs | Implemented CLI commands exceed documented command list. | `Program.cs` command switch versus `PrintUsage()` and README. | Generate CLI help and docs from command metadata; add command-discovery tests. | P1 |
| Local GUI | Main window XAML is very large and feature-dense. | `MainWindow.xaml` has about 2,500 lines. | Split workspace tabs into smaller views and add screenshot QA targets for each tab. | P2 |
| Community comments | Users report BitLocker/recovery and Safe Mode risks when manual guides omit SafeBoot keys. | Reddit snippets and existing app design both emphasize SafeBoot and BitLocker. | Add "recovery proof" UX that requires users to confirm recovery-kit path, BitLocker key access, and SafeBoot entries before patching higher-risk profiles. | P1 |

### Cycle 5: Release And Build Triage

**Build environment findings:**

- CI and release workflows explicitly install `.NET 9` with `actions/setup-dotnet@v4`, but this local machine resolves `dotnet` to SDK `10.0.300`; no SDK 9.x appears in `dotnet --info`.
- No `global.json` is present, so local contributors can silently build with a different SDK than CI.
- The solution includes five projects, including `NVMeDriverPatcher.Watchdog`, and CI builds the whole solution. Release publishing does not publish the watchdog project.
- The current local build/test timeout may be SDK mismatch, UNC/network filesystem behavior, WPF build target behavior, or an MSBuild worker hang. The next useful diagnostic should generate a binary log and compare SDK 9 versus SDK 10 from a local-drive clone or mapped drive.

**Release workflow findings:**

- `.github/workflows/release.yml` publishes GUI, CLI, and tray only. It signs/checksums/uploads those same surfaces plus MSI and the legacy PowerShell script; no watchdog exe is included.
- `src/NVMeDriverPatcher.Watchdog/Program.cs` supports `/install` and `/uninstall` only. It wraps `sc.exe create/delete`, runs as LocalSystem, starts automatically, and drains stdout/stderr asynchronously to avoid pipe-buffer deadlocks.
- The watchdog service has no dedicated packaged status/start/stop command in the service exe, and the main CLI does not currently expose service installation lifecycle commands.
- `packaging/wix/NVMeDriverPatcher.wxs` installs GUI, CLI, tray, `compat.json`, icon, event log source, Start Menu shortcut, and ADMX files. It does not install/register the watchdog service.
- `packaging/wix/README.md` says the MSI has three features, publishes three projects, copies `icon.ico`, and shows a sample output `NVMeDriverPatcher-4.5.0.msi`; it is stale versus v4.6+ and the watchdog-service claim.
- The changelog says v4.6.0 added the optional Windows Service and added it to the solution; it also says all csproj, winget, WiX, and README versions were bumped to `4.6.0`. Current repo state has only the GUI project at `4.6.1`.

**Roadmap implications:**

- AR-2026-002 should include both packaging and lifecycle UX: publish/sign/checksum/upload watchdog, add WiX service install, add CLI/GUI service state, and decide whether default install is disabled, manual, or feature-selected.
- AR-2026-003 should validate every release input before the release body is created. Missing `icon.ico`, missing MSI, or missing watchdog artifacts should fail with an explicit message unless intentionally configured as optional.
- AR-2026-004 should add SDK and runtime parity checks: CI can keep .NET 9, but local docs need `global.json` or an install/runtime recipe so SDK 10 does not become the accidental default and tests do not abort from missing .NET 9 runtime/Desktop runtime.

### Cycle 6: Release Artifact Matrix

**Release contract findings:**

- `.github/workflows/release.yml` publishes GUI, CLI, and tray self-contained executables. It does not publish `src/NVMeDriverPatcher.Watchdog`, even though the solution and changelog describe the watchdog service as a v4.6 capability.
- The release workflow signs GUI, CLI, tray, and MSI artifacts only. It does not sign a watchdog executable because no watchdog publish output exists in the workflow.
- The release workflow checksums GUI, CLI, tray, MSI, and the legacy `NVMe_Driver_Patcher.ps1`; watchdog, PowerShell module files, ADMX/ADML sidecars, schema files, and Intune helpers are not represented in the checksum target list.
- The "Stage MSI inputs" step copies `icon.ico` from the repository root, but the tracked icon asset is `src/NVMeDriverPatcher/nvme.ico`; a missing root `icon.ico` can abort the release before the nominally best-effort WiX step.
- The WiX build step has `continue-on-error: true`, but the final release upload requires the MSI glob with `fail_on_unmatched_files: true`. The release therefore treats MSI as optional during build but required during upload, which makes failure behavior ambiguous and late.
- `packaging/winget/SysAdminDoc.NVMeDriverPatcher.yaml` has a fixed `InstallerUrl` pointing at `v4.6.0`. The workflow updates `PackageVersion` and `InstallerSha256`, but not the tag segment in `InstallerUrl`, so a new generated manifest can pair a new hash with an old download URL.
- `AutoUpdaterService.FetchLatestAssetAsync` selects the first release asset whose name ends with `.exe`. Because the release uploads GUI, CLI, and tray `.exe` assets, the updater should prefer exact `NVMeDriverPatcher.exe` by name and treat CLI/tray/watchdog exes as non-update payloads.
- `packaging/powershell` contains a module that shells to `NVMeDriverPatcher.Cli.exe`, but the release workflow does not upload a module zip nor publish a PSGallery package with the CLI beside it.
- `packaging/intune/Detect-NVMeDriverPatcher.ps1` assumes an MSI install path and EventLog registry marker. Intune/SCCM delivery is only reliable if MSI is made a required release artifact or an `.intunewin` package is generated from known-good MSI output.
- Local ignored `publish/` outputs are stale and incomplete (`gui` and `cli` exes exist from older dates; tray/watchdog/staging outputs are missing), so local publish state should not be used as release evidence.

**Artifact coverage matrix:**

| Artifact / Channel | Built | Staged / Packaged | Signed | Checksummed | Uploaded / Published | Current Risk | Priority |
|---|---|---|---|---|---|---|---|
| GUI EXE `NVMeDriverPatcher.exe` | Yes | Yes | Yes | Yes | Yes | Auto-updater can choose another `.exe` asset unless exact-name selection is enforced. | P0 |
| CLI EXE `NVMeDriverPatcher.Cli.exe` | Yes | MSI input only | Yes | Yes | Yes | PowerShell module expects CLI availability, but module distribution does not bundle it. | P1 |
| Tray EXE `NVMeDriverPatcher.Tray.exe` | Yes | MSI input only | Yes | Yes | Yes | Release path covers it; local ignored publish output is stale/missing, so docs should avoid local publish assumptions. | P2 |
| Watchdog EXE `NVMeDriverPatcher.Watchdog.exe` | No | No | No | No | No | Shipped capability claim is not backed by public artifact, MSI component, service install, or release sidecar. | P0 |
| MSI package | Attempted | Yes, but root `icon.ico` may be missing | Yes, if built | Yes, if built | Required by release upload | Build is configured as best-effort while upload is required; failures surface late and break Intune assumptions. | P0 |
| Winget manifest | Generated from template | N/A | N/A | Uses GUI hash | Uploaded as artifact | `InstallerUrl` remains pinned to `v4.6.0` while version/hash are replaced. | P0 |
| Legacy PowerShell script | Source file | N/A | No explicit script signing | Yes | Yes | README still promotes it; integrity sidecar exists but Authenticode/signing posture is unclear. | P1 |
| PowerShell module | Source files only | No module zip / PSGallery job | No | No | No | Module docs imply installability but release gives no supported package. | P1 |
| Intune/SCCM package | Detection script only | No `.intunewin` | No | No | No | Managed deployment depends on MSI availability but has no generated deployment bundle. | P1 |
| ADMX/ADML templates | Source files | MSI only | No | No separate sidecar | No separate release asset | Enterprise policy admins cannot consume templates without MSI extraction. | P2 |
| JSON schemas | Source files | No | No | No | No | External automation cannot pin schema versions from release assets. | P2 |
| SHA256 sidecars / `SHA256SUMS.txt` | Yes | N/A | N/A | N/A | Yes | Target list omits watchdog/module/admin assets; MSI sidecar depends on optional build. | P0 |
| Authenticode signing | Conditional | N/A | EXE/MSI only | N/A | N/A | Unsigned releases should state signing skipped and preserve hash evidence in the release body. | P2 |

**Roadmap implications:**

- AR-2026-015 should formalize a release artifact contract and validation script before another tag. Each artifact needs an explicit owner, source path, publish command, version source, signing expectation, checksum sidecar, upload policy, and optional/required flag.
- AR-2026-016 should harden auto-update asset selection so GUI updates cannot accidentally stage CLI, tray, watchdog, or future admin executables.
- AR-2026-017 should convert the existing PowerShell/Intune packaging assets into supported release channels rather than source-only folders.

### Cycle 7: Build/Test Hang Triage

**Environment and CI parity findings:**

- `dotnet --info` on this machine reports SDKs `8.0.421` and `10.0.300`, but no .NET 9 SDK. Installed runtimes include .NET 6, 8, and 10, but no `Microsoft.NETCore.App 9.0.x` or `Microsoft.WindowsDesktop.App 9.0.x`.
- No `global.json` exists in the repo root, so local SDK resolution currently chooses SDK `10.0.300` while CI and release workflows install `9.0.x`.
- `.github/workflows/ci.yml` uses `dotnet restore`, `dotnet build -c Debug --no-restore`, `dotnet test -c Debug --no-build`, and a release-mode compile smoke test. The release workflow also installs `9.0.x`.
- All five solution projects target `net9.0-windows`. GUI, CLI, tray, and watchdog set `SelfContained=true` and `RuntimeIdentifier=win-x64`; the test project is framework-dependent and references the GUI project.
- A bounded diagnostic build from the UNC repo path completed successfully in this cycle: `dotnet build NVMeDriverPatcher.sln -c Debug --no-restore --verbosity minimal /m:1 /bl:<work>\solution-debug-20260606-102422.binlog` finished in about 15 seconds with `Build succeeded`, `0 Warning(s)`, and `0 Error(s)`.
- The CI-style test command did not hang in this cycle. `dotnet test NVMeDriverPatcher.sln -c Debug --no-build --verbosity minimal` aborted in about 4 seconds because `testhost.exe` requires `Microsoft.NETCore.App` version `9.0.0`; the installed runtime set does not include .NET 9.
- `tests/NVMeDriverPatcher.Tests/bin/Debug/net9.0-windows/NVMeDriverPatcher.Tests.runtimeconfig.json` explicitly requires `Microsoft.NETCore.App` `9.0.0` and `Microsoft.WindowsDesktop.App` `9.0.0`.
- A `VBCSCompiler.exe` process remained after the successful build and was stopped. No `dotnet`, `MSBuild`, `VBCSCompiler`, or `testhost` processes remained after the bounded test run.
- The earlier 120-180 second build/test timeout is not currently reproducible after process cleanup and a successful no-restore build. The confirmed hard blocker is local runtime parity: tests cannot execute without .NET 9 runtime/Desktop runtime even though SDK 10 can compile the `net9.0-windows` projects.

**Diagnostic artifacts saved outside the repo:**

- `C:\Users\Matt\Documents\Codex\2026-06-06\autonomous-clean-win11-nvme-driver-patcher\work\cycle7-build-triage\solution-debug-20260606-102422.binlog`
- `C:\Users\Matt\Documents\Codex\2026-06-06\autonomous-clean-win11-nvme-driver-patcher\work\cycle7-build-triage\solution-debug-20260606-102422.out.log`
- `C:\Users\Matt\Documents\Codex\2026-06-06\autonomous-clean-win11-nvme-driver-patcher\work\cycle7-build-triage\solution-test-20260606-102505.err.log`

**Roadmap implications:**

- AR-2026-004 should stop describing the current state as an unresolved build hang. The current evidence is: build succeeds under SDK 10 from UNC after cleanup, while tests abort because .NET 9 runtime/Desktop runtime are absent.
- The repo needs a CI-parity doctor command that checks SDK, runtime, Desktop runtime, `global.json`, working directory style, and child process cleanup before running the full build/test suite.
- If the project stays on `net9.0-windows`, either install and document .NET 9 SDK/Desktop runtime locally or add a repo-local setup script that uses `dotnet-install.ps1`/known installer steps. A `global.json` must pin an exact SDK version plus roll-forward policy; it cannot use the CI-style `9.0.x` wildcard.
- Build/test troubleshooting should include bounded runs with binary logs and explicit cleanup, but routine CI should keep normal verbosity.

### Cycle 8: CLI Discoverability Audit

**CLI route inventory findings:**

- `src/NVMeDriverPatcher.Cli/Program.cs` routes 49 operational command entries through `command switch` plus `help` and `version` pre-switch handling.
- The router accepts 74 unique command tokens when aliases are included: `install`, `uninstall`, `export-diagnostics`, `support-bundle`, `vivetool-fallback`, `export-recovery-kit`, `preview`, `events-tail`, `per-controller`, `feature-store`, `help-topic`, `html-report`, `firmware-nudge`, `a11y`, `window`, and others.
- `PrintUsage()` lists only 20 help entries and collapses `verifier-on/off` into one line. It omits `help`, `guardrails`, `controllers`, `tail`, `physical-disks`, `bypassio`, `apst`, `identify`, config import/export, tuning import/export, benchmark comparison, compat checksum, backup verification, scheduled task registration, portable mode, update check, WinRE, FeatureStore, recovery-kit freshness, docs/help-topic, clean-data, dashboard/html-report, firmware nudge, Safe Mode verify, accessibility, and maintenance-window.
- The README extended CLI section documents only the v4.4-era commands (`apply --dry-run`, `watchdog`, `reliability`, `minidump`, `firmware`, `scope`, `etw`, `winpe`, `telemetry`, and Driver Verifier). It does not document later operational routes such as `controllers`, `guardrails`, `bypassio`, `apst`, `identify`, `config-export`, `compare-benchmarks`, `update-check`, `featurestore`, `docs`, `clean-data`, `dashboard`, `fw-nudge`, `safemode-verify`, or `accessibility`.
- `packaging/powershell/NVMeDriverPatcher.psm1` wraps eight CLI commands: `status`, `apply`, `remove`, `watchdog`, `controllers`, `dry-run`, `diagnostics`, and `dashboard`. The module README lists those eight, but release packaging still needs to decide whether PowerShell should cover only stable lifecycle/fleet commands or every CLI route.
- There are no apparent router-level tests for `Program.Main`, `IsKnownOperationalCommand`, unknown-command handling, `PrintUsage()` coverage, or alias coverage. Current test references are primarily service-level tests.
- Because the CLI assembly has an admin manifest and most operational commands are gated by `PreflightService.IsRunningAsAdmin()`, a descriptor/test design needs a non-admin-safe way to validate routing and help coverage without executing privileged operations.

**Risk grouping for command metadata:**

| Group | Commands | Risk / Permission Model | Documentation Need |
|---|---|---|---|
| Public lifecycle | `status`, `apply`, `remove`, `dry-run`, `verify`, `recovery-kit`, `bundle`, `diagnostics` | Admin required except help/version; changes system or writes support artifacts. | README, PrintUsage, PowerShell wrappers, exit codes. |
| Post-patch stability | `watchdog`, `reliability`, `minidump`, `tail`, `guardrails`, `controllers`, `physical-disks` | Mostly read-only but admin-gated by current router. | Help grouping plus examples for interpreting risk. |
| Storage diagnostics | `bypassio`, `apst`, `identify`, `firmware`, `fw-nudge`, `scope`, `etw` | Mix of read-only and trace capture; ETW can write files and require tooling. | Clear output artifact paths and gaming/DirectStorage caveats. |
| Config and tuning | `config-export`, `config-import`, `tuning-export`, `tuning-import`, `compat-checksum`, `verify-backup`, `clean-data` | Writes or deletes local app data; needs explicit backup/rollback copy. | Options, required arguments, exit codes, data-loss warnings. |
| Admin/fleet | `register-tasks`, `unregister-tasks`, `portable-enable`, `portable-disable`, `winre`, `winpe`, `telemetry`, `dashboard`, `maintenance-window` | Schedules tasks, writes local mode flags, builds admin artifacts, or submits telemetry. | Fleet docs and PowerShell wrapper decision. |
| Experimental/research | `fallback`, `featurestore`, `docs`, `update-check`, Driver Verifier commands, `accessibility`, `safemode-verify` | Can invoke external dependency, inspect FeatureStore, stress drivers, or generate Safe Mode scripts. | Prominent risk labels and confidence/source fields. |

**Descriptor design recommendation:**

- Add a `CliCommandDescriptor` table with `Name`, `Aliases`, `Group`, `Summary`, `LongDescription`, `Options`, `RequiresAdmin`, `RiskLevel`, `IsExperimental`, `OutputKind`, `ExitCodes`, `HandlerKey`, `ReadmeVisibility`, `PowerShellWrapper`, and `Examples`.
- Generate `IsKnownOperationalCommand`, `PrintUsage()`, README CLI markdown, and a PowerShell wrapper coverage report from descriptors rather than hand-maintaining each list.
- Keep high-risk or rarely used commands discoverable but grouped under "Advanced diagnostics", "Fleet/admin", or "Experimental" so the help output stays scan-friendly.
- Add tests that parse the descriptor table and assert every handler has a descriptor, every alias routes to the same handler, every public command has examples/exit codes, and every hidden/experimental command explicitly declares why it is not in README or PowerShell.

### Feature Backlog Additions

| ID | Feature | Description | User Value | Business Value | Evidence | Effort | Impact | Priority | Confidence |
|---|---|---|---|---|---|---|---|---|---|
| AR-2026-010 | Offline Image Mode | Allow apply/remove/verify planning against mounted Windows images for Intune/SCCM lab builds without touching the host OS. | 4 | 4 | DriverStoreExplorer's online/offline model; repo already has WinPE/Intune packaging. | L | Medium | P2 | Medium |
| AR-2026-011 | Driver Backend Abstraction | Introduce a backend capability layer for WMI, registry, PnPUtil, DISM, and native API queries with explicit fallbacks. | 4 | 3 | DriverStoreExplorer uses multiple APIs; current services call several Windows surfaces directly. | L | Medium | P2 | Medium |
| AR-2026-012 | GUI Workspace Decomposition | Split `MainWindow.xaml` into focused tab/user controls and add screenshot QA scripts for key states. | 3 | 3 | Monolithic 2,500-line XAML slows UI iteration and regression isolation. | M | Medium | P2 | High |

### Detailed Feature Specs

### Technical Architecture Improvements

| Area | Current Observation | Recommended Improvement | Why It Matters | Priority |
|---|---|---|---|---|
| UI structure | `MainWindow.xaml` is monolithic. | Split tabs/workspaces into focused user controls and add screenshot QA. | Lowers UI regression risk. | P2 |
| Offline/admin workflows | Intune/WinPE packaging exists, but mounted-image patch planning is not first-class. | Add offline image mode with dry-run first. | Supports imaging labs without touching the host OS. | P2 |

### Design System and Premium UI Plan Additions

- Turn the first viewport into a "Patch Readiness Command Center": current stack, Windows build rule, recovery readiness, BypassIO impact, and next action.
- Add a "Trust and Recovery" panel that shows recovery kit freshness, BitLocker reminder, SafeBoot entries, backup integrity, and support-bundle status in one place.
- Add a "Gaming / DirectStorage" persona switch or recommendation badge driven by BypassIO per-volume state.
- Add service-state cards for tray agent and real-time watchdog service: installed, running, stopped, unavailable, needs elevation, and package missing.
- Add a CLI/docs parity generated reference surfaced from GUI Help so advanced users can discover non-GUI diagnostics.
- Break Diagnostics+ into scoped panels: Stability, Storage Stack, Performance, Recovery, Packaging/Admin, and Advanced.
- Preserve restrained Windows-admin styling; avoid decorative UI that competes with risk/status information.

### Implementation Phases Update

#### Phase 0: Release Integrity And Verification

**Goals:** Make the current repo shippable and verifiable before adding new user-facing scope.

**Features:** (all shipped).

**Dependencies:** Decide whether to pin .NET 9 or migrate to .NET 10.

**Estimated complexity:** Medium.

**Risks:** Release workflow changes can affect all artifacts; validate on a dry-run tag or workflow_dispatch.

**Definition of done:** Versions agree, watchdog is packaged or claims are corrected, missing icon reference is fixed, release artifact validation passes before upload, updater selects only the GUI asset, and restore/build/test complete under documented local and CI environments.

#### Phase 1: Current-Build Truthfulness

**Goals:** Keep client/server build behavior, fallback routing, and risk explanations current.

**Features:** AR-2026-006.

**Dependencies:** External research refresh and local ruleset schema.

**Estimated complexity:** Medium to Large.

**Risks:** Native FeatureStore writing is risky; keep write path gated until the decoder/corpus proves correct.

**Definition of done:** App can explain matched build behavior, support bundles include matched rule/fallback trust data, and native writer research has fixtures before any write operation.

#### Phase 2: Discoverability And Recovery Confidence

**Goals:** Make existing functionality easier to find and safer to execute.

**Features:** AR-2026-007, AR-2026-008, AR-2026-009.

**Dependencies:** Command metadata and UX decomposition.

**Estimated complexity:** Medium.

**Risks:** More pre-apply gates can annoy power users; keep expert bypasses but make defaults conservative.

**Definition of done:** CLI help/docs are complete, recovery proof is visible before apply, BypassIO/DirectStorage impact has per-volume evidence.

#### Phase 3: Fleet And Lab Workflows

**Goals:** Improve enterprise/lab repeatability without turning the app into a generic storage manager.

**Features:** AR-2026-010, AR-2026-011, AR-2026-017.

**Dependencies:** Backend abstraction and build/package integrity.

**Estimated complexity:** Large.

**Risks:** Offline image mode and driver backend abstraction can drift into broad driver-store management. Keep every workflow tied to enable/disable/verify/rollback.

**Definition of done:** Mounted-image dry-run exists, backend probes are explicit, compat DB lifecycle is testable and source-backed, and PowerShell/Intune release packages are generated from validated artifacts.

#### Phase 4: UI Maintainability And Premium Workflow Polish

**Goals:** Reduce WPF change risk and improve repeated-use ergonomics.

**Features:** AR-2026-012 plus screenshot QA.

**Dependencies:** No major functional blockers.

**Estimated complexity:** Medium.

**Risks:** UI refactor can regress bindings. Use narrow component extraction with screenshot/interaction checks.

**Definition of done:** MainWindow is decomposed by workspace, key states have screenshot baselines, and the app still handles high contrast/reduced motion/text scale.

### Research Log

| Date | Cycle | Research Area | Sources / Files Reviewed | Key Findings | Roadmap Changes |
|---|---|---|---|---|---|
| 2026-06-06 | Cycle 1 | Repository comprehension | `README.md`, `CLAUDE.md`, `ROADMAP.md`, `rg --files`, git log/status | Mature WPF/CLI/tray/watchdog project; existing roadmap is historically useful but stale versus v4.6.1 drift and packaging state. | Added current summary, users, jobs, strengths, weaknesses. |
| 2026-06-06 | Cycle 2 | Source/package/test inventory | `src/**/*.csproj`, `Program.cs`, `MainWindow.xaml`, `.github/workflows/*`, `packaging/*`, `tests/*` | 37 test files and 176 tests; CLI surface is larger than docs/help; release omits watchdog and references missing `icon.ico`. | Added feature inventory, pain points, P0 backlog items. |
| 2026-06-06 | Cycle 3 | Verification | `dotnet --info`, `dotnet restore`, `dotnet build`, `dotnet test`, process cleanup | SDK 10.0.300 present; restore succeeds; build/test time out locally; raw UNC child-tool behavior can default to `C:\Windows`. | Added Build Environment Hardening spec and continuation item. |
| 2026-06-06 | Cycle 4 | External research | Microsoft Tech Community, Microsoft Learn BypassIO, Microsoft Learn PnPUtil, Tom's Hardware, Windows Central, DriverStoreExplorer, ViVe GitHub | Server native NVMe is official/opt-in; client registry behavior changed; ViVeTool fallback IDs remain a moving target; BypassIO matters for DirectStorage. | Added competitive research, ruleset, BypassIO, FeatureStore, and support-bundle trust items. |
| 2026-06-06 | Cycle 5 | Release and build triage | `src/NVMeDriverPatcher.Watchdog/Program.cs`, `.github/workflows/ci.yml`, `.github/workflows/release.yml`, `packaging/wix/*`, `CHANGELOG.md`, `NVMeDriverPatcher.sln` | CI pins SDK 9 while local uses SDK 10; release omits watchdog; WiX docs/package omit watchdog and reference stale icon/version samples. | Added release/build triage section and refined AR-2026-002 through AR-2026-004. |
| 2026-06-06 | Cycle 6 | Release artifact matrix | `.github/workflows/release.yml`, `packaging/wix/*`, `packaging/winget/*`, `packaging/powershell/*`, `packaging/intune/*`, `src/NVMeDriverPatcher/Services/AutoUpdaterService.cs`, local `publish/` inventory | MSI optional/required behavior conflicts; winget URL replacement is incomplete; updater can select the wrong `.exe`; watchdog, PowerShell, Intune, ADMX, and schemas lack release-channel coverage. | Added artifact coverage matrix plus AR-2026-015 through AR-2026-017. |
| 2026-06-06 | Cycle 7 | Build/test hang triage | `dotnet --info`, `dotnet --list-sdks`, `.github/workflows/ci.yml`, project `.csproj` files, `NVMeDriverPatcher.Tests.runtimeconfig.json`, bounded `dotnet build`/`dotnet test` diagnostics | Build succeeds from UNC under SDK 10 after cleanup; tests abort because .NET 9 runtime/Desktop runtime are missing; no `global.json`; previous hang is not currently reproducible. | Added Cycle 7 section and refined AR-2026-004 into a CI-parity doctor/runtime-prerequisite plan. |
| 2026-06-06 | Cycle 8 | CLI discoverability audit | `src/NVMeDriverPatcher.Cli/Program.cs`, `PrintUsage()`, `README.md`, `packaging/powershell/NVMeDriverPatcher.psm1`, `packaging/powershell/README.md`, `tests/NVMeDriverPatcher.Tests/*` | CLI has 49 routed command entries and 74 accepted tokens; `PrintUsage()` lists 20 entries; README/PowerShell/tests each cover different subsets and no router-level tests were found. | Added Cycle 8 section and expanded AR-2026-008 descriptor schema, generated docs/help, PowerShell coverage, and router-test acceptance criteria. |

### Research Queries To Run Later

- `site:blogs.windows.com/windows-insider nvmedisk.sys native NVMe 25H2 26H2`
- `site:support.microsoft.com Windows 11 25H2 nvmedisk native NVMe KB`
- `site:learn.microsoft.com nvmedisk Storage Disks SafeBoot`
- `site:github.com native nvme windows 11 nvmedisk patcher`
- `site:github.com thebookisclosed ViVe FeatureStore protobuf feature configurations`
- `DriverStoreExplorer native API DISM PnPUtil backend auto detection source`
- `Windows 11 DirectStorage BypassIO nvmedisk stornvme driver veto`
- `KB5079391 nvmedisk SafeBoot service name storage disks`
- `Windows Server 2025 native NVMe group policy MSI 1176759950`
- `VeraCrypt native NVMe nvmedisk BitLocker recovery prompt`

### Open Questions

- Should the canonical current version be `4.6.1`, or should recent v4.7.0 reverted commits trigger a new `4.6.2`/`4.7.1` release train?
- Is the watchdog service intended to ship in public releases, or is it a source-only advanced component for now?
- Should the MSI remain best-effort, or should missing MSI inputs fail the release before GitHub assets are created?
- Should local development pin .NET 9 SDK or intentionally migrate to .NET 10 while still targeting `net9.0-windows` or a newer target?
- What caused the local build/test hang: SDK mismatch, UNC/network filesystem behavior, WPF build targets, source generators, analyzer behavior, or something else?
- Should `windows_build_rules.json` be bundled only, updateable from GitHub releases, or both?
- How much of FeatureStore should be decoded before a native writer is considered safe enough even behind an experimental switch?

### Next Research Cycles

1. Cycle 9: Windows build/channel ruleset schema and initial data population.
2. Cycle 10: FeatureStore writer research plan, decoder fixtures, blob export safety, and ViVeTool trust ledger.
3. Cycle 11: DirectStorage/BypassIO UX flow and per-volume history model.
4. Cycle 12: Recovery proof gate UX and CLI/GUI acceptance criteria.
5. Cycle 13: Compat DB lifecycle model and telemetry feedback loop.
6. Cycle 14: MainWindow decomposition plan with screenshot QA states.
7. Cycle 15: Offline image/WinPE/mounted-WIM dry-run mode feasibility.
8. Cycle 16: Release-channel documentation pass after artifact contract decisions are made.
9. Cycle 17: CI-parity implementation design after a .NET 9 SDK/runtime pin is chosen.
10. Cycle 18: PowerShell module coverage design after CLI descriptors define stable public commands.

## Continuation State

### Last Completed Cycle

Cycle 8: CLI discoverability audit.

### Current Focus

Continue with Cycle 9: Windows build/channel ruleset schema and initial data population. The highest-value next work is to inspect existing build/version/fallback logic, current compat/rules data, and external Windows native NVMe rollout sources, then refine AR-2026-006 with a concrete JSON schema and rule matching model.

### Important Findings So Far

- `ROADMAP.md` has been expanded with a dated 2026-06-06 autonomous section instead of replacing historical tiers.
- `dotnet restore NVMeDriverPatcher.sln --verbosity minimal` succeeds.
- Earlier `dotnet test` and `dotnet build` runs timed out after about 120-180 seconds; that hang was not reproducible in Cycle 7 after process cleanup.
- Cycle 7 bounded build from the UNC repo path succeeded in about 15 seconds with `Build succeeded`, `0 Warning(s)`, and `0 Error(s)`.
- Cycle 7 CI-style no-build test aborted in about 4 seconds because .NET 9 runtime/Desktop runtime are missing locally.
- Diagnostic artifacts were saved under `C:\Users\Matt\Documents\Codex\2026-06-06\autonomous-clean-win11-nvme-driver-patcher\work\cycle7-build-triage`.
- `rtk` was requested by repo startup instructions but is not on this Windows PATH; regular `git log -10` was used.
- Claude shared memory dir `C:\Users\Matt\.claude\projects\c--Users----repos\memory` was not found.
- Current git status before editing showed untracked `AGENTS.md`; do not remove or commit it.
- User explicitly said not to commit or push in this thread.
- CI/release workflows pin .NET 9, but local `dotnet` resolves to SDK 10.0.300; no SDK 9 or .NET 9 runtime/Desktop runtime is installed.
- `NVMeDriverPatcher.Watchdog` supports `/install` and `/uninstall`; packaging and main CLI lifecycle surfacing are still missing.
- WiX docs and authoring currently describe a three-project package and do not include the watchdog service.
- Release artifact matrix is now documented for GUI, CLI, tray, watchdog, MSI, winget, legacy PowerShell script, PowerShell module, Intune/SCCM, ADMX/ADML, schemas, checksums, and signing.
- `packaging/winget/SysAdminDoc.NVMeDriverPatcher.yaml` has a fixed `InstallerUrl` pointing at `v4.6.0`; the release workflow updates version/hash but not URL.
- MSI policy is internally inconsistent: WiX build is best-effort, but release upload requires the MSI artifact.
- `AutoUpdaterService.FetchLatestAssetAsync` can select the wrong executable because it chooses the first `.exe` asset instead of exact `NVMeDriverPatcher.exe`.
- AR-2026-004 now tracks a CI-parity doctor script, exact SDK/runtime checks, optional `global.json`, bounded binary logs, and process cleanup guidance.
- Cycle 8 found 49 routed CLI command entries and 74 accepted command tokens in `Program.cs`.
- `PrintUsage()` lists only 20 help entries; README and PowerShell wrappers cover smaller/stable subsets, and no router-level tests were found for `Program.Main`, aliases, unknown commands, or help coverage.
- AR-2026-008 now specifies a `CliCommandDescriptor` schema, generated help/README/PowerShell coverage plan, and router-test acceptance criteria.

### Next Best Actions

1. Inspect build and fallback logic in `DriveService`, `PreflightService`, `PatchVerificationService`, `ViVeToolService`, `FeatureStoreWriterService`, `DocsService`, and `MainViewModel.Guidance`.
2. Compare local logic against current README/ROADMAP claims and targeted external sources for Windows 11/Server native NVMe rollout, blocked registry overrides, fallback IDs, and SafeBoot requirements.
3. Refine AR-2026-006 with a `windows_build_rules.json` schema, matching precedence, source/confidence fields, support-bundle output, and acceptance criteria.

### Unprocessed Leads

- `CHANGELOG.md` should be mined further for v4.7.0 revert context and folded into the versioning plan.
- The exact .NET 9 SDK feature band/global.json policy should be chosen before implementing AR-2026-004.
- PowerShell wrapper coverage should be revisited after AR-2026-008 defines which CLI commands are stable public automation surfaces.
- `packaging/telemetry-receiver/cloudflare-worker.js` should be reviewed for production telemetry hardening and privacy notes.
- `packaging/schemas/*.json` should be checked against current config/data model defaults.

### Files Still To Inspect

- `src/NVMeDriverPatcher/Services/DriveService.cs`
- `src/NVMeDriverPatcher/Services/PreflightService.cs`
- `src/NVMeDriverPatcher/Services/PatchVerificationService.cs`
- `src/NVMeDriverPatcher/Services/ViVeToolService.cs`
- `src/NVMeDriverPatcher/Services/FeatureStoreWriterService.cs`
- `src/NVMeDriverPatcher/Services/DocsService.cs`
- `src/NVMeDriverPatcher/ViewModels/MainViewModel.Guidance.cs`
- `CHANGELOG.md` deeper v4.7.0/v4.6.1 release-history pass
- `packaging/telemetry-receiver/cloudflare-worker.js`
- `packaging/schemas/config.schema.json`
- `packaging/schemas/watchdog.schema.json`
- `src/NVMeDriverPatcher/Services/PatchService.cs`

### Searches Still To Run

- `site:blogs.windows.com/windows-insider nvmedisk.sys native NVMe 25H2 26H2`
- `site:support.microsoft.com KB5083631 nvmedisk native NVMe`
- `site:github.com/thebookisclosed/ViVe FeatureStore protobuf`
- `Windows 11 native NVMe BypassIO DirectStorage nvmedisk stornvme 2026`
- `DriverStoreExplorer backend PnPUtil DISM Native API source code`

## Open-Source Research (Round 2)

### Related OSS Projects
- https://github.com/ken-yossy/nvmetool-win — NVMe IOCTL sample via Windows inbox driver (reference for read-only controller queries without switching drivers)
- https://github.com/lheer/nvmetool-win-exe — prebuilt fork of nvmetool-win, useful as a dependency for diagnostics
- https://github.com/jtjones1001/nvme_info — cross-platform NVMe info CLI (Win + Linux), good CLI UX reference
- https://github.com/gigaherz/nvmewin — OpenFabrics NVMe Windows driver mirror (historical context for StorNVMe vs OFA driver)
- https://github.com/MicrosoftDocs/windows-driver-docs — authoritative StorNVMe + SCSI translation docs, pin specific commits in your docs
- https://github.com/maurice-daly/DriverAutomationTool — PowerShell WPF driver management UX reference (CMTrace log viewer, dark/light runtime toggle, curl.exe download with hash verification + retry)
- https://github.com/lostindark/DriverStoreExplorer — Driver Store GUI with PnPUtil/DISM/native-API backend auto-detection
- https://github.com/microsoft/Windows-driver-samples — DevCon source + install samples, cite for why you chose PnPUtil over DevCon
- https://hochwald.net/post/enabling-native-nvme-drivers-windows-11-server-2025/ — reference write-up on the nvmedisk.sys swap (community prior art)

### Features to Borrow
- CMTrace-compatible XML logging with a built-in log viewer — DriverAutomationTool; complements your existing diagnostics export
- curl.exe-based downloads with HTTP resume + configurable retry + SHA256 hash verification for the driver payload — DriverAutomationTool
- Multi-backend abstraction (Native Win32 API / DISM / PnPUtil) with auto-detection + capability probe — DriverStoreExplorer
- "Old drivers" heuristic pass that flags stale stornvme versions in the driver store and offers cleanup — DriverStoreExplorer
- Offline Windows image mode: apply/remove the patch against a mounted WIM for Intune/SCCM image builds — DriverStoreExplorer offline mode
- Portable-EXE publishing mode (no installer required) for the CLI — DriverAutomationTool; you already self-contain .NET 9 but ship as ZIP vs MSI
- Read-only NVMe controller identify dump in Diagnostics+ tab using nvmetool-win IOCTLs — powers a pre-patch compatibility matrix (vendor/firmware → known-good list)
- Device association view — show which physical NVMe disks each StorNVMe instance backs (by bus/target/LUN) — DriverStoreExplorer "Device Association"
- Light/dark runtime theme toggle without app restart — DriverAutomationTool (your Catppuccin-style WPF resource dictionary can already hot-swap)
- PSGallery parity: also publish CLI subcommands as a pure-PowerShell module variant — DriverAutomationTool distribution pattern

### Patterns & Architectures Worth Studying
- DriverAutomationTool's single-file WPF app architecture: no installer, $PSScriptRoot + embedded XAML, self-contained logs dir — contrasts with your 5-project solution and is worth documenting in README as a deliberate trade-off
- DriverStoreExplorer's backend-capability probe (try PnPUtil → DISM → Native API) wrapped behind an `IDriverStoreBackend` interface — port to your service layer so the Watchdog service can operate headless without WPF dependencies
- PnPUtil `/enum-drivers /class {4d36e967-e325-11ce-bfc1-08002be10318}` filter for enumerating *only* disk-class drivers — reduces noise vs `/enum-drivers` full dump
- Microsoft's deprecation note on DevCon → use as a design-decision reference in CLAUDE.md (you already use pnputil; cite the doc commit so future contributors don't "simplify" back to DevCon)
- Hochwald's nvmedisk.sys migration verification flow (compare before/after `Get-PnpDevice -Class SCSIAdapter`) — formalize as a pester test fixture

## Implementation Deep Dive (Round 3)

### Reference Implementations to Study
- **dotnet/docs `windows-service.md`** — https://github.com/dotnet/docs/blob/main/docs/core/extensions/windows-service.md — canonical .NET 9 `BackgroundService` + `.UseWindowsService()` registration; direct template for the watchdog service.
- **habiburrahman-mu/Dotnet-BackgroundWorkerExamples** — https://github.com/habiburrahman-mu/Dotnet-BackgroundWorkerExamples — side-by-side IHostedService / BackgroundService / WorkerService / Hangfire patterns; useful for comparing the watchdog architecture to alternatives.
- **CommunityToolkit/MVVM-Samples** — https://github.com/CommunityToolkit/MVVM-Samples — `[ObservableProperty]` + `[RelayCommand]` source generators; reference for the Diagnostics+ tab bindings.
- **microsoft/winget-cli `src/Microsoft.WinGet.Client`** — https://github.com/microsoft/winget-cli — PowerShell module reference for Intune/SCCM detection flows.
- **DevExpress-Examples/wpf-mvvm-framework-windowservice** — https://github.com/DevExpress-Examples/wpf-mvvm-framework-windowservice — MVVM-first window/dialog coordination (useful for the tray → GUI invocation).
- **Microsoft Learn `Create Windows Service using BackgroundService`** — https://learn.microsoft.com/en-us/dotnet/core/extensions/windows-service — `Microsoft.Extensions.Hosting.WindowsServices` 9.0.x wiring; required for the service project.
- **jeremybytes/backgroundworker-dotnet6** — https://github.com/jeremybytes/backgroundworker-dotnet6 — `BGW.MVVM` branch shows WPF MVVM + BackgroundWorker with progress/cancellation; adapt for Patcher's long-running DISM operations.

### Known Pitfalls from Similar Projects
- `sc.exe create` with an unquoted binPath containing spaces silently fails — always wrap `"C:\Path With Spaces\svc.exe"` in extra quotes inside the command string.
- `BackgroundService.ExecuteAsync` unhandled exception after .NET 6 kills the host by default — set `HostOptions.BackgroundServiceExceptionBehavior = Ignore` if you want the watchdog to survive transient failures (https://learn.microsoft.com/en-us/dotnet/core/extensions/windows-service).
- Authenticode-signing a self-contained .NET 9 single-file exe requires signing the extracted apphost, not the wrapper — use `signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256` on both the bundle and every AOT-extracted assembly.
- `Microsoft.Extensions.Hosting.WindowsServices` 9.0.x requires running under SYSTEM or a service account — `UseWindowsService()` throws `PlatformNotSupportedException` if launched as a regular user from Visual Studio debug.
- Intune `.intunewin` packages expect a detection script that exits 0 on "present"; returning a non-empty stdout without exit 0 causes spurious "failed" status.
- AppLocker/SRP rules cached in GPO can block unsigned PowerShell modules even after signing — publish to PSGallery (signed) and require module installation via `Install-Module -Scope AllUsers`.
- WPF `RelayCommand.CanExecuteChanged` must raise on the UI thread — `CommunityToolkit.Mvvm` handles this, but manual `ICommand` impls cause stale-button bugs.

### Library Integration Checklist
- `Microsoft.Extensions.Hosting==9.0.1` + `Microsoft.Extensions.Hosting.WindowsServices==9.0.1` — key API: `Host.CreateDefaultBuilder(args).UseWindowsService(opts => opts.ServiceName = "NvmePatcherWatchdog")`. Gotcha: must reference both packages; `UseWindowsService` extension lives in the second.
- `CommunityToolkit.Mvvm==8.3.2` — `[ObservableProperty]`, `[RelayCommand]` source gen. Gotcha: private fields must use underscore prefix OR `[field:]` attribute targeting.
- `Microsoft.WinGet.Client==1.9.25200` — Intune/SCCM package-detection. Gotcha: on LTSC builds without App Installer, call `Repair-WinGetPackageManager -AllUsers` first.
- `Serilog.Sinks.EventLog==4.0.0` — required for the watchdog to write to Windows Event Log properly (source registration must happen as admin once).
- `Microsoft.Win32.TaskScheduler==2.11.0` — https://www.nuget.org/packages/TaskScheduler — managed wrapper for scheduled-task creation (firmware-nudge service); gotcha: PowerShell `Register-ScheduledTask` is simpler if already in an admin context.
- `System.Management.Automation==7.4.6` — host PowerShell runspace inside the WPF process for live script execution. Gotcha: redistributable `System.Management.Automation` is framework-dependent only — self-contained publish requires PSHost fork.
- `Wix==5.0.2` (or `WixToolset.UI.wixext==5.0.2`) — MSI for SCCM deployment; gotcha: Wix 4+ changed schema, old v3 `.wxs` files fail to compile.

## Research-Driven Additions (2026-06-09)

Items below are derived from exhaustive external research (30+ sources) and deep codebase audit.
Duplicates of AR-2026-001 through AR-2026-017 have been excluded.


## Research-Driven Additions (2026-06-10)

Net-new items from the 2026-06-10 research pass (live GitHub release/asset audit, upstream
dependency audit, fallback-ID intelligence refresh). Verified against AR-2026-001..017 and
RD-001..010 for duplicates; relationships to existing items are noted inline. Full evidence
in RESEARCH.md (2026-06-10 revision).

### P1

### P2

- [ ] P2 — Preflight check: feature-management prerequisites broken by debloat tools
  Why: A community report describes the native NVMe driver refusing to bind until previously disabled scheduled tasks were restored — debloated systems are common in exactly this tool's audience, and today the failure is silent and misdiagnosed as the Microsoft block. Reproduce on a debloated VM first; ship the check only once the responsible task/service set is confirmed.
  Evidence: Overclock.net thread 1818467 page 5 user report (secondhand; Needs live validation — see RESEARCH.md Open Question 3).
  Touches: `Services/SystemGuardrailsService.cs` (new finding alongside HVCI/WDAC/VROC/AppLocker), `PreflightService`, tests.
  Acceptance: On a VM with the offending tasks/services disabled, preflight surfaces a named warning with a one-click/`schtasks` restore hint; healthy systems show no new warning.
  Complexity: M (including the reproduction work)

### P3

## Research-Driven Additions (2026-06-10, second pass)

Net-new items from the 2026-06-10 second pass: code audit of previously uninspected services
(PatchService, RegistryService, HotSwap, Benchmark, AutoRevert, CleanData, Scheduler, GPO,
recovery kit, watchdog/tray), test-suite gap map, and external refresh (ViVe #164, mach2
FeatureStore docs, gigxp real-world data, KB5083769). Deduplicated against AR-2026-001..017,
RD-001..010, and the 2026-06-10 first-pass items. Evidence in RESEARCH.md (second-pass revision).

### P1

### P2

### P3

## Research-Driven Additions

Items below were added from the 2026-06-12 research pass after replacing the shared research
prompt's `{REPO_PATH}` with `C:\Users\--\repos\win11-nvme-driver-patcher`. They were
deduplicated against earlier AR/RD items and focus only on native NVMe enablement,
verification, rollback, safety data, and fleet trust.

### P1

- [ ] P1 - Fix telemetry client/receiver schema drift and summary accuracy
  Why: The app sends compatibility telemetry as `controllers[]` plus `verification`, while the Cloudflare receiver summarizes `controller`, `firmware`, and `verificationResult`. Real uploads can therefore appear as `unknown/unknown/Other`, making fleet compatibility summaries misleading.
  Evidence: `src/NVMeDriverPatcher/Services/CompatTelemetryService.cs`, `packaging/telemetry-receiver/cloudflare-worker.js`, Cloudflare Worker/KV docs.
  Touches: `CompatTelemetryService`, `packaging/telemetry-receiver/cloudflare-worker.js`, telemetry receiver README, tests/fixtures.
  Acceptance: A captured app payload fixture produces correct `topControllers`, firmware counts, and verdict distribution in the Worker summary; docs show the real payload shape; regression tests fail if client and receiver fields drift again.
  Complexity: M

- [ ] P1 - Add FeatureStore fallback disable/reset coverage to rollback and recovery flows
  Why: Enablement can use native FeatureStore writes or ViVeTool fallback IDs, but removal and recovery flows focus on registry/SafeBoot cleanup. Users need a way to undo fallback FeatureStore IDs as part of the same trusted rollback story.
  Evidence: `FeatureStoreWriterService`, `ViVeToolService`, `PatchService.Remove`, `RecoveryKitService`, ViVe `/disable` and `/fullreset` behavior.
  Touches: FeatureStore writer/service contracts, CLI command surface, GUI rollback flow, recovery kit output, tests.
  Acceptance: CLI exposes an explicit FeatureStore fallback disable/reset path; GUI rollback reports FeatureStore cleanup status; recovery kit explains what can be undone offline versus inside Windows; tests cover successful disable, partial failure, and no-applied-IDs cases.
  Complexity: L

- [ ] P1 - Verify native FeatureStore writes in both Runtime and Boot stores
  Why: Native writes target Runtime and Boot stores, but current success verification checks Runtime only. A Boot-store failure can be hidden until reboot, which undermines post-apply trust.
  Evidence: `FeatureStoreWriterService.WriteOverrides()` and ViVe issue history around Runtime/Boot store divergence.
  Touches: `FeatureStoreWriterService`, `FeatureStoreCommand`, GUI/CLI result rendering, tests.
  Acceptance: Native write results report per-ID Runtime and Boot status; apply success requires the requested IDs to be enabled in both stores or emits a named partial-failure warning; unit tests cover Runtime-only, Boot-only, both-success, and both-failure states.
  Complexity: M

### P2

- [ ] P2 - Enforce HTTPS-only remote telemetry endpoints
  Why: Compatibility telemetry avoids serials and machine names, but still includes stable OS, CPU, controller, firmware, verification, watchdog, and benchmark data. Remote HTTP endpoints should not be accepted silently.
  Evidence: `CompatTelemetryService.SubmitAsync` currently accepts `http` and `https`; telemetry README describes endpoint submission.
  Touches: `CompatTelemetryService`, CLI endpoint validation, GUI config validation, telemetry docs, tests.
  Acceptance: Non-local `http://` endpoints are rejected with a clear error; `https://` endpoints continue to work; `localhost`/loopback HTTP remains allowed only if explicitly kept for development; tests cover remote HTTP, HTTPS, localhost, malformed URI, and unset endpoint.
  Complexity: S

- [ ] P2 - Add JSON schemas and CI validation for safety data and telemetry payloads
  Why: `windows_build_rules.json` and `compat.json` drive eligibility and warnings, but they do not have schemas beside the existing config/watchdog/drive/maintenance schemas. Telemetry payload shape also needs a contract shared with the receiver.
  Evidence: `windows_build_rules.json`, `compat.json`, `packaging/schemas/*.json`, telemetry client/receiver field drift.
  Touches: `packaging/schemas`, CI workflow, data-loading tests, telemetry receiver fixtures, support bundle metadata.
  Acceptance: Schemas exist for Windows build rules, compatibility data, and telemetry payloads; bundled JSON validates in CI; support bundles include data schema/source/review metadata; intentionally malformed fixture files fail tests.
  Complexity: M

- [ ] P2 - Harden telemetry receiver pagination and rate limiting
  Why: The Worker lists one KV page with `limit: 1000` and then summarizes only a slice, which can omit data once uploads grow. The current KV rate limit is also best-effort check-then-increment logic.
  Evidence: `packaging/telemetry-receiver/cloudflare-worker.js`, Cloudflare KV `list_complete`/cursor docs, Cloudflare Rate Limiting binding docs.
  Touches: Cloudflare Worker, telemetry receiver README, Worker tests/mocks.
  Acceptance: Summary either paginates using cursors or maintains tested aggregate counters; large fixture sets do not silently drop expected records; rate limiting uses a documented binding or clearly documented durable alternative with tests for limit and reset behavior.
  Complexity: M

### P3

- [ ] P3 - Add charting/native dependency smoke coverage before Skia/OpenTK/HarfBuzz updates
  Why: Dependency audit found only low-risk transitive updates, but chart rendering depends on native graphics packages. A storage safety UI should not accept chart regressions while updating UI-native dependencies.
  Evidence: `dotnet list package --outdated --include-transitive`, LiveCharts/SkiaSharp WPF diagnostics surfaces.
  Touches: GUI smoke tests, chart view models, dependency-update checklist, CI if an STA/WPF smoke can run reliably.
  Acceptance: A small automated smoke renders the diagnostics/benchmark chart path without exceptions and with non-empty series before native graphics package updates are applied; update notes document any skipped package and reason.
  Complexity: S
