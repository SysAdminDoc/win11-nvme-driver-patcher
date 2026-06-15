# Compat-telemetry receiver (reference implementation)

Everything you need to stand up a privacy-respecting receiver for the opt-in compat reports
that `NVMeDriverPatcher.Cli telemetry --endpoint=<url>` POSTs.

- `cloudflare-worker.js` — the worker code. Validates schema, rehashes anonId with a SALT
  secret, stores each submission in Workers KV keyed by `YYYY-MM-DD/<keyHash>` with a
  1-year TTL. Includes IP-based rate limiting (10 req/min per IP) and a summary aggregation
  endpoint.
- `wrangler.toml` — deploy config. Fill in `account_id` and the KV namespace ID.

## Deploy

```bash
npm i -g wrangler
wrangler login
wrangler kv:namespace create COMPAT
# Copy the returned ID into wrangler.toml

wrangler secret put SALT   # paste a long random string
wrangler deploy
```

## CORS allowlist (browser submissions)

By default the worker grants **no** browser origin — it returns an `Access-Control-Allow-Origin`
header only to origins you explicitly allowlist, so a random website cannot drive a cross-origin
`POST` of fake telemetry (the app sends `Content-Type: application/json`, which always triggers a
CORS preflight; an unauthorized origin's preflight gets no grant and the browser blocks the POST).

To allow a web dashboard, set a comma-separated `ALLOWED_ORIGINS` var with exact origins:

```bash
wrangler deploy --var ALLOWED_ORIGINS:"https://sysadmindoc.github.io"
# or add to wrangler.toml [vars]:  ALLOWED_ORIGINS = "https://sysadmindoc.github.io"
```

**CLI submissions are unaffected** — `NVMeDriverPatcher.Cli telemetry` is not a browser and sends
no `Origin` header, so CORS (which is browser-enforced) never applies to it. The client also refuses
to submit over plaintext HTTP to a remote endpoint; use an `https://` endpoint (loopback `http://`
is allowed only for local development).

Your endpoints:

| Method | Path | Purpose |
|--------|------|---------|
| POST | `/nvme/compat` | Submit a compat report (the CLI calls this) |
| GET | `/nvme/compat/summary` | Public aggregation: top controllers, verdict counts |
| OPTIONS | `/nvme/compat` | CORS preflight |

## Rate Limiting

IP-based rate limiting is built in: each IP gets 10 submissions per 60-second window.
IPs are hashed before any storage — no raw addresses are persisted.

Two backends, selected automatically:

- **Preferred — Cloudflare Workers Rate Limiting binding.** Bind it as `RATE_LIMITER` (see the
  commented `[[unsafe.bindings]]` block in `wrangler.toml`). The worker calls `RATE_LIMITER.limit()`,
  which checks and consumes a token atomically, so there is no check-then-write race.
- **Fallback — best-effort KV counter.** When no `RATE_LIMITER` binding is present, the worker uses
  ephemeral KV keys (`ratelimit:<hash>`) that auto-expire after the window. This is approximate:
  concurrent bursts from one IP can slip through between the read and the write. Acceptable for an
  opt-in receiver; switch to the binding for a hard guarantee. The decision (`rateLimitVerdict`) is
  unit-tested for the limit boundary and window reset.

## Summary pagination

`GET /nvme/compat/summary` paginates the full KV keyspace by following list cursors, so a dataset
larger than one 1000-key list page is no longer silently dropped. It reads up to
`MAX_SUMMARY_RECORDS` (5000) stored records and reports `scannedKeys`, `summarizedRecords`, and a
`truncated` flag so any cap is explicit rather than silent. The cursor-follow is unit-tested.

## Submission payload shape

`POST /nvme/compat` receives exactly what `CompatTelemetryService.CompatReport` serializes.
The summary endpoint reads `controllers[]` and `verification` from this shape — if you fork the
worker, keep those field names in sync with the client (a contract test pins them):

```json
{
  "schemaVersion": 1,
  "submittedAt": "2026-06-14T12:00:00.0000000Z",
  "anonId": "5f9c1e2a-3b4d-4c5e-8f90-1a2b3c4d5e6f",
  "appVersion": "5.0.0",
  "osBuild": "26100.4651",
  "cpu": "Intel64 Family 6 Model 154, GenuineIntel",
  "controllers": [
    { "model": "Samsung SSD 990 Pro 2TB", "firmware": "4B2QJXD7", "migrated": true }
  ],
  "profile": "Safe",
  "verification": "Confirmed",
  "watchdog": "Healthy",
  "watchdogEvents": 0,
  "reliabilityDelta": 0.5,
  "benchmarkDeltaPercent": 42.0
}
```

Controllers are counted **per drive** (`model/firmware`), so a two-NVMe machine contributes two
controller rows but one `totalSubmissions`. `verification` is bucketed per submission against the
`VerificationOutcome` set (`Confirmed`, `AwaitingRestart`, `OverrideBlocked`, `FlagsEnabledNotBound`,
`Reverted`, `StalePending`, `None`) plus `Unknown`; anything else falls into `Other`.

## Aggregation

`GET /nvme/compat/summary` returns a JSON summary:

```json
{
  "totalSubmissions": 142,
  "controllersReported": 23,
  "topControllers": [
    { "controller": "Samsung 990 Pro/3B2QJXM7", "count": 18 }
  ],
  "verdicts": {
    "Confirmed": 98,
    "AwaitingRestart": 12,
    "OverrideBlocked": 25,
    "FlagsEnabledNotBound": 5,
    "Reverted": 0,
    "StalePending": 0,
    "None": 0,
    "Unknown": 0,
    "Other": 2
  },
  "generatedAt": "2026-06-11T12:00:00.000Z"
}
```

## Privacy

- No PII — the client never sends serials, machine names, drive letters, or user names.
  See `src/NVMeDriverPatcher/Services/CompatTelemetryService.cs` for the exact payload.
- No IP storage — rate-limit keys are hashed and auto-expire; submission records contain
  no network-level identifiers.
- The SALT secret ensures anonId hashes cannot be reversed or cross-referenced across
  different deployments.

## Opting your users out

Ship `HKLM\SOFTWARE\Policies\SysAdminDoc\NVMeDriverPatcher\CompatTelemetryEnabled=0` via
the ADMX template in `packaging/admx/`. The GpoPolicyService overlay refuses to submit when
the policy disables telemetry, regardless of local config.
