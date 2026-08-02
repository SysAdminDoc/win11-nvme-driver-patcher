# NVMe Driver Patcher — Roadmap

Living document — **incomplete work only**. Shipped items are deleted (git history + [CHANGELOG.md](CHANGELOG.md) are the record). Blocked items live in [Roadmap_Blocked.md](Roadmap_Blocked.md). Current ship: **v5.2.0**.

**Scope rule:** every item must improve the core function — enabling, disabling, verifying, or rolling back Microsoft's native NVMe driver swap on Windows 11. No external integrations, no general-purpose storage tools, no theme/UI-locale polish. If an idea drifts into "separate tool that happens to live in the same exe," it doesn't belong here. Priority is by user impact / regret cost, not effort; S/M/L/XL are rough effort estimates.

---

Items waiting on external resources (hardware, VMs, live validation, credentials) live in [Roadmap_Blocked.md](Roadmap_Blocked.md).

# Security backlog — 2026-07-30 scan (rev `1d46be5`)

**Unverified.** These came out of a multi-agent security scan of the whole tree that was stopped
after the research pass and before its adversarial verification panel ran. They are researcher
candidates, not confirmed vulnerabilities — **reproduce each one against the current code before
fixing it**, and delete the item outright if it does not hold up. Nineteen raw candidates deduped
to the eleven distinct issues below.

## P3 — Re-run the security scan to completion once the above are addressed
  Why: the run these items came from was stopped before its verification panel, so nothing here
  carries a confidence rating and there is no coverage record — the areas the scan had not yet
  reached (`src/NVMeDriverPatcher.Core` services, the WPF app, the CLI, the EF/SQLite data layer)
  produced no findings simply because they were never examined, which is not the same as clean.
  Acceptance: a completed scan whose report is stamped `verified`, with this section replaced by
  its survivors.
  Complexity: S (unattended run)
