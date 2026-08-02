using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace NVMeDriverPatcher.Tests;

// Exercises scripts/Validate-BuildRulesFreshness.ps1, the release gate that stops a build whose
// bundled windows_build_rules.json is stale or about to go stale. Every rule ships with the same
// review date, so the whole ruleset expires on one day and apply silently becomes verify/rollback-
// only on every build -- no code change, no release, no user action. All dates here are pinned so
// the tests do not themselves rot.
public sealed class BuildRulesFreshnessScriptTests
{
    private const string ReviewDate = "2026-08-02";

    private static string Ruleset(string lastReviewed, string updated = ReviewDate) => $$"""
        {
          "schemaVersion": 1,
          "updated": "{{updated}}",
          "rules": [
            {
              "id": "test-rule",
              "expectedPath": "vivetool-fallback",
              "sourceUrl": "https://example.invalid/evidence",
              "lastReviewed": "{{lastReviewed}}"
            }
          ]
        }
        """;

    [Fact]
    public void FreshRuleset_Passes()
    {
        var result = Run(Ruleset(ReviewDate), asOf: "2026-08-05");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("passed", result.StdOut, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AlreadyStaleRuleset_FailsAndSaysApplyIsBlocked()
    {
        // 31 days after review: past DefaultStaleAfterDays, so the shipped tool cannot apply at all.
        var result = Run(Ruleset(ReviewDate), asOf: "2026-09-02");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("already stale", result.StdOut, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("apply is blocked", result.StdOut, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RulesetAboutToGoStale_FailsBeforeItReachesUsers()
    {
        // 20 days after review: still usable today, but only 10 days of life left -- shipping it
        // would strand users mid-cycle. This is the case the gate exists for.
        var result = Run(Ruleset(ReviewDate), asOf: "2026-08-22");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("goes stale in", result.StdOut, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("re-verify", result.StdOut, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnparseableReviewDate_FailsBecauseThePolicyTreatsItAsPermanentlyStale()
    {
        var result = Run(Ruleset("July 2026"), asOf: "2026-08-05");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("permanently stale", result.StdOut, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FutureReviewDate_Fails()
    {
        var result = Run(Ruleset("2027-01-01", updated: "2027-01-01"), asOf: "2026-08-05");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("in the future", result.StdOut, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UpdatedOlderThanNewestReview_Fails()
    {
        var result = Run(Ruleset(ReviewDate, updated: "2026-07-14"), asOf: "2026-08-05");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("older than its newest rule review", result.StdOut, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RuleWithoutASource_FailsBecauseItsVerdictCannotBeReVerified()
    {
        var json = """
            {
              "schemaVersion": 1,
              "updated": "2026-08-02",
              "rules": [
                { "id": "sourceless", "expectedPath": "none-known", "lastReviewed": "2026-08-02" }
              ]
            }
            """;

        var result = Run(json, asOf: "2026-08-05");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("no sourceUrl", result.StdOut, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EmptyRuleset_Fails()
    {
        var result = Run("""{ "schemaVersion": 1, "updated": "2026-08-02", "rules": [] }""", asOf: "2026-08-05");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("no rules", result.StdOut, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ShippedRuleset_PassesTheGateOnItsOwnReviewDate()
    {
        var shipped = Path.Combine(RepoRoot(), "src", "NVMeDriverPatcher.Core", "windows_build_rules.json");
        var result = RunAgainst(shipped, asOf: ReviewDate);

        Assert.Equal(0, result.ExitCode);
    }

    private static ScriptResult Run(string rulesetJson, string asOf)
    {
        var path = Path.Combine(Path.GetTempPath(), $"nvme-rules-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, rulesetJson);
        try
        {
            return RunAgainst(path, asOf);
        }
        finally
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }

    private static ScriptResult RunAgainst(string rulesPath, string asOf)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        foreach (var arg in new[]
                 {
                     "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", ScriptPath(),
                     "-RulesPath", rulesPath, "-AsOf", asOf
                 })
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        process.Start();
        // Read asynchronously and bound the wait: a synchronous ReadToEnd would turn a script-level
        // hang into a wedged suite instead of one failing test.
        var stdOut = process.StandardOutput.ReadToEndAsync();
        var stdErr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw new TimeoutException("Validate-BuildRulesFreshness.ps1 did not exit within 30s.");
        }

        return new ScriptResult(process.ExitCode, stdOut.GetAwaiter().GetResult(), stdErr.GetAwaiter().GetResult());
    }

    private static string RepoRoot([CallerFilePath] string sourceFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));

    private static string ScriptPath() =>
        Path.Combine(RepoRoot(), "scripts", "Validate-BuildRulesFreshness.ps1");

    private sealed record ScriptResult(int ExitCode, string StdOut, string StdErr);
}
