// Reference Cloudflare Worker for the NVMe Driver Patcher opt-in telemetry endpoint.
// Deploy via `wrangler publish`, then point `--endpoint=https://<your-worker>.workers.dev/nvme/compat`
// at your worker URL when calling `NVMeDriverPatcher.Cli telemetry --endpoint=...`.
//
// Stores submissions in Workers KV. The client never sends identifying data — see the
// CompatTelemetryService in the repo for the exact payload shape.
//
// Privacy: no IP addresses, user agents, or identifying headers are stored. The SALT
// secret rotates the anonId hash so a leaked ID can't cross-reference submissions.
// Rate limiting uses ephemeral KV keys that expire automatically.

const RATE_LIMIT_MAX = 10;
const RATE_LIMIT_WINDOW_SECONDS = 60;

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    const corsOrigin = resolveAllowedOrigin(request, env);

    if (request.method === "OPTIONS") return corsResponse(204, corsOrigin);

    if (url.pathname === "/nvme/compat/summary" && request.method === "GET") {
      return handleSummary(env, corsOrigin);
    }

    if (url.pathname !== "/nvme/compat") return new Response("Not found", { status: 404 });
    if (request.method !== "POST") return new Response("Method not allowed", { status: 405 });

    const clientIp = request.headers.get("cf-connecting-ip") || "unknown";
    const limited = await checkRateLimit(env, clientIp);
    if (limited) return json({ error: "Rate limited. Try again later." }, 429, corsOrigin);

    let body;
    try {
      body = await request.json();
    } catch {
      return json({ error: "Body must be JSON" }, 400, corsOrigin);
    }

    const schemaVersion = Number(body?.schemaVersion ?? 0);
    if (schemaVersion < 1) return json({ error: "Unsupported schemaVersion" }, 400, corsOrigin);

    const anonId = String(body?.anonId ?? "");
    if (!/^[0-9a-f-]{32,36}$/i.test(anonId)) return json({ error: "anonId malformed" }, 400, corsOrigin);

    const raw = JSON.stringify(body);
    if (raw.length > 16_384) return json({ error: "Payload too large" }, 413, corsOrigin);

    const keyHash = await sha256Hex(anonId + (env.SALT ?? ""));
    const ts = new Date().toISOString();
    const record = { receivedAt: ts, payload: body };

    const dayKey = ts.slice(0, 10);
    await env.COMPAT.put(`${dayKey}/${keyHash}`, JSON.stringify(record), { expirationTtl: 60 * 60 * 24 * 365 });

    await incrementRateLimit(env, clientIp);

    return json({ accepted: true, ts }, 200, corsOrigin);
  }
};

// Browser CORS allowlist. `env.ALLOWED_ORIGINS` is a comma-separated list of exact origins
// (e.g. "https://sysadmindoc.github.io"). Only a request whose Origin header matches gets an
// Access-Control-Allow-Origin echo — so an unauthorized site's preflight (the app POSTs JSON,
// which always triggers a preflight) fails and the browser blocks the cross-origin submission.
// CLI / non-browser clients send no Origin and are unaffected (CORS is browser-enforced).
// Default is empty: no browser origin is allowed until you set ALLOWED_ORIGINS for a dashboard.
export function resolveAllowedOrigin(request, env) {
  const origin = request.headers.get("Origin");
  if (!origin) return null;
  const allow = (env.ALLOWED_ORIGINS ?? "")
    .split(",")
    .map(s => s.trim())
    .filter(Boolean);
  return allow.includes(origin) ? origin : null;
}

async function checkRateLimit(env, ip) {
  const key = `ratelimit:${await sha256Hex(ip)}`;
  const val = await env.COMPAT.get(key);
  if (val === null) return false;
  return Number(val) >= RATE_LIMIT_MAX;
}

async function incrementRateLimit(env, ip) {
  const key = `ratelimit:${await sha256Hex(ip)}`;
  const val = await env.COMPAT.get(key);
  const count = val === null ? 1 : Number(val) + 1;
  await env.COMPAT.put(key, String(count), { expirationTtl: RATE_LIMIT_WINDOW_SECONDS });
}

async function handleSummary(env, corsOrigin) {
  const list = await env.COMPAT.list({ limit: 1000 });
  const entries = list.keys.filter(k => !k.name.startsWith("ratelimit:"));

  const reports = [];
  for (const key of entries.slice(0, 200)) {
    try {
      const raw = await env.COMPAT.get(key.name, { type: "json" });
      if (raw?.payload) reports.push(raw.payload);
    } catch { /* skip corrupt entries */ }
  }

  return json({ ...summarizeReports(reports), generatedAt: new Date().toISOString() }, 200, corsOrigin);
}

// Pure aggregation over the stored client payloads. Reads the EXACT field shape the app
// emits (CompatTelemetryService.CompatReport): `controllers[]` of {model, firmware, migrated}
// plus a top-level `verification` outcome string. Exported so a contract test can feed it a
// real serialized payload and fail if either side renames a field again.
//
// `VERDICT_BUCKETS` mirrors VerificationOutcome (+ the client's "Unknown" null-fallback).
// Anything unrecognized lands in `Other` so an added enum value never silently vanishes.
const VERDICT_BUCKETS = [
  "Confirmed", "AwaitingRestart", "OverrideBlocked", "FlagsEnabledNotBound",
  "Reverted", "StalePending", "None", "Unknown", "Other"
];

export function summarizeReports(reports) {
  const controllers = {};
  const verdicts = Object.fromEntries(VERDICT_BUCKETS.map(k => [k, 0]));
  let total = 0;

  for (const p of reports) {
    if (!p || typeof p !== "object") continue;
    total++;

    const list = Array.isArray(p.controllers) ? p.controllers : [];
    if (list.length === 0) {
      controllers["unknown/unknown"] = (controllers["unknown/unknown"] || 0) + 1;
    } else {
      for (const c of list) {
        const model = String(c?.model ?? "").trim() || "unknown";
        const firmware = String(c?.firmware ?? "").trim() || "unknown";
        const ctrlKey = `${model}/${firmware}`;
        controllers[ctrlKey] = (controllers[ctrlKey] || 0) + 1;
      }
    }

    const v = String(p.verification ?? "Unknown");
    if (Object.prototype.hasOwnProperty.call(verdicts, v)) verdicts[v]++;
    else verdicts.Other++;
  }

  return {
    totalSubmissions: total,
    controllersReported: Object.keys(controllers).length,
    topControllers: Object.entries(controllers)
      .sort((a, b) => b[1] - a[1])
      .slice(0, 20)
      .map(([key, count]) => ({ controller: key, count })),
    verdicts
  };
}

async function sha256Hex(input) {
  const data = new TextEncoder().encode(input);
  const buf = await crypto.subtle.digest("SHA-256", data);
  return [...new Uint8Array(buf)].map(b => b.toString(16).padStart(2, "0")).join("");
}

// CORS headers scoped to a single allowed origin. The Access-Control-Allow-Origin header is
// emitted ONLY when corsOrigin is non-null (an allowlisted browser origin); omitting it is what
// makes an unauthorized origin's request fail in the browser. `Vary: Origin` keeps caches honest.
function corsHeaders(corsOrigin) {
  const headers = {
    "access-control-allow-methods": "GET, POST, OPTIONS",
    "access-control-allow-headers": "content-type",
    "vary": "Origin"
  };
  if (corsOrigin) headers["access-control-allow-origin"] = corsOrigin;
  return headers;
}

function json(obj, status, corsOrigin) {
  return new Response(JSON.stringify(obj), {
    status,
    headers: { "content-type": "application/json", ...corsHeaders(corsOrigin) }
  });
}

function corsResponse(status, corsOrigin) {
  return new Response(null, { status, headers: corsHeaders(corsOrigin) });
}
