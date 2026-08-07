# NVMe Driver Patcher — Roadmap

Living document — **incomplete work only**. Shipped items are deleted (git history + [CHANGELOG.md](CHANGELOG.md) are the record). Blocked items live in [Roadmap_Blocked.md](Roadmap_Blocked.md). Current ship: **v5.5.0**.

**Scope rule:** every item must improve the core function — enabling, disabling, verifying, or rolling back Microsoft's native NVMe driver swap on Windows 11. No external integrations, no general-purpose storage tools, no theme/UI-locale polish. If an idea drifts into "separate tool that happens to live in the same exe," it doesn't belong here. Priority is by user impact / regret cost, not effort; S/M/L/XL are rough effort estimates.

---

Items waiting on external resources (hardware, VMs, live validation, credentials) live in [Roadmap_Blocked.md](Roadmap_Blocked.md).

---

## P1 — Mirror patch writes into every existing ControlSet00N (issue #15, part 1)
  Why: GitHub issue #15 — `chkdsk /f c:` or an offline `bootstat.dat` deletion makes the next
  boot run Windows boot recovery, which can promote the LastKnownGood control set. Apply writes
  only `SYSTEM\CurrentControlSet\...` while removal already sweeps `ControlSet001–009`
  (`RecoveryKitService.cs:270`, README "for /L %N" removal), so a promoted pre-patch set simply
  lacks every override and SafeBoot key and the machine "reverts" to stornvme. Writing the same
  flags into every *existing* control set (enumerated, never created; skip the one
  `SYSTEM\Select\Current` points at) makes recovery-boot promotion a non-event.
  Evidence: `PatchService.BuildRequiredRegistryMutations` (`PatchService.cs:37-90`) targets
  `AppConfig.cs:53-64` CurrentControlSet paths only; `PatchVerificationService.cs:237` classifies
  the resulting keys-gone state as Reverted; v5.5.0 shipped the diagnosis half (preflight
  `BootRecoveryRisk` check + honest Reverted attribution).
  Touches: `PatchService.BuildRequiredRegistryMutations` (new `mirrorControlSets` parameter,
  mirrors with `CountsTowardPatchTotal:false`), a `SYSTEM\Select`/subkey enumerator,
  `MutationLedgerService.CaptureBaseline` + `RestoreOriginalState` (baseline must capture the
  mirrored paths or exact-restore breaks — this is the hard part), `RecoveryKitService`
  consistency, `DurableRegistryCommitServiceTests`.
  Acceptance: Apply on a machine with ControlSet001+002 writes both sets and the ledger baseline
  records both; RestoreOriginalState removes both exactly; simulated LastKnownGood promotion
  (registry rename in a VM) leaves the patch keys present; verification totals unchanged.
  Complexity: L (ledger baseline symmetry is the bulk)

## P2 — Opt-in boot-time persistence guard (issue #15, part 2)
  Why: Even with mirrored control sets, Startup Repair's driver-rollback diagnostics can remove
  the keys outright. The `BootVerify` ONSTART/SYSTEM task already runs revert-only consumers;
  a `PersistenceGuardService` re-applying a patch the ledger says the user never removed
  (`VerificationOutcome.Reverted` + terminal ledger phase `Applied`/`Verified`) closes the loop.
  Touches: new `PersistenceGuardService` (pure decision function + executor, mirroring
  `AutoRevertService` shape), wired at `Cli/Program.cs` strictly AFTER `AutoRevertService.MaybeRun`
  and `FallbackRecoveryCoordinator.RunOnce` so watchdog reverts always win; `AppConfig` opt-in
  field (off by default, bump ConfigVersion), ADMX knob, CLI command (updates the
  release-validated command count), README.
  Acceptance: Guard refuses after N consecutive re-applies (persisted counter), hard-defers when
  the watchdog is Unstable, honors `BuildActionPolicyService` and the fail-closed startup latch;
  a deliberate `remove` is never overridden. Anti-boot-loop truth table unit-tested.
  Complexity: L — and if validation requires live boot-failure reproduction, cut to
  Roadmap_Blocked.md per the scope rule.

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
