# NVMe Driver Patcher — Roadmap

Living document — **incomplete work only**. Shipped items are deleted (git history + [CHANGELOG.md](CHANGELOG.md) are the record). Blocked items live in [Roadmap_Blocked.md](Roadmap_Blocked.md). Current ship: **v5.0.0**.

**Scope rule:** every item must improve the core function — enabling, disabling, verifying, or rolling back Microsoft's native NVMe driver swap on Windows 11. No external integrations, no general-purpose storage tools, no theme/UI-locale polish. If an idea drifts into "separate tool that happens to live in the same exe," it doesn't belong here. Priority is by user impact / regret cost, not effort; S/M/L/XL are rough effort estimates.

---

Items waiting on external resources (hardware, VMs, live validation, credentials) live in [Roadmap_Blocked.md](Roadmap_Blocked.md).

## Audit backlog (deep audit 2026-07-14)

Found during the engineering + UX audit; not fixed in that pass because they need visual verification, are medium-effort, or are lower priority. The high-confidence findings from the same audit were fixed directly (see CHANGELOG).

- [ ] P2 — Re-theme LiveCharts colors on live theme toggle
  Why: Telemetry/benchmark chart series + axis colors are resolved once via `BrushResources.ResolveColor` (a Color value, not a live brush), so after a Dark↔Light toggle the charts keep the old stroke colors until the next data refresh.
  Where: `src/NVMeDriverPatcher/Views/TelemetryView.xaml.cs`, `BenchmarkComparisonView.xaml.cs`, `Services/ThemeService.cs` (ThemeChanged) — subscribe the chart views and re-render, the way MainWindow re-tints the title bar.

- [ ] P2 — Re-verify the staged updater binary at swap time (TOCTOU)
  Why: `AutoUpdaterService` verifies the downloaded exe, stages it, then prints a PowerShell one-liner that copies it over the running exe after exit. Nothing re-checks the staged file's hash at copy time, and the staging dir may not be admin-only.
  Where: `src/NVMeDriverPatcher.Core/Services/AutoUpdaterService.cs` (BuildRestartCommand / staging) — re-hash inside the generated swap script before Copy-Item; place staging under an admin-ACL'd dir.

- [ ] P3 — Give focusable controls a real keyboard focus ring
  Why: `FocusHidden` (empty ControlTemplate) is applied app-wide; most controls compensate with an `IsKeyboardFocused` border trigger, but a future control using the default style would inherit an invisible focus with no trigger — a silent a11y regression.
  Where: `src/NVMeDriverPatcher/Themes/DarkTheme.xaml` (`FocusHidden` at ~L45) — replace with a 1px focus-ring template so nothing can regress.

- [ ] P3 — Drive the indeterminate progress slider off the track's real width
  Why: `RoundProgressIndeterminate` animates a fixed 120px slider from -120 to 600, so on the wide readiness overlay (~460px) it overshoots and resets with a visible gap instead of looping smoothly.
  Where: `src/NVMeDriverPatcher/Themes/DarkTheme.xaml` (~L481-485).

- [ ] P3 — Anchor WinRE BCD GUID parsing to the BCD-identifier context
  Why: `WinReBcdPrepService.ParseReagentcInfo` returns the first GUID matched anywhere in `reagentc /info`; if a non-BCD GUID ever appears first, `bcdedit /enum {guid}` matches nothing and the WinRE fallback is silently mis-reported as unavailable.
  Where: `src/NVMeDriverPatcher.Core/Services/WinReBcdPrepService.cs:44-53,76-84`.

- [ ] P3 — Await DISM/WinPE stdout/stderr reader tasks on the success path
  Why: `RunProcessAsync` only awaits the reader tasks on non-zero exit; on success they're abandoned and cancelled at method exit (harmless but inconsistent with the drained pattern elsewhere).
  Where: `src/NVMeDriverPatcher.Core/Services/WinReDriverInjectionService.cs:304-322`, `WinPERecoveryBuilderService.cs:299-316`.

- [ ] P3 — Distinguish updater transport/parse errors from "no update available"
  Why: `FetchLatestAssetAsync` collapses network errors, GitHub rate-limiting (403), and JSON parse failures all to `(null,null,null)`, so a scripted `update` check can't tell "offline/rate-limited" from "up to date."
  Where: `src/NVMeDriverPatcher.Cli/Program.cs` update path + `AutoUpdaterService.FetchLatestAssetAsync`.
