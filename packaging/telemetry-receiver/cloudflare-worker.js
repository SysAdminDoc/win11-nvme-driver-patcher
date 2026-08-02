// Reference Cloudflare Worker for the NVMe Driver Patcher opt-in telemetry endpoint.
// Deploy via `wrangler publish`, then point `--endpoint=https://<your-worker>.workers.dev/nvme/compat`
// at your worker URL when calling `NVMeDriverPatcher.Cli telemetry --endpoint=...`.
//
// Stores submissions in Workers KV. The client never sends identifying data — see the
// CompatTelemetryService in the repo for the exact payload shape.
//
// Privacy, precisely (the README says the same; keep them in step):
//   * No IP-derived value is persisted at all. Throttling goes through Cloudflare's rate-limiting
//     binding, which keeps its own counters outside this Worker's KV namespace.
//   * The submitter's `anonId` is never stored. It is turned into a keyed HMAC using the
//     mandatory per-deployment SECRET and only the digest becomes a KV key, so a leaked ID cannot
//     be used to look up or cross-reference submissions. An unkeyed hash would not do this: the
//     input space is small enough to enumerate.
//   * The Worker refuses to serve rather than falling back to a weaker mode when SECRET or the
//     rate-limiting binding is missing. A misconfigured deployment fails closed, not open.
//   * Only a normalized, validated projection of the payload is written — never the request body.

const MAX_BODY_BYTES = 16_384;
// Upper bound on how many stored records one /summary call will READ. KV reads are billed and the
// summary is advisory, so we bound the work and report `truncated` instead of silently dropping
// records.
const MAX_SUMMARY_RECORDS = 5000;
// Hard ceiling on list pages per summary. Combined with the cache below, this stops the endpoint
// from being an unbounded namespace enumeration on every request.
const MAX_SUMMARY_LIST_PAGES = 50;
// The summary is an aggregate over a year of records; serving a slightly stale one is free, and it
// turns N concurrent readers into one namespace scan.
const SUMMARY_CACHE_TTL_SECONDS = 300;
const SUMMARY_CACHE_KEY = "cache:summary";

const RECORD_TTL_SECONDS = 60 * 60 * 24 * 365;

// Mirrors packaging/schemas/telemetry_payload.schema.json. A Worker cannot read the repo at
// runtime, so the constraints are inlined here and TelemetryReceiverSummaryTests asserts the two
// stay in agreement — a field added to the schema and not to this list is a test failure, not a
// silently accepted unknown field.
const ALLOWED_TOP_LEVEL_FIELDS = [
  "schemaVersion", "submittedAt", "anonId", "appVersion", "osBuild", "cpu",
  "controllers", "profile", "verification", "watchdog", "watchdogEvents",
  "reliabilityDelta", "benchmarkDeltaPercent"
];
const ALLOWED_CONTROLLER_FIELDS = ["model", "firmware", "migrated"];

// Enum domains, mirroring VerificationOutcome, WatchdogVerdict and PatchProfile plus the client's
// documented null-fallbacks ("Unknown" for verification, "Idle" for watchdog).
const VERIFICATION_VALUES = [
  "None", "Confirmed", "AwaitingRestart", "OverrideBlocked",
  "FlagsEnabledNotBound", "Reverted", "StalePending", "Unknown"
];
const WATCHDOG_VALUES = ["Unavailable", "Idle", "Healthy", "Warning", "Unstable", "Completed"];
const PROFILE_VALUES = ["Safe", "Full"];

const MAX_CONTROLLERS = 16;
const MAX_MODEL_LENGTH = 64;
const MAX_FIRMWARE_LENGTH = 32;
const MAX_SHORT_STRING = 96;
// Free text is never stored. Controller model/firmware are hardware identifier strings, so the
// allowlist is deliberately narrow: this is what stops a hostile `model` dictating the content of
// a public endpoint.
const IDENTIFIER_PATTERN = /^[A-Za-z0-9 ._\-+/(),:#]+$/;

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    const origin = request.headers.get("Origin");
    const corsOrigin = resolveAllowedOrigin(request, env);

    if (request.method === "OPTIONS") return corsResponse(204, corsOrigin);

    // Omitting Access-Control-Allow-Origin only stops an unauthorized site READING the response —
    // the request still lands. Refuse it outright so a cross-origin POST performs no work.
    // A client with no Origin header (the CLI) is unaffected; CORS is a browser concept.
    if (origin && !corsOrigin) {
      return json({ error: "Origin not allowed" }, 403, null);
    }

    const misconfigured = describeMisconfiguration(env);
    if (misconfigured) {
      // Fail closed. Serving with an absent secret or an absent limiter would silently downgrade
      // the two controls this endpoint's privacy claims rest on.
      return json({ error: `Receiver misconfigured: ${misconfigured}` }, 500, corsOrigin);
    }

    const isSummary = url.pathname === "/nvme/compat/summary" && request.method === "GET";
    const isSubmit = url.pathname === "/nvme/compat" && request.method === "POST";

    if (!isSummary && !isSubmit) {
      if (url.pathname !== "/nvme/compat" && url.pathname !== "/nvme/compat/summary") {
        return new Response("Not found", { status: 404 });
      }
      return new Response("Method not allowed", { status: 405 });
    }

    // Throttle BEFORE dispatch so every route is covered. The summary is the most expensive
    // endpoint (it enumerates the namespace), so it gets its own, tighter budget rather than
    // running with no throttle at all.
    const clientIp = request.headers.get("cf-connecting-ip") || "unknown";
    const limited = await checkRateLimit(env, clientIp, isSummary ? "summary" : "submit");
    if (limited) return json({ error: "Rate limited. Try again later." }, 429, corsOrigin);

    return isSummary ? handleSummary(env, corsOrigin) : handleSubmit(request, env, corsOrigin);
  }
};

// Returns a description of the first fatal configuration gap, or null when the deployment is
// complete. Exported so a test can pin the fail-closed contract.
export function describeMisconfiguration(env) {
  if (!env?.SECRET) {
    return "the SECRET binding is not set, so anonId cannot be keyed. Run `wrangler secret put SECRET`.";
  }
  if (!env?.RATE_LIMITER || typeof env.RATE_LIMITER.limit !== "function") {
    return "the RATE_LIMITER binding is missing. Uncomment it in wrangler.toml; the previous KV counter was a check-then-write race and persisted an IP-derived key.";
  }
  if (!env?.SUMMARY_RATE_LIMITER || typeof env.SUMMARY_RATE_LIMITER.limit !== "function") {
    return "the SUMMARY_RATE_LIMITER binding is missing. /nvme/compat/summary enumerates the namespace, so it needs its own tighter budget rather than the submit budget.";
  }
  return null;
}

async function handleSubmit(request, env, corsOrigin) {
  // Force a CORS preflight and reject anything else. application/json is not a CORS-safelisted
  // media type, so requiring it means a cross-origin request can never be a "simple" POST that
  // skips preflight — which is what previously let any website make its visitors submit records.
  const contentType = request.headers.get("content-type") || "";
  if (!/^application\/json\s*(;.*)?$/i.test(contentType)) {
    return json({ error: "Content-Type must be application/json" }, 415, corsOrigin);
  }

  // Size-gate BEFORE parsing. The old order deserialized an unbounded stream and only then
  // measured JSON.stringify(body).length — the parse cost was already paid, and the
  // re-serialization doubled it.
  const declaredLength = Number(request.headers.get("content-length") ?? NaN);
  if (Number.isFinite(declaredLength) && declaredLength > MAX_BODY_BYTES) {
    return json({ error: "Payload too large" }, 413, corsOrigin);
  }

  let text;
  try {
    text = await readCapped(request, MAX_BODY_BYTES);
  } catch (err) {
    if (err && err.code === "TOO_LARGE") return json({ error: "Payload too large" }, 413, corsOrigin);
    return json({ error: "Body could not be read" }, 400, corsOrigin);
  }

  let body;
  try {
    body = JSON.parse(text);
  } catch {
    return json({ error: "Body must be JSON" }, 400, corsOrigin);
  }

  const validation = validatePayload(body);
  if (!validation.ok) return json({ error: validation.error }, 400, corsOrigin);

  // Keyed HMAC over a mandatory per-deployment secret. An unkeyed digest of a value from a small
  // input space is reversible by enumeration, so it is not pseudonymisation.
  const keyHash = await hmacHex(env.SECRET, `anon:${validation.anonId}`);
  const ts = new Date().toISOString();

  // The raw anonId is deliberately absent from what is persisted — storing it beside its own
  // digest would nullify the keyed-hash design for the full record lifetime.
  const record = { receivedAt: ts, payload: validation.payload };

  const dayKey = ts.slice(0, 10);
  await env.COMPAT.put(`${dayKey}/${keyHash}`, JSON.stringify(record), { expirationTtl: RECORD_TTL_SECONDS });

  return json({ accepted: true, ts }, 200, corsOrigin);
}

// Reads at most `maxBytes` from the request, throwing { code: "TOO_LARGE" } as soon as the cap is
// passed. A content-length header is a claim, not a guarantee, so the stream is capped too.
// Exported for tests.
export async function readCapped(request, maxBytes) {
  const reader = request.body?.getReader?.();
  if (!reader) {
    const text = await request.text();
    if (byteLength(text) > maxBytes) throw { code: "TOO_LARGE" };
    return text;
  }

  const chunks = [];
  let total = 0;
  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    total += value.byteLength;
    if (total > maxBytes) {
      try { await reader.cancel(); } catch { /* already closed */ }
      throw { code: "TOO_LARGE" };
    }
    chunks.push(value);
  }

  const merged = new Uint8Array(total);
  let offset = 0;
  for (const chunk of chunks) {
    merged.set(chunk, offset);
    offset += chunk.byteLength;
  }
  return new TextDecoder().decode(merged);
}

function byteLength(text) {
  return new TextEncoder().encode(text).byteLength;
}

/// Validates against the shipped schema's constraints and returns a normalized projection.
/// Everything the summary later republishes has to come through here: an unknown top-level field,
/// an oversized controllers array, or a model string outside the identifier allowlist is rejected
/// rather than stored. Exported for tests.
export function validatePayload(body) {
  if (!body || typeof body !== "object" || Array.isArray(body)) {
    return { ok: false, error: "Body must be a JSON object" };
  }

  const unknown = Object.keys(body).filter(k => !ALLOWED_TOP_LEVEL_FIELDS.includes(k));
  if (unknown.length > 0) {
    return { ok: false, error: `Unknown field(s): ${unknown.slice(0, 5).join(", ")}` };
  }

  const schemaVersion = body.schemaVersion;
  if (!Number.isInteger(schemaVersion) || schemaVersion < 1) {
    return { ok: false, error: "Unsupported schemaVersion" };
  }

  const anonId = typeof body.anonId === "string" ? body.anonId : "";
  if (!/^[0-9a-f-]{32,36}$/i.test(anonId)) return { ok: false, error: "anonId malformed" };

  if (!Array.isArray(body.controllers)) return { ok: false, error: "controllers must be an array" };
  if (body.controllers.length > MAX_CONTROLLERS) {
    return { ok: false, error: `controllers must contain at most ${MAX_CONTROLLERS} entries` };
  }

  const controllers = [];
  for (const entry of body.controllers) {
    if (!entry || typeof entry !== "object" || Array.isArray(entry)) {
      return { ok: false, error: "controllers entries must be objects" };
    }
    const unknownController = Object.keys(entry).filter(k => !ALLOWED_CONTROLLER_FIELDS.includes(k));
    if (unknownController.length > 0) {
      return { ok: false, error: `Unknown controller field(s): ${unknownController.slice(0, 5).join(", ")}` };
    }
    const model = checkIdentifier(entry.model, MAX_MODEL_LENGTH, "model");
    if (model.error) return { ok: false, error: model.error };
    const firmware = checkIdentifier(entry.firmware, MAX_FIRMWARE_LENGTH, "firmware");
    if (firmware.error) return { ok: false, error: firmware.error };
    if (typeof entry.migrated !== "boolean") return { ok: false, error: "controllers[].migrated must be a boolean" };

    controllers.push({ model: model.value, firmware: firmware.value, migrated: entry.migrated });
  }

  const verification = checkEnum(body.verification, VERIFICATION_VALUES, "verification", "Unknown");
  if (verification.error) return { ok: false, error: verification.error };
  const watchdog = checkEnum(body.watchdog, WATCHDOG_VALUES, "watchdog", "Idle");
  if (watchdog.error) return { ok: false, error: watchdog.error };
  const profile = checkEnum(body.profile, PROFILE_VALUES, "profile", "Safe");
  if (profile.error) return { ok: false, error: profile.error };

  const appVersion = checkIdentifier(body.appVersion ?? "", MAX_SHORT_STRING, "appVersion", true);
  if (appVersion.error) return { ok: false, error: appVersion.error };
  const osBuild = checkIdentifier(body.osBuild ?? "", MAX_SHORT_STRING, "osBuild", true);
  if (osBuild.error) return { ok: false, error: osBuild.error };
  const cpu = checkIdentifier(body.cpu ?? "", MAX_SHORT_STRING, "cpu", true);
  if (cpu.error) return { ok: false, error: cpu.error };

  const watchdogEvents = body.watchdogEvents ?? 0;
  if (!Number.isInteger(watchdogEvents) || watchdogEvents < 0) {
    return { ok: false, error: "watchdogEvents must be a non-negative integer" };
  }

  const reliabilityDelta = checkNullableNumber(body.reliabilityDelta, "reliabilityDelta");
  if (reliabilityDelta.error) return { ok: false, error: reliabilityDelta.error };
  const benchmarkDeltaPercent = checkNullableNumber(body.benchmarkDeltaPercent, "benchmarkDeltaPercent");
  if (benchmarkDeltaPercent.error) return { ok: false, error: benchmarkDeltaPercent.error };

  return {
    ok: true,
    anonId,
    // The projection, not the body: submittedAt and anonId are dropped, every string is bounded
    // and allowlisted, and no field the client did not declare can survive.
    payload: {
      schemaVersion,
      appVersion: appVersion.value,
      osBuild: osBuild.value,
      cpu: cpu.value,
      controllers,
      profile: profile.value,
      verification: verification.value,
      watchdog: watchdog.value,
      watchdogEvents,
      reliabilityDelta: reliabilityDelta.value,
      benchmarkDeltaPercent: benchmarkDeltaPercent.value
    }
  };
}

function checkIdentifier(value, maxLength, field, allowEmpty = false) {
  if (value === undefined || value === null) {
    return allowEmpty ? { value: "" } : { error: `${field} is required` };
  }
  if (typeof value !== "string") return { error: `${field} must be a string` };
  const trimmed = value.trim();
  if (trimmed.length === 0) {
    return allowEmpty ? { value: "" } : { error: `${field} must not be empty` };
  }
  if (trimmed.length > maxLength) return { error: `${field} exceeds ${maxLength} characters` };
  if (!IDENTIFIER_PATTERN.test(trimmed)) return { error: `${field} contains disallowed characters` };
  return { value: trimmed };
}

function checkEnum(value, allowed, field, fallback) {
  if (value === undefined || value === null || value === "") return { value: fallback };
  if (typeof value !== "string") return { error: `${field} must be a string` };
  if (!allowed.includes(value)) return { error: `${field} must be one of: ${allowed.join(", ")}` };
  return { value };
}

function checkNullableNumber(value, field) {
  if (value === undefined || value === null) return { value: null };
  if (typeof value !== "number" || !Number.isFinite(value)) {
    return { error: `${field} must be a finite number or null` };
  }
  return { value };
}

// Browser CORS allowlist. `env.ALLOWED_ORIGINS` is a comma-separated list of exact origins
// (e.g. "https://sysadmindoc.github.io"). A request carrying an Origin header that is not on the
// list is rejected with 403 in fetch() — the allowlist is a request-blocking control, not just a
// response-header decision. CLI / non-browser clients send no Origin and are unaffected.
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

// Cloudflare's Workers Rate Limiting binding: limit() checks AND consumes atomically, so there is
// no check-then-write window for concurrent requests to slip through, and no IP-derived value is
// persisted in our KV namespace. The binding is required (see describeMisconfiguration) precisely
// because the KV counter it replaced was racy and did persist one.
async function checkRateLimit(env, ip, bucket) {
  const limiter = bucket === "summary" ? env.SUMMARY_RATE_LIMITER : env.RATE_LIMITER;
  const { success } = await limiter.limit({ key: `${bucket}:${await sha256Hex(ip)}` });
  return !success;
}

// Enumerate stored KV keys by following list cursors, so a growing dataset is never silently
// truncated at the first 1000-key page. Skips keys with `excludePrefix`. Stops at `maxPages` and
// reports it, rather than scanning an unbounded namespace on every request. Exported for tests.
export async function paginateKeys(kv, excludePrefix, maxPages = MAX_SUMMARY_LIST_PAGES) {
  const names = [];
  let cursor;
  let complete = false;
  let pages = 0;
  for (; pages < maxPages; pages++) {
    const res = await kv.list(cursor ? { cursor } : {});
    for (const k of res.keys) {
      if (!excludePrefix || !k.name.startsWith(excludePrefix)) names.push(k.name);
    }
    if (res.list_complete || !res.cursor) { complete = true; break; }
    cursor = res.cursor;
  }
  return { names, complete };
}

async function handleSummary(env, corsOrigin) {
  // One namespace scan serves every reader inside the TTL. Without this, the most expensive route
  // recomputed the whole aggregate per request.
  const cached = await env.COMPAT.get(SUMMARY_CACHE_KEY);
  if (cached) {
    return new Response(cached, {
      status: 200,
      headers: {
        "content-type": "application/json",
        "cache-control": `public, max-age=${SUMMARY_CACHE_TTL_SECONDS}`,
        ...corsHeaders(corsOrigin)
      }
    });
  }

  const { names, complete } = await paginateKeys(env.COMPAT, "cache:", MAX_SUMMARY_LIST_PAGES);
  const scannedKeys = names.length;
  const cap = Math.min(scannedKeys, MAX_SUMMARY_RECORDS);

  const reports = [];
  for (let i = 0; i < cap; i++) {
    try {
      const raw = await env.COMPAT.get(names[i], { type: "json" });
      if (raw?.payload) reports.push(raw.payload);
    } catch { /* skip corrupt entries */ }
  }

  const payload = JSON.stringify({
    ...summarizeReports(reports),
    scannedKeys,
    summarizedRecords: reports.length,
    truncated: scannedKeys > MAX_SUMMARY_RECORDS || !complete,
    generatedAt: new Date().toISOString()
  });

  await env.COMPAT.put(SUMMARY_CACHE_KEY, payload, { expirationTtl: SUMMARY_CACHE_TTL_SECONDS });

  return new Response(payload, {
    status: 200,
    headers: {
      "content-type": "application/json",
      "cache-control": `public, max-age=${SUMMARY_CACHE_TTL_SECONDS}`,
      ...corsHeaders(corsOrigin)
    }
  });
}

// Pure aggregation over the stored, normalized projections. Reads the EXACT field shape the app
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
  return toHex(buf);
}

// Keyed digest. Unlike a bare SHA-256, an attacker holding a candidate anonId cannot compute the
// stored key without the deployment secret.
export async function hmacHex(secret, message) {
  const key = await crypto.subtle.importKey(
    "raw",
    new TextEncoder().encode(secret),
    { name: "HMAC", hash: "SHA-256" },
    false,
    ["sign"]
  );
  const sig = await crypto.subtle.sign("HMAC", key, new TextEncoder().encode(message));
  return toHex(sig);
}

function toHex(buffer) {
  return [...new Uint8Array(buffer)].map(b => b.toString(16).padStart(2, "0")).join("");
}

// CORS headers scoped to a single allowed origin. `Vary: Origin` keeps caches honest. Note that
// omitting Access-Control-Allow-Origin is NOT what blocks an unauthorized origin — fetch() rejects
// those requests with 403 before any work happens. This only shapes the response.
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
