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

---

# Security backlog — 2026-07-30 scan (rev `1d46be5`)

**Unverified.** These came out of a multi-agent security scan of the whole tree that was stopped
after the research pass and before its adversarial verification panel ran. They are researcher
candidates, not confirmed vulnerabilities — **reproduce each one against the current code before
fixing it**, and delete the item outright if it does not hold up. Nineteen raw candidates deduped
to the eleven distinct issues below.

## P0 — Watchdog service installer launches `sc.exe` by bare name, defeating the repo's own anti-planting rule
  Why: `RunSc` in the watchdog passes the bare string `sc.exe` to `ProcessStartInfo`, so Windows
  resolves it through the executable directory and the current working directory before
  `System32`. The control verbs (`/install`, `/uninstall`) only ever run elevated — the manifest is
  `requireAdministrator` and the WiX custom action runs them as SYSTEM with `Impersonate="no"` — so
  a planted `sc.exe` inherits that token. `RunSc` is called six times during `/install`, and a
  planted stub can return success to hide that no service was ever registered. This directly
  violates the documented invariant in CLAUDE.md ("never pass a bare tool name to
  `ProcessStartInfo`"), which means the guard test is not catching what it claims to.
  Exposure: highest for the standalone released `*-win-x64.exe` run from Downloads, a portable
  folder, or a USB stick (a path the project supports via `PortableModeService`); an MSI install
  into Program Files is not exposed via the exe directory, but still is via the working directory
  of the elevated console that invokes the verb.
  Touches: `src/NVMeDriverPatcher.Watchdog/Program.cs:158` (`RunProcess`/`RunSc`) — route through
  `SystemToolPathService.Resolve("sc.exe")` like every other shipped call site, or drop the
  shell-out for the `System.ServiceProcess`/advapi32 service APIs.
  Also fix the gate: `SystemToolPathServiceTests.NoShippedSourceLaunchesAToolByBareName` scans
  `src/` and was green while this shipped — its `new ProcessStartInfo` regex does not match how the
  watchdog constructs the call. Widen the detector, and self-check it against this defect's exact
  shape (the same way the recovery-kit absolute-path test self-checks).
  Acceptance: the watchdog resolves `sc.exe` to `%SystemRoot%\System32\sc.exe`; the widened
  regression test fails against the old code and passes against the new; a fake `sc.exe` dropped
  beside the exe and in the CWD is provably not executed during `/install`.
  Complexity: S (fix), M (test detector is the real work)

## P1 — Shipped PowerShell module resolves the privileged CLI from the current directory and `$PATH`
  Why: `Invoke-Cli` builds its candidate list for `NVMeDriverPatcher.Cli.exe` starting from a bare
  relative path and falls back to a `Get-Command` `$PATH` lookup, then executes whatever it finds.
  The module is written to be used from an elevated session, so this is the same binary-planting
  class as the P0 above, in the packaging surface rather than in `src/` — which is exactly why the
  `src/`-only regression scan never saw it.
  Touches: `packaging/powershell/NVMeDriverPatcher.psm1:38`. Drop the bare relative candidate and
  the `$PATH` fallback; resolve only from `$PSScriptRoot` and the MSI's recorded install location
  (HKLM install path / `%ProgramFiles%\NVMe Driver Patcher\`), require a fully-qualified path, and
  reject any resolved path whose directory is writable by non-administrators.
  Acceptance: a planted `NVMeDriverPatcher.Cli.exe` in the CWD and on `$PATH` is not invoked; the
  bare-name regression gate is extended to cover `packaging/` (and `scripts/`), not just `src/`.
  Complexity: S

## P1 — MSI never ACL-hardens `INSTALLFOLDER`, yet runs an auto-start service and a SYSTEM custom action from it
  Why: `INSTALLFOLDER` is user-selectable and inherits its parent's DACL, so an install to a
  non-Program-Files path leaves the watchdog binary writable by a standard user while the MSI
  registers it as an auto-start service and invokes it from a deferred SYSTEM custom action —
  a straight write-to-SYSTEM-execution path. `PROGRAMDATAFOLDER` already gets the correct
  treatment, so the pattern to copy is in the same file.
  Touches: `packaging/wix/NVMeDriverPatcher.wxs:121` (`ComponentGroup:WatchdogFiles` /
  `Component:WatchdogExe`) — apply an explicit `PermissionEx` DACL (SYSTEM + Administrators full,
  Users read+execute), or add a LaunchCondition that refuses the WatchdogService feature when the
  resolved `INSTALLFOLDER` is outside `ProgramFiles64Folder`.
  Acceptance: after an install to a user-writable path, the watchdog directory denies write to
  standard users (or the service feature refuses to install there); covered by an installer test
  alongside the existing `InstallerContentTests`.
  Complexity: M

## P2 — Telemetry worker stores unvalidated request bodies and republishes them as the public compat summary
  Why: `POST /nvme/compat` checks only `schemaVersion`, the `anonId` regex, and a 16 KiB total
  size, then writes the entire parsed body to KV verbatim with a one-year TTL. The anonymous
  `GET /nvme/compat/summary` then aggregates those stored records — including each `model` and
  `firmware` string and an uncapped `controllers` array — so any anonymous client dictates the
  content and the cardinality of a public endpoint. `packaging/schemas/telemetry_payload.schema.json`
  exists and is never enforced at ingest.
  Touches: `packaging/telemetry-receiver/cloudflare-worker.js:39,58`. Validate against the shipped
  schema before the write: reject unknown top-level fields, cap `controllers` length, cap and
  allowlist `model`/`firmware`, constrain `verification`/`profile`/`watchdog` to their enums, and
  persist a normalized projection rather than `body`.
  Acceptance: a payload with extra fields, an oversized `controllers` array, or a hostile
  `model` string is rejected at ingest and cannot reach the summary; schema and code agree.
  Complexity: M

## P2 — Telemetry write endpoint is reachable cross-origin; the CORS allowlist is not a request-blocking control
  Why: the worker omits `Access-Control-Allow-Origin` for non-allowlisted origins but still
  executes the KV write — omitting the response header only stops the attacker reading the
  response, not the write landing. A simple `POST` (no preflight) from any website makes visitors
  submit telemetry records. The file comment (lines 66-71) and `packaging/telemetry-receiver/README.md`
  (lines 24-29) both assert a protection the code does not provide, so the docs need correcting
  along with the code.
  Touches: `packaging/telemetry-receiver/cloudflare-worker.js:58`; require
  `content-type: application/json` exactly (forcing a preflight) and/or a preflight-forcing custom
  header, and return 403 when an `Origin` header is present and not on the allowlist.
  Acceptance: a cross-origin simple POST performs no KV write; the comment and README describe the
  control that actually exists.
  Complexity: S

## P2 — Rate limiting is a check-then-write race, and the atomic limiter is commented out by default
  Why: `checkRateLimit` reads the counter, the whole request body is then read and durably
  written, and only afterwards is the counter incremented — concurrent requests all observe the
  pre-increment value, so the budget is bypassed by parallelism. The `RATE_LIMITER` binding, whose
  `limit()` checks and consumes atomically, is disabled in `wrangler.toml`, so a stock deployment
  silently degrades to the racy KV counter.
  Touches: `packaging/telemetry-receiver/cloudflare-worker.js:93,103` and
  `packaging/telemetry-receiver/wrangler.toml`. Require the `RATE_LIMITER` binding and fail closed
  (429) when it is absent; if the KV fallback stays, consume before doing any work rather than
  after the durable write, and drop the redundant second `get` in `incrementRateLimit`.
  Acceptance: N concurrent requests over the budget yield the expected number of 429s; a
  deployment without the binding fails closed rather than degrading.
  Complexity: M

## P2 — `GET /nvme/compat/summary` returns before the rate-limit gate, giving unthrottled read amplification
  Why: the route dispatch runs ahead of `checkRateLimit`, so the most expensive endpoint — it
  enumerates the whole KV namespace via `paginateKeys` and recomputes the aggregate per request —
  is the one path with no throttle at all.
  Touches: `packaging/telemetry-receiver/cloudflare-worker.js:27`. Move `checkRateLimit` above the
  dispatch so it covers every route, give the summary its own tighter budget, cache the computed
  summary with a short TTL, and bound the pagination.
  Acceptance: repeated summary requests are throttled and served from cache; no request
  enumerates the full namespace.
  Complexity: S

## P2 — Privacy claims in the telemetry README are not what the worker implements
  Why: three separate defects each contradict a documented guarantee, and they are cheapest to fix
  as one pass over the same file:
  - Submitter IPs are hashed with an **unkeyed, unsalted** SHA-256 and persisted as KV keys. The
    IPv4 space is exhaustively enumerable, so this is reversible — it is not pseudonymisation, and
    the "no IP addresses are stored" claim does not hold.
  - `env.SALT` silently defaults to `""` (line 53), so a stock deployment that forgot the secret
    hashes `anonId` with no salt and fails open rather than closed.
  - The raw `anonId` is stored verbatim inside the record value next to its own hash (line 58),
    which nullifies the salted-hash design for the full one-year TTL.
  Touches: `packaging/telemetry-receiver/cloudflare-worker.js:53,58,100`,
  `packaging/telemetry-receiver/README.md`. Use keyed HMAC over a mandatory per-deployment secret
  (plus a rotating epoch for the IP key), refuse to serve when that secret is unset, strip
  `anonId` from the persisted projection, and prefer the `RATE_LIMITER` binding so no IP-derived
  value is persisted at all. Then make the README describe what the code does.
  Acceptance: no unkeyed IP-derived value and no raw `anonId` is persisted; a salt-less deployment
  returns 500 instead of hashing with `""`; README claims match the implementation.
  Complexity: M

## P3 — Request body is fully parsed and re-serialized before the 16 KiB size gate applies
  Why: `request.json()` deserializes an unbounded stream, and only then is `JSON.stringify(body).length`
  measured against the cap — the parse cost is already paid, and the re-serialization doubles it.
  Touches: `packaging/telemetry-receiver/cloudflare-worker.js:39`. Check `content-length` first and
  return 413 immediately, read through a size-capped reader, and drop the `JSON.stringify` round-trip.
  Acceptance: an oversized body is rejected without being parsed.
  Complexity: S

## P3 — Extend the bare-name execution gate beyond `src/`
  Why: the P0 and P1 items above are the same defect class in two places, and the existing
  `NoShippedSourceLaunchesAToolByBareName` scan covers neither — one because its regex misses the
  call shape, the other because the scan never looks outside `src/`. Fixing the two call sites
  without widening the gate leaves the next one to be found by the next scan.
  Touches: `tests/NVMeDriverPatcher.Tests/SystemToolPathServiceTests.cs` — scan `src/`,
  `packaging/`, and `scripts/`, cover `.ps1`/`.psm1` invocation as well as `ProcessStartInfo`, and
  assert the detector fires on both known defect shapes.
  Acceptance: the widened gate fails on the pre-fix tree at both sites and passes after.
  Complexity: M

## P3 — Re-run the security scan to completion once the above are addressed
  Why: the run these items came from was stopped before its verification panel, so nothing here
  carries a confidence rating and there is no coverage record — the areas the scan had not yet
  reached (`src/NVMeDriverPatcher.Core` services, the WPF app, the CLI, the EF/SQLite data layer)
  produced no findings simply because they were never examined, which is not the same as clean.
  Acceptance: a completed scan whose report is stamped `verified`, with this section replaced by
  its survivors.
  Complexity: S (unattended run)
