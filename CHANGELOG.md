# Changelog

All notable changes to win11-nvme-driver-patcher will be documented in this file.

## [Unreleased] — 2026-06-16

### Added
- **NVMeDriverPatcher.Core library** — extracted all shared services, models, data, and interop
  into a framework-agnostic class library; Tray no longer pulls the entire WPF framework.
- **`--json` for firmware, featurestore, reliability, minidump** — all read CLI commands now return
  the versioned JSON envelope, completing fleet-scriptable coverage.
- **PatchService core-flow unit tests** — 15 tests covering pre-registry abort classification,
  rollback state detection, profile-driven key sets, and result defaults.
- **Roadmap_Blocked.md** — items blocked on external resources (VMs, hardware) now live in a
  separate file to keep ROADMAP.md actionable-only.
- **PowerShell module: 6 new JSON cmdlets** — `Get-NvmeRecoveryProof`, `Get-NvmeBypassIo`,
  `Get-NvmeFirmwareCompat`, `Get-NvmeFeatureStore`, `Get-NvmeReliability`, `Get-NvmeMinidump`.
  All read commands now use `--json` via `Invoke-CliJson` — no regex parsing remains.
- **WmiQueryHelper** — centralized 30s timeout wrapper for all `ManagementObjectSearcher` calls
  across 11 services. Prevents WMI provider hangs from freezing the GUI.
- **Safety-service unit coverage** — added pure fixtures for `BackupIntegrityService`,
  `PortableModeService`, `WmiQueryHelper`, `TuningProfileIoService`, and `ReliabilityService`,
  covering backup parsing, portable-mode flags, timeout option wiring, tuning JSON round-trips,
  and Reliability Monitor correlation math.
- **Firmware compatibility DB refresh** — added source-backed warnings for WD/SanDisk 2TB HMB
  BSOD firmware baselines, WD SN850X Critical Failure reports, Samsung 990 Pro 2TB `7B2QJXD7`
  degradation, SK hynix Platinum P41 mixed performance, and Phison E18/E26 power-loss risk.
- **BypassIO gaming-impact guidance** — preflight, GUI, CLI, JSON output, and offline docs now
  name DirectStorage titles affected by `nvmedisk.sys` BypassIO vetoes and recommend per-drive
  scope for game-library drives.
- **CrystalDiskInfo incompatibility warning** — preflight now detects CrystalDiskInfo process,
  service, and install-path signals, warns that SMART monitoring may fail under `nvmedisk.sys`,
  and points users to `Get-StorageReliabilityCounter`.
- **Phison power-loss preflight advisory** — compat entries can now carry `powerLossRisk`;
  Phison E18/E26 matches surface a non-blocking UPS/power-protection warning before patching.

### Security
- **Watchdog service downgraded from LocalSystem to LocalService** — the watchdog only reads the
  System event log and writes to the shared `%ProgramData%\NVMePatcher\` working directory. LocalSystem was unnecessarily
  privileged. Also adds a restricted service SID via `sc sidtype ... restricted`.
- **SQLite hardening PRAGMAs** — added `trusted_schema=OFF` (prevent schema-based injection),
  `cell_size_check=ON` (catch corrupted pages), `quick_check` at startup (detect corruption).
  3 new test fixtures verify each PRAGMA.
- **DiskSpd Authenticode verification** — extracted `diskspd.exe` is now Authenticode-verified
  when signtool is available. Download URL pinned to v2.2 release instead of `/releases/latest/`.

### Fixed
- **Watchdog shared state under LocalService** — non-portable app state now resolves to
  `%ProgramData%\NVMePatcher\` so the GUI, CLI, tray, and LocalService watchdog read the same
  `config.json`, `watchdog.json`, and SQLite database. First launch copies legacy
  `%LocalAppData%\NVMePatcher\` state files forward without overwriting already-shared files,
  and the MSI creates the ProgramData directory during install.
- **Watchdog LocalService System log access** — added an SDDL merge helper that grants
  LocalService read access to the System event-log channel without replacing existing ACLs.
  The watchdog `/install` path attempts the grant, `/grant-eventlog` exposes it for automation,
  and the MSI runs it before starting the optional watchdog service.
- **StatusToColorConverter fallback brushes** — replaced dark-theme-only fallback hex values with
  neutral mid-range colors that read acceptably in both light and dark themes.
- **WMI query timeouts** — all 35+ `ManagementObjectSearcher.Get()` calls across DriveService,
  HotSwapService, BenchmarkService, DiagnosticsService, and 7 other services now use
  `WmiQueryHelper.ExecuteWithTimeout()` with a 30s default timeout.
- **DeviceInfoSetSafeHandle** — `SetupDiGetClassDevs` now returns a `SafeHandle` that calls
  `SetupDiDestroyDeviceInfoList` automatically, preventing leaks on exceptions.
- **SP_DEVINFO_DATA/SP_CLASSINSTALL_HEADER factory methods** — `Create()` pre-sets `cbSize` to
  prevent silent SetupDi API failures from missing initialization.
- **EventLogWatchdogService file lock** — `Evaluate()` acquires a file lock so concurrent
  GUI+CLI evaluations can't clobber each other's cumulative event counts.
- **FeatureStoreWriterService write lock** — `WriteOverrides`/`ResetOverrides` serialized with
  `SemaphoreSlim` to prevent interleaved writes from producing incorrect state.
- **FirmwareUpdateNudgeService "ct" pattern** — narrowed from `Contains` to `StartsWith` so
  drives like "Direct" or "Connected" don't false-match as Crucial.
- **xunit version alignment** — `xunit.runner.visualstudio` downgraded from 3.1.5 to 2.8.2
  to match `xunit` 2.9.3 major version.
- **DarkExpander keyboard focus** — added `IsKeyboardFocused` trigger to the expander's toggle
  button so keyboard users can see which control is focused.
- **PowerShell DiskSpd hash verification** — pinned to v2.2 release with SHA-256 check before
  extraction, preventing MITM binary substitution on admin-executed downloads.

### Fixed (correctness / security — engineering audit)
- **PatchService: untracked registry value on verify-read failure** — a written feature flag was not added
  to the rollback list when the verify-read returned an unexpected type, leaving orphaned registry values
  on partial-failure rollback. Now tracked before the write so rollback is always complete.
- **PatchService: RegistryKey leak in Rollback** — the `overrides` key was opened without `using` and
  manually disposed; an exception in the supplemental cleanup path could skip disposal. Wrapped in
  try/finally.
- **FeatureStoreWriterService: split-state on Boot store failure** — if the Runtime write succeeded but
  the Boot store write failed, the Runtime store was left with enabled features that wouldn't survive
  reboot. Now rolls back the Runtime store on Boot failure.
- **ViVeToolService: partial fallback cleared tracking list** — on partial failure, `AppliedIDs.Clear()`
  discarded the record of already-applied IDs. Those IDs remained enabled with no rollback record.
  Now preserves the list and warns about them.
- **CompatTelemetryService: firmware lookup always missed** — firmware map was keyed by drive name, but
  `FirmwareVersions` is keyed by disk number. Telemetry reports always had empty firmware. Fixed to use
  `drive.Number`. Also fixed migration detection to match on `PNPDeviceID` as well as friendly name.
- **BenchmarkService: NVMe partition never found** — WMI's `MSFT_Partition.DriveLetter` is boxed as
  `ushort`, not `char`. The pattern match silently fell through, always benchmarking the working directory
  instead of the NVMe drive. Now handles `ushort` and `int` WMI types.
- **VerifiedDownloader: HTTP downgrade not blocked** — redirect-following checked the host allowlist but
  not the scheme. A MITM could redirect from HTTPS to HTTP on the same allowed host. Now rejects any
  non-HTTPS redirect.
- **HtmlDashboardService: XSS via unescaped single quotes** — `WebEscape` did not escape `'`; drive model
  names containing single quotes could break attributes. Added `&#39;` escaping.
- **CleanDataService: SweepTree left empty directories** — file deletion worked but empty directory trees
  remained. Now removes empty directories bottom-up after file sweep.
- **ExportLog: non-atomic write** — unlike all other write paths, the manual log export used direct
  `File.WriteAllLines` without staging. Now writes to `.tmp` and atomically moves.
- **MainViewModel.Workspace: 3 redundant registry reads per refresh** — `UpdateOperationalHistory`
  called `GetPatchStatus()` independently in three sub-methods. Now reads once and passes through.

### Fixed (PowerShell legacy script)
- **VeraCrypt CLI bypass removed** — `-Force` previously bypassed the VeraCrypt hard block in CLI mode,
  but the GUI says "This block cannot be overridden." A VeraCrypt system with nvmedisk.sys is unbootable.
  The block is now unconditional.
- **SafeBoot `New-Item -Path` → `-LiteralPath`** — GUID-containing registry paths with braces were
  created with `-Path`, which interprets braces as wildcards. Changed to `-LiteralPath`.
- **RestartDelay config validation** — config-file values were loaded without type or range validation.
  Malformed values could pass invalid `/t` arguments to `shutdown.exe`. Now validates as int in 5–300.
- **Version parsing for pre-release tags** — SemVer suffixes like `v5.0.0-beta.1` crashed the `[version]`
  cast. Now strips pre-release suffix before parsing.
- **Wear percentage could exceed 100%** — negative `StorageReliabilityCounter.Wear` values (some drivers)
  produced >100%. Added upper-bound clamp.

### Fixed (build / packaging)
- **app.manifest version 4.0.0.0 → 5.0.0.0** — both GUI and CLI manifests had stale v4 identity.
- **CLI manifest missing `<compatibility>` section** — CLI process could get inaccurate
  `Environment.OSVersion` on Windows 10+ without the `supportedOS` GUID. Added matching section from
  the GUI manifest.
- **.gitignore blocked CHANGELOG.md** — the blanket `*.md` + `!README.md` rule prevented tracking.
  Added `!CHANGELOG.md` exclusion.
- **Release workflow PFX cleanup not in try/finally** — if the signing step threw, the PFX remained on
  the runner disk. Wrapped in try/finally with ErrorAction SilentlyContinue.
- **Release workflow signtool missing `/d` description** — added `/d "NVMe Driver Patcher"` for better
  SmartScreen and certificate dialog metadata.
- **CLAUDE.md: ".NET 9.0 SDK" → ".NET 10.0 SDK"** — the build snippet contradicted the tech stack
  section. Also simplified to `dotnet build NVMeDriverPatcher.sln`.

### Improved (UX / accessibility)
- **Theme toggle button icon** — previously always showed ☀ regardless of theme. Now shows ☀ in dark
  mode (switch to light) and 🌙 in light mode (switch to dark).
- **Hyperlink underline** — links were styled without underline, relying on color alone (WCAG 1.4.1
  violation). Default state now includes underline.
- **ThemedDialog keyboard accessibility** — `FlowDocumentScrollViewer` was `Focusable="False"`, making
  long dialog content unreachable by keyboard. Now focusable with tab index.
- **TuningPanel warning callout** — used `TextPrimary` foreground while all other warnings use `Yellow`.
  Aligned for consistency.

### Fixed (telemetry receiver)
- **Summary endpoint paginates the full KV keyspace** (P2): the reference Cloudflare Worker listed a
  single `list({ limit: 1000 })` page and then summarized only `entries.slice(0, 200)`, so a dataset
  larger than that silently dropped records. It now follows list cursors to enumerate all keys
  (`paginateKeys`), reads up to a documented `MAX_SUMMARY_RECORDS` cap, and returns `scannedKeys`,
  `summarizedRecords`, and a `truncated` flag so any cap is explicit. Cursor-follow is unit-tested.
- **Rate limiting can use the atomic Workers binding** (P2): the KV check-then-increment counter has
  an inherent race; the worker now prefers Cloudflare's Workers Rate Limiting binding (`RATE_LIMITER`,
  documented in `wrangler.toml`/README) when bound, falling back to the best-effort KV counter
  otherwise. The pure `rateLimitVerdict` decision is unit-tested for the limit boundary and reset.

### Added (recovery)
- **`winre-inject` previews the WinRE stornvme injection plan** (P2, partial): the recovery kit assumes
  the user can still reach WinRE, but the native stack can wedge it. The new `winre-inject` CLI command
  probes WinRE for its image path, locates `stornvme.inf`, and prints the exact ordered DISM plan
  (mount → add-driver → unmount/commit) with blast-radius warnings — preview only, it never mounts or
  mutates the image. New `WinReDriverInjectionService` plan builder + renderer are unit-tested. The
  actual image mutation + a real-WinRE-boot validation are intentionally left as a deliberate operator
  step (tracked as a remaining roadmap item).

### Added (test coverage)
- **Dedicated test fixtures for the previously-untested pure-logic services** (P2): added unit tests for
  `GpoPolicyService` (policy-overlay merge), `SchedulerService` (schtasks argument construction — extracted
  into pure `Build*Args` builders), and `SystemGuardrailsService` (finding aggregation / blocker
  precedence), joining the `SafeBootUpgradeService`, `RecoveryProofGateService`, and
  `PerControllerAuditService` fixtures also added this cycle. Services whose only behavior is external I/O
  (EtwTrace, WinPE builder, Toast, EventLogRegistration, PortableMode) remain integration-test-only.

### Added (fleet automation)
- **Machine-readable `--json` CLI output for core fleet state** (P2): `status`, `watchdog`, `controllers`,
  `recovery-proof`, and `bypassio` now accept `--json` and emit a versioned `{ schemaVersion, command, data }`
  envelope with stable camelCase fields. The PowerShell module's three regex-parsing wrappers
  (`Get-NvmePatchStatus`, `Get-NvmeWatchdogReport`, `Get-NvmeControllerAudit`) now consume that JSON instead
  of scraping prose, so upstream wording changes can't silently break fleet automation. Text output stays
  the default for humans. New `CliJson` builders with field-name contract tests that force a `SchemaVersion`
  bump on any rename.

### Added (policy deployment)
- **`policy-install` / `policy-uninstall` CLI commands for ADMX/ADML templates** (P2): the MSI dropped the
  Group Policy templates in the app folder and the docs required a manual copy to `PolicyDefinitions`, so
  GPO support was easy to misdeploy. The CLI now installs them into the local policy store (`.admx` →
  `PolicyDefinitions`, each `.adml` → `PolicyDefinitions\<lang>`) or a domain Central Store via
  `--central-store=<dir>`, and removes them again with `policy-uninstall`. The templates ship beside the
  exe (`admx\`) for the portable build. New `PolicyTemplateInstallService` (plan/install/uninstall are
  unit-tested); WiX README documents the install/uninstall/central-store flow.

### Added (firmware workflow)
- **`disable-for-update` / `re-enable-after-update` CLI workflow** (P2): vendor SSD tools (Samsung
  Magician, WD Dashboard, Crucial Storage Executive) can't detect drives while nvmedisk.sys is active.
  `disable-for-update` reverts to the legacy stack, records the active profile in a marker file, and
  prints each detected drive's vendor firmware-update guide link; `re-enable-after-update` re-applies
  that exact remembered profile and clears the marker. Both ends log to the event audit trail. New
  `FirmwareUpdateWorkflowService` (marker round-trip, profile resolution, and instruction rendering are
  unit-tested); `FirmwareUpdateNudge` gained a `HowToUpdateUrl` guide link.

### Added (data contracts)
- **JSON Schemas for safety data + telemetry payload** (P2): `windows_build_rules.json`, `compat.json`,
  and the opt-in compat telemetry payload (`CompatReport`, shared with the Cloudflare Worker receiver)
  now have packaged JSON Schemas under `packaging/schemas/`, alongside the existing config/drive-scope/
  watchdog/maintenance schemas. A test fixture validates the actual bundled data files against their
  schemas in CI (via the test suite) and asserts that intentionally malformed fixtures — unknown
  `expectedPath`/`level`, missing required fields, additional properties, wrong value types — fail.
  Validation uses a small built-in subset validator (no new dependency). Support-bundle diagnostics
  already carry per-file schema version, source, SHA-256, and review-freshness metadata.

### Fixed (recovery & safety hardening)
- **Restore-point gate no longer trusts RPSessionInterval** (P3): `RecoveryProofGateService` treated a
  non-zero `RPSessionInterval` (a checkpoint-cadence value) as "System Restore enabled", so it could
  promise a restore point on a system where System Protection for the system drive is OFF and
  `CreateRestorePoint` silently no-ops. It now keys off the authoritative signals — the global
  `DisableSR` flag and whether the system drive actually has shadow-copy storage configured — via a
  pure, unit-tested `ClassifyRestoreCapability`.
- **SafeBoot upgrade verify gate can no longer report false success** (P3): `UpgradeEntries` returned
  failure only when `GuidEntriesPresent && !ServiceEntriesComplete`, so a silent `CreateSubKey` no-op on
  a machine with no GUID entries skipped the failure branch and claimed the entries were written. The new
  `VerifyUpgrade` asserts `ServiceEntriesComplete` directly (tested, including the no-op regression case).
- **Config-import bundles are validated before applying** (P3): `Import` never checked `SchemaVersion`
  and copied `RestartDelay`/`PatchProfile` without bounds, so a foreign/malformed bundle could feed an
  out-of-range delay into `shutdown /r /t` or set an undefined profile. A pure `ValidateBundleJson` now
  rejects unknown schema versions, out-of-range `RestartDelay`, and undefined `PatchProfile` (validated
  against the raw wire format, before the setter clamp) — failing with a named error and no config mutation.
- **CleanData subtree guard extended to all protected roots** (P3): `IsSafeCleanRoot` only refused the
  exact ProgramFiles/UserProfile/MyDocuments dirs (not their subtrees), so a portable install dropped
  directly under one passed. It now refuses any subtree of a protected root while still allowing the
  app-managed zones (the LocalAppData tree, including TEMP, and the portable exe `Data\` dir).

### Fixed (GUI robustness)
- **Autosave log written atomically on close** (P3): `MainViewModel.OnClosing` used `File.WriteAllLines`,
  which can leave a truncated file if the process is killed mid-write during a shutdown-after-patch. It now
  uses the codebase's temp + flush-to-disk + rename pattern.
- **Log timestamps now include the date** (P3): entries used time-only `HH:mm:ss`, so an exported log from
  a session spanning midnight appeared to go backwards. Now `yyyy-MM-dd HH:mm:ss`.
- **Removed dead update-check timeout** (P3): the `Task.WhenAny(completed, Task.Delay(12s))` inside
  `ObserveLateUpdateCheck`'s continuation always returned the already-finished task immediately, so the
  timeout never fired. Removed the misleading wrapper; behavior is unchanged.

### Changed (release pipeline)
- **Version validator now covers narrative docs** (P3): `Validate-ReleaseVersions.ps1` checked packaging
  surfaces but not the README version badge, the ROADMAP "Current ship" line, or the CLAUDE.md status
  version, so those could silently lag the `Directory.Build.props` SSOT. It now validates each (guarded by
  presence, so gitignored/missing files are skipped) and the relative-path computation no longer crashes
  on UNC checkouts. CLAUDE.md was reconciled to v5.0.0 locally.
- **WiX pinned via a restorable tool manifest** (P3): WiX was hard-installed in the release workflow
  (`dotnet tool install --global wix --version 5.0.2`), outside Dependabot's update coverage despite
  WiX's security releases. It now lives in `.config/dotnet-tools.json` (still pinned to 5.0.2, which
  avoids the WiX 7.x OSMF gate); the release runs `dotnet tool restore` and invokes `dotnet wix`.
  Dependabot's nuget ecosystem watches the manifest, so WiX updates surface as PRs.

### Added (release pipeline)
- **Chocolatey + Scoop manifests are produced and validated per release** (P2): the manifests pinned
  `v5.0.0` URLs and a `REPLACE_ME_WITH_RELEASE_SHA256` hash with no automation, so those channels went
  stale/broken. A new `Update-PackageManifests.ps1` (unit-tested) rewrites the tagged download URL, the
  real GUI exe SHA-256, and the version into both manifests; the release workflow runs it, packs the
  Chocolatey `.nupkg`, and uploads the `.nupkg` + updated Scoop JSON as release assets.
  `release-artifacts.json` now lists both, and `Validate-ReleaseAssets.ps1` verifies the Scoop manifest's
  version, tagged URL, and hash match the release (failing on the REPLACE_ME placeholder or a stale version).

### Fixed (release pipeline)
- **Authenticode signing gate now actually runs when secrets are configured** (P2): the signing step
  gated on `if: ${{ env.CODE_SIGN_PFX_BASE64 != '' }}` where the env var was defined only in that
  same step — and a step's own `env:` is not visible to its own `if:`, so the condition was always
  empty and signing was silently skipped even with secrets set. The secret is now promoted to a
  job-level `env`, an explicit "UNSIGNED mode" warning is logged when it is absent, and
  `Validate-ReleaseAssets.ps1` gained `-ExpectSigned` (passed when secrets are present) which fails
  the release if any `sign:true` artifact lacks a Valid Authenticode signature. New
  `ReleaseAssetsScriptTests` cover both the signed-expectation failure and the unsigned-ok path.
- **PowerShell module ZIP added to the artifact contract** (P2): the release workflow produces and
  uploads `NVMeDriverPatcher.PowerShell-{version}.zip` with a `.sha256` sidecar and SHA256SUMS entry,
  but `packaging/release-artifacts.json` didn't list it, so `Validate-ReleaseAssets.ps1` never checked
  it — a broken module upload would ship undetected. Added the `powershell-module` entry
  (required, checksummed); the validator now enforces presence, sidecar, and SHA256SUMS coverage.

### Fixed (recovery)
- **WinPE recovery `startnet.cmd` now actually executes** (P2): `WinPERecoveryBuilderService` wrote the
  boot-time recovery announcement to `<tree>\media\sources\startnet.cmd`, but WinPE only runs the copy
  *inside* `boot.wim` at `\Windows\System32\startnet.cmd` — so the stick booted to a bare prompt with no
  removal instructions on the exact can't-boot path it exists for. It now `Dism /Mount-Image`s the
  boot.wim, writes `startnet.cmd` into `\Windows\System32`, and `/Unmount /Commit`s (injection failure
  degrades to a warning, not a failed build). `WimPath` is now set unconditionally after copype, the kit
  guidance points at the media drive, and the injection target/content are covered by unit tests.
- **WinRE readiness probe is now locale-independent** (P2): `WinReBcdPrepService.Probe` matched the
  English literals "Windows RE status : Enabled" and the BCD-identifier label, so on any non-English
  Windows it reported WinRE disabled even when fully provisioned — telling the user the recovery path
  won't work and returning a misleading `winre` exit code. Enabled-state is now derived from the
  structural BCD identifier GUID (a non-zero GUID = enabled; all-zeros = disabled) and the location
  from the device path, neither of which is translated. Tests cover en-US/de-DE/ja-JP enabled fixtures
  plus the zero-GUID disabled case via a pure `ParseReagentcInfo`.
- **Recovery kit generated from AppConfig** (P2): the `.reg` and `.bat` embedded literal feature IDs
  (`735209102`, …), the SafeBoot GUID, and the `nvmedisk` service name. If any ID changed, a kit from
  the new version would delete the wrong values and leave the actual patch keys in place — defeating
  the recovery purpose. Both are now built by `BuildRegContent`/`BuildBatContent` from
  `AppConfig.FeatureIDs` + `ServerFeatureID` + `SafeBootGuid` + `SafeBootServiceName` (matching
  `PatchService.Uninstall`), with uniform CRLF. Tests assert the generated content tracks AppConfig.

### Added (status honesty)
- **Force-loaded "driver method" detection** (P2): when nvmedisk.sys is bound but none of this tool's
  breadcrumbs exist (no override keys, no known FeatureStore fallback IDs), preflight and CLI `status`
  now surface an "untracked activation" note instead of implying "not applied". This covers both
  Microsoft's official rollout and a forced Device Manager / PnPUtil install — which the registry/
  FeatureStore state genuinely cannot distinguish — and, critically, explains that a forced install
  reverts via Device Manager (roll back to stornvme), not registry cleanup. New
  `IsUntrackedDriverActivation` classifier (tested); recovery DocsService topic updated. (Implemented as
  an honest dual-possibility note rather than a separate `DriverForced` state, since the two are
  indistinguishable from the data.)
- **Per-controller PnP driver-method evidence** (P1): the per-controller audit now captures each NVMe
  controller's INF name, driver provider, device class, and hardware/compatible IDs, and the support
  bundle's `diagnostics.txt` includes a "PER-CONTROLLER PnP EVIDENCE" section. When nvmedisk.sys is bound
  with no patch breadcrumbs, the report prints the forced-vs-official evidence (a Microsoft INF/provider =
  official rollout; a non-Microsoft provider or manually-matched compatible ID = forced install). The CLI
  `controllers` command surfaces the same fields. New `RenderForcedDriverEvidence` renderer with unit tests.

### Added (build awareness)
- **Native Feature flags page detection (Windows 11 26300+)** (P2): Insider build 26300.8155 added a
  built-in "Feature flags" page (Settings > Windows Update > Windows Insider Program) where Microsoft
  may eventually expose native NVMe as an official toggle. Preflight now surfaces an informational note
  on 26300+ pointing users there first; a dedicated `windows_build_rules.json` rule carries the same
  guidance (keeping `none-known` enablement, since the GenNvmeDisk ID is still removed); a `featureflags`
  DocsService topic explains the shift to a verify/monitor/rollback role. Gated via
  `AppConfig.HasNativeFeatureFlagsPage`; pre-26300 builds are unchanged.

### Added (preflight)
- **Pending-reboot detection** (P2): preflight now warns when Windows already has a reboot queued
  (`Component Based Servicing\RebootPending` or `WindowsUpdate\Auto Update\RebootRequired`) — applying
  the patch on top of a pending servicing reboot can interact unpredictably (KB5055621 compounds this).
  Warning, not a blocker; clean systems are unchanged.
- **Working-directory disk-space check** (P3): preflight warns when the working-dir drive has under
  100 MB free, so recovery-kit/bundle/log writes don't fail silently. Both surfaced via pure
  `ClassifyPendingReboot`/`ClassifyWorkingDirSpace` seams with unit tests.

### Fixed (drive detection)
- **DriveService.TestLaptopChassis WMI type mismatch** (P2): `chassis["ChassisTypes"] is ushort[]`
  silently missed the laptop chassis codes when WMI boxed the array as `int[]`/`uint[]`/`object[]`
  (seen on VMs and some OEM images), falling through to the weaker battery heuristic. Now matches on
  `Array` and converts each element via the existing `AsInt` helper. Extracted to a testable
  `IsLaptopChassis`; tests cover ushort/int/uint/object boxings plus desktop/null/empty.
- **DriveService third-party driver regex pre-compiled** (P3): the seven vendor-driver patterns were
  re-interpreted per PnP driver row (hundreds) on every preflight; now a `static readonly` compiled
  `ThirdPartyDriverPatterns` array, matching the existing incompatible-software pattern.

### Fixed (auto-revert safety)
- **MaintenanceWindowService overnight day-boundary** (P2): `IsInWindow` evaluated active-day
  membership against the current instant, so the morning tail of an overnight window landed on the
  wrong calendar day — Saturday 02:00 (tail of the Friday-night window) was wrongly rejected while
  Monday 02:00 (no window opened Sunday night) was wrongly accepted. The overnight branch now
  attributes the tail to the day the window opened (previous day). Tests cover Fri/Sat/Sun/Mon
  boundary hours. This directly gates auto-revert/scheduled actions — the "don't yank the driver at
  the wrong time" safety.
- **AutoRevertService swallowed fatal exceptions** (P2): the safety-critical revert path caught all
  `Exception` types, so an `OutOfMemoryException` (or memory-corruption fault) mid-revert was summarized
  as a benign abort and the app continued with a possibly half-reverted patch state. Fatal exceptions
  now propagate (filtered via a testable `IsFatal` predicate); ordinary WMI/registry/process errors stay
  caught and reported.

### Security
- **Telemetry privacy/transport hardening** (P2, three items):
  - *CPU sanitizer* now actually strips the ` Stepping N`/`Revision N` token from
    `PROCESSOR_IDENTIFIER` (it previously only truncated to 80 chars, leaking the stepping the
    privacy docs claimed to remove). `SanitizeCpu("…Model 154 Stepping 3, GenuineIntel")` →
    `"…Model 154, GenuineIntel"`.
  - *HTTPS-only remote endpoints*: `SubmitAsync` refuses plaintext `http://` to a remote host
    (the report carries stable OS/CPU/controller/firmware/benchmark data); `https://` works and
    loopback `http://` stays allowed for local dev. Validation extracted to a testable
    `TryValidateEndpoint`.
  - *Receiver CORS allowlist*: the worker no longer returns `access-control-allow-origin: *`. It
    echoes an allowlisted origin only (`env.ALLOWED_ORIGINS`, default none), so an unauthorized
    site's JSON preflight is blocked; CLI (no `Origin`) is unaffected. Documented in the receiver
    README; covered by the node-driven contract test.
- **SkiaSharp pinned to 3.119.4 for libpng CVE coverage** (P1): LiveChartsCore pulled SkiaSharp
  3.119.0 transitively, whose native `libSkiaSharp` bundles libpng 1.6.44 — vulnerable to
  CVE-2025-64505/64506/64720/65018. Pinned the full SkiaSharp graph directly to 3.119.4, which
  bundles libpng 1.6.54 (mono/SkiaSharp#3426, PR #3452). NuGet advisories don't track the
  statically-linked libpng, so `--vulnerable` can't see it; the direct pin is the control.

### Added
- **Charting native-dependency smoke** (P3): `ChartingSmokeTests` renders the benchmark
  (`ColumnSeries`) and telemetry (`LineSeries`) chart shapes headlessly via `SKCartesianChart` and
  PNG-encodes them, exercising the native Skia + libpng path with no WPF window. Run before/after
  any Skia/OpenTK/HarfBuzz bump per the new dependency-update checklist in CLAUDE.md; a native ABI
  break or broken libpng now fails a test instead of crashing in front of a user.
- **FeatureStore fallback undo in rollback & recovery** (P1): removal only ever cleaned the
  registry-override route — a user who enabled native NVMe via the ViVeTool/native FeatureStore
  fallback would "Remove Patch" yet keep `nvmedisk` bound after reboot because the FeatureStore
  configuration was never touched. `FeatureStoreWriterService.ResetOverrides` /
  `ResetAppliedFallback` now clear the User-priority overrides in both stores (ViVeTool `/reset`
  semantics) and verify the result. `PatchService.Uninstall` calls this automatically (best-effort,
  no-op on registry-only installs) and reports status in the activity rail; the CLI exposes
  `featurestore --reset-native`. The recovery-kit README now explains that the `.reg`/`.bat` undo
  the registry/SafeBoot route offline from WinRE, but the FeatureStore fallback can only be reset
  from inside Windows. Tests cover successful reset, partial failure, reset semantics, and the
  no-applied-IDs no-op.

### Fixed
- **FeatureStore native write verified in both stores** (P1): `WriteOverrides` writes the
  enable to the Runtime AND Boot configuration stores but only ever verified Runtime, so a
  Boot-store write failure passed silently and surfaced as a non-binding driver after reboot.
  Verification now queries both stores per ID; success requires every requested ID enabled in
  both, otherwise a named partial-failure result spells out which store each ID is missing
  from. Result carries per-ID `IdStatuses` (Runtime/Boot) which the CLI `featurestore
  --write-native` path now renders. Classifier extracted to a pure `ClassifyVerification` seam
  with unit tests for both-success, Runtime-only, Boot-only, both-failure, and mixed-ID states.
- **Telemetry receiver schema drift** (P1): the Cloudflare worker's `/nvme/compat/summary`
  read `payload.controller`, `payload.firmware`, and `payload.verificationResult` — none of
  which the client emits — so every fleet summary degraded to `unknown/unknown` controllers
  and an `Other` verdict bucket. The summarizer now reads the real `CompatReport` shape
  (`controllers[]` of `{model, firmware}` counted per drive, top-level `verification` bucketed
  against the full `VerificationOutcome` set). Extracted the aggregation into a pure exported
  `summarizeReports()` and added a C# contract test that serializes a real `CompatReport` and
  runs it through the worker via node — it fails if either side renames a field again. Receiver
  README now documents the exact POST payload shape. Added `package.json` marking the worker
  directory as ESM.

## [Unreleased] — 2026-06-11

### Features
- **Windows build ruleset** (AR-2026-006): bundled `windows_build_rules.json` maps OS build
  ranges to expected enablement paths with source URLs and confidence levels. Integrated into
  preflight, verification, CLI status, diagnostics, and support bundles.
- **CLI command registry** (AR-2026-008): all 42 commands now have descriptors with grouped
  help output (Lifecycle/Recovery/Diagnostics/Storage/Config/Fleet/Advanced), risk labels,
  and aliases. `PrintUsage()` and `IsKnownOperationalCommand()` are registry-derived.
- **Recovery proof gate** (AR-2026-009): pre-apply check of recovery kit freshness, backup
  directory writability, SafeBoot entries, and System Restore. CLI blocks unless `--force`;
  GUI surfaces failing items as warnings. New `recovery-proof` standalone command.
- **BypassIO history tracking** (RD-006): per-volume BypassIO snapshots captured in SQLite
  before and after every patch install/remove. CLI `bypassio --history` renders pre/post
  diff and identifies volumes that lost DirectStorage capability.
- **APST battery impact estimator** (RD-007): computes idle power savings from the NVMe
  power state table, detects laptops, and recommends whether the patch trade-off is worth it.
- **NVMe Identify Controller enrichment** (RD-009): extracts power state descriptors,
  namespace count, firmware/format/NS-mgmt capabilities. Diagnostics export includes
  controller identity with redacted serial numbers.
- **Chocolatey and Scoop distribution manifests**: `packaging/chocolatey/` and `packaging/scoop/`
  with SHA256-verified downloads and version validation.
- **PowerShell module release package** (AR-2026-017): release workflow now produces a
  checksummed module zip with psd1 + psm1 + CLI binary.

### Fixed
- **HotSwap WMI timeout**: batched partition-to-logical-disk resolution into a single WMI
  query, avoiding per-partition round trips that could exceed the 10s device-return window.
- **Config schema drift gate**: `config.schema.json` now includes persisted theme mode,
  default config files start at the current migration schema version, and tests lock the
  saved config contract against the packaged schema.
- **Backup driver blocklist evidence**: preflight now scans CodeIntegrity Event ID 3077/3076
  for blocked backup image-mount drivers such as `psmounterex.sys`, separates those warnings
  from NVMe-patch risks, and includes redacted evidence in diagnostics/support bundles.
- **Rules and compat DB provenance**: preflight, CLI status, diagnostics, and support bundles
  now report active `windows_build_rules.json` / `compat.json` source, schema, SHA-256,
  review freshness, and local-customization state.

### Tests
- Added fixture coverage for service-name compatibility detection, Intel RST/VMD hard blocks,
  backup blocklist-note separation, VeraCrypt/BitLocker pre-registry abort decisions, and
  incomplete rollback recovery warnings.

### Repo hygiene
- Untracked 34 session screenshots from `artifacts/` and a stray `icon - Copy.png`;
  `.gitignore` now covers all of `artifacts/`.
- Root release-surface hygiene guard now fails CI on unsupported root backup/copy
  artifacts, unrelated project files, or root PowerShell scripts other than the
  supported legacy entrypoint.
- Packaging documentation examples now use durable `<version>` artifact placeholders,
  and release-version validation fails on stale concrete package names or mismatched
  major-version placeholders.
- Low-risk package updates: `CommunityToolkit.Mvvm` 8.4.2,
  `Microsoft.NET.Test.Sdk` 18.6.0, and `xunit.runner.visualstudio` 3.1.5.
  Charting/Skia/OpenTK transitive updates remain pinned pending UI smoke.
- Accessibility regression smoke now creates the real WPF app/window on an STA thread,
  loads dark/light/high-contrast resources, and verifies named focus targets for
  readiness, apply/remove, recovery, verification, diagnostics, and dialog controls.
- Fixed a runtime-only XAML resource typo where the DirectStorage panel referenced
  `BoolToVis` instead of the defined `BoolVis` converter.

## [5.0.0] — 2026-06-10

### Release channel repaired
- v5.0.0 ships the COMPLETE asset set (GUI 84 MB compressed, CLI, tray, watchdog, MSI,
  legacy PS1, per-asset `.sha256` sidecars, SHA256SUMS.txt, winget manifest) — v4.6.1 had
  shipped a single 183 MB exe, 404ing the README quick-start and permanently breaking the
  fail-closed auto-updater. Root causes fixed: `icon.ico` staging bug + unpinned WiX tool
  (7.0.0 OSMF EULA failure, now pinned 5.0.2). Quick-start URL and sidecars verified live.

### Breaking / platform
- Migrated all five projects to **.NET 10 LTS** (`net10.0-windows`); .NET 9 STS hits EOL
  2026-11-10. Runtime packages to 10.0.9. SDK pinned via `global.json` (10.0.301); CI and
  release workflows resolve the SDK from it.
- CVE-2025-6965: direct `SQLitePCLRaw.bundle_e_sqlite3` 3.0.3 reference upgrades bundled
  native SQLite past 3.50.2 (EF 10.0.9 still bundled 3.49.1); `SqliteVersionTests` pins the floor.
- Dependabot (nuget + actions) and a CI `dotnet list package --vulnerable` gate added.

### Verification honesty
- New `FlagsEnabledNotBound` verification outcome for builds 26200.8524+ where stornvme no
  longer exposes the GenNvmeDisk compatible ID (ViVe issue #164) — the ViVeTool fallback
  enables flags that can never bind. GUI/CLI/preflight all distinguish this from the
  registry-only block and stop re-suggesting the fallback that just failed.
- Post-reboot classification extracted to a pure, truth-table-tested function; fallback-only
  or official enablement now reads Confirmed instead of Reverted.
- New preflight warning `NativeBindSupport` on known bind-blocked builds.

### SafeBoot
- SafeBoot upgrade checker (KB5079391): detects pre-v4.6.1 patches missing the service-name
  SafeBoot entries; one-click GUI badge + CLI `upgrade-safeboot` write them idempotently.

### Asset selection hardening
- Auto-updater selects only the exact `NVMeDriverPatcher.exe` asset (fail-closed).
- ViVeTool downloader selects release zips by CPU architecture (ViVe v0.3.4 split-arch
  assets); never stages an ARM64 binary on x64.

## [Unreleased] — release-integrity pass (2026-06-10)

### Release pipeline
- Centralized all assembly versions in `Directory.Build.props` (VersionPrefix SSOT); per-project
  `<Version>` literals removed. `scripts/Validate-ReleaseVersions.ps1` checks psd1/winget/wix/intune/
  AppConfig-fallback surfaces and runs in CI; the release workflow additionally asserts the tag
  matches repo metadata.
- Watchdog service is now a shipped artifact: published, signed, checksummed, uploaded, and
  installable via a new opt-in WiX feature (`ADDLOCAL=WatchdogService`) that registers/removes the
  `NVMeDriverPatcherWatchdog` service.
- Fixed the release-aborting `icon.ico` staging bug (now stages `src/NVMeDriverPatcher/nvme.ico`) —
  this is what shipped v4.6.1 with a single asset.
- MSI is now a required artifact (no more best-effort build + required upload contradiction).
- winget manifest emission now rewrites the `InstallerUrl` tag segment (previously paired new
  hashes with the stale v4.6.0 URL).
- New `packaging/release-artifacts.json` contract + `scripts/Validate-ReleaseAssets.ps1` gate runs
  before release creation: required assets, `.sha256` sidecars, SHA256SUMS coverage, winget
  URL/hash consistency.

### Features
- `WatchdogServiceStateService` + CLI `watchdog-service` (alias `service-status`) report the
  real-time service SCM state with fleet-scriptable exit codes; GUI watchdog card shows it inline.
  Suite at 313 tests.

## [Unreleased] — hardening pass

A multi-stage production-hardening sweep. Every change preserves the existing public API
and ships with regression tests. Build is clean (0 warnings, 0 errors) and all tests pass.

### Security

- **Auto-updater now verifies every download.** `AutoUpdaterService.StageUpdateAsync`
  refuses to promote a downloaded binary into the staging path unless one of two
  integrity signals validates: (a) the release's per-asset `<asset>.sha256` sidecar
  matches the downloaded bytes, or (b) the binary carries a valid Authenticode signature
  that `signtool verify /pa` accepts. If neither signal is available the download is
  deleted and staging fails closed — host allowlisting alone is no longer treated as a
  sufficient defense. The CI release workflow now emits `<asset>.sha256` sidecars
  alongside the combined `SHA256SUMS.txt`.
- **ViVeTool downloader** learned the same SHA-256 sidecar check (opportunistic —
  activates automatically when upstream publishes sidecars). A user-visible log entry
  notes whether the install used `sha256`, `authenticode`, or `weak` (size + host match
  only) integrity so operators can audit the assurance level.

### Fixed (critical)

- **`PhysicalDiskTelemetryService` — per-drive reliability counters were identical
  across every disk.** The reliability WMI query used `WHERE DeviceId LIKE '%'` + `break`
  at the first row, so every NVMe in the Telemetry tab showed the wear / temperature /
  error counts of whichever disk happened to enumerate first. Now scopes by `DeviceId`
  with a fallback to `ASSOCIATORS OF {MSFT_PhysicalDisk.ObjectId=…}` when DeviceId is
  empty, and applies consistent WQL escaping.

### Fixed (high)

- **`PatchService.Install` — BitLocker suspension failure now routes through the shared
  `finally` block.** Previously `Install` early-returned and never captured the
  after-snapshot or cleared the progress bar — inconsistent with the sibling VeraCrypt
  path. A private `PatchAbortedException` sentinel threads the abort through the
  existing cleanup.
- **`MainViewModel` — ViVeTool fallback dialog was an async-void in disguise.** Moved
  the post-preflight dialog flow into `HandlePendingVerificationDialogAsync` so
  exceptions from `ApplyViVeToolFallback()` propagate through a real `Task`, get logged
  to the activity rail, and write to the Windows Event Log.
- **`Watchdog` service-control / `EtwTraceService` / `SchedulerService.IsRegistered`
  — pipe-buffer deadlocks.** All three spawned processes with redirected stdout+stderr
  but only drained the pipes *after* `WaitForExit`, so a chatty child could block on
  write forever. Each now drains asynchronously before awaiting exit, matching the
  pattern used elsewhere in the codebase.
- **`MainWindow.OnContentRendered` — catastrophic preflight failures no longer leave a
  blank "Checking…" UI.** Errors are now logged to the activity rail + Windows Event
  Log, and a themed dialog explains what happened and points the user at Refresh.
- **`EventLogWatchdogService.CountEvents` — unbounded scan loop.** Capped at 10 000
  records per evaluation; the verdict is already locked in well before that on any
  genuinely unstable system, and the cap prevents a chatty event log from stalling the
  watchdog.

### Fixed (medium)

- **`ConfigService.Save` — silent swallow on persistent save failure.** Retries up to
  5× with 100 ms backoff on transient `IOException` / `UnauthorizedAccessException`
  (AV scanner, file-explorer locks), then writes a Warning to the Windows Event Log
  when it finally gives up. Stale `.tmp` files are cleaned up.
- **`ConfigMigrationService` — no downgrade detection.** If a user ran an older build
  against a `config.json` written by a newer build, the migration silently proceeded.
  Now detects `ConfigVersion > CurrentSchemaVersion`, preserves the file untouched, and
  surfaces a warning summary.
- **`DiagnosticsService.ExportBundle` — `.tmp` sidecar leak.** Outer `finally` now
  sweeps `<reportPath>.tmp` in addition to `reportPath`; bundle promote uses
  `File.Move(overwrite:true)` atomically.
- **`ViVeToolService.RunViVeToolAsync` — `Task.Run(() => proc.WaitForExit(30000))`
  anti-pattern** replaced with `proc.WaitForExitAsync(timeoutCts.Token)` + linked CTS.
- **`MainViewModel.OnClosing` — `_settingsSaveDebouncer` DispatcherTimer wasn't stopped
  before the final synchronous Save**, so a pending tick could fire after close. Now
  stopped explicitly.
- **`HtmlDashboardService.SaveTo` — wasn't atomic.** Now uses the `.tmp → flush(true) →
  File.Move(overwrite)` pattern. Also creates the output directory when writing to a
  custom path.
- **`EventLogWatchdogService.SaveState`** hardened with the same flush-then-rename
  pattern.

### Changed

- **⚠ Behavior change: `NVMeDriverPatcher.Cli apply --unattended`.** Previously this
  flag set `SkipWarnings = true` and *printed* `shutdown /r /t <delay>` to stdout
  without executing it — MDT / Intune / Autopilot workflows relying on it for
  unattended auto-restart never actually rebooted. The flag now invokes
  `PatchService.InitiateRestart()` for real when `--no-restart` is not also passed.
  Scripted callers that want the old "advise only" behavior can add `--no-restart`
  explicitly.
- **`Tray` agent reloads `config.json` every poll tick** (30 s) so settings changes
  made in the main GUI (auto-revert toggle, verification state, renamed working
  directory) surface in the tray tooltip within one interval instead of requiring a
  tray restart.

### Added — tests

- 36 new regression tests covering SHA-256 parsing + restart-command escaping
  (`AutoUpdaterService`), forward-schema preservation (`ConfigMigrationService`), WQL
  escaping + status-code mapping (`PhysicalDiskTelemetryService`), and the shared
  `VerifiedDownloader` pure helpers (hash parsing against NIST test vectors, sidecar
  host-allowlist rejection). Suite now runs 306 tests (previously 270).

### Known limitations

- **Telemetry rows written before this release retain their original local-time stamp.**
  New rows are UTC; retention window (`PruneTelemetry`, default 90 days) self-heals the
  legacy rows. An in-place DST-safe migration was considered but rejected — the
  ambiguity around spring-forward rows would risk permanent loss of a sample, which is
  a worse outcome than the short-lived wear-trend smoothing that retention repairs.
  See `Data/TelemetryRecord.cs` XML-doc for context.

- **DiskSpd downloads use `weak` integrity (size + host allowlist only).** The benchmark
  workflow pulls Microsoft's [diskspd](https://github.com/microsoft/diskspd) archive
  from `github.com/microsoft/diskspd/releases/latest/download/DiskSpd.ZIP`. Microsoft
  does not publish a `.sha256` sidecar for DiskSpd, and the archive isn't Authenticode-
  signed as a zip, so our download path enforces size caps + the host allowlist only.
  Our own `AutoUpdaterService` and the `ViVeToolService` fallback both use stronger
  signals (sidecar + Authenticode fallback for the app's own updater; opportunistic
  sidecar for ViVeTool, activating the moment upstream publishes one). To upgrade
  DiskSpd to the same trust level, file an issue against `microsoft/diskspd` asking for
  per-asset `.sha256` sidecars — once they're present, `VerifiedDownloader` picks them
  up automatically with no code change required. Until then, the zip-slip defense in
  `BenchmarkService` plus the post-extraction exe size floor remain the last line of
  defense.

## [v4.6.1] - 2026-04-21

Patch fix for SafeBoot regression on Windows 11 24H2/25H2 after KB5079391 (March 2026).

### Fixed

- **SafeBoot service-name entry missing on 25H2 / KB5079391 systems** — KB5079391 (released
  March 26, 2026, re-packaged as KB5086672 on March 31) tightened how Windows resolves
  kernel-mode storage drivers at Safe Mode boot. The existing device-class GUID entry
  (`{75416E63-5912-4DFA-AE8F-3EFACCAFFB14}`) was sufficient on 24H2 and earlier, but
  25H2 (build 26200) and patched 24H2 systems (build 26100.8117+) now also require the
  canonical **service-name-based** SafeBoot entry — the same pattern used natively by
  storport, stornvme, and storahci — to reliably load nvmedisk.sys during a Safe Mode boot.

  The patcher now writes `HKLM\...\SafeBoot\Minimal\nvmedisk` and
  `HKLM\...\SafeBoot\Network\nvmedisk` (default value `"Service"`) alongside the existing
  GUID entry on both Install and Rollback/Uninstall cleanup. These writes are best-effort:
  a failure is logged as `[WARN]` but never causes the patch to fail or roll back on
  pre-25H2 systems. (Fixes [#1](https://github.com/SysAdminDoc/win11-nvme-driver-patcher/issues/1))

### Changed

- **`RegistryService` — backup and snapshot** — `ExportRegistryBackup` now includes the
  service-name SafeBoot keys in the `.reg` backup when present. `GetPatchSnapshot` adds
  `SafeBootMinimalService` and `SafeBootNetworkService` to the components map so diagnostic
  snapshots surface 25H2 compat state.

- **`SafeModeVerifyScriptService` — enhanced PS1 report** — the emitted
  `Verify-NVMeSafeMode.ps1` now independently checks both the GUID key and the service-name
  key for each scope (Minimal / Network). Per-key `[OK]` / `[MISS]` status plus a
  color-coded `[PASS]` / `[WARN]` / `[FAIL]` summary clearly communicates whether the
  system is safe on the current Windows build.

## [v4.6.0] - 2026-04-19

Quality-of-life release. Diagnostics tab exposed in the GUI, nine new services for the
"live with the patch" phase, full packaging pipeline (PowerShell module, Intune/SCCM
detection, JSON schemas, issue templates), optional real-time Windows Service, and
Authenticode-signing hooks in the release workflow.

### Added — GUI

- **Diagnostics+ tab in MainWindow.xaml** — four new buttons bound to the existing
  `RefreshWatchdogStatusCommand` / `RefreshReliabilityCommand` / `TriageMinidumpsCommand` /
  `PreviewDryRunCommand` RelayCommands. Summary text properties render into a dark-theme
  InsetCard with a scrollable Cascadia Code text box for the dry-run Markdown. Finishes
  the v4.4 loose-end "XAML bindings for new commands" that was deferred for two releases.

### Added — Services

- **`AccessibilityService`** — probes HighContrast, Narrator install, ReducedMotion,
  TextScaleFactor from HKCU / HKLM. Gives support bundles visibility when the user is on
  an accessible-settings profile the dark theme should consider.
- **`MaintenanceWindowService`** — user-definable window (start hour, end hour, active days)
  with overnight-wrap handling. `IsInWindow` is a pure function covered by 5 fixtures.
  `AutoRevertService` now consults this — eligible verdicts outside the window defer to
  the next run so we don't yank the driver mid-workday.
- **`CleanDataService`** — named-target purge for `%LocalAppData%\NVMePatcher\`. Targets:
  logs, etl, backups, db, bundles, staging. Default purges everything; CLI `clean-data`
  invokes it. Reports bytes freed + per-file errors.
- **`HtmlDashboardService`** — single-file HTML report composing verification, watchdog,
  reliability, minidump, guardrails, per-controller audit into a dark-themed shareable
  snapshot. Escapes hostile chars in every summary (pinned by test). CLI: `dashboard`.
- **`FirmwareUpdateNudgeService`** — `{vendor substring → vendor update-tool URL}` map for
  13 common NVMe vendors (Samsung, WD, Crucial, SK hynix, Kingston, Sabrent, Intel/Solidigm,
  Seagate, Corsair, ADATA, Phison, Micron). CLI: `fw-nudge [model] [firmware]` — iterates
  all detected NVMe drives when no model is given.
- **`SafeModeVerifyScriptService`** — emits `Verify-NVMeSafeMode.ps1` beside the existing
  verification script. Run FROM Safe Mode to confirm SafeBoot keys actually bound
  nvmedisk.sys. Distinct from the normal post-reboot verify. CLI: `safemode-verify`.
- **`DocsService`** — curated offline help. 10 topics covering overview, profiles, recovery,
  watchdog, vivetool, firmware, gpo, portable, telemetry, uninstall. CLI: `docs [topic]`.
- **`SystemGuardrailsService.CheckAppLockerOrSrp`** — extends the HVCI / WDAC / VROC /
  NTFS-compression suite with AppLocker EnforcementMode + SRP DefaultLevel detection.
  Both refuse our ViVeTool download path when enforced — warn early so the user can pre-
  approve rather than discovering it mid-reboot.

### Added — Packaging

- **`packaging/powershell/`** — PSGallery-ready module (`NVMeDriverPatcher.psd1` +
  `.psm1` + README). Cmdlets: `Get-NvmePatchStatus`, `Invoke-NvmePatchApply`,
  `Invoke-NvmePatchRemove`, `Get-NvmeWatchdogReport`, `Get-NvmeControllerAudit`,
  `Invoke-NvmeDryRun`, `Export-NvmeDiagnostics`, `Export-NvmeDashboard`. Parses CLI
  output defensively — a CLI-messaging change degrades fields to null rather than
  breaking the module.
- **`packaging/intune/`** — `Detect-NVMeDriverPatcher.ps1` for Intune Win32 custom
  detection, plus a README covering Intune + SCCM deployment modes with the exact
  `msiexec` commands and product code.
- **`packaging/schemas/`** — JSON Schema files for `config.json`, `drive_scope.json`,
  `watchdog.json`, `maintenance_window.json`. Third-party tooling (VS Code, validators)
  can now lint user-edited config files against the shipped schema.
- **`.github/ISSUE_TEMPLATE/`** — bug_report.yml, feature_request.yml, config.yml. Bug
  reports ask for a support bundle up front; feature requests reference the scope rule.

### Added — Optional Windows Service

- **`NVMeDriverPatcher.Watchdog` new project** — Microsoft.Extensions.Hosting-based Windows
  Service. Subscribes to `System` event log via `EventLogWatcher` (push, not poll) for
  nvmedisk/stornvme/storport/storahci/disk/BugCheck/Kernel-Power providers. Flushes every
  5 minutes; shares the same `watchdog.json` the CLI/GUI read. `/install` / `/uninstall`
  wrap `sc.exe`. Fully opt-in — polling path remains the default for users who'd rather
  not run a persistent service.
- Added to `NVMeDriverPatcher.sln` as `{E5F6A7B8-C9D0-1234-EF01-345678901234}`.

### Added — CI/CD

- **Authenticode signing step in release.yml** — conditionally signs the GUI, CLI, Tray,
  and MSI when `CODE_SIGN_PFX_BASE64` + `CODE_SIGN_PFX_PASSWORD` repository secrets are
  configured. No-op otherwise so the release pipeline keeps working for unsigned builds.
  Timestamps via `timestamp.sectigo.com`. Sha-256 digest.

### Added — CLI (10 new subcommands)

- `docs [topic]` / `help-topic` — offline help.
- `clean-data` — purge per-user working dir.
- `dashboard` / `html-report` — generate the HTML diagnostics report.
- `fw-nudge [model] [firmware]` / `firmware-nudge` — vendor update-tool nudge.
- `safemode-verify` — emit the Safe-Mode PS verify script.
- `accessibility` / `a11y` — probe user accessibility flags.
- `maintenance-window` / `window` — show window config + current in/out state.

### Added — Tests

- **MaintenanceWindowServiceTests** — 5 fixtures: disabled, same-day inside/outside,
  overnight wrap, inactive day, zero-width window.
- **FirmwareUpdateNudgeServiceTests** — theory across 9 known vendors + unknown + empty
  model.
- **DocsServiceTests** — theory across all 10 documented topics + unknown/empty/case
  insensitivity.
- **CleanDataServiceTests** — 4 fixtures with per-test TEMP dir: missing dir,
  clean-all-defaults, selective targets, unrelated-files-preserved.
- **HtmlDashboardServiceTests** — 3 fixtures: skeleton structure, watchdog table
  population, HTML-escape of hostile summary strings.
- **WinReBcdPrepServiceTests** — 1 fixture pinning non-null report contract.

### Changed

- `AutoRevertService.MaybeRun` now consults `MaintenanceWindowService` before running
  the uninstall — eligible but outside the window defers silently.
- `SystemGuardrailsService.Evaluate` grew a 5th finding (AppLocker / SRP).
- All csproj `<Version>` strings, winget manifest, WiX, and the README badge bumped to `4.6.0`.
- `NVMeDriverPatcher.sln` now has 5 projects (GUI, CLI, Tests, Tray, Watchdog).

## [v4.5.1] - 2026-04-19

Follow-up to v4.5.0 that closes the last three open ROADMAP items, lights up CI test
coverage for the v4.4/v4.5 service layer, adds a dedicated CI workflow, and finishes the
portable-mode wiring.

### Added

- **`RecoveryKitFreshnessService`** (closes ROADMAP §1.5) — pure function that returns
  `Missing` / `Fresh` / `Stale` / `Unknown` based on the newest file in
  `LastRecoveryKitPath`. Staleness threshold is 30 days. Used to drive the persistent
  hero-card CTA and the new `kit-freshness` CLI subcommand. Exit code 1 when nagging is
  warranted (Stale or Missing) — lets scripts gate on it.
- **`FeatureStoreWriterService`** (ROADMAP §3.1 — stub) — seats the surface for a future
  native FeatureStore encoder that replaces the ViVeTool download path. Ships today with:
  `WriteOverrides` returning a `not yet implemented` result (stable contract for callers
  that will use the real encoder once it lands); `HasFallbackEvidence` probes the blob for
  little-endian occurrences of the two post-block feature IDs (60786016, 48433719);
  `ExportBlob` dumps the raw blob + an ASCII hex sidecar for support bundles. New CLI:
  `featurestore`.
- **`WinReBcdPrepService`** (closes ROADMAP §3.3, probe-only for v4.5.1) — wraps
  `reagentc /info` + `bcdedit /enum <guid> /v` to report WinRE enabled state, location,
  BCD identifier, and image path. `EnableWinRe()` shells `reagentc /enable` for systems
  with WinRE staged but the entry missing. Driver-injection into the WinRE image is still
  deferred (out of scope for this bump). New CLI: `winre`.
- **`PortableModeService` wiring** — `AppConfig.GetWorkingDir` now honors `portable.flag`
  beside the exe and redirects writable state to `Data\`. Takes precedence over the
  LocalAppData / TEMP / CurrentDir fallback chain.
- **`.github/workflows/ci.yml`** — PR + push CI: restore → debug build → `dotnet test`
  against the full solution → upload `test-results.trx` → release-mode compile smoke.
  Cancels in-flight older runs on a new push to the same ref.
- **Release workflow extension** — also publishes `NVMeDriverPatcher.Tray.exe`, produces
  a WiX v4 MSI (continue-on-error during the transition so portable exe release is never
  blocked by MSI tooling), stages it into `publish/`, computes real SHA-256 for the
  winget manifest's `InstallerSha256` field, and emits a release-ready
  `SysAdminDoc.NVMeDriverPatcher.yaml` with the version + sha pre-filled.

### Tests — cover the v4.4 + v4.5 pure surfaces

- **`DryRunServiceTests`** — 5 fixtures pinning Safe/Full profile item counts, Server-key
  inclusion, VeraCrypt blocker propagation, Markdown render completeness.
- **`FirmwareCompatServiceTests`** — 6 fixtures covering no-match default, wildcard match,
  exact-firmware-beats-wildcard precedence, `=` exact-controller syntax, worst-severity
  tiebreak, empty-model short-circuit.
- **`PerDriveScopeServiceTests`** — 5 fixtures covering disabled-scope passthrough, serial
  match, model-pattern match, serial-over-pattern precedence, summary-count correctness.
- **`LogRotationServiceTests`** — 4 fixtures in a per-test TEMP dir: below-limit no-op,
  oversized rotation, multi-generation shift, missing-file no-op.
- **`AutoBenchmarkServiceTests`** — 5 fixtures for the regression compare: both-arms
  improved, read-regressed, write-regressed, zero-baseline safe, within-threshold tolerated.
- **`ConfigMigrationServiceTests`** — 4 fixtures: v0→current, v2→current, up-to-date
  no-change, idempotency.
- **`CompatChecksumServiceTests`** — 4 fixtures in TEMP: identical files, differing files,
  missing local (fall back to shipped), both missing.
- **`EventLogWatchdogServiceTests`** — 6 fixtures pin BuildSummary + BuildDetail messaging
  across Healthy / Warning / Unstable / Completed / Idle verdicts and threshold printing.
- **`MinidumpTriageServiceTests`** — 4 fixtures pin BuildSummary across no-dumps,
  old-only, new-non-NVMe, and NVMe-referencing outcomes.
- **`AutoUpdaterServiceTests`** — 4 fixtures covering all pre-network rejection paths
  (non-HTTPS, unknown-host, path-traversal asset name, malformed URI).
- **`RecoveryKitFreshnessServiceTests`** — 5 fixtures: null path, missing dir, empty dir,
  fresh file, stale file.
- **`FeatureStoreWriterServiceTests`** — 5 fixtures: `IndexOfBytes` positive/negative/empty
  needle, stub contract stability, pinned post-block feature IDs.

### CLI additions

- `winre` — WinRE enabled state, location, image path, BCD identifier.
- `featurestore` / `feature-store` — fallback-evidence probe + blob export.
- `kit-freshness` / `recovery-kit-freshness` — freshness report with exit code.

### Changed

- `AppConfig.GetWorkingDir` short-circuits to `PortableModeService.PortableDataPath()`
  when `portable.flag` is present. Existing fallback chain (LocalAppData → TEMP → CWD)
  runs unchanged when portable mode is inactive.
- All csproj `<Version>` strings bumped to `4.5.1`. Winget manifest version + download
  URL updated. WiX `.wxs` product version updated.

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
