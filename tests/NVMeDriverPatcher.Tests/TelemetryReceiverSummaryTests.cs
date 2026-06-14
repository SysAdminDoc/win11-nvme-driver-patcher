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
