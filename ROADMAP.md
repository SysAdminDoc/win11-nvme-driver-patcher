# NVMe Driver Patcher — Roadmap

Living document — **incomplete work only**. Shipped items are deleted (git history + [CHANGELOG.md](CHANGELOG.md) are the record). Current ship: **v5.0.0**.

**Scope rule:** every item must improve the core function — enabling, disabling, verifying, or rolling back Microsoft's native NVMe driver swap on Windows 11. No external integrations, no general-purpose storage tools, no theme/UI-locale polish. If an idea drifts into "separate tool that happens to live in the same exe," it doesn't belong here. Priority is by user impact / regret cost, not effort; S/M/L/XL are rough effort estimates.

---

## P1 — Trust & correctness

- [ ] P1 — Fix telemetry client/receiver schema drift and summary accuracy
  Why: The app sends compatibility telemetry as `controllers[]` plus `verification`, while the Cloudflare receiver summarizes `controller`, `firmware`, and `verificationResult`. Real uploads can therefore appear as `unknown/unknown/Other`, making fleet compatibility summaries misleading.
  Evidence: `src/NVMeDriverPatcher/Services/CompatTelemetryService.cs`, `packaging/telemetry-receiver/cloudflare-worker.js`, Cloudflare Worker/KV docs.
  Touches: `CompatTelemetryService`, `packaging/telemetry-receiver/cloudflare-worker.js`, telemetry receiver README, tests/fixtures.
  Acceptance: A captured app payload fixture produces correct `topControllers`, firmware counts, and verdict distribution in the Worker summary; docs show the real payload shape; regression tests fail if client and receiver fields drift again.
  Complexity: M

- [ ] P1 — Add FeatureStore fallback disable/reset coverage to rollback and recovery flows
  Why: Enablement can use native FeatureStore writes or ViVeTool fallback IDs, but removal and recovery flows focus on registry/SafeBoot cleanup. Users need a way to undo fallback FeatureStore IDs as part of the same trusted rollback story.
  Evidence: `FeatureStoreWriterService`, `ViVeToolService`, `PatchService.Remove`, `RecoveryKitService`, ViVe `/disable` and `/fullreset` behavior.
  Touches: FeatureStore writer/service contracts, CLI command surface, GUI rollback flow, recovery kit output, tests.
  Acceptance: CLI exposes an explicit FeatureStore fallback disable/reset path; GUI rollback reports FeatureStore cleanup status; recovery kit explains what can be undone offline versus inside Windows; tests cover successful disable, partial failure, and no-applied-IDs cases.
  Complexity: L

- [ ] P1 — Verify native FeatureStore writes in both Runtime and Boot stores
  Why: Native writes target Runtime and Boot stores, but current success verification checks Runtime only. A Boot-store failure can be hidden until reboot, which undermines post-apply trust.
  Evidence: `FeatureStoreWriterService.WriteOverrides()` line 156 (writes both) vs line 181 (verifies Runtime only); ViVe issue history around Runtime/Boot store divergence.
  Touches: `FeatureStoreWriterService`, `FeatureStoreCommand`, GUI/CLI result rendering, tests.
  Acceptance: Native write results report per-ID Runtime and Boot status; apply success requires the requested IDs enabled in both stores or emits a named partial-failure warning; unit tests cover Runtime-only, Boot-only, both-success, and both-failure states.
  Complexity: M

- [ ] P1 — Update SkiaSharp native dependencies for libpng CVE coverage
  Why: SkiaSharp bundles libpng, and libpng <1.6.51 has multiple CVEs (CVE-2025-64505, -64506, -64720, -65018). The project uses SkiaSharp transitively through LiveChartsCore for chart rendering. While exploitation requires processing attacker-controlled PNG data (unlikely here), a storage safety tool should not ship known-vulnerable native binaries.
  Evidence: SkiaSharp issue #3426; `dotnet list package --outdated --include-transitive` shows SkiaSharp/OpenTK/HarfBuzz updates available.
  Touches: Update LiveChartsCore and/or pin SkiaSharp to a version with libpng >=1.6.51; run the charting smoke test (P3 item) before and after.
  Acceptance: `dotnet list package --vulnerable --include-transitive` stays clean; chart rendering still works after the update; update is documented in the dependency-update checklist.
  Complexity: S (depends on the P3 charting smoke item for safe verification)

---

## P2 — Reliability, safety, and fleet

- [ ] P2 — Fix WinPE recovery `startnet.cmd` injection (currently never executes)
  Why: `WinPERecoveryBuilderService.BuildAsync` writes the boot-time recovery announcement to `<tree>\media\sources\startnet.cmd`, but WinPE runs `startnet.cmd` from inside `boot.wim` at `\Windows\System32\startnet.cmd`. The file on the media is never read, so the recovery stick boots to a bare WinPE prompt with no removal instructions — on the exact can't-boot path the stick exists to cover. The feature silently does nothing.
  Evidence: `src/NVMeDriverPatcher/Services/WinPERecoveryBuilderService.cs:138` (writes to media\sources); Microsoft WinPE startup-script + mount-and-customize docs.
  Touches: `WinPERecoveryBuilderService.BuildAsync` — `Dism /Mount-Image` the `boot.wim`, write `startnet.cmd` (and optionally `winpeshl.ini`) into `\Windows\System32`, then `Dism /Unmount-Image /Commit`; also set `result.WimPath` unconditionally after copype (line 132 currently sets it only when a recovery-kit dir is supplied).
  Acceptance: A built WinPE stick booted in a VM shows the NVMe-removal instructions automatically after `wpeinit`; an integration/mock test asserts the script is injected into the mounted image, not the media root.
  Complexity: M

- [ ] P2 — Make the WinRE/BCD readiness probe locale-independent
  Why: `WinReBcdPrepService.Probe` matches the English literals "Windows RE status : Enabled" and the BCD-identifier label. `reagentc /info` output is localized, so on any non-English Windows `WinReEnabled` is false and `DeviceGuid` is never extracted — the tool tells the user "WinRE not currently enabled — recovery-from-WinRE path will NOT work" even when WinRE is fully provisioned, and the `winre` CLI command returns a misleading exit code. This is a correctness bug independent of UI translation.
  Evidence: `src/NVMeDriverPatcher/Services/WinReBcdPrepService.cs:23-25,35-39,62-64`.
  Touches: `WinReBcdPrepService.Probe` — derive enabled-state from `reagentc /info` exit code / structural fields, and extract the WinRE GUID via a locale-independent pattern (the `{guid}` token / `bcdedit /enum {current}` recovery-sequence) rather than localized labels.
  Acceptance: On a non-English Windows install with WinRE enabled, `Probe` reports enabled and resolves the device GUID; tests cover localized `reagentc` fixtures (e.g. de-DE, ja-JP) for enabled and disabled states.
  Complexity: M

- [ ] P2 — Preflight check: feature-management prerequisites broken by debloat tools
  Why: A community report describes the native NVMe driver refusing to bind until previously disabled scheduled tasks were restored — debloated systems are common in exactly this tool's audience, and today the failure is silent and misdiagnosed as the Microsoft block. Reproduce on a debloated VM first; ship the check only once the responsible task/service set is confirmed.
  Evidence: Overclock.net thread 1818467 page 5 user report (secondhand; Needs live validation — see RESEARCH.md Open Question 3).
  Touches: `Services/SystemGuardrailsService.cs` (alongside HVCI/WDAC/VROC/AppLocker), `PreflightService`, tests.
  Acceptance: On a VM with the offending tasks/services disabled, preflight surfaces a named warning with a one-click/`schtasks` restore hint; healthy systems show no new warning.
  Complexity: M (including the reproduction work)

- [ ] P2 — Enforce HTTPS-only remote telemetry endpoints
  Why: Compatibility telemetry avoids serials and machine names, but still includes stable OS, CPU, controller, firmware, verification, watchdog, and benchmark data. Remote HTTP endpoints should not be accepted silently.
  Evidence: `CompatTelemetryService.SubmitAsync` currently accepts `http` and `https`; telemetry README describes endpoint submission.
  Touches: `CompatTelemetryService`, CLI endpoint validation, GUI config validation, telemetry docs, tests.
  Acceptance: Non-local `http://` endpoints are rejected with a clear error; `https://` endpoints continue to work; `localhost`/loopback HTTP remains allowed only if explicitly kept for development; tests cover remote HTTP, HTTPS, localhost, malformed URI, and unset endpoint.
  Complexity: S

- [ ] P2 — Add JSON schemas and CI validation for safety data and telemetry payloads
  Why: `windows_build_rules.json` and `compat.json` drive eligibility and warnings, but they do not have schemas beside the existing config/watchdog/drive/maintenance schemas. Telemetry payload shape also needs a contract shared with the receiver.
  Evidence: `windows_build_rules.json`, `compat.json`, `packaging/schemas/*.json`, telemetry client/receiver field drift.
  Touches: `packaging/schemas`, CI workflow, data-loading tests, telemetry receiver fixtures, support bundle metadata.
  Acceptance: Schemas exist for Windows build rules, compatibility data, and telemetry payloads; bundled JSON validates in CI; support bundles include data schema/source/review metadata; intentionally malformed fixture files fail tests.
  Complexity: M

- [ ] P2 — Harden telemetry receiver pagination and rate limiting
  Why: The Worker lists one KV page with `limit: 1000` and then summarizes only a slice, which can omit data once uploads grow. The current KV rate limit is also best-effort check-then-increment logic.
  Evidence: `packaging/telemetry-receiver/cloudflare-worker.js`, Cloudflare KV `list_complete`/cursor docs, Cloudflare Rate Limiting binding docs.
  Touches: Cloudflare Worker, telemetry receiver README, Worker tests/mocks.
  Acceptance: Summary either paginates using cursors or maintains tested aggregate counters; large fixture sets do not silently drop expected records; rate limiting uses a documented binding or clearly documented durable alternative with tests for limit and reset behavior.
  Complexity: M

- [ ] P2 — Fix CPU sanitizer to strip stepping info as documented
  Why: `CompatTelemetryService.SanitizeCpu` has an XML comment claiming it "strips stepping and microcode details to reduce entropy" but the implementation only truncates to 80 characters. `PROCESSOR_IDENTIFIER` typically contains "Stepping N" which passes through unmodified, increasing fingerprint entropy beyond what the privacy documentation promises.
  Evidence: `src/NVMeDriverPatcher/Services/CompatTelemetryService.cs` lines 217-224; `PROCESSOR_IDENTIFIER` format is `Intel64 Family 6 Model 154 Stepping 3, GenuineIntel`.
  Touches: `CompatTelemetryService.SanitizeCpu` — add regex to strip stepping/revision/microcode info; update tests.
  Acceptance: `SanitizeCpu("Intel64 Family 6 Model 154 Stepping 3, GenuineIntel")` returns `"Intel64 Family 6 Model 154, GenuineIntel"` (stepping removed); existing telemetry rows are unaffected (server-side only).
  Complexity: S

- [ ] P2 — Add pending-reboot detection to preflight
  Why: Applying registry changes while Windows has a pending reboot (from Windows Update, a previous patch attempt, or a driver install) can interact unpredictably. The `Component Based Servicing\RebootPending` and `WindowsUpdate\Auto Update\RebootRequired` registry keys are the standard detection points.
  Evidence: `src/NVMeDriverPatcher/Services/PreflightService.cs` — no pending-reboot check exists; KB5055621 (April 2026) independently triggers reboots that compound with the NVMe driver swap.
  Touches: `PreflightService.RunAll` — add a check reading `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending` and `...\WindowsUpdate\Auto Update\RebootRequired`; surface as a warning (not a blocker) with "Restart Windows first, then retry."
  Acceptance: On a system with a pending Windows Update reboot, preflight surfaces a named warning; clean systems show no new warning; test fixture covers both keys present and absent.
  Complexity: S

- [ ] P2 — Restrict telemetry receiver CORS to known origins
  Why: The Cloudflare Worker sets `access-control-allow-origin: *`, allowing any website to submit fake telemetry data via cross-origin POST. The KV-based rate limiter has a TOCTOU race that does not prevent burst poisoning.
  Evidence: `packaging/telemetry-receiver/cloudflare-worker.js` lines 121-124; Cloudflare Rate Limiting binding docs.
  Touches: Worker CORS headers — replace `*` with a configurable allowlist (e.g., the project's GitHub Pages domain or `null` for non-browser clients); document that CLI submissions use direct HTTPS POST without CORS.
  Acceptance: Browser-based cross-origin POST from an unauthorized origin receives a CORS rejection; CLI submissions still work; Worker README documents the allowlist configuration.
  Complexity: S

- [ ] P2 — Expand test coverage for 20+ untested services
  Why: 20+ services have no dedicated test file. Most interact with external state (WMI, registry, filesystem, process spawning) and need test doubles, but several have pure logic that can be unit-tested directly: `AutoRevertService` (maintenance window decision), `GpoPolicyService` (overlay merge), `SchedulerService` (argument construction), `SystemGuardrailsService` (finding aggregation), `CompatTelemetryService` (report construction and CPU sanitizer), plus the verify-logic gaps in `SafeBootUpgradeService` and `RecoveryProofGateService`.
  Evidence: Cross-reference of `src/NVMeDriverPatcher/Services/*.cs` against `tests/NVMeDriverPatcher.Tests/*Tests.cs`.
  Touches: `tests/NVMeDriverPatcher.Tests/` — add fixtures for the pure-logic surfaces of untested services. Priority: AutoRevert, GpoPolicyService, SchedulerService, SystemGuardrails, CompatTelemetry, SafeBootUpgrade, RecoveryProofGate, PerControllerAudit, PortableMode.
  Acceptance: Every service with extractable pure logic has at least one dedicated test fixture; services whose only behavior is external I/O (EtwTrace, WinPE, Toast, EventLogRegistration) are documented as integration-test-only with the reason.
  Complexity: L

- [ ] P2 — Add Modern Standby + APST conflict warning for laptop users
  Why: StorNVMe does not support APST on Modern Standby systems (per Microsoft Learn). The project already warns about APST battery impact on laptops, but does not specifically flag the Modern Standby + nvmedisk.sys combination, which can cause NVMe drives to "vanish" on wake from sleep when controller firmware is too optimistic about wake-up timing.
  Evidence: Microsoft Learn NVMe power management docs; HowToGeek APST article; `src/NVMeDriverPatcher/Services/ApstInspectorService.cs`.
  Touches: `ApstInspectorService`, `PreflightService` (add Modern Standby detection via `HKLM\SYSTEM\...\Control\Power\CsEnabled`), GUI/CLI warning copy.
  Acceptance: On a Modern Standby laptop, preflight surfaces a distinct warning about APST/sleep-wake risks with specific mitigation steps (disable Fast Startup, set PCIe link state to Off); desktops and non-Modern-Standby laptops see no change.
  Complexity: M

- [ ] P2 — Generate recovery kit .reg/.bat content from AppConfig constants instead of hardcoded IDs
  Why: The recovery kit .reg file (lines 53-56) and .bat file (lines 137-154) embed literal feature ID strings (`735209102`, `1853569164`, `156965516`, `1176759950`). If IDs change, recovery kits from the new version would delete wrong values and leave actual patch keys in place — defeating the recovery purpose. The verification script generator at line 270 already correctly uses `AppConfig.GetFeatureIDsForProfile()`.
  Evidence: `src/NVMeDriverPatcher/Services/RecoveryKitService.cs` lines 53-62, 137-158 vs line 270.
  Touches: `RecoveryKitService.Export` — build .reg and .bat content from `AppConfig.FeatureIDs`, `AppConfig.ServerFeatureID`, and `AppConfig.SafeBootGuid`.
  Acceptance: Recovery kit regenerated after a hypothetical ID change produces .reg/.bat with the new IDs; test fixture asserts the generated content references `AppConfig.FeatureIDs`.
  Complexity: S

- [ ] P2 — Add Chocolatey/Scoop manifest automation to the release pipeline
  Why: `packaging/chocolatey/` and `packaging/scoop/` exist with hardcoded `v5.0.0` URLs and `REPLACE_ME_WITH_RELEASE_SHA256` hashes, but the release workflow doesn't produce a `.nupkg`, update the URL/hash, push to Chocolatey, or update the Scoop manifest. Users following these channels get stale or broken packages.
  Evidence: `.github/workflows/release.yml` — no Chocolatey or Scoop steps; `packaging/chocolatey/tools/chocolateyInstall.ps1` line 6 and `packaging/scoop/nvme-driver-patcher.json` line 8 both pin `v5.0.0`.
  Touches: `.github/workflows/release.yml` — add steps to rewrite URL/hash in the Chocolatey install script and Scoop JSON, pack the `.nupkg`, upload both as release assets; `packaging/release-artifacts.json` — add entries for the Chocolatey `.nupkg` and updated Scoop manifest.
  Acceptance: A release tag produces a valid `.nupkg` with correct URL/hash and an updated Scoop JSON with correct URL/hash; both uploaded as release assets; `Validate-ReleaseAssets.ps1` validates their presence.
  Complexity: M

- [ ] P2 — Add PowerShell module ZIP to release artifact contract
  Why: The release workflow produces and uploads `NVMeDriverPatcher.PowerShell-{version}.zip` with a SHA-256 sidecar, but `packaging/release-artifacts.json` doesn't list it, so `Validate-ReleaseAssets.ps1` doesn't validate its presence. A broken module zip upload would go undetected.
  Evidence: `.github/workflows/release.yml` lines 187-202 (module zip production); `packaging/release-artifacts.json` (no module entry).
  Touches: `packaging/release-artifacts.json` — add a `powershell-module` artifact entry with appropriate path, required/optional, checksum flags.
  Acceptance: `Validate-ReleaseAssets.ps1` validates the PowerShell module ZIP is present, has a sidecar, and appears in `SHA256SUMS.txt`.
  Complexity: S

- [ ] P2 — Detect force-loaded nvmedisk.sys ("driver method") without FeatureStore/registry breadcrumbs
  Why: Community users on Overclock.net have discovered a "driver method" that enables nvmedisk.sys by forcing the driver via Device Manager or PnPUtil, bypassing both registry overrides and ViVeTool. Systems in this state have nvmedisk.sys active but no registry keys or FeatureStore entries for the patcher to detect. Preflight, verification, and rollback logic assume one of the two known enablement paths and will misreport status on driver-method systems.
  Evidence: Overclock.net thread 1818467 (page 4+), confirmed working on 25H2.
  Touches: `PatchVerificationService` — add a `DriverForced` outcome when nvmedisk.sys is bound but no registry keys or FeatureStore evidence exists; `PreflightService` — surface informational note; CLI `status` — report it; recovery kit docs — explain that driver-method systems need Device Manager revert, not registry cleanup.
  Acceptance: On a system where nvmedisk.sys was force-loaded without registry/FeatureStore keys, `status` reports `DriverForced` instead of `None`; the GUI surfaces an informational badge explaining the state.
  Complexity: M

- [ ] P2 — Detect Windows 11 26300+ native Feature Flags settings page
  Why: Starting with Insider build 26300.8155, Microsoft is adding a built-in "Feature flags" page under Settings > Windows Update > Windows Insider Program. If/when Microsoft officially exposes native NVMe as a toggleable feature there, the patcher's role shifts from "enable hidden feature" to "verify + monitor + rollback." The patcher should detect this surface and adjust its messaging.
  Evidence: gHacks, ElevenForum, WindowsForum, Pureinfotech (all May/June 2026).
  Touches: `windows_build_rules.json` — add a rule for builds with the native Feature Flags page; `PreflightService` — add informational check; `DocsService` — add guidance topic.
  Acceptance: On a 26300+ build where the Feature Flags settings page exists, preflight surfaces an informational note that the user can check there for an official NVMe toggle; on builds without the page, no change.
  Complexity: S

- [ ] P2 — Add "temporarily disable for firmware update" workflow for vendor SSD tools
  Why: Samsung Magician (and WD Dashboard, Crucial Storage Executive) cannot detect drives or update firmware when nvmedisk.sys is active because the disk identity changes (GenNvmeDisk vs GenDisk). Users must manually revert to the legacy stack, update firmware, then re-enable. The patcher should guide this workflow instead of leaving users to figure it out.
  Evidence: ElevenForum thread 45721, Whirlpool forums; `compat.json` already lists Samsung Magician/WD Dashboard/Crucial as "cannot detect drives."
  Touches: CLI — add `disable-for-update` / `re-enable-after-update` subcommands that are thin wrappers around remove + targeted re-apply; GUI — add a button on the firmware nudge panel; `FirmwareUpdateNudgeService` — extend the vendor URL map with a "how to update" guide link.
  Acceptance: User runs `disable-for-update`, sees clear instructions to update firmware, then runs `re-enable-after-update` which re-applies the same profile. Full roundtrip logged in the activity rail.
  Complexity: M

- [ ] P2 — Fix DriveService.TestLaptopChassis WMI type mismatch
  Why: `chassis["ChassisTypes"] is ushort[] types` silently fails when WMI returns a differently-boxed array type (observed on some VMs and OEM images). The code falls through to the battery-based fallback, but the primary chassis detection is unreliable. The same file already has the more robust `ExtractStatusCodes` pattern.
  Evidence: `src/NVMeDriverPatcher/Services/DriveService.cs` line 636.
  Touches: `DriveService.TestLaptopChassis` — use `chassis["ChassisTypes"] is Array arr` and convert elements via `Convert.ToInt32`.
  Acceptance: Laptop detection works on VMs and OEM images where WMI returns non-`ushort[]` chassis types; existing tests pass.
  Complexity: S

- [ ] P2 — Fix AutoRevertService catching fatal exceptions
  Why: The safety-critical auto-revert path catches all `Exception` types (line 66) including `OutOfMemoryException`. If auto-revert fails due to a fatal exception, the app continues silently with a possibly corrupt patch state rather than crashing and forcing manual intervention.
  Evidence: `src/NVMeDriverPatcher/Services/AutoRevertService.cs` lines 66-69 — `catch (Exception ex)` with no filter.
  Touches: `AutoRevertService.MaybeRun` — add exception filter `when (ex is not OutOfMemoryException)` or re-throw fatal exceptions after logging.
  Acceptance: `OutOfMemoryException` during auto-revert propagates instead of being swallowed; non-fatal exceptions continue to be caught and reported.
  Complexity: S

- [ ] P2 — Fix MaintenanceWindowService overnight-window day-of-week boundary bug
  Why: `IsInWindow` tests `ActiveDays.Contains(local.DayOfWeek)` against the *current* instant, then for an overnight window (default 22:00→06:00, ActiveDays Mon–Fri) accepts `hour < EndHour`. The early-morning tail of an overnight window therefore lands on the wrong calendar day: Saturday 02:00 (the tail of the Friday-night window) is wrongly *rejected*, while Monday 02:00 is wrongly *accepted* even though no window opened Sunday night. This mis-gates auto-revert and scheduled actions — the exact "don't yank the driver at the wrong time" safety the window exists to provide.
  Evidence: `src/NVMeDriverPatcher/Services/MaintenanceWindowService.cs:65-81` (day check at line 70 uses current-instant `DayOfWeek`; overnight branch at line 80); consumed by `AutoRevertService` window gating. `MaintenanceWindowServiceTests.cs` exists but does not cover boundary hours.
  Touches: `MaintenanceWindowService.IsInWindow` — for the overnight branch, evaluate the active-day test against the window's *start* day (when `hour < EndHour`, check the previous day's membership); add tests.
  Acceptance: With an overnight 22:00→06:00 Mon–Fri window, Friday 23:00 and Saturday 02:00 are in-window, Monday 02:00 is out-of-window, and same-day windows are unchanged; unit tests cover Fri/Sat/Sun/Mon boundary hours for both overnight and same-day windows.
  Complexity: S

---

## P3 — Hardening & cleanup

- [ ] P3 — Harden the recovery-proof System Restore check (RPSessionInterval is a weak proxy)
  Why: `RecoveryProofGateService.EvaluateRestorePointCapability` treats `RPSessionInterval != 0` as "System Restore enabled." `RPSessionInterval` governs scheduled-checkpoint cadence and can be present/non-zero while System Protection on the system drive is OFF — so the gate can report "a restore point will be created" when `CreateRestorePoint` silently no-ops, defeating the recovery-confidence promise. (Likely — confirm the authoritative signal first; see RESEARCH.md Open Question 6.)
  Evidence: `src/NVMeDriverPatcher/Services/RecoveryProofGateService.cs:140-155`.
  Touches: `RecoveryProofGateService.EvaluateRestorePointCapability` — query per-drive protection (e.g. `Get-ComputerRestorePoint`/WMI `SystemRestoreConfig`/shadow storage) or attempt a checkpoint and verify it was created.
  Acceptance: On a system where System Protection is disabled for the system drive (but `RPSessionInterval` is non-zero), the gate reports System Restore unavailable; on a protection-enabled system it reports available; tests cover both.
  Complexity: S

- [ ] P3 — Fix SafeBootUpgradeService verify gate (can report false success)
  Why: `UpgradeEntries` returns failure only when `after.GuidEntriesPresent && !after.ServiceEntriesComplete`. If the service-name writes silently no-op (`CreateSubKey` returns null) on a machine with no GUID entries, the failure branch is skipped and the method reports the entries are "in place" when nothing was written.
  Evidence: `src/NVMeDriverPatcher/Services/SafeBootUpgradeService.cs:96-100`.
  Touches: `SafeBootUpgradeService.UpgradeEntries` — assert `after.ServiceEntriesComplete == true` directly rather than gating on `GuidEntriesPresent`.
  Acceptance: A simulated write failure (no service entries after the call) returns `(false, ...)`; a successful write returns `(true, ...)`; unit test covers both via the `Classify` seam.
  Complexity: S

- [ ] P3 — Validate config-import bundles before applying
  Why: `ConfigImportExportService.Import` never checks `Bundle.SchemaVersion` and copies `RestartDelay`/`PatchProfile` without bounds or `Enum.IsDefined` validation. A malformed or foreign bundle can set a negative `RestartDelay` (which flows into a `shutdown /r /t` argument) or an undefined `PatchProfile` enum value.
  Evidence: `src/NVMeDriverPatcher/Services/ConfigImportExportService.cs:41-66` (SchemaVersion field defined at line 17 but never read on import).
  Touches: `ConfigImportExportService.Import` — reject unknown `SchemaVersion`, clamp `RestartDelay` to a sane range, and `Enum.IsDefined` the `PatchProfile` before persisting; surface a clear error.
  Acceptance: Importing a bundle with a future `SchemaVersion`, negative `RestartDelay`, or undefined `PatchProfile` fails with a named error and does not mutate config; a valid bundle imports unchanged; tests cover each case.
  Complexity: S

- [ ] P3 — Extend CleanData subtree guard to all protected roots (defense-in-depth)
  Why: `CleanDataService.IsSafeCleanRoot` refuses paths *under* the Windows directory but only refuses the *exact* ProgramFiles/ProgramFilesX86/UserProfile/MyDocuments dirs, not their subtrees. Blast radius is currently limited to the app's own known subpaths, so this is hardening rather than an active data-loss bug — but a portable install dropped directly under one of those roots passes the guard.
  Evidence: `src/NVMeDriverPatcher/Services/CleanDataService.cs:138-145`.
  Touches: `CleanDataService.IsSafeCleanRoot` — apply the `StartsWith(prot + sep)` subtree refusal to every protected root, not just Windows (or require the path to equal the known working-dir/portable-data dir).
  Acceptance: A clean root under Program Files or the user profile is refused; the default `%LocalAppData%\NVMePatcher` and exe-adjacent portable dir still pass; tests cover both.
  Complexity: S

- [ ] P3 — Add charting/native dependency smoke coverage before Skia/OpenTK/HarfBuzz updates
  Why: Dependency audit found only low-risk transitive updates, but chart rendering depends on native graphics packages. A storage safety UI should not accept chart regressions while updating UI-native dependencies.
  Evidence: `dotnet list package --outdated --include-transitive`; LiveCharts/SkiaSharp WPF diagnostics surfaces.
  Touches: GUI smoke tests, chart view models, dependency-update checklist, CI if an STA/WPF smoke can run reliably.
  Acceptance: A small automated smoke renders the diagnostics/benchmark chart path without exceptions and with non-empty series before native graphics package updates are applied; update notes document any skipped package and reason.
  Complexity: S

- [ ] P3 — Add disk space check for working directory in preflight
  Why: While the patch itself is small registry writes, the recovery kit, benchmark files, diagnostics exports, support bundles, and log files all write to the working directory. A full disk causes silent failures in backup creation, config saving, or bundle export.
  Evidence: `src/NVMeDriverPatcher/Services/PreflightService.cs` — no disk space check exists; `AppConfig.GetWorkingDir()` returns `%LocalAppData%\NVMePatcher` by default.
  Touches: `PreflightService.RunAll` — add a check for minimum available space (e.g., 100 MB) on the working directory drive; surface as a warning.
  Acceptance: On a system with <100 MB free on the working directory drive, preflight warns; healthy systems show no change.
  Complexity: S

- [ ] P3 — Fix MainViewModel autosave non-atomic file write
  Why: Every other file write in the codebase uses the atomic temp-then-rename pattern with `fs.Flush(flushToDisk: true)`. The autosave log at app close uses `File.WriteAllLines`, which can produce a truncated file if the process is killed during write — common during system shutdown right after patching.
  Evidence: `src/NVMeDriverPatcher/ViewModels/MainViewModel.cs` line 1344.
  Touches: `MainViewModel.OnClosing` autosave — use the same atomic-write pattern with temp file + flush + rename.
  Acceptance: A simulated mid-write process kill does not produce a truncated autosave file.
  Complexity: S

- [ ] P3 — Fix MainViewModel.Log timestamp dropping date information
  Why: Log entries use `HH:mm:ss` (time-only) format. Sessions spanning midnight produce confusing exported logs with entries that appear to go backwards in time. `RegistryService.GetPatchSnapshot` already fixed this same pattern.
  Evidence: `src/NVMeDriverPatcher/ViewModels/MainViewModel.cs` line 248.
  Touches: `MainViewModel.Log` — use `yyyy-MM-dd HH:mm:ss` or ISO 8601.
  Acceptance: Exported log files from sessions spanning midnight have monotonically increasing timestamps.
  Complexity: S

- [ ] P3 — Pre-compile third-party driver detection regex patterns in DriveService
  Why: 7 regex patterns are compiled per-call inside a loop over all PnP signed drivers (potentially hundreds). The incompatible-software detection pre-compiles its patterns but the third-party driver patterns do not, causing unnecessary GC pressure on the startup-critical preflight path.
  Evidence: `src/NVMeDriverPatcher/Services/DriveService.cs` lines 182-185 — `Regex.IsMatch` with string patterns inside a WMI query loop.
  Touches: `DriveService.GetNVMeDriverInfo` — pre-compile as `static readonly Regex[]` or use `string.Contains` where patterns are simple substrings.
  Acceptance: Preflight runs without per-iteration regex compilation; existing tests pass.
  Complexity: S

- [ ] P3 — Remove dead timeout code in MainViewModel.ObserveLateUpdateCheck
  Why: `Task.WhenAny(completed, Task.Delay(12s))` inside a `ContinueWith` callback always returns immediately because `completed` is the already-finished task that triggered the continuation. The 12-second timeout never fires — it's dead code that misleads readers into thinking there's a timeout guard.
  Evidence: `src/NVMeDriverPatcher/ViewModels/MainViewModel.cs` line 619.
  Touches: `MainViewModel.ObserveLateUpdateCheck` — remove the dead `Task.WhenAny` wrapper or restructure to place the timeout around the original task.
  Acceptance: Update check behavior is unchanged (no functional regression); dead code is removed.
  Complexity: S

- [ ] P3 — Reconcile version strings across docs to the SSOT
  Why: `Directory.Build.props` (`VersionPrefix` = 5.0.0) and the README badge say 5.0.0, but `CLAUDE.md` status and the ROADMAP intro lagged at v4.6.0 (ROADMAP corrected this pass). The project's own rule requires all version strings to match.
  Evidence: `Directory.Build.props` VersionPrefix 5.0.0 vs `CLAUDE.md` "Version: v4.6.0"; `scripts/Validate-ReleaseVersions.ps1` validates release artifacts but not narrative docs.
  Touches: `CLAUDE.md` status line; optionally extend `Validate-ReleaseVersions.ps1` to flag stale version mentions in CLAUDE.md/ROADMAP intros.
  Acceptance: All narrative version mentions match the `Directory.Build.props` SSOT; the validation script (if extended) fails when they drift.
  Complexity: S

---

## Strategic / larger bets

- [ ] P2 — WinRE driver-injection (inject stornvme.sys into the WinRE boot image)
  Why: The recovery kit and SafeBoot keys assume the user can still reach WinRE/Safe Mode. The genuinely-incomplete part of the original §3.3 is injecting the legacy `stornvme.sys` into WinRE's `boot.wim` so the recovery environment itself can always mount the system drive even when the native stack wedges startup. `WinReBcdPrepService` is probe-only today.
  Evidence: `src/NVMeDriverPatcher/Services/WinReBcdPrepService.cs` header comment ("full inject-stornvme.sys-into-WinRE flow deliberately left as future effort"); RESEARCH.md recovery assessment.
  Touches: a new injection path (`Dism /Mount-Image` of the WinRE image, `Add-Driver`, commit), `WinReBcdPrepService`/recovery flow, CLI command, tests/mocks; shares the DISM mount/commit machinery with the WinPE `startnet.cmd` fix.
  Acceptance: After running the prep, WinRE boots and can access the system volume on a machine with the native stack enabled; a dry-run mode reports the planned DISM operations without mounting; documented blast-radius warnings precede any image mutation.
  Complexity: XL
