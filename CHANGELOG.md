# Changelog

All notable changes to win11-nvme-driver-patcher will be documented in this file.

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
