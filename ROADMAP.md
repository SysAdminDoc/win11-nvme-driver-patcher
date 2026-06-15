# NVMe Driver Patcher — Roadmap

Living document — **incomplete work only**. Shipped items are deleted (git history + [CHANGELOG.md](CHANGELOG.md) are the record). Current ship: **v5.0.0**.

**Scope rule:** every item must improve the core function — enabling, disabling, verifying, or rolling back Microsoft's native NVMe driver swap on Windows 11. No external integrations, no general-purpose storage tools, no theme/UI-locale polish. If an idea drifts into "separate tool that happens to live in the same exe," it doesn't belong here. Priority is by user impact / regret cost, not effort; S/M/L/XL are rough effort estimates.

---

## P2 — Reliability, safety, and fleet

- [ ] P2 — Preflight check: feature-management prerequisites broken by debloat tools
  Why: A community report describes the native NVMe driver refusing to bind until previously disabled scheduled tasks were restored — debloated systems are common in exactly this tool's audience, and today the failure is silent and misdiagnosed as the Microsoft block. Reproduce on a debloated VM first; ship the check only once the responsible task/service set is confirmed.
  Evidence: Overclock.net thread 1818467 page 5 user report (secondhand; Needs live validation — see RESEARCH.md Open Question 3).
  Touches: `Services/SystemGuardrailsService.cs` (alongside HVCI/WDAC/VROC/AppLocker), `PreflightService`, tests.
  Acceptance: On a VM with the offending tasks/services disabled, preflight surfaces a named warning with a one-click/`schtasks` restore hint; healthy systems show no new warning.
  Complexity: M (including the reproduction work)

- [ ] P3 — Surface the firmware disable/re-enable workflow in the GUI
  Why: The CLI `disable-for-update` / `re-enable-after-update` roundtrip shipped, but there is no GUI
  firmware-nudge panel yet, so GUI users can't trigger it with one click.
  Touches: a new firmware-nudge panel in `MainWindow.xaml` + `MainViewModel` commands that call
  `FirmwareUpdateWorkflowService` and the existing apply/remove flow.
  Acceptance: A GUI button reverts to legacy with update guidance and a second re-applies the
  remembered profile; the activity log shows the full roundtrip.
  Complexity: M

---

## P3 — Hardening & cleanup

---

## Strategic / larger bets

- [ ] P2 — WinRE driver-injection: execute the mount/commit (preview shipped)
  Why: The `winre-inject` CLI command now previews the exact DISM plan (mount → add-driver →
  unmount/commit) with blast-radius warnings via `WinReDriverInjectionService` (plan/render unit-tested,
  no image mutation). The remaining genuinely-incomplete + risky part is executing the mount/commit and
  validating it with a real WinRE boot — which needs live hardware/VM with WinRE, not available in the
  build/CI environment.
  Touches: an opt-in `--commit` execution path reusing the proven `WinPERecoveryBuilderService` DISM
  mount/commit pattern (with discard-on-failure), recovery flow wiring, and a manual WinRE-boot test plan.
  Acceptance: With `--commit`, the plan mounts the WinRE image, adds stornvme, and commits with rollback
  on failure; after running it, WinRE boots and can access the system volume on a native-stack machine.
  Complexity: L (down from XL — planning + preview done)

## Research-Driven Additions

- [ ] P3 — Extend `--json` to the last few read commands
  Why: The versioned `--json` envelope + `CliJson` framework now covers `status`, `watchdog`, `controllers`,
  `recovery-proof`, and `bypassio`. The remaining read commands — `firmware`, `featurestore`, `reliability`,
  `minidump` — could adopt the same one-line pattern so all fleet state is scriptable.
  Touches: `CliJson` builders for each report type, `--json` branch in each command, contract tests.
  Acceptance: Each listed command returns the versioned JSON envelope under `--json` with field-name tests; text stays the default.
  Complexity: S

### P1 — Reliability and Hardening

- [ ] P1 — Extract shared services into a NVMeDriverPatcher.Core library
  Why: The Tray project references the GUI project transitively, pulling the entire WPF framework into a WinForms-only app. Shared models and services should live in a framework-agnostic class library.
  Touches: New `NVMeDriverPatcher.Core` project; move `Services/`, `Models/`, `Data/` from the GUI project; update all ProjectReferences.
  Complexity: L

- [ ] P1 — PatchService core-flow unit tests
  Why: The most critical service (install/uninstall/rollback) has zero test coverage for its main flows. Only helper methods are tested.
  Touches: `PatchServiceTests.cs`, mock infrastructure for `RegistryKey`/`DataService`/`EventLogService`.
  Complexity: M

- [ ] P2 — NativeMethods: SafeHandle for SetupDi device info sets
  Why: `SetupDiGetClassDevs` returns a raw `IntPtr` that callers must manually free with `SetupDiDestroyDeviceInfoList`. A custom `SafeHandle` subclass would ensure cleanup on exceptions.
  Touches: `Interop/NativeMethods.cs`, `HotSwapService.cs`, any SetupDi callers.
  Complexity: S

- [ ] P2 — NativeMethods: enforce SP_DEVINFO_DATA.cbSize initialization
  Why: `SP_DEVINFO_DATA` and `SP_CLASSINSTALL_HEADER` require `cbSize` to be pre-set. Missing initialization causes silent SetupDi API failures.
  Touches: `Interop/NativeMethods.cs` — add static factory methods.
  Complexity: S

- [ ] P2 — EventLogWatchdogService: file-level locking for concurrent evaluate
  Why: The read-modify-write cycle on the watchdog state file has no file lock, so concurrent evaluations (GUI + CLI) can clobber each other's cumulative event counts.
  Touches: `Services/EventLogWatchdogService.cs`.
  Complexity: S

- [ ] P2 — FeatureStoreWriterService: concurrency protection for WriteOverrides
  Why: No lock serializes concurrent calls; interleaved writes could produce incorrect verification results.
  Touches: `Services/FeatureStoreWriterService.cs` — add SemaphoreSlim.
  Complexity: S

- [ ] P2 — PowerShell: DiskSpd hash verification
  Why: DiskSpd is downloaded from GitHub and executed as admin without SHA-256 hash verification. A compromised proxy could inject a malicious binary.
  Touches: `NVMe_Driver_Patcher.ps1` lines 910-912.
  Complexity: S

- [ ] P3 — ComboBox/Expander keyboard focus indicators
  Why: `DarkComboBox` and `DarkExpander` suppress focus visuals via `FocusHidden` and have no substitute trigger. Keyboard users cannot see which control is focused.
  Touches: `Themes/DarkTheme.xaml` ComboBox and Expander templates.
  Complexity: S

- [ ] P3 — FirmwareUpdateNudgeService: overly broad "ct" vendor pattern
  Why: The `"ct"` pattern intended for Crucial drives matches any model containing "ct" (e.g., "Direct", "Connected"). Could show wrong firmware update guidance.
  Touches: `Services/FirmwareUpdateNudgeService.cs`.
  Complexity: S

- [ ] P3 — xunit 2.x / xunit.runner.visualstudio 3.x version alignment
  Why: Cross-major pairing can cause subtle test discovery issues. Should be on consistent major version.
  Touches: `tests/NVMeDriverPatcher.Tests/NVMeDriverPatcher.Tests.csproj`.
  Complexity: S
