using NVMeDriverPatcher.Models;

namespace NVMeDriverPatcher.Services;

public enum PatchActionDisposition
{
    /// <summary>A known, fresh rule describes a route that can bind — mutation is allowed.</summary>
    Allowed,

    /// <summary>No trusted route is known for this build — verify / monitor / rollback only.</summary>
    VerifyRollbackOnly
}

/// <summary>
/// The single source of truth for "may this build be mutated?". GUI command enablement, CLI
/// apply/fallback, dry-run messaging, and restart decisions all consume this instead of each
/// re-interpreting advisory preflight strings. A build only becomes mutable when a fresh, valid
/// rule names a route that actually binds (registry-override or vivetool-fallback); every other
/// state (none-known, official-optin, unknown/no-match, invalid/empty ruleset, stale rule,
/// unrecognized expectedPath) collapses to verify/rollback-only.
/// </summary>
public sealed record BuildActionPolicy(
    PatchActionDisposition Disposition,
    string Reason,
    string? RuleId,
    string ExpectedPath,
    bool RulesetValid,
    bool Stale)
{
    public bool MutationAllowed => Disposition == PatchActionDisposition.Allowed;
}

public static class BuildActionPolicyService
{
    public const int DefaultStaleAfterDays = 30;

    // expectedPath tokens that describe a route capable of binding the driver.
    private static readonly HashSet<string> MutatingPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "registry-override",
        "vivetool-fallback"
    };

    public static BuildActionPolicy Evaluate(
        WindowsBuildRuleset? ruleset,
        WindowsBuildRule? rule,
        DateTime? nowUtc = null,
        int staleAfterDays = DefaultStaleAfterDays)
    {
        if (ruleset is null || ruleset.Rules.Count == 0)
            return new BuildActionPolicy(
                PatchActionDisposition.VerifyRollbackOnly,
                "Build-rule data is missing or empty; the enablement path cannot be validated. Verify/rollback only.",
                null, string.Empty, RulesetValid: false, Stale: false);

        if (rule is null)
            return new BuildActionPolicy(
                PatchActionDisposition.VerifyRollbackOnly,
                "No rule matches this Windows build — behavior is unknown. Verify/rollback only.",
                null, string.Empty, RulesetValid: true, Stale: false);

        var stale = IsStale(rule.LastReviewed, nowUtc ?? DateTime.UtcNow, staleAfterDays);
        if (stale)
            return new BuildActionPolicy(
                PatchActionDisposition.VerifyRollbackOnly,
                $"Build rule [{rule.Id}] was last reviewed {DisplayDate(rule.LastReviewed)} (> {staleAfterDays} days) and is stale. Verify/rollback only until it is re-reviewed.",
                rule.Id, rule.ExpectedPath, RulesetValid: true, Stale: true);

        if (MutatingPaths.Contains(rule.ExpectedPath))
            return new BuildActionPolicy(
                PatchActionDisposition.Allowed,
                $"Build rule [{rule.Id}] expects a working '{rule.ExpectedPath}' route on this build.",
                rule.Id, rule.ExpectedPath, RulesetValid: true, Stale: false);

        var reason = rule.ExpectedPath switch
        {
            "none-known" => $"Build rule [{rule.Id}] reports no known enablement path binds on this build. Verify/rollback only.",
            "official-optin" => $"Build rule [{rule.Id}] reports native NVMe is an official opt-in on this build — use Windows' own path, not a registry mutation. Verify/rollback only.",
            _ => $"Build rule [{rule.Id}] has an unrecognized expected path '{rule.ExpectedPath}'. Verify/rollback only."
        };
        return new BuildActionPolicy(
            PatchActionDisposition.VerifyRollbackOnly, reason,
            rule.Id, rule.ExpectedPath, RulesetValid: true, Stale: false);
    }

    /// <summary>Evaluate against the live system's build rules.</summary>
    public static BuildActionPolicy EvaluateCurrent(string? workingDir = null, DateTime? nowUtc = null, int staleAfterDays = DefaultStaleAfterDays)
    {
        try
        {
            var ruleset = WindowsBuildRulesService.LoadRuleset(workingDir);
            var rule = WindowsBuildRulesService.MatchCurrent(workingDir);
            return Evaluate(ruleset, rule, nowUtc, staleAfterDays);
        }
        catch
        {
            return new BuildActionPolicy(
                PatchActionDisposition.VerifyRollbackOnly,
                "Build-rule evaluation failed; the enablement path cannot be validated. Verify/rollback only.",
                null, string.Empty, RulesetValid: false, Stale: false);
        }
    }

    private static bool IsStale(string date, DateTime nowUtc, int staleAfterDays)
    {
        // A rule with no/invalid review date is treated as stale — we will not mutate on the
        // strength of an undated rule.
        if (!DateTime.TryParse(date, out var parsed))
            return true;
        return (nowUtc.ToUniversalTime().Date - parsed.ToUniversalTime().Date).TotalDays > staleAfterDays;
    }

    private static string DisplayDate(string? date) =>
        string.IsNullOrWhiteSpace(date) ? "an unknown date" : date;
}
