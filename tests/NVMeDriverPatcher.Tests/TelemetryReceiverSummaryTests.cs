using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

/// <summary>
/// Contract test for the opt-in telemetry receiver. Serializes a REAL
/// <see cref="CompatReport"/> (the exact bytes the client POSTs) and feeds it through the
/// Cloudflare worker's <c>summarizeReports</c> function via node. If either side renames a
/// field — the client's <c>controllers[]</c>/<c>verification</c> JSON names or the worker's
/// reads of them — the summary collapses to <c>unknown/unknown</c> / <c>Other</c> and these
/// assertions fail. This pins the schema drift that previously made fleet summaries useless.
/// </summary>
public sealed class TelemetryReceiverSummaryTests
{
    [Fact]
    public void Summary_CountsRealClientPayload_ByControllerAndVerdict()
    {
        // Two identical Samsung drives reporting Confirmed, one WD drive OverrideBlocked.
        var reports = new[]
        {
            MakeReport("Confirmed", ("Samsung SSD 990 Pro 2TB", "4B2QJXD7")),
            MakeReport("Confirmed", ("Samsung SSD 990 Pro 2TB", "4B2QJXD7")),
            MakeReport("OverrideBlocked", ("WD Black SN850X 1TB", "620361WD")),
        };

        var summary = RunSummary(reports);

        Assert.Equal(3, summary.GetProperty("totalSubmissions").GetInt32());

        var controllers = summary.GetProperty("topControllers").EnumerateArray()
            .ToDictionary(c => c.GetProperty("controller").GetString()!, c => c.GetProperty("count").GetInt32());

        // Correct field alignment means real controller keys — NOT the unknown/unknown the
        // drifted worker produced for every record.
        Assert.False(controllers.ContainsKey("unknown/unknown"));
        Assert.Equal(2, controllers["Samsung SSD 990 Pro 2TB/4B2QJXD7"]);
        Assert.Equal(1, controllers["WD Black SN850X 1TB/620361WD"]);

        var verdicts = summary.GetProperty("verdicts");
        Assert.Equal(2, verdicts.GetProperty("Confirmed").GetInt32());
        Assert.Equal(1, verdicts.GetProperty("OverrideBlocked").GetInt32());
        // No verification should fall through to Other when the field name matches.
        Assert.Equal(0, verdicts.GetProperty("Other").GetInt32());
    }

    [Fact]
    public void Summary_BucketsEveryVerificationOutcome_NoneFallThroughToOther()
    {
        // Every real VerificationOutcome the client can emit must have a named bucket.
        var outcomes = Enum.GetNames<VerificationOutcome>();
        var reports = outcomes
            .Select(o => MakeReport(o, ("Generic NVMe", "1.0")))
            .ToArray();

        var summary = RunSummary(reports);
        var verdicts = summary.GetProperty("verdicts");

        Assert.Equal(0, verdicts.GetProperty("Other").GetInt32());
        foreach (var outcome in outcomes)
        {
            Assert.Equal(1, verdicts.GetProperty(outcome).GetInt32());
        }
    }

    [Theory]
    // Allowlisted origin echoes back; everything else (unauthorized site, no-Origin CLI,
    // empty allowlist) resolves to no CORS grant so the browser blocks the cross-origin POST.
    [InlineData("https://sysadmindoc.github.io", "https://sysadmindoc.github.io", "https://sysadmindoc.github.io")]
    [InlineData("https://evil.example", "https://sysadmindoc.github.io", "")]
    [InlineData("", "https://sysadmindoc.github.io", "")]
    [InlineData("https://sysadmindoc.github.io", "", "")]
    public void ResolveAllowedOrigin_OnlyEchoesAllowlistedOrigins(string requestOrigin, string allowList, string expected)
    {
        var harness =
            "import { pathToFileURL } from 'node:url';\n" +
            "const m = await import(pathToFileURL(process.argv[2]).href);\n" +
            "const reqOrigin = process.argv[3] || null;\n" +
            "const request = { headers: { get: (k) => (k === 'Origin' ? reqOrigin : null) } };\n" +
            "const env = { ALLOWED_ORIGINS: process.argv[4] };\n" +
            "const r = m.resolveAllowedOrigin(request, env);\n" +
            "process.stdout.write(r == null ? '' : String(r));\n";

        var tempDir = Path.Combine(Path.GetTempPath(), $"NVMeDriverPatcher.Cors.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var harnessPath = Path.Combine(tempDir, "cors.mjs");
            File.WriteAllText(harnessPath, harness);
            var result = RunNode(harnessPath, WorkerPath(), requestOrigin, allowList);
            Assert.True(result.ExitCode == 0, $"node exited {result.ExitCode}. stderr: {result.StdErr}");
            Assert.Equal(expected, result.StdOut);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void PaginateKeys_FollowsCursorsExcludesCacheKeysAndReportsAnIncompleteScan()
    {
        // A mock KV that returns three list pages (forcing cursor follow) — the old single
        // list({limit:1000}) call would have stopped after page one and dropped page two/three.
        var harness =
            "import { pathToFileURL } from 'node:url';\n" +
            "const m = await import(pathToFileURL(process.argv[2]).href);\n" +
            "const pages = [\n" +
            "  { keys: [{name:'2026-01-01/a'},{name:'cache:summary'}], list_complete:false, cursor:'c1' },\n" +
            "  { keys: [{name:'2026-01-02/b'},{name:'2026-01-02/c'}], list_complete:false, cursor:'c2' },\n" +
            "  { keys: [{name:'2026-01-03/d'}], list_complete:true }\n" +
            "];\n" +
            "const make = () => { let i = 0; return { list: async () => pages[i++] }; };\n" +
            "const full = await m.paginateKeys(make(), 'cache:');\n" +
            // A page ceiling below the page count must report the scan as incomplete rather than
            // silently returning a short list the summary would present as the whole dataset.
            "const capped = await m.paginateKeys(make(), 'cache:', 2);\n" +
            "process.stdout.write(JSON.stringify({ full, capped }));\n";

        var output = RunHarness(harness);
        using var doc = JsonDocument.Parse(output);

        var full = doc.RootElement.GetProperty("full");
        var names = full.GetProperty("names").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(new[] { "2026-01-01/a", "2026-01-02/b", "2026-01-02/c", "2026-01-03/d" }, names);
        Assert.DoesNotContain("cache:summary", names);
        Assert.True(full.GetProperty("complete").GetBoolean());

        var capped = doc.RootElement.GetProperty("capped");
        Assert.False(capped.GetProperty("complete").GetBoolean());
        Assert.Equal(3, capped.GetProperty("names").GetArrayLength());
    }

    [Fact]
    public void Misconfiguration_FailsClosedRatherThanDegrading()
    {
        // A stock deployment that forgot the secret used to hash anonId with "" and serve happily;
        // one without the rate-limit binding silently fell back to a racy KV counter that also
        // persisted an IP-derived key. Both must now refuse to serve.
        var harness =
            "import { pathToFileURL } from 'node:url';\n" +
            "const m = await import(pathToFileURL(process.argv[2]).href);\n" +
            "const limiter = { limit: async () => ({ success: true }) };\n" +
            "const out = {\n" +
            "  noSecret: m.describeMisconfiguration({ RATE_LIMITER: limiter, SUMMARY_RATE_LIMITER: limiter }),\n" +
            "  emptySecret: m.describeMisconfiguration({ SECRET: '', RATE_LIMITER: limiter, SUMMARY_RATE_LIMITER: limiter }),\n" +
            "  noLimiter: m.describeMisconfiguration({ SECRET: 's', SUMMARY_RATE_LIMITER: limiter }),\n" +
            "  noSummaryLimiter: m.describeMisconfiguration({ SECRET: 's', RATE_LIMITER: limiter }),\n" +
            "  complete: m.describeMisconfiguration({ SECRET: 's', RATE_LIMITER: limiter, SUMMARY_RATE_LIMITER: limiter })\n" +
            "};\n" +
            "process.stdout.write(JSON.stringify(out));\n";

        var output = RunHarness(harness);
        using var doc = JsonDocument.Parse(output);
        var root = doc.RootElement;

        Assert.Contains("SECRET", root.GetProperty("noSecret").GetString());
        Assert.Contains("SECRET", root.GetProperty("emptySecret").GetString());
        Assert.Contains("RATE_LIMITER", root.GetProperty("noLimiter").GetString());
        Assert.Contains("SUMMARY_RATE_LIMITER", root.GetProperty("noSummaryLimiter").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("complete").ValueKind);
    }

    [Fact]
    public void HmacHex_IsKeyedSoTheDigestCannotBeRecomputedWithoutTheSecret()
    {
        var harness =
            "import { pathToFileURL } from 'node:url';\n" +
            "import { webcrypto } from 'node:crypto';\n" +
            "globalThis.crypto ??= webcrypto;\n" +
            "const m = await import(pathToFileURL(process.argv[2]).href);\n" +
            "const id = 'anon:11111111-2222-3333-4444-555555555555';\n" +
            "process.stdout.write(JSON.stringify({\n" +
            "  a: await m.hmacHex('secret-one', id),\n" +
            "  aAgain: await m.hmacHex('secret-one', id),\n" +
            "  b: await m.hmacHex('secret-two', id)\n" +
            "}));\n";

        var output = RunHarness(harness);
        using var doc = JsonDocument.Parse(output);
        var root = doc.RootElement;

        var a = root.GetProperty("a").GetString()!;
        Assert.Equal(64, a.Length);
        Assert.Equal(a, root.GetProperty("aAgain").GetString());   // deterministic per deployment
        Assert.NotEqual(a, root.GetProperty("b").GetString());     // and useless across deployments
    }

    [Fact]
    public void ReadCapped_RejectsAnOversizedBodyWithoutParsingIt()
    {
        var harness =
            "import { pathToFileURL } from 'node:url';\n" +
            "const m = await import(pathToFileURL(process.argv[2]).href);\n" +
            "const enc = new TextEncoder();\n" +
            "const makeRequest = (bytes) => ({\n" +
            "  body: { getReader: () => { let sent = false; return {\n" +
            "    read: async () => sent ? { done: true } : (sent = true, { done: false, value: enc.encode(bytes) }),\n" +
            "    cancel: async () => {}\n" +
            "  }; } }\n" +
            "});\n" +
            "let tooLarge = false;\n" +
            "try { await m.readCapped(makeRequest('x'.repeat(64)), 16); } catch (e) { tooLarge = e.code === 'TOO_LARGE'; }\n" +
            "const small = await m.readCapped(makeRequest('hello'), 16);\n" +
            "process.stdout.write(JSON.stringify({ tooLarge, small }));\n";

        var output = RunHarness(harness);
        using var doc = JsonDocument.Parse(output);

        Assert.True(doc.RootElement.GetProperty("tooLarge").GetBoolean());
        Assert.Equal("hello", doc.RootElement.GetProperty("small").GetString());
    }

    [Fact]
    public void ValidatePayload_AcceptsARealClientReportAndStripsAnonIdFromWhatIsPersisted()
    {
        // The exact bytes the client POSTs must survive validation, or the receiver rejects every
        // genuine submission. And the persisted projection must not carry the raw anonId next to
        // its own digest — that would nullify the keyed-hash design for the record's whole life.
        var report = MakeReport("Confirmed", ("Samsung SSD 990 Pro 2TB", "4B2QJXD7"));
        report.Cpu = "Intel64 Family 6 Model 154 Stepping 3, GenuineIntel";

        var result = RunValidate(JsonSerializer.Serialize(report));

        Assert.True(result.GetProperty("ok").GetBoolean(),
            $"a real CompatReport was rejected: {(result.TryGetProperty("error", out var e) ? e.GetString() : "")}");

        var payload = result.GetProperty("payload");
        Assert.False(payload.TryGetProperty("anonId", out _), "the raw anonId is persisted alongside its digest.");
        Assert.False(payload.TryGetProperty("submittedAt", out _));
        Assert.Equal("Confirmed", payload.GetProperty("verification").GetString());
        Assert.Equal("Samsung SSD 990 Pro 2TB", payload.GetProperty("controllers")[0].GetProperty("model").GetString());
    }

    [Theory]
    // An unknown top-level field previously reached KV verbatim and then the public summary.
    [InlineData("""{"schemaVersion":1,"anonId":"11111111-2222-3333-4444-555555555555","controllers":[],"evil":"x"}""", "Unknown field")]
    // An uncapped controllers array let one client dictate the cardinality of a public endpoint.
    [InlineData("""{"schemaVersion":1,"anonId":"11111111-2222-3333-4444-555555555555","controllers":[{"model":"a","firmware":"b","migrated":true},{"model":"a","firmware":"b","migrated":true},{"model":"a","firmware":"b","migrated":true},{"model":"a","firmware":"b","migrated":true},{"model":"a","firmware":"b","migrated":true},{"model":"a","firmware":"b","migrated":true},{"model":"a","firmware":"b","migrated":true},{"model":"a","firmware":"b","migrated":true},{"model":"a","firmware":"b","migrated":true},{"model":"a","firmware":"b","migrated":true},{"model":"a","firmware":"b","migrated":true},{"model":"a","firmware":"b","migrated":true},{"model":"a","firmware":"b","migrated":true},{"model":"a","firmware":"b","migrated":true},{"model":"a","firmware":"b","migrated":true},{"model":"a","firmware":"b","migrated":true},{"model":"a","firmware":"b","migrated":true}]}""", "at most")]
    // A hostile model string was republished verbatim in the public compat summary.
    [InlineData("""{"schemaVersion":1,"anonId":"11111111-2222-3333-4444-555555555555","controllers":[{"model":"<script>alert(1)</script>","firmware":"b","migrated":true}]}""", "disallowed characters")]
    [InlineData("""{"schemaVersion":1,"anonId":"11111111-2222-3333-4444-555555555555","controllers":[{"model":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","firmware":"b","migrated":true}]}""", "exceeds")]
    // verification / profile / watchdog were free-form strings straight into the aggregate.
    [InlineData("""{"schemaVersion":1,"anonId":"11111111-2222-3333-4444-555555555555","controllers":[],"verification":"NotAnOutcome"}""", "verification must be one of")]
    [InlineData("""{"schemaVersion":1,"anonId":"11111111-2222-3333-4444-555555555555","controllers":[],"profile":"Root"}""", "profile must be one of")]
    [InlineData("""{"schemaVersion":1,"anonId":"11111111-2222-3333-4444-555555555555","controllers":[],"watchdog":"Pwned"}""", "watchdog must be one of")]
    [InlineData("""{"schemaVersion":1,"anonId":"nope","controllers":[]}""", "anonId malformed")]
    [InlineData("""{"schemaVersion":0,"anonId":"11111111-2222-3333-4444-555555555555","controllers":[]}""", "schemaVersion")]
    [InlineData("""{"schemaVersion":1,"anonId":"11111111-2222-3333-4444-555555555555","controllers":{}}""", "controllers must be an array")]
    public void ValidatePayload_RejectsWhatWouldReachThePublicSummary(string json, string expectedError)
    {
        var result = RunValidate(json);

        Assert.False(result.GetProperty("ok").GetBoolean(), $"payload was accepted: {json}");
        Assert.Contains(expectedError, result.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WorkerFieldAllowlists_MatchTheShippedSchema()
    {
        // The Worker cannot read the schema at runtime, so the two are kept in step here: a field
        // added to one and not the other is a test failure rather than a silently accepted unknown
        // field (or a rejected legitimate one).
        var schemaPath = Path.Combine(RepoRoot(), "packaging", "schemas", "telemetry_payload.schema.json");
        using var schema = JsonDocument.Parse(File.ReadAllText(schemaPath));
        var root = schema.RootElement;

        var schemaTopLevel = root.GetProperty("properties").EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray();
        var schemaController = root.GetProperty("properties").GetProperty("controllers")
            .GetProperty("items").GetProperty("properties").EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray();

        var harness =
            "import { pathToFileURL } from 'node:url';\n" +
            "const src = await import('node:fs').then(fs => fs.readFileSync(process.argv[2], 'utf8'));\n" +
            "const grab = (name) => JSON.parse(src.match(new RegExp(name + ' = (\\\\[[^\\\\]]*\\\\])'))[1].replace(/\\n/g, ' '));\n" +
            "process.stdout.write(JSON.stringify({\n" +
            "  top: grab('ALLOWED_TOP_LEVEL_FIELDS').sort(),\n" +
            "  controller: grab('ALLOWED_CONTROLLER_FIELDS').sort()\n" +
            "}));\n";

        var output = RunHarness(harness);
        using var doc = JsonDocument.Parse(output);

        var workerTopLevel = doc.RootElement.GetProperty("top").EnumerateArray().Select(e => e.GetString()).ToArray();
        var workerController = doc.RootElement.GetProperty("controller").EnumerateArray().Select(e => e.GetString()).ToArray();

        Assert.Equal(schemaTopLevel, workerTopLevel);
        Assert.Equal(schemaController, workerController);

        // The schema itself must stay closed, or "reject unknown fields" is only half enforced.
        Assert.False(root.GetProperty("additionalProperties").GetBoolean());
        Assert.False(root.GetProperty("properties").GetProperty("controllers")
            .GetProperty("items").GetProperty("additionalProperties").GetBoolean());
    }

    /// <summary>
    /// Drives the Worker's real <c>fetch</c> entry point against an in-memory KV and rate limiter,
    /// so the request-ordering guarantees are asserted end to end rather than per-helper. Returns
    /// the status of each scenario plus how many KV writes it caused.
    /// </summary>
    [Fact]
    public void Fetch_EnforcesOriginContentTypeSizeAndThrottlingBeforeAnyWrite()
    {
        var harness = """
            import { pathToFileURL } from 'node:url';
            import { webcrypto } from 'node:crypto';
            globalThis.crypto ??= webcrypto;
            const worker = (await import(pathToFileURL(process.argv[2]).href)).default;

            const valid = {
              schemaVersion: 1,
              anonId: '11111111-2222-3333-4444-555555555555',
              controllers: [{ model: 'Samsung SSD 990 Pro 2TB', firmware: '4B2QJXD7', migrated: true }],
              profile: 'Safe', verification: 'Confirmed', watchdog: 'Healthy'
            };

            function makeEnv({ secret = 'deployment-secret', submitBudget = 100, summaryBudget = 100 } = {}) {
              const store = new Map();
              const env = {
                SECRET: secret,
                ALLOWED_ORIGINS: 'https://sysadmindoc.github.io',
                writes: 0,
                COMPAT: {
                  put: async (k, v) => { env.writes++; store.set(k, v); },
                  get: async (k, opts) => {
                    const raw = store.get(k);
                    if (raw === undefined) return null;
                    return opts?.type === 'json' ? JSON.parse(raw) : raw;
                  },
                  list: async () => ({ keys: [...store.keys()].map(name => ({ name })), list_complete: true })
                },
                RATE_LIMITER: { limit: async () => ({ success: submitBudget-- > 0 }) },
                SUMMARY_RATE_LIMITER: { limit: async () => ({ success: summaryBudget-- > 0 }) }
              };
              return env;
            }

            function makeRequest(method, url, { origin, contentType = 'application/json', body } = {}) {
              const headers = new Map();
              if (origin) headers.set('origin', origin);
              if (contentType) headers.set('content-type', contentType);
              if (body !== undefined) headers.set('content-length', String(new TextEncoder().encode(body).byteLength));
              const bytes = body === undefined ? null : new TextEncoder().encode(body);
              return {
                method, url,
                headers: { get: (k) => headers.get(String(k).toLowerCase()) ?? null },
                body: bytes === null ? null : { getReader: () => { let sent = false; return {
                  read: async () => sent ? { done: true } : (sent = true, { done: false, value: bytes }),
                  cancel: async () => {}
                }; } },
                text: async () => body ?? ''
              };
            }

            const out = {};
            const submit = 'https://w.example/nvme/compat';
            const summary = 'https://w.example/nvme/compat/summary';

            // A cross-origin POST from a site that is not allowlisted must perform NO write.
            let env = makeEnv();
            let res = await worker.fetch(makeRequest('POST', submit, { origin: 'https://evil.example', body: JSON.stringify(valid) }), env);
            out.crossOrigin = { status: res.status, writes: env.writes };

            // A simple POST (text/plain skips preflight) must be refused before any work.
            env = makeEnv();
            res = await worker.fetch(makeRequest('POST', submit, { contentType: 'text/plain;charset=UTF-8', body: JSON.stringify(valid) }), env);
            out.simplePost = { status: res.status, writes: env.writes };

            // Oversized body: rejected on content-length, never parsed, never stored.
            env = makeEnv();
            const huge = JSON.stringify({ ...valid, cpu: 'A'.repeat(40000) });
            res = await worker.fetch(makeRequest('POST', submit, { body: huge }), env);
            out.oversized = { status: res.status, writes: env.writes };

            // A hostile model string is rejected at ingest and cannot reach the summary.
            env = makeEnv();
            const hostile = JSON.stringify({ ...valid, controllers: [{ model: '<script>x</script>', firmware: 'a', migrated: true }] });
            res = await worker.fetch(makeRequest('POST', submit, { body: hostile }), env);
            out.hostile = { status: res.status, writes: env.writes };

            // The CLI (no Origin) submitting a valid report succeeds and writes exactly once.
            env = makeEnv();
            res = await worker.fetch(makeRequest('POST', submit, { body: JSON.stringify(valid) }), env);
            out.accepted = { status: res.status, writes: env.writes };
            const storedKey = [...(await env.COMPAT.list()).keys].map(k => k.name).find(n => !n.startsWith('cache:'));
            const storedRecord = await env.COMPAT.get(storedKey);
            out.storedKeyContainsAnonId = storedKey.includes(valid.anonId);
            out.storedRecordContainsAnonId = storedRecord.includes(valid.anonId);

            // A misconfigured deployment (no secret) refuses rather than hashing with "".
            env = makeEnv({ secret: '' });
            res = await worker.fetch(makeRequest('POST', submit, { body: JSON.stringify(valid) }), env);
            out.noSecret = { status: res.status, writes: env.writes };

            // The summary is throttled -- it previously returned before the rate-limit gate ran.
            env = makeEnv({ summaryBudget: 1 });
            const first = await worker.fetch(makeRequest('GET', summary), env);
            const second = await worker.fetch(makeRequest('GET', summary), env);
            out.summaryFirst = first.status;
            out.summarySecond = second.status;

            // ...and cached, so N readers cause one namespace scan rather than N.
            env = makeEnv();
            let scans = 0;
            const innerList = env.COMPAT.list;
            env.COMPAT.list = async (...a) => { scans++; return innerList(...a); };
            await worker.fetch(makeRequest('GET', summary), env);
            await worker.fetch(makeRequest('GET', summary), env);
            out.summaryScans = scans;

            process.stdout.write(JSON.stringify(out));
            """;

        var output = RunHarness(harness);
        using var doc = JsonDocument.Parse(output);
        var root = doc.RootElement;

        static void AssertNoWrite(JsonElement scenario, int expectedStatus, string because)
        {
            Assert.Equal(expectedStatus, scenario.GetProperty("status").GetInt32());
            Assert.True(scenario.GetProperty("writes").GetInt32() == 0, because);
        }

        AssertNoWrite(root.GetProperty("crossOrigin"), 403, "a non-allowlisted origin must not land a KV write");
        AssertNoWrite(root.GetProperty("simplePost"), 415, "a preflight-skipping simple POST must be refused");
        AssertNoWrite(root.GetProperty("oversized"), 413, "an oversized body must be rejected before parsing");
        AssertNoWrite(root.GetProperty("hostile"), 400, "a hostile model string must not reach storage");
        AssertNoWrite(root.GetProperty("noSecret"), 500, "a salt-less deployment must fail closed");

        var accepted = root.GetProperty("accepted");
        Assert.Equal(200, accepted.GetProperty("status").GetInt32());
        Assert.Equal(1, accepted.GetProperty("writes").GetInt32());
        Assert.False(root.GetProperty("storedKeyContainsAnonId").GetBoolean());
        Assert.False(root.GetProperty("storedRecordContainsAnonId").GetBoolean());

        Assert.Equal(200, root.GetProperty("summaryFirst").GetInt32());
        Assert.Equal(429, root.GetProperty("summarySecond").GetInt32());
        Assert.Equal(1, root.GetProperty("summaryScans").GetInt32());
    }

    private static JsonElement RunValidate(string payloadJson)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"NVMeDriverPatcher.Validate.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var payloadPath = Path.Combine(tempDir, "payload.json");
            File.WriteAllText(payloadPath, payloadJson);

            var harnessPath = Path.Combine(tempDir, "harness.mjs");
            File.WriteAllText(harnessPath, """
                import { pathToFileURL } from 'node:url';
                import { readFileSync } from 'node:fs';
                const mod = await import(pathToFileURL(process.argv[2]).href);
                const body = JSON.parse(readFileSync(process.argv[3], 'utf8'));
                process.stdout.write(JSON.stringify(mod.validatePayload(body)));
                """);

            var result = RunNode(harnessPath, WorkerPath(), payloadPath);
            Assert.True(result.ExitCode == 0, $"node exited {result.ExitCode}. stderr: {result.StdErr}");

            using var doc = JsonDocument.Parse(result.StdOut);
            return doc.RootElement.Clone();
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static string RepoRoot([CallerFilePath] string sourceFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));

    // Runs an inline node ESM harness whose only extra arg is the worker path, returns stdout.
    private static string RunHarness(string harness)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"NVMeDriverPatcher.Worker.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var harnessPath = Path.Combine(tempDir, "harness.mjs");
            File.WriteAllText(harnessPath, harness);
            var result = RunNode(harnessPath, WorkerPath());
            Assert.True(result.ExitCode == 0, $"node exited {result.ExitCode}. stderr: {result.StdErr}");
            return result.StdOut;
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static CompatReport MakeReport(string verification, params (string model, string firmware)[] controllers)
    {
        var report = new CompatReport
        {
            AnonId = Guid.NewGuid().ToString(),
            OsBuild = "26100.4651",
            Cpu = "Intel64 Family 6 Model 154, GenuineIntel",
            Profile = "Safe",
            Verification = verification,
            Watchdog = "Healthy",
        };
        foreach (var (model, firmware) in controllers)
        {
            report.Controllers.Add(new CompatController { Model = model, Firmware = firmware, Migrated = true });
        }
        return report;
    }

    private static JsonElement RunSummary(IReadOnlyList<CompatReport> reports)
    {
        // Serialize via the real CompatReport type so its [JsonPropertyName] contract is exercised.
        var payloadJson = JsonSerializer.Serialize(reports);

        var tempDir = Path.Combine(Path.GetTempPath(), $"NVMeDriverPatcher.Telemetry.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var payloadPath = Path.Combine(tempDir, "payload.json");
            File.WriteAllText(payloadPath, payloadJson);

            var harnessPath = Path.Combine(tempDir, "harness.mjs");
            File.WriteAllText(harnessPath, """
                import { pathToFileURL } from 'node:url';
                import { readFileSync } from 'node:fs';
                const mod = await import(pathToFileURL(process.argv[2]).href);
                const reports = JSON.parse(readFileSync(process.argv[3], 'utf8'));
                process.stdout.write(JSON.stringify(mod.summarizeReports(reports)));
                """);

            var result = RunNode(harnessPath, WorkerPath(), payloadPath);
            Assert.True(result.ExitCode == 0, $"node exited {result.ExitCode}. stderr: {result.StdErr}");

            using var doc = JsonDocument.Parse(result.StdOut);
            return doc.RootElement.Clone();
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static ProcessResult RunNode(params string[] args)
    {
        var startInfo = new ProcessStartInfo("node")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        foreach (var arg in args) startInfo.ArgumentList.Add(arg);

        try
        {
            var result = TestProcessRunner.Run(startInfo, TimeSpan.FromSeconds(20));
            return new ProcessResult(result.ExitCode, result.StdOut, result.StdErr);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // Mirrors the powershell hard-dependency in PackagingVersionScriptTests: node is
            // required to validate the JS worker. CI (windows-latest) and the dev box both ship it.
            throw new InvalidOperationException(
                "node is required to run the telemetry-receiver contract test but was not found on PATH.", ex);
        }
    }

    private static string WorkerPath([CallerFilePath] string sourceFile = "")
    {
        var repoRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));
        return Path.Combine(repoRoot, "packaging", "telemetry-receiver", "cloudflare-worker.js");
    }

    private sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);
}
