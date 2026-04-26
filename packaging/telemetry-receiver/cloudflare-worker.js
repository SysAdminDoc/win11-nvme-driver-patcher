// Reference Cloudflare Worker for the NVMe Driver Patcher opt-in telemetry endpoint.
// Deploy via `wrangler publish`, then point `--endpoint=https://<your-worker>.workers.dev/nvme/compat`
// at your worker URL when calling `NVMeDriverPatcher.Cli telemetry --endpoint=...`.
//
// Stores submissions in Workers KV. The client never sends identifying data — see the
// CompatTelemetryService in the repo for the exact payload shape.

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    if (url.pathname !== "/nvme/compat") return new Response("Not found", { status: 404 });
    if (request.method !== "POST") return new Response("Method not allowed", { status: 405 });

    let body;
    try {
      body = await request.json();
    } catch {
      return json({ error: "Body must be JSON" }, 400);
    }

    // Minimal schema guard. We never trust client-supplied anonId — we rehash it so a
    // leaked ID from one submission can't be replayed to clobber another.
    const schemaVersion = Number(body?.schemaVersion ?? 0);
    if (schemaVersion < 1) return json({ error: "Unsupported schemaVersion" }, 400);

    const anonId = String(body?.anonId ?? "");
    if (!/^[0-9a-f-]{32,36}$/i.test(anonId)) return json({ error: "anonId malformed" }, 400);

    // Size cap — legitimate payloads are well under 8 KB.
    const raw = JSON.stringify(body);
    if (raw.length > 16_384) return json({ error: "Payload too large" }, 413);

    const keyHash = await sha256Hex(anonId + (env.SALT ?? ""));
    const ts = new Date().toISOString();
    const record = { receivedAt: ts, payload: body };

    // Store under a content-addressable + time-ordered key so duplicate submissions from
    // the same machine overwrite cleanly while still keeping a per-day history.
    const dayKey = ts.slice(0, 10);  // YYYY-MM-DD
    await env.COMPAT.put(`${dayKey}/${keyHash}`, JSON.stringify(record), { expirationTtl: 60 * 60 * 24 * 365 });

    return json({ accepted: true, ts }, 200);
  }
};

async function sha256Hex(input) {
  const data = new TextEncoder().encode(input);
  const buf = await crypto.subtle.digest("SHA-256", data);
  return [...new Uint8Array(buf)].map(b => b.toString(16).padStart(2, "0")).join("");
}

function json(obj, status) {
  return new Response(JSON.stringify(obj), {
    status,
    headers: { "content-type": "application/json" }
  });
}

/**
 * Encodes a string to Base64URL format.
 * Replaces '+' with '-', '/' with '_', and removes padding '='.
 * @param {string} input The string to encode.
 * @returns {string} The Base64URL encoded string.
 */
function base64urlEncode(input) {
  const base64 = btoa(input); // Standard Base64 encoding
  return base64.replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, ''); // Base64URL modifications
}

