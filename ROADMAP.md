# NVMe Driver Patcher — Roadmap

Living document — **incomplete work only**. Shipped items are deleted (git history + [CHANGELOG.md](CHANGELOG.md) are the record). Blocked items live in [Roadmap_Blocked.md](Roadmap_Blocked.md). Current ship: **v5.2.0**.

**Scope rule:** every item must improve the core function — enabling, disabling, verifying, or rolling back Microsoft's native NVMe driver swap on Windows 11. No external integrations, no general-purpose storage tools, no theme/UI-locale polish. If an idea drifts into "separate tool that happens to live in the same exe," it doesn't belong here. Priority is by user impact / regret cost, not effort; S/M/L/XL are rough effort estimates.

---

Items waiting on external resources (hardware, VMs, live validation, credentials) live in [Roadmap_Blocked.md](Roadmap_Blocked.md).

## P2 — Refresh `windows_build_rules.json` review dates before they go stale on 2026-08-13
  Why: `BuildActionPolicyService` treats a rule whose `lastReviewed` is more than 30 days old as
  stale, and a stale rule is verify/rollback-only. Every bundled rule is dated `2026-07-14`, so on
  **2026-08-13** apply becomes globally blocked on every build with no code change and no user
  action — the tool silently stops being able to do its primary job.
  Touches: `src/NVMeDriverPatcher.Core/windows_build_rules.json` (`updated` + each rule's
  `lastReviewed`), after re-verifying each rule's `sourceUrl` still supports its `expectedPath`.
  Acceptance: each rule's verdict is re-confirmed against its source (or corrected), dates are
  refreshed, and `BuildActionPolicyService` reports no stale-rule block on a current build.
  Note: this recurs every 30 days — consider whether the staleness window should be widened or the
  refresh should become a release-gate checklist item rather than a silent expiry.
  Complexity: S (re-verification is the work, not the edit)
