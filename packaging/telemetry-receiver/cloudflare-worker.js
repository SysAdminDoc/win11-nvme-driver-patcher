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

    if (request.method === "OPTIONS") return corsResponse(204);

    if (url.pathname === "/nvme/compat/summary" && request.method === "GET") {
      return handleSummary(env);
    }

    if (url.pathname !== "/nvme/compat") return new Response("Not found", { status: 404 });
    if (request.method !== "POST") return new Response("Method not allowed", { status: 405 });

    const clientIp = request.headers.get("cf-connecting-ip") || "unknown";
    const limited = await checkRateLimit(env, clientIp);
    if (limited) return json({ error: "Rate limited. Try again later." }, 429);

    let body;
    try {
      body = await request.json();
    } catch {
      return json({ error: "Body must be JSON" }, 400);
    }

    const schemaVersion = Number(body?.schemaVersion ?? 0);
    if (schemaVersion < 1) return json({ error: "Unsupported schemaVersion" }, 400);

    const anonId = String(body?.anonId ?? "");
    if (!/^[0-9a-f-]{32,36}$/i.test(anonId)) return json({ error: "anonId malformed" }, 400);

    const raw = JSON.stringify(body);
    if (raw.length > 16_384) return json({ error: "Payload too large" }, 413);

    const keyHash = await sha256Hex(anonId + (env.SALT ?? ""));
    const ts = new Date().toISOString();
    const record = { receivedAt: ts, payload: body };

    const dayKey = ts.slice(0, 10);
    await env.COMPAT.put(`${dayKey}/${keyHash}`, JSON.stringify(record), { expirationTtl: 60 * 60 * 24 * 365 });

    await incrementRateLimit(env, clientIp);

    return json({ accepted: true, ts }, 200);
  }
};

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

async function handleSummary(env) {
  const list = await env.COMPAT.list({ limit: 1000 });
  const entries = list.keys.filter(k => !k.name.startsWith("ratelimit:"));

  const controllers = {};
  const verdicts = { Confirmed: 0, AwaitingRestart: 0, OverrideBlocked: 0, FlagsEnabledNotBound: 0, Other: 0 };
  let total = 0;

  for (const key of entries.slice(0, 200)) {
    try {
      const raw = await env.COMPAT.get(key.name, { type: "json" });
      if (!raw?.payload) continue;
      total++;
      const p = raw.payload;
      const ctrlKey = `${p.controller ?? "unknown"}/${p.firmware ?? "unknown"}`;
      controllers[ctrlKey] = (controllers[ctrlKey] || 0) + 1;
      const v = p.verificationResult ?? "Other";
      if (v in verdicts) verdicts[v]++;
      else verdicts.Other++;
    } catch { /* skip corrupt entries */ }
  }

  return json({
    totalSubmissions: total,
    controllersReported: Object.keys(controllers).length,
    topControllers: Object.entries(controllers)
      .sort((a, b) => b[1] - a[1])
      .slice(0, 20)
      .map(([key, count]) => ({ controller: key, count })),
    verdicts,
    generatedAt: new Date().toISOString()
  }, 200);
}

async function sha256Hex(input) {
  const data = new TextEncoder().encode(input);
  const buf = await crypto.subtle.digest("SHA-256", data);
  return [...new Uint8Array(buf)].map(b => b.toString(16).padStart(2, "0")).join("");
}

function json(obj, status) {
  return new Response(JSON.stringify(obj), {
    status,
    headers: {
      "content-type": "application/json",
      "access-control-allow-origin": "*",
      "access-control-allow-methods": "GET, POST, OPTIONS",
      "access-control-allow-headers": "content-type"
    }
  });
}

function corsResponse(status) {
  return new Response(null, {
    status,
    headers: {
      "access-control-allow-origin": "*",
      "access-control-allow-methods": "GET, POST, OPTIONS",
      "access-control-allow-headers": "content-type"
    }
  });
}
