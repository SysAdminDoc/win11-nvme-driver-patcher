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

Your endpoints:

| Method | Path | Purpose |
|--------|------|---------|
| POST | `/nvme/compat` | Submit a compat report (the CLI calls this) |
| GET | `/nvme/compat/summary` | Public aggregation: top controllers, verdict counts |
| OPTIONS | `/nvme/compat` | CORS preflight |

## Rate Limiting

IP-based rate limiting is built in: each IP gets 10 submissions per 60-second window.
Rate-limit state is stored as ephemeral KV keys (`ratelimit:<hash>`) that auto-expire.
IPs are hashed before storage — no raw addresses are persisted.

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
