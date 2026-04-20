# Compat-telemetry receiver (reference implementation)

Everything you need to stand up a privacy-respecting receiver for the opt-in compat reports
that `NVMeDriverPatcher.Cli telemetry --endpoint=<url>` POSTs.

- `cloudflare-worker.js` — the worker code. Validates schema, rehashes anonId with a SALT
  secret, stores each submission in Workers KV keyed by `YYYY-MM-DD/<keyHash>` with a
  1-year TTL.
- `wrangler.toml` — deploy config. Fill in `account_id` and the KV namespace ID.

## Deploy

```bash
npm i -g wrangler
wrangler login
wrangler kv:namespace create COMPAT
# Copy the returned ID into wrangler.toml

wrangler secret put SALT   # paste a long random string
wrangler publish
```

Your endpoint becomes `https://<worker-name>.<subdomain>.workers.dev/nvme/compat`.

## Scope

This is deliberately minimal:

- No auth — anyone can submit. Run it behind Cloudflare Access if you want to restrict.
- No aggregation — submissions land in KV. Export + analyze offline.
- No PII — the client never sends serials, machine names, drive letters, or user names.
  See `src/NVMeDriverPatcher/Services/CompatTelemetryService.cs` for the exact payload.

## Opting your users out

Ship `HKLM\SOFTWARE\Policies\SysAdminDoc\NVMeDriverPatcher\CompatTelemetryEnabled=0` via
the ADMX template in `packaging/admx/`. The GpoPolicyService overlay refuses to submit when
the policy disables telemetry, regardless of local config.
