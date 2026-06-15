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
    public void PaginateKeys_FollowsCursorsAndExcludesRateLimitKeys()
    {
        // A mock KV that returns three list pages (forcing cursor follow) — the old single
        // list({limit:1000}) call would have stopped after page one and dropped page two/three.
        var harness =
            "import { pathToFileURL } from 'node:url';\n" +
            "const m = await import(pathToFileURL(process.argv[2]).href);\n" +
            "const pages = [\n" +
            "  { keys: [{name:'2026-01-01/a'},{name:'ratelimit:zzz'}], list_complete:false, cursor:'c1' },\n" +
            "  { keys: [{name:'2026-01-02/b'},{name:'2026-01-02/c'}], list_complete:false, cursor:'c2' },\n" +
            "  { keys: [{name:'2026-01-03/d'}], list_complete:true }\n" +
            "];\n" +
            "let i = 0;\n" +
            "const kv = { list: async () => pages[i++] };\n" +
            "const names = await m.paginateKeys(kv, 'ratelimit:');\n" +
            "process.stdout.write(JSON.stringify(names));\n";

        var output = RunHarness(harness);
        using var doc = JsonDocument.Parse(output);
        var names = doc.RootElement.EnumerateArray().Select(e => e.GetString()).ToArray();

        Assert.Equal(new[] { "2026-01-01/a", "2026-01-02/b", "2026-01-02/c", "2026-01-03/d" }, names);
        Assert.DoesNotContain("ratelimit:zzz", names);
    }

    [Fact]
    public void RateLimitVerdict_LimitBoundaryAndReset()
    {
        var harness =
            "import { pathToFileURL } from 'node:url';\n" +
            "const m = await import(pathToFileURL(process.argv[2]).href);\n" +
            "const out = {\n" +
            "  fresh: m.rateLimitVerdict(null, 10),\n" +     // window reset → not limited, next=1
            "  underLimit: m.rateLimitVerdict('9', 10),\n" + // 9 < 10 → allowed, next=10
            "  atLimit: m.rateLimitVerdict('10', 10),\n" +   // 10 >= 10 → limited
            "};\n" +
            "process.stdout.write(JSON.stringify(out));\n";

        var output = RunHarness(harness);
        using var doc = JsonDocument.Parse(output);
        var root = doc.RootElement;

        Assert.False(root.GetProperty("fresh").GetProperty("limited").GetBoolean());
        Assert.Equal(1, root.GetProperty("fresh").GetProperty("nextValue").GetInt32());
        Assert.False(root.GetProperty("underLimit").GetProperty("limited").GetBoolean());
        Assert.Equal(10, root.GetProperty("underLimit").GetProperty("nextValue").GetInt32());
        Assert.True(root.GetProperty("atLimit").GetProperty("limited").GetBoolean());
    }

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
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo("node")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);

        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // Mirrors the powershell hard-dependency in PackagingVersionScriptTests: node is
            // required to validate the JS worker. CI (windows-latest) and the dev box both ship it.
            throw new InvalidOperationException(
                "node is required to run the telemetry-receiver contract test but was not found on PATH.", ex);
        }

        var stdOut = process.StandardOutput.ReadToEnd();
        var stdErr = process.StandardError.ReadToEnd();
        process.WaitForExit(20000);
        return new ProcessResult(process.ExitCode, stdOut, stdErr);
    }

    private static string WorkerPath([CallerFilePath] string sourceFile = "")
    {
        var repoRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));
        return Path.Combine(repoRoot, "packaging", "telemetry-receiver", "cloudflare-worker.js");
    }

    private sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);
}
