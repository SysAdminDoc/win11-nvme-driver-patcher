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

## P3 — Re-run the security scan to completion once the above are addressed
  Why: the run these items came from was stopped before its verification panel, so nothing here
  carries a confidence rating and there is no coverage record — the areas the scan had not yet
  reached (`src/NVMeDriverPatcher.Core` services, the WPF app, the CLI, the EF/SQLite data layer)
  produced no findings simply because they were never examined, which is not the same as clean.
  Acceptance: a completed scan whose report is stamped `verified`, with this section replaced by
  its survivors.
  Complexity: S (unattended run)
