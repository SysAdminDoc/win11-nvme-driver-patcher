# NVMe Driver Patcher — Roadmap

Living document — **incomplete work only**. Shipped items are deleted (git history + [CHANGELOG.md](CHANGELOG.md) are the record). Blocked items live in [Roadmap_Blocked.md](Roadmap_Blocked.md). Current ship: **v5.0.0**.

**Scope rule:** every item must improve the core function — enabling, disabling, verifying, or rolling back Microsoft's native NVMe driver swap on Windows 11. No external integrations, no general-purpose storage tools, no theme/UI-locale polish. If an idea drifts into "separate tool that happens to live in the same exe," it doesn't belong here. Priority is by user impact / regret cost, not effort; S/M/L/XL are rough effort estimates.

---

## Research-Driven Additions

### P0

### P1

### P2

- [ ] P2 — Replace MSI placeholder copy and add an installer-content contract
  Why: the shipping MSI displays lorem ipsum, and its README misstates the watchdog service account, eroding trust before an elevated system change.
  Evidence: GitHub issue #12; `packaging/wix/NVMeDriverPatcher.wxs:14-55`; `packaging/wix/README.md:21`; WiX `WixUI_InstallDir` and localization guidance.
  Touches: `packaging/wix/NVMeDriverPatcher.wxs`, an installer `.wxl` localization asset, `packaging/wix/README.md`, packaging/release contract tests.
  Acceptance: A built MSI shows product-specific purpose, risk, and recovery copy with no placeholder text; all strings come from the en-US localization contract; automated inspection rejects lorem ipsum/version/service-account drift; documentation says LocalService and matches the MSI service table.
  Complexity: S

- [ ] P2 — Emit deletion directives in the pre-patch registry backup and fix recovery messaging
  Why: `ExportRegistryBackup` records only pre-existing values, so re-importing it after a patch never removes the keys the patch added; recovery guidance that points users at this backup over-promises in exactly the incomplete-rollback path.
  Evidence: `src/NVMeDriverPatcher.Core/Services/RegistryService.cs:152-264`; recovery message at `src/NVMeDriverPatcher.Core/Services/PatchService.cs:316`.
  Touches: `RegistryService` (add `"<id>"=-` and `[-...GUID]` directives for keys absent pre-patch, or a separate revert.reg), `PatchService` recovery copy, tests.
  Acceptance: Importing the generated backup after a patch removes every app-added override/SafeBoot key and restores prior values; a test asserts the emitted `.reg` deletes keys that were absent pre-patch; recovery messaging accurately describes what the backup restores.
  Complexity: M

- [ ] P2 — Refresh windows_build_rules.json and compat.json against 2026 sources
  Why: Static safety data drifts against a fast-moving target — the exact registry-override block build (reported `26100.8106`) is modeled fuzzily, the Samsung 990 Pro firmware entry conflicts with community reports (`4B2QJXD7` bad / `6B2QJXD7` fixed), and new HMB firmware advisories (WD SN5000 2TB fix `291020WD`, SN770 2TB) aren't captured.
  Evidence: `src/NVMeDriverPatcher.Core/windows_build_rules.json`; `src/NVMeDriverPatcher.Core/compat.json:7-28`; RESEARCH.md Open Questions; heise/SanDisk KB 51469; Samsung EU community 12822796.
  Touches: `windows_build_rules.json`, `compat.json`, provenance `sourceUrl`/`lastReviewed` fields, schema/provenance tests.
  Acceptance: Block/known-working build numbers carry precise UBRs with sources; the 990 Pro entry is reconciled to the confirmed bad/fixed revisions (or explicitly marked needs-validation per the open question); WD SN5000 2TB and SN770 2TB HMB entries exist with fix firmware; provenance tests stay green.
  Complexity: S

### P3

- [ ] P3 — Gate the StorNVMe tuning surface on the currently-bound driver
  Why: `TuningService` writes `Services\stornvme\Parameters\Device`, which the native `nvmedisk` driver ignores; once the patch is active the tuning UI/CLI reports "applied/verified" while changing nothing on the bound stack.
  Evidence: `src/NVMeDriverPatcher.Core/Services/TuningService.cs:8`, `:89-90`; `TuningPanel`, CLI `tuning-import`.
  Touches: `TuningService`, `TuningPanel`/`MainViewModel`, CLI tuning commands.
  Acceptance: When `nvmedisk` is the bound driver, tuning controls are disabled or clearly labeled legacy-stack-only (or target the correct service key), and no command reports success for a write that cannot take effect.
  Complexity: S

- [ ] P3 — Warn that native NVMe changes the disk ID before it breaks backup tools
  Why: Enabling the native stack mutates the disk ID, which is the root cause of Acronis/Veeam/Macrium losing the drive; today the tool warns those tools are affected but not why or when the mount/backup chains break.
  Evidence: guru3D forums 458842 (disk-ID mutation root cause); existing backup-tool warnings in `DriveService`/preflight.
  Touches: preflight/confirmation copy for detected backup software, README known-issues, tests.
  Acceptance: When a supported backup tool is detected, the warning names the disk-ID change as the cause and advises re-registering backup jobs/chains after the swap; the copy is covered by a preflight message test.
  Complexity: S
