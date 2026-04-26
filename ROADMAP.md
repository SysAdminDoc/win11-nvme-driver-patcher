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
