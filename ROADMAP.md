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
