# NVMe Driver Patcher — Roadmap

Living document — **incomplete work only**. Shipped items are deleted (git history + [CHANGELOG.md](CHANGELOG.md) are the record). Blocked items live in [Roadmap_Blocked.md](Roadmap_Blocked.md). Current ship: **v5.3.0**.

**Scope rule:** every item must improve the core function — enabling, disabling, verifying, or rolling back Microsoft's native NVMe driver swap on Windows 11. No external integrations, no general-purpose storage tools, no theme/UI-locale polish. If an idea drifts into "separate tool that happens to live in the same exe," it doesn't belong here. Priority is by user impact / regret cost, not effort; S/M/L/XL are rough effort estimates.

---

Items waiting on external resources (hardware, VMs, live validation, credentials) live in [Roadmap_Blocked.md](Roadmap_Blocked.md).

---

## Audit Findings — 2026-08-02

Baseline recorded before any analysis: `dotnet build NVMeDriverPatcher.sln` = 8 projects,
**0 errors, 0 warnings**; `dotnet test NVMeDriverPatcher.sln` = **1088 passed, 0 failed, 0 skipped**;
working tree clean at `95bbf11`. **There are no pre-existing baseline failures** — every item below
is a new finding, not a known-red test.

Audit-only pass: no source file, CHANGELOG, or README was modified, and nothing was committed.
This repo has already absorbed two full audit drains (the 2026-07-14 deep audit → v5.1.0 and the
2026-07-30 security backlog → v5.3.0), so candidate findings were traced to their callers before
being written down. **Six suspicions were investigated and discarded as false positives** rather
than logged (see "Checked and found clean" at the end) — that list is deliberately included so a
future pass does not re-raise them.

- [ ] P3 — Settings help text calls the Safe *profile* "Safe Mode", colliding with Windows Safe Mode in a tool that writes SafeBoot keys
  Category: ux
  Where: `src/NVMeDriverPatcher/ViewModels/MainViewModel.cs:147` and `:259`
  (`PatchProfileHelpText`); the controls it describes are
  `src/NVMeDriverPatcher/Views/MainWindow.xaml:2653` and `:2658`.
  Problem: the radio buttons, the `PatchProfile` enum and the CLI flags all use "Safe profile" /
  `--safe`. The help text directly beneath them reads "**Safe Mode** writes only 735209102 — enough
  to swap the driver with no boot-crash reports tied to it." In this specific product "Safe Mode"
  is not a synonym: the tool writes `SafeBoot\Minimal` and `SafeBoot\Network` registry entries and
  its entire documented rollback story is "boot into Windows Safe Mode and run the recovery kit". A
  user reading this line can reasonably conclude the radio button controls Windows Safe Mode
  behaviour rather than which feature flags get written. The phrase "no boot-crash reports tied to
  it" is also doing too much work for a first-run reader.
  Evidence: confirmed both strings and both radio labels by grep; verified in the rendered
  `settings-light.png` / `settings-compact-dark.png` snapshots that this help line sits immediately
  under the "Safe profile (recommended)" / "Full profile" radios, so the two terms appear together
  on screen.
  Fix: replace "Safe Mode" with "Safe profile" in both strings so the help text matches the control
  label, the enum and the CLI flag. Suggested: "Safe profile writes only feature flag 735209102,
  plus the Safe Boot entries used for rollback — enough to swap the driver, and the profile with no
  community boot-crash reports against it. This is what you want on a daily-driver machine."
  Acceptance: no user-facing string uses "Safe Mode" to mean the patch profile; `grep -rn "Safe Mode"
  src/NVMeDriverPatcher` returns only strings that genuinely refer to Windows Safe Mode / Safe Boot.
  Confidence: Verified
  Effort: S

- [ ] P3 — Every status-text update re-reads and re-parses the benchmark history file on the UI thread
  Category: perf
  Where: `src/NVMeDriverPatcher/Views/MainWindow.xaml.cs:633-648` (`ViewModel_PropertyChanged`) →
  `:718-728` (`RefreshBenchmarkWorkspace`) → `BenchmarkService.GetHistory`
  (`src/NVMeDriverPatcher.Core/Services/BenchmarkService.cs`).
  Problem: `ViewModel_PropertyChanged` calls `RefreshBenchmarkWorkspace()` on every change to
  `StatusText` or `BenchLabelText` while workspace tab 0 is selected — and tab 0 (Benchmarks) is
  the default selection. `RefreshBenchmarkWorkspace()` synchronously calls
  `BenchmarkService.GetHistory(...)`, which does `File.Exists` + `File.ReadAllText` +
  `JsonSerializer.Deserialize` + a sanitize pass, then rebuilds the LiveCharts series via
  `BenchmarkView.UpdateChart(history)`. All of this runs on the UI thread. `StatusText` is assigned
  from 17 sites across the view models and updates repeatedly through a readiness refresh, which is
  the app's startup path — so the default view does a file read, JSON parse and chart rebuild per
  status tick.
  Evidence: read the call chain; the existing comment at `:634-636` shows the frequency was already
  known ("BenchLabelText and StatusText fire often during preflight") and was addressed only by
  skipping the work when the tab is *not* visible — which leaves the default, visible case doing
  the full read every time. `AccessibilitySmokeTests` asserts `Assert.Equal(0, workspace.SelectedIndex)`,
  confirming tab 0 is the default.
  Fix: cache the parsed history in the view model or `MainWindow` and only re-read when it actually
  changes — invalidate on benchmark completion rather than on status text. At minimum, debounce
  `RefreshBenchmarkWorkspace` (a `DispatcherTimer` trailing edge, matching the existing
  `_settingsSaveDebouncer` pattern in `MainViewModel.Settings.cs:75-95`) and compare the file's
  `LastWriteTimeUtc` before re-parsing.
  Acceptance: during a full readiness refresh with the Benchmarks tab visible,
  `BenchmarkService.GetHistory` is invoked at most once (or once per actual history change) rather
  than once per status update; the chart still updates when a benchmark completes.
  Confidence: Verified
  Effort: S

- [ ] P3 — No regression gate pins code-side colour tokens to the theme, or asserts the compact layout does not clip
  Category: testing
  Where: `tests/NVMeDriverPatcher.Tests/AccessibilitySmokeTests.cs` (the only UI-level test);
  no test covers `src/NVMeDriverPatcher/Views/BrushResources.cs`.
  Problem: the two visual defects above both shipped silently because nothing tests for them.
  `AccessibilitySmokeTests` loads all three themes, asserts a dozen resource keys exist, checks
  automation names and focus order, and *renders* `settings-compact-dark.png` — but it makes no
  assertion about the compact render, so the clipped action buttons pass CI. Separately, nothing
  compares `BrushResources.SemanticFallbacks` against `DarkTheme.xaml`, which is why 33 tokens
  drifted between two commits on the same day without failing anything.
  Evidence: `AssertThemeResources` (`AccessibilitySmokeTests.cs:167-180`) only calls
  `resources.Contains(key)` — presence, never value. `SaveWorkspaceSnapshots` writes PNGs and
  asserts nothing about them. No test file references `BrushResources`.
  Fix: add two tests in the existing style. (1) A drift gate parsing `DarkTheme.xaml` and asserting
  every `SemanticFallbacks` entry matches — same shape as
  `TelemetryReceiverSummaryTests.WorkerFieldAllowlists_MatchTheShippedSchema`. (2) A layout
  assertion in `AccessibilitySmokeTests` that after `UpdateAdaptiveLayout` at 1150x900 each settings
  card's `ActualHeight >= DesiredSize.Height` and its action buttons are within the card bounds.
  Both must fail against the current tree and pass after the P1/P2 fixes.
  Acceptance: both tests fail on the pre-fix tree and pass afterwards; total suite stays green.
  Confidence: Verified
  Effort: S

- [ ] P3 — Unaudited surfaces from the 2026-08-02 pass — needs a follow-up
  Category: docs
  Where: repository-wide.
  Problem: this pass did not reach everything, and the gaps should be explicit rather than implied
  as clean. Not covered: (a) the **running elevated GUI** — both shipped exes carry a
  `requireAdministrator` manifest and this environment is non-elevated, so all UI observation came
  from the in-process `AccessibilitySmokeTests` render harness; no live interaction, hover/focus
  states, toast behaviour, dialog flows, or keyboard navigation was exercised against a real
  window. (b) The **HighContrast theme was never rendered** — the snapshot harness only emits Dark
  and Light, so high-contrast layout/visuals are unverified beyond resource-key presence and static
  contrast maths. (c) The **tray agent** (`NVMeDriverPatcher.Tray`) received no review.
  (d) Roughly 70 of the 85 `Core` services were not read line-by-line — the pass targeted the
  largest and most safety-adjacent ones plus pattern sweeps. (e) `NVMe_Driver_Patcher.ps1` (208 KB
  legacy read/recover-only artifact) was not reviewed beyond its release gate. (f) No fuzzing or
  malformed-input testing of the recovery-kit / WinPE / manifest parsers.
  Fix: schedule a follow-up pass covering (a)-(f); the highest value is (a) and (b), since the
  harness could be extended to emit HighContrast snapshots cheaply by adding
  `AppThemeMode.HighContrast` to the loop in `AccessibilitySmokeTests.SaveWorkspaceSnapshots:143-147`.
  Acceptance: a subsequent audit records coverage for each of (a)-(f) or converts them into
  concrete findings.
  Confidence: Verified
  Effort: M

### Checked and found clean — do not re-raise without new evidence

Recorded so a later pass does not spend effort re-deriving these, and does not "fix" working code:

- **Theme styles missing from Light/HighContrast.** Those dictionaries define only 38 of Dark's 82
  keys, but both merge `DarkTheme.xaml` as their base (`LightTheme.xaml:3-5`,
  `HighContrastTheme.xaml:3-5`), so the 44 control Styles resolve in every theme. Dialogs using
  `{StaticResource ActionButton}` do not break after a theme switch.
- **Settings summary showing "Theme: Dark" while Light is selected** in the rendered snapshot is a
  harness artifact: the snapshot code calls `ThemeService.ApplyMode` directly, bypassing
  `MainViewModel.SetThemeMode`. The real ComboBox path
  (`MainWindow.xaml.cs:231-241` → `SetThemeMode` → `RefreshThemeModeSummary`) updates both strings,
  and `ThemeService_ThemeChanged` (`:223-229`) also refreshes them on OS-driven changes in System mode.
- **WCAG contrast.** Computed every text/surface and border/surface pair across all three themes.
  The only sub-4.5:1 text token is `TextDimmer`, whose sole use is a decorative "•" separator
  (`MainWindow.xaml:3153`). Sub-3:1 border pairs are decorative card edges, not the sole means of
  identifying a control.
- **Accessible names.** 97 interactive elements; every icon-only control has
  `AutomationProperties.Name`. No unnamed glyph-only buttons.
- **CLI exit codes.** `verify-payload` returns 3 for usage errors, 1 for verification failure, 0 on
  success; unknown commands return 3. (An earlier measurement suggesting 0 was a shell error —
  `$?` after a pipe reports `head`, not the CLI.)
- **`TuningService` bounds and `HotSwapService` transaction safety.** All six writable StorNVMe
  parameters are bounds-checked before any registry write; `HotSwapTransactionService` is a
  `partial HotSwapService` and is covered through the injected platform seam by 11 tests in
  `HotSwapServiceTests`, including flush failure, partial dismount and restore paths.
