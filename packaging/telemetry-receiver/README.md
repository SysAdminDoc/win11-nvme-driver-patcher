# Compat-telemetry receiver (reference implementation)

Everything you need to stand up a privacy-respecting receiver for the opt-in compat reports
that `NVMeDriverPatcher.Cli telemetry --endpoint=<url>` POSTs.

- `cloudflare-worker.js` — the worker code. Validates every submission against the shipped schema,
  derives a keyed HMAC of `anonId` using the mandatory `SECRET`, and stores a **normalized
  projection** (never the request body) in Workers KV keyed by `YYYY-MM-DD/<hmac>` with a 1-year
  TTL. Rate limiting covers every route, and a summary aggregation endpoint serves a cached
  aggregate.
- `wrangler.toml` — deploy config. Fill in `account_id` and the KV namespace ID, and keep both
  rate-limit bindings.

## Deploy

```bash
npm i -g wrangler
wrangler login
wrangler kv:namespace create COMPAT
# Copy the returned ID into wrangler.toml

wrangler secret put SECRET   # paste a long random string -- REQUIRED, see Privacy below
wrangler deploy
```

## CORS allowlist (browser submissions)

The allowlist is a **request-blocking** control, not just a response-header decision. A request
carrying an `Origin` header that is not on the list is refused with `403` before any work happens
and before any KV write.

This distinction matters: omitting `Access-Control-Allow-Origin` only stops an unauthorized site
*reading* the response — the request still lands. Earlier versions of this worker did exactly that
and described it as protection, so any website could make its visitors submit telemetry records
with a simple `POST`. The worker additionally requires `Content-Type: application/json`, which is
not a CORS-safelisted media type, so a cross-origin request can never be a "simple" POST that skips
preflight; anything else is refused with `415`.

By default the worker allowlists **no** browser origin.

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

## Rate limiting

Both Cloudflare Workers Rate Limiting bindings in `wrangler.toml` are **required**. `limit()` checks
and consumes a token atomically, so there is no check-then-write window, and no IP-derived value is
persisted in your KV namespace.

| Binding | Route | Default budget |
|---------|-------|----------------|
| `RATE_LIMITER` | `POST /nvme/compat` | 10 / 60s per IP |
| `SUMMARY_RATE_LIMITER` | `GET /nvme/compat/summary` | 3 / 60s per IP |

The gate runs **before** route dispatch, so every route is covered. The summary gets its own,
tighter budget because it is the most expensive endpoint.

A deployment missing either binding **fails closed** (`500`) rather than degrading. The previous
best-effort KV counter has been removed entirely: it read the counter, then read and durably wrote
the whole request body, and only afterwards incremented — so concurrent requests all observed the
pre-increment value and the budget was bypassed by parallelism. It also persisted an IP-derived KV
key, which the privacy section below no longer has to qualify.

## Submission validation

Every field is checked against `packaging/schemas/telemetry_payload.schema.json` before anything is
stored, and only a normalized projection is written:

- unknown top-level and per-controller fields are **rejected**, not stored;
- `controllers` is capped at 16 entries;
- `model` (max 64 chars) and `firmware` (max 32) must match a narrow identifier allowlist;
- `verification`, `profile` and `watchdog` must be one of their enum values;
- `anonId` and `submittedAt` are **dropped** from what is persisted.

This is what stops an anonymous client dictating the content and cardinality of the public summary,
which republishes these strings. The body size is gated on `Content-Length` and again through a
capped reader **before** parsing, so an oversized payload is rejected without being deserialized.

## Summary pagination and caching

`GET /nvme/compat/summary` paginates the KV keyspace by following list cursors, so a dataset larger
than one 1000-key list page is not silently dropped. It stops after `MAX_SUMMARY_LIST_PAGES` (50)
pages, reads up to `MAX_SUMMARY_RECORDS` (5000) stored records, and reports `scannedKeys`,
`summarizedRecords` and a `truncated` flag so any cap is explicit rather than silent. The computed
aggregate is cached for 5 minutes, so N concurrent readers cause one namespace scan rather than N.
The cursor-follow, the page ceiling and the cache are all tested.

## Submission payload shape

`POST /nvme/compat` receives exactly what `CompatTelemetryService.CompatReport` serializes.
The summary endpoint reads `controllers[]` and `verification` from this shape — if you fork the
worker, keep those field names in sync with the client (a contract test pins them):

```json
{
  "schemaVersion": 1,
  "submittedAt": "2026-06-14T12:00:00.0000000Z",
  "anonId": "5f9c1e2a-3b4d-4c5e-8f90-1a2b3c4d5e6f",
  "appVersion": "5.1.0",
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

- **No PII** — the client never sends serials, machine names, drive letters, or user names.
  See `src/NVMeDriverPatcher.Core/Services/CompatTelemetryService.cs` for the exact payload.
- **No IP-derived value is persisted at all.** Throttling goes through the rate-limiting bindings,
  which keep their counters outside your KV namespace. Earlier versions stored an **unkeyed,
  unsalted** SHA-256 of the submitter's IP as a KV key; the IPv4 space is small enough to enumerate
  exhaustively, so that was reversible and the "no IP addresses are stored" claim did not hold.
- **The raw `anonId` is never stored.** It is turned into a keyed HMAC over the per-deployment
  `SECRET`, and only the digest becomes a KV key. Earlier versions wrote the raw `anonId` into the
  record value next to its own hash, which nullified the design for the record's whole lifetime.
- **`SECRET` is mandatory.** It previously defaulted to the empty string, so a deployment that
  forgot the secret hashed with an empty key and failed open. The worker now returns `500` instead
  of serving. Rotating `SECRET` makes existing records unreachable from any future submission by
  the same client — a privacy property, not a bug.

## Opting your users out

Ship `HKLM\SOFTWARE\Policies\SysAdminDoc\NVMeDriverPatcher\CompatTelemetryEnabled=0` via
the ADMX template in `packaging/admx/`. The GpoPolicyService overlay refuses to submit when
the policy disables telemetry, regardless of local config.
