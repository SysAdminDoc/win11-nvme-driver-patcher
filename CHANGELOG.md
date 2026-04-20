# Changelog

All notable changes to win11-nvme-driver-patcher will be documented in this file.

## [v4.5.0] - 2026-04-19

Feature release — closes every outstanding ROADMAP item that still fit the scope rule, plus
the five loose ends from v4.4.0. 18 new services, 21 new CLI subcommands, an MSI (WiX v4)
scaffold, a Cloudflare Worker reference implementation for the opt-in compat-telemetry
receiver, and a light-theme resource dictionary.

### Added — Watchdog auto-revert path (v4.4 loose end #1)

- **`AutoRevertService`** — runs once on `App.OnStartup` and via the CLI's `watchdog --auto-revert`
  path. Reads `EventLogWatchdogService.Evaluate` + `ShouldAutoRevert`; if Unstable AND the
  user (or GPO) opted into `AutoRevertEnabled`, calls `PatchService.Uninstall` then
  `EventLogWatchdogService.Disarm`. Writes a themed one-shot notice in the GUI path and an
  Event Log record (Warning, ID 3010).
- **`SchedulerService`** — `schtasks` wrapper that registers two tasks under `\SysAdminDoc\
  NVMePatcher\`: `BootVerify` (runs at system startup as SYSTEM) and `WatchdogSweep` (every
  60min). `BootVerify` invokes `NVMeDriverPatcher.Cli watchdog --auto-revert` so the consumer
  runs even when the user never launches the GUI. New CLI commands: `register-tasks` / `unregister-tasks`.

### Added — Event Log source registration (v4.4 loose end #5)

- **`EventLogRegistrationService`** — idempotent call in both `App.OnStartup` and
  `Program.Main`. Creates the `"NVMe Driver Patcher"` source under the `Application` log so
  `EventLogService.Write` stops falling back to the generic source. Silent no-op if not
  elevated (admin is already a prereq but the call is defensive).

### Added — Safety depth

- **`SystemGuardrailsService`** — HVCI / Memory Integrity detection via
  `root\Microsoft\Windows\DeviceGuard\Win32_DeviceGuard.SecurityServicesRunning`, WDAC
  enforcement via `HKLM\...\Control\CI\Protected\EnforceMode`, Intel VROC via
  `Win32_SystemDriver` filter for `iaStorAfs` / `iaStorVD` / `iaVROC`, and NTFS compression
  on `%SystemDrive%`. New CLI: `guardrails`. Blocker-severity VROC aborts with exit 1.
- **`BackupIntegrityService`** — round-trip sanity check on the `.reg` backup file before we
  trust it as our rollback path. Validates `Windows Registry Editor` header, counts key
  sections + value assignments, and cleans up any staging scratch on every exit. New CLI:
  `verify-backup [--import=<path>]` — defaults to the most recent `Pre_*_Backup_*.reg`.

### Added — Enterprise / deployment

- **Silent / unattended CLI** (ROADMAP §1.1). New flag `--unattended` on `apply`: implies
  `--skip-warnings`, pairs with existing `--no-restart`. CLI still respects preflight
  blockers unless `--force` is also passed.
- **`ConfigMigrationService`** (§1.4) — runs before `GpoPolicyService.ApplyTo`. Ships a v1→v2
  no-op hook and a v2→v3 hook that stamps `ConfigVersion = 3` so the v4.5 watchdog fields
  have a precedent to migrate from.
- **`ConfigImportExportService`** (§2.9) — `config-export` / `config-import` CLI commands.
  Bundles AppConfig (user-safe subset), `drive_scope.json`, and `watchdog.json` into a single
  portable JSON.
- **`PortableModeService`** (§2.7) — `portable-enable` / `portable-disable` CLI commands.
  Creates `portable.flag` beside the exe; future launches redirect to `Data\` beside the exe
  instead of `%LocalAppData%`. Lets field techs carry the patcher on a USB stick without
  installer state.
- **`AutoUpdaterService`** — downloads the latest GitHub release asset (.exe) into a staging
  dir, validates the allowed host list (mirrors `ViVeToolService`), and emits a PowerShell
  one-liner the user runs post-exit to swap the file. New CLI: `update-check`.

### Added — Observability / correlation

- **`PerControllerAuditService`** (§2.1) — per-controller version of
  `PatchVerificationService`. Enumerates `Win32_PnPSignedDriver WHERE DeviceClass='SCSIAdapter'
  OR 'DiskDrive'`, filters to NVMe-matching drivers, reports which controllers have `nvmedisk`
  bound vs `stornvme`. New CLI: `controllers` / `per-controller`.
- **`EventLogTailService`** (§2.2) — ad-hoc tail of the last N minutes of `System` events
  filtered to the NVMe stack providers (nvmedisk, stornvme, storport, storahci, disk,
  partmgr, volmgr, Kernel-Power). Single XPath query so chatty systems don't time out.
  New CLI: `tail` / `events-tail`.
- **`PhysicalDiskTelemetryService`** (§2.4) — `MSFT_PhysicalDisk` + `MSFT_StorageReliabilityCounter`
  rollup (health, operational status, media type, bus type, temperature, wear, PoH,
  uncorrected read/write errors). Degrades gracefully on minimal SKUs. New CLI:
  `physical-disks`.
- **`BypassIoInspectorService`** (§2.5) — per-volume `fsutil bypassio state <drive>` wrapper
  for every fixed drive. Parses the stack + enabled state; caps output length. New CLI:
  `bypassio`.
- **`AutoBenchmarkService`** (§2.6 partial) — persistent `baseline.json` with read/write IOPS
  + latency. `Compare` returns a `RegressionVerdict` with per-arm percent deltas and a
  configurable threshold. New CLI: `compare-benchmarks --threshold=<percent>`.

### Added — Tuning surface

- **`TuningProfile` expansion** (§2.3) — added `AsyncEventNotificationEnabled`,
  `ThermalMgmtEnabled`, `PowerStateTransitionLatency`, `NoLowPowerTransitions`,
  `ApstIdleTimeout`. Registry key constants added alongside.
- **`ApstInspectorService`** (§3.2) — reads `stornvme\Parameters\Device` for APST flags +
  enumerates per-state entry/exit latency + idle times. `OverrideIdleTimeout` clamps user
  input to 250µs–60s and writes the override. New CLI: `apst`.
- **`TuningProfileIoService`** (§1.2) — named-bundle `tuning-export` / `tuning-import` CLI
  commands so fleet admins can ship a curated profile across machines.
- **`NvmeIdentifyService`** (§2.8) — raw NVMe Admin Identify Controller via
  `IOCTL_STORAGE_PROTOCOL_COMMAND`. Pulls VID / SSVID / serial / firmware / model from the
  4KB response payload — fields WMI doesn't expose. Feeds `FirmwareCompatService` with the
  authoritative identity. New CLI: `identify`.

### Added — Integrity

- **`CompatChecksumService`** — SHA-256 of the loaded `compat.json` compared against the
  shipped default. Flags user customizations in the support bundle. New CLI: `compat-checksum`.

### Added — Packaging

- **`packaging/wix/NVMeDriverPatcher.wxs`** (WiX v4) — per-machine MSI installer with three
  features (Main / TrayAgent / AdmxTemplates), `util:EventSource` registration for the
  `NVMe Driver Patcher` source, Start-Menu shortcut, and Programs-and-Features entry with
  proper upgrade-code handling. `packaging/wix/README.md` documents the build steps +
  signtool invocation.
- **`packaging/telemetry-receiver/`** — reference Cloudflare Worker implementation for the
  compat-telemetry endpoint. Rehashes anonId with a server-side SALT so leaked client IDs
  can't replay. 16KB payload cap, 1-year KV TTL. Deploy doc included.
- **`src/NVMeDriverPatcher/Themes/LightTheme.xaml`** — companion to DarkTheme. Same key set,
  light palette (zinc-50 / slate-100 / blue-600 accent). Dark stays the default per the
  global CLAUDE.md rule.

### Changed

- `AppConfig.AppVersion` and all `csproj` `<Version>` strings bumped to `4.5.0`.
- `App.xaml.cs` now: rotates logs → registers Event Log source → runs `AutoRevertService.MaybeRun`
  → proceeds with normal startup. Failures in any of the new hooks are swallowed (non-fatal).
- CLI `Program.cs` now: migrates config → applies GPO overlay → rotates logs → registers
  Event Log source → initializes EventLog → dispatches command.
- CLI command table grew from 12 to 33 recognized operational commands.

### Internal

- 18 new services, 1 new XAML resource dictionary, 3 new packaging subdirectories.
- No breaking changes to existing public service APIs.

## [v4.4.0] - 2026-04-19

Feature release. Fourteen new capabilities across stability correlation, enterprise
deployment, and non-admin ambient status. Scope-rule preserved: every item improves how
the patch is enabled, disabled, verified, or rolled back.

### Added — Safety & auto-recovery

- **`EventLogWatchdogService`** — post-patch stability watchdog. Armed on a successful
  Install (`PatchVerificationService.MarkPending` is paired with `EventLogWatchdogService.Arm`),
  disarmed on Uninstall. Watches the `System` channel for Storport ID 129, disk ID 51/153,
  Kernel-Power 41, and BugCheck 1001 inside a configurable window (default 48h / 3-warn / 6-revert).
  Verdicts: `Healthy` / `Warning` / `Unstable` / `Completed` / `Idle`. `Unstable` + opt-in
  `AutoRevertEnabled` is what the next-boot auto-revert path keys on.
- **`MinidumpTriageService`** — scans `C:\Windows\Minidump` + `LiveKernelReports` for dumps
  newer than the patch apply timestamp. Lightweight ASCII string-match for `nvmedisk.sys`,
  `stornvme.sys`, `storport.sys`, `disk.sys`, `partmgr.sys`, `volmgr.sys`. Caps per-file
  scan at 8MB and max 20 dumps so we never chew minutes on giant `LiveKernelReports` files.
- **`ReliabilityService`** — pulls `Win32_ReliabilityStabilityMetrics` (last 30 days),
  computes pre-patch / post-patch averages, and produces a human-readable delta summary.
  Degrades gracefully to "data unavailable" on hardened SKUs where the service is disabled.
- **`FirmwareCompatService` + shipped `compat.json`** — curated
  `{controller substring, firmware or '*'}` → `{Good, Caution, Bad, Unknown}` table. Copy
  precedence: `%LocalAppData%\NVMePatcher\compat.json` (user-editable) → app base directory
  (shipped default). Worst-level wins on ties; exact firmware beats wildcard.

### Added — Deployment control

- **`PerDriveScopeService`** — `drive_scope.json` lets users exclude specific NVMe drives
  from the global swap by serial (from `PNPDeviceID`) or model pattern. Pure decision function;
  preflight renders "3 drives — 1 excluded by scope, 2 will swap."
- **`DryRunService`** — computes exactly what `PatchService.Install` would write, without
  touching the registry. Produces a Markdown table: `WRITE/CREATE/DELETE`, target path, value
  name, `before → after`, kind, note. Wired to CLI `apply --dry-run` and ViewModel
  `PreviewDryRunCommand`. Replays preflight blockers + warnings in the output.
- **`EtwTraceService`** — wraps `wpr.exe` for 60-second pre/post storage-IO captures using
  the inbox `GeneralProfile.Storage` profile. ETL files land in
  `%LocalAppData%\NVMePatcher\etl\`. Cancellation-safe (`wpr -cancel` on abort). Skips
  gracefully when `wpr.exe` is missing on minimal SKUs.
- **`WinPERecoveryBuilderService`** — detects the Windows ADK + WinPE add-on at the two
  canonical install paths, wraps `copype.cmd` + `MakeWinPEMedia.cmd`, copies the existing
  Recovery Kit into the WinPE media root as `NVMe_Recovery_Kit\`, and produces a custom
  `startnet.cmd` that auto-announces the recovery path on boot. x64 only for v4.4.
- **`DriverVerifierService`** — dev-mode harness around `verifier.exe`. `EnableForNVMeStack`
  runs `/standard /driver nvmedisk.sys stornvme.sys disk.sys`; `/reset` backs it out.
  Narrow by design — never enables `/all`. 15s timeout on query, 30s on enable/reset.

### Added — Enterprise & distribution

- **`packaging/admx/NVMeDriverPatcher.admx` + `en-US/NVMeDriverPatcher.adml`** — machine-scope
  policies at `HKLM\SOFTWARE\Policies\SysAdminDoc\NVMeDriverPatcher`: PatchProfile (Safe/Full),
  IncludeServerKey, SkipWarnings, WatchdogAutoRevert, WatchdogWindowHours (1–168),
  CompatTelemetryEnabled.
- **`GpoPolicyService`** — reads the policy overlay and `ApplyTo`s the loaded `AppConfig`.
  Called in both `App.OnStartup` (GUI) and `Program.Main` (CLI) so a pinned fleet policy
  isn't overridden by stale local config.
- **`packaging/winget/SysAdminDoc.NVMeDriverPatcher.yaml`** — portable-installer manifest at
  manifest version 1.6.0. `winget install SysAdminDoc.NVMeDriverPatcher` post-release.
- **`NVMeDriverPatcher.Tray` (new project)** — non-admin system-tray agent. Named-mutex
  single-instance, 30s poll of `RegistryService.GetPatchStatus` + `PatchVerificationService`
  + `EventLogWatchdogService`. Right-click → "Open Main App (elevated)" shells the main
  exe via `runas`. Ships no manifest — the whole point is it doesn't prompt UAC.
- **`CompatTelemetryService`** — opt-in crowdsourced compat report. `BuildReport` composes
  `{anonId (Guid cached in anon_id.txt), appVersion, osBuild, cpu (sanitised),
  controllers[{model, firmware, migrated}], profile, verification, watchdog, watchdogEvents,
  reliabilityDelta, benchmarkDeltaPercent}`. `SubmitAsync` POSTs to a user-provided HTTPS
  endpoint; `SaveReport` writes `compat_report.json` locally. Never contains serials,
  machine names, drive letters, or user names.

### Added — Housekeeping

- **`LogRotationService`** — called in `App.OnStartup` and `Program.Main` before any service
  writes. Rotates `crash.log`, `activity.log`, `watchdog.log`, `diagnostics.log` at 5MB with
  5 generations retained (25MB headroom per-log).
- **CLI** (`Program.cs`) — new subcommands: `dry-run` / `preview`, `watchdog`, `reliability`,
  `minidump` / `triage`, `firmware` / `compat`, `scope`, `etw`, `winpe`, `telemetry`,
  `verifier-on` / `-off` / `-status`. New options: `--dry-run`, `--output=<dir>`,
  `--endpoint=<url>`. `apply --dry-run` shortcuts to the preview path without taking
  Admin-gated actions.
- **`MainViewModel`** — new `[RelayCommand]`s (`RefreshWatchdogStatus`, `RefreshReliability`,
  `TriageMinidumps`, `PreviewDryRun`) plus observable text properties for future XAML
  binding. Watchdog arm/disarm wired into the Install/Uninstall flows.

### Internal

- `AppConfig.AppVersion` and all `csproj` `<Version>` strings bumped to `4.4.0`.
- Tray project added to `NVMeDriverPatcher.sln` as guid `{D4E5F6A7-B8C9-0123-DEF0-234567890123}`.
- `compat.json` marked `CopyToOutputDirectory=PreserveNewest` so the publish drop picks it up.

## [v4.3.7] - 2026-04-17

### Refactored

- **Profile-classification logic extracted from `RegistryService.GetPatchStatus`
  into a pure `ClassifyPatchState` helper.** The v4.3.1 fix for "Safe Mode reports
  as PARTIAL" is now exercised by direct unit tests against the helper's inputs
  (5 booleans + a count) rather than only via the registry-reading call path.
  `GetPatchStatus` retains the same public shape; the pure helper is `internal`
  so it stays opaque to external callers.

### Tests

- **`RegistryServiceClassifyTests` new, 11 cases + a 9-row theory**: empty state
  returns None; clean Safe (primary + both SafeBoot) reports Applied; clean Full
  (all three flags + both SafeBoot) reports Applied; missing-one-SafeBoot demotes
  a Safe install to Mixed; missing-one-extended demotes a Full install to Mixed;
  no-primary-but-extended reports Mixed; only-SafeBoot-entries reports Mixed;
  zero count overrides all booleans; negative count treated as empty; plus an
  `[InlineData]` theory covering the combinations the individual tests don't
  spell out row-by-row. The historical "Safe install shown as PARTIAL" regression
  is now pinned by the `CleanSafeInstall_ClassifiesAsSafeApplied` case.

## [v4.3.6] - 2026-04-17

### Fixed — correctness

- **`EventLogService.Write` no longer splits a UTF-16 surrogate pair at the truncation
  boundary.** The prior implementation's `Substring(0, 30000)` could leave a lone high
  surrogate at the end when the 30000th character landed mid-pair, producing an
  invalid UTF-16 sequence that the Windows Event Log API may reject or mangle. Emoji
  and CJK extension-plane characters in a logged message now truncate cleanly. Pulled
  the truncation into a new `internal` helper (`TruncatePreservingSurrogates`) so the
  behavior is directly unit-testable.

### Tests

- **`PatchVerificationServiceTests` expanded** from one case to eight: covers
  `None` path (no pending flag), whitespace-only timestamp, invalid timestamp
  (StalePending with "invalid" message), over-30-days-old timestamp (StalePending
  with "too old" message), within-window timestamp, Local→UTC kind coercion,
  `MarkPending` ISO-8601 round-trip, `Clear` state recording.
- **`EventLogServiceTests` new**: six cases covering the surrogate-pair fix
  (unchanged-when-short, unchanged-when-equal, BMP-only split, lone-high-surrogate
  trim, pair-after-cutoff preservation, empty/null input).

## [v4.3.5] - 2026-04-17

Polish pass. Small quality fixes across several services and view code-behinds.

### Fixed — safety & defense-in-depth

- **`PatchService.InitiateRestart` now passes arguments via `ProcessStartInfo.ArgumentList`**
  instead of the concatenated-string form. `delaySeconds` is already clamped so there's no
  real injection surface, but the explicit-per-arg form is the pattern the rest of the
  codebase converged on in v4.3.1.
- **CLI rejects conflicting `--safe` / `--full` flags** with exit 3 and a clear error,
  instead of silently picking Safe via `if/else if`. Automation callers deserve an
  explicit audit-trail failure when the intended profile is ambiguous.
- **`BenchmarkService.InstallDiskSpdAsync` takes a `CancellationToken`**, propagated into
  `SemaphoreSlim.WaitAsync`, `HttpClient.GetAsync`, and `HttpContent.CopyToAsync`. A user
  who hits Cancel during the DiskSpd download now aborts cleanly instead of having to
  wait out the full transfer.

### Fixed — concurrency & correctness

- **`BenchmarkComparisonView`, `TelemetryView`, and `TuningPanel` replaced shared
  `BrushConverter` usage** with pre-frozen `SolidColorBrush` singletons (constant palette)
  or a locked cache of parsed hex brushes (dynamic fallback). `BrushConverter` is not
  thread-safe; converting the same hex string on every delta paint was also wasteful.
  Fixes the thread-safety concern raised in the earlier audit pass.
- **`MainWindow` hoists the `Loaded` handler into a named method and unsubscribes it**
  (alongside `StateChanged` and `SizeChanged`) on close, closing a small delegate-leak
  pattern where event handlers retained references to the window after it was closed.

### Fixed — UX polish

- **`TuningProfile.Balanced` / `Performance` / `PowerSave` carry an explicit "do not
  mutate" comment**. These are shared singletons used for read-only UI comparison;
  future contributors should clone before customizing.
- **`AppConfig.GetWorkingDir` now records a `WorkingDirFallbackReason`** when the primary
  LocalAppData path is unavailable and we fall back to TEMP / CWD. Lets startup surface a
  warning on unusual SKUs instead of silently hiding the fact that config is living in a
  temp folder.

## [v4.3.4] - 2026-04-17

Closes the three follow-up items listed in the v4.3.3 summary.

### Fixed — safety

- **HotSwap detects BitLocker-protected volumes before dismount.** Queries
  `Win32_EncryptableVolume` for each captured volume; any protected volume is logged with
  a clear "will require BitLocker unlock after the hot-swap" warning and surfaced on
  `HotSwapResult.BitLockerLockedLetters`. BitLocker is detected-and-informed, not blocked —
  the user gets the information up front instead of discovering the drive is locked after
  the swap completes. Missing BitLocker WMI namespace (older SKUs) is treated as
  "no risk detected" so it doesn't break the swap on systems without the feature.

### Fixed — concurrency

- **Re-entrancy guards are now `private static`.** The Interlocked counters guarded the
  six long-running commands per-`MainViewModel` instance; moving them to `static` covers
  a hypothetical future multi-window scenario where two VMs could concurrently run the
  same registry-mutating command. The single-instance mutex in `App.xaml.cs` already
  handles process-level concurrency; these guards handle in-process concurrency.

### Added — UX

- **Cancel Benchmark button.** Long-awaited UX affordance for a 60+ second operation.
  `BenchmarkService.RunBenchmarkAsync` now takes a `CancellationToken`. MainViewModel
  creates a `CancellationTokenSource` when `RunBenchmark` starts, exposes a
  `CancelBenchmarkCommand` bound to a Cancel button that's visible only while a run is
  active (`BenchmarkRunning` observable property), and disposes the CTS in the finally
  block. `RunDiskSpd` links the external token with its internal 120-second timeout via
  `CreateLinkedTokenSource` and preserves the semantics: a user-cancel throws
  `OperationCanceledException`, while a 120-second timeout still throws
  `InvalidOperationException`. Canceled benchmarks log `[CANCELED]` instead of `[ERROR]`.

## [v4.3.3] - 2026-04-17

### Fixed — reliability

- **`HotSwapService.RemountVolumes` retries `mountvol` up to 3 times** with a 1-second
  backoff between attempts. On slow NVMe controllers the first remount can race the
  storage stack's post-re-enumerate volume-GUID publication and fail with
  "volume not found" even though the device is visibly back. The retry window is also
  auto-mount-aware: if Windows auto-mount lands between retries we detect it and skip
  the redundant call instead of logging a spurious failure.

## [v4.3.2] - 2026-04-17

Follow-up hardening pass on the two items deferred from v4.3.1.

### Fixed — data safety

- **`HotSwapService` now explicitly re-attaches drive letters after dismount.**
  `mountvol /P` removes the mount point and only Windows auto-mount would otherwise bring
  it back. On systems where auto-mount is disabled (servers, SAN hosts, hardened
  workstations) the dismounted volumes stayed offline until the user opened Disk Management.
  We now capture each volume's `(letter, \\?\Volume{GUID}\ path)` before dismount and
  call `mountvol <letter>: <guid>` after the device returns, skipping any letter
  Windows auto-mount already restored. Unrecoverable letters surface on
  `HotSwapResult.FailedRemountLetters` and are logged with a pointer to Disk Management.
  Volume GUIDs are pulled via `Win32_Volume.DeviceID`; volumes that can't be resolved to
  a stable GUID are refused at capture time so we never dismount something we can't
  deterministically restore. `mountvol` is now invoked via `ProcessStartInfo.ArgumentList`
  with a strict `\\?\Volume{…}\` shape check on the GUID path.

### Fixed — concurrency

- **Re-entrancy guards on every long-running command** (`ApplyPatch`, `RemovePatch`,
  `RunBenchmark`, `RunBackup`, `ApplyViVeToolFallback`). `ButtonsEnabled = false` was the
  only prior guard, but it's set AFTER the async method starts — a rapid double-click
  before the binding re-rendered could race two concurrent invocations of the same
  command. Each command now wraps its body in an `Interlocked.CompareExchange`-based
  `TryAcquireInFlight` / `ReleaseInFlight` pair (same pattern `Refresh` has used since
  the first audit pass). The existing `Refresh` guard was refactored onto the new helpers
  for consistency.
- `RunBackup` now re-enables `ButtonsEnabled` in a `finally`, so a mid-operation exception
  can't leave the UI locked.

## [v4.3.1] - 2026-04-17

Deep hardening pass. No new features — existing behavior made substantially more robust.

### Fixed — correctness

- **Safe Mode no longer reports as "Partial"**. `RegistryService.GetPatchStatus()` used to
  compare applied-count against `AppConfig.TotalComponents` (always 5), so a Safe Mode
  install (3 components applied cleanly) was labeled PARTIAL in every readout — the GUI
  status card, the CLI `status` exit code, and the diagnostics report. It now detects the
  effective profile — `Safe`, `Full`, `Mixed`, or `None` — and reports `Applied=true` for
  either clean profile. Mixed (e.g. a prior install left an orphaned key) still reports
  Partial correctly.
- **SafeBoot keys no longer leak on partial install failure**. The previous code only
  added SafeBoot keys to the rollback list if `SetValue` succeeded. If `CreateSubKey`
  succeeded but `SetValue` threw, the empty subkey remained in the registry and rollback
  skipped it. Install now registers the subkey for rollback the moment `CreateSubKey`
  returns non-null, covering the full failure window.
- **Rollback failure is now surfaced to the user**. `Rollback()` was void and swallowed
  per-key exceptions; a failed rollback still showed "rolled back cleanly" in the toast.
  It now returns a bool, verifies each deletion, and the ViewModel shows a dedicated
  "Rollback Incomplete" dialog pointing at the pre-patch `.reg` backup and System Restore
  point when it fails.
- **Post-reboot verification time-boxed**. If a user applied the patch and never
  rebooted, `PatchVerificationService` would stay in `AwaitingRestart` forever. A new
  `StalePending` outcome clears the flag silently after 30 days. Also added a 5-minute
  clock-skew tolerance so an NTP sync between "apply" and "reboot" can't leave the app
  stuck comparing `lastBoot < appliedAt`.

### Fixed — security

- **ViVeTool download host is now whitelisted**. `browser_download_url` is checked against
  `github.com`, `api.github.com`, `objects.githubusercontent.com`,
  `release-assets.githubusercontent.com`, `codeload.github.com`. Non-HTTPS URLs, unknown
  hosts, and malformed URIs are refused before any bytes are fetched.
- **ViVeTool zip-slip check hardened**. The prefix comparison now appends
  `Path.DirectorySeparatorChar` before `StartsWith`, closing a theoretical traversal into
  a sibling folder with a name that happens to share a prefix with the tools dir
  (`.../tools_evil/` slipping past `.../tools`). Also extracts into a per-call staging
  directory that's promoted atomically, so a mid-extract failure can't leave the cache
  half-populated.
- **ViVeTool cached exe now size-sanity-checked**. A 0-byte `ViVeTool.exe` from a prior
  failed extraction no longer fools `IsInstalled` into reporting success. Minimum 10 KB,
  maximum 32 MB on both the API-reported and on-disk sizes.
- **ViVeTool invocation switched to `ProcessStartInfo.ArgumentList`**. Eliminates any
  possibility of Windows command-line parsing surprises. Feature IDs are also validated
  to be digit-only strings before being passed.
- **Concurrent `ViVeToolService.EnsureInstalledAsync` serialised**. A `SemaphoreSlim`
  guards against two simultaneous download/extract pipelines racing on the tools folder
  (triggered by e.g. a double-click on the fallback badge).

### Fixed — UX

- **The "Patch Applied But Inactive" dialog now fires AFTER preflight renders**, not
  during it. Before, a modal confirmation popped up over a half-drawn UI and blocked the
  rest of preflight from rendering until answered. The dialog is now deferred via
  `Dispatcher.BeginInvoke` at `Background` priority so drive/status cards show first.
- **Fixed spurious config write on every startup**. A new `_suppressConfigWrites` flag
  prevents `OnIsFullModeSelectedChanged` / `OnIsSafeModeSelectedChanged` from
  round-tripping config to disk while the ViewModel ctor is priming bindings.

## [v4.3.0] - 2026-04-17

Closes out Tier 0 of the ROADMAP: the **ViVeTool fallback** now actually works instead
of just being described to the user.

### Added
- **`ViVeToolService`** — downloads ViVeTool from its official GitHub release
  (https://github.com/thebookisclosed/ViVe) on demand, caches it in
  `%LocalAppData%\NVMePatcher\tools\ViVeTool.exe`, and applies community feature IDs
  **60786016** and **48433719** to the `FeatureStore` that Microsoft did **not** block
  in Feb/Mar 2026. Zip-slip guarded extraction, 60-second timeout, hash verification
  (release-tag-pinned) on the roadmap for v4.4.
- **Post-reboot fallback prompt** — when `PatchVerificationService` reports
  `OverrideBlocked`, the user gets a yes/no dialog that lays out the tradeoffs and,
  on Yes, downloads and applies the fallback in-app. No more "go read the README".
- **Persistent fallback badge on Overview** — once the block state is detected the
  yellow "Registry override blocked by Windows" notice stays on the Patch Readiness
  card with a "Try ViVeTool Fallback" button until the fallback succeeds or the patch
  is removed.
- **CLI `fallback` / `vivetool-fallback` / `apply-fallback`** — same flow headlessly
  for automation. Exit 0 on success, exit 1 on any failure with the reason to stderr.

### Changed
- Post-reboot verification flow uses `ConfirmDialog` (yes/no) instead of the info-only
  dialog so the user can act in one click.

## [v4.2.0] - 2026-04-17

Tier 0 roadmap items — addresses the Microsoft override block (Feb/Mar 2026) and moves the
tool from "works great until the next Insider build" to "honest across build changes".

### Added
- **Install mode selector — Safe (default) vs Full.** Safe Mode writes only the primary
  feature flag (735209102) plus Safe Boot entries, which is the community-recommended
  default for 2026. Full Mode adds the two extended flags (1853569164, 156965516) that
  correlate with community BSOD reports. Persisted in `config.json` as a readable string.
- **`PatchVerificationService`** — after a patch + reboot, confirms `nvmedisk.sys` actually
  bound. On post-block Insider builds the registry writes succeed but the driver never
  swaps; the app now says so clearly and points to the ViVeTool fallback IDs
  (60786016, 48433719) instead of silently looking broken.
- CLI: `--safe` / `--full` flags on `apply`. `status` now surfaces the block-detection
  note when keys are written but the driver isn't bound.
- `config.json` gains `PatchProfile`, `ConfigVersion`, and three verification tracking
  fields. Enums persist as strings for sysadmin readability.

### Changed
- **Apply-Patch confirmation dialog rewritten** for clarity. New three-tier structure —
  CRITICAL / TRADEOFFS / GOOD TO KNOW — leads with what the patch actually does
  (swap stornvme.sys for nvmedisk.sys, ~80% random-write IOPS, ~45% CPU under load)
  before listing tradeoffs. BypassIO / DirectStorage regression elevated from
  afterthought to explicit first-class warning when the system drive currently honors
  BypassIO. Rollback paths are now called out up front.
- Patch Defaults card in the Overview tab now shows the Install Mode radio picker with
  inline educational text explaining each mode's tradeoffs.

### Fixed
- (Preemptive) Users on 2026-era Insider builds would previously see a successful "patch
  applied" and no speedup at all. They'll now be told what happened and offered a safe
  path out.

## [v4.1.0] - 2026-04-17

- Support bundle export: single-click ZIP with diagnostics report, config.json, crash logs, recent registry backups, and SQLite DB (Diagnostics tab -> "Support Bundle (ZIP)")
- CLI: new `bundle` / `export-bundle` subcommand wires the same ZIP export into automation
- Recovery kit freshness warning: apply-patch confirmation flags kits older than 30 days or missing, so users regenerate before a fresh patch
- Theme: new dark Expander and RadioButton control templates for future settings UI

## [v4.0.0] - 2026-04-15

- NVMe Driver Patcher v4.0.0 -- C# WPF port with telemetry, tuning, and charts
- Speed up preflight: drop CIM cache, use fast service checks, lazy health
- Verbose preflight logging with CIM query timing
- Cache CIM queries in preflight: eliminates ~40s of redundant WMI calls
- Adopt Zinc palette from LibreSpot for consistent dark theme
- Fixed: Fix DarkColorTable assembly resolution for PS7
- Fixed: Fix light scrollbar: apply SetWindowTheme DarkMode_Explorer to form
- Strip all DWM dark mode hacks -- restore original v3.0.0 theme behavior
- Force PS 5.1 for GUI: re-launch from pwsh.exe to powershell.exe
- Triple dark mode application: HandleCreated + Load + pre-ShowDialog
