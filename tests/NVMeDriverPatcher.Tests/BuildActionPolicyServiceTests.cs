using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class BuildActionPolicyServiceTests
{
    // A fixed "now" close to the bundled rules' review dates so freshness is deterministic.
    private static readonly DateTime Now = new(2026, 6, 20, 0, 0, 0, DateTimeKind.Utc);

    private static WindowsBuildRule Rule(string id, string expectedPath, string reviewed, bool? server = false,
        int minBuild = 0, int maxBuild = int.MaxValue, int minUbr = 0, int maxUbr = int.MaxValue) =>
        new()
        {
            Id = id,
            ExpectedPath = expectedPath,
            LastReviewed = reviewed,
            Server = server,
            MinBuild = minBuild,
            MaxBuild = maxBuild,
            MinUbr = minUbr,
            MaxUbr = maxUbr
        };

    private static WindowsBuildRuleset Ruleset(params WindowsBuildRule[] rules) =>
        new() { Rules = rules.ToList() };

    [Fact]
    public void RegistryOverride_FreshRule_AllowsMutation()
    {
        var rule = Rule("24h2-pre-block", "registry-override", "2026-06-10");
        var policy = BuildActionPolicyService.Evaluate(Ruleset(rule), rule, Now);
        Assert.True(policy.MutationAllowed);
    }

    [Fact]
    public void VivetoolFallback_FreshRule_AllowsMutation()
    {
        var rule = Rule("24h2-post-block", "vivetool-fallback", "2026-06-10");
        var policy = BuildActionPolicyService.Evaluate(Ruleset(rule), rule, Now);
        Assert.True(policy.MutationAllowed);
    }

    [Theory]
    [InlineData("none-known")]
    [InlineData("official-optin")]
    [InlineData("something-unexpected")]
    public void NonBindingPaths_AreVerifyRollbackOnly(string expectedPath)
    {
        var rule = Rule("r", expectedPath, "2026-06-10");
        var policy = BuildActionPolicyService.Evaluate(Ruleset(rule), rule, Now);
        Assert.False(policy.MutationAllowed);
        Assert.Equal(PatchActionDisposition.VerifyRollbackOnly, policy.Disposition);
    }

    [Fact]
    public void NoMatchingRule_IsVerifyRollbackOnly()
    {
        var policy = BuildActionPolicyService.Evaluate(Ruleset(Rule("r", "registry-override", "2026-06-10")), rule: null, Now);
        Assert.False(policy.MutationAllowed);
        Assert.True(policy.RulesetValid);
    }

    [Fact]
    public void EmptyOrNullRuleset_IsInvalidAndVerifyRollbackOnly()
    {
        var empty = BuildActionPolicyService.Evaluate(new WindowsBuildRuleset(), rule: null, Now);
        Assert.False(empty.MutationAllowed);
        Assert.False(empty.RulesetValid);

        var nullSet = BuildActionPolicyService.Evaluate(null, rule: null, Now);
        Assert.False(nullSet.MutationAllowed);
        Assert.False(nullSet.RulesetValid);
    }

    [Fact]
    public void StaleRule_EvenWithBindingPath_IsVerifyRollbackOnly()
    {
        var rule = Rule("old", "registry-override", "2026-01-01");
        var policy = BuildActionPolicyService.Evaluate(Ruleset(rule), rule, Now, staleAfterDays: 30);
        Assert.False(policy.MutationAllowed);
        Assert.True(policy.Stale);
    }

    [Fact]
    public void UndatedRule_IsTreatedAsStale()
    {
        var rule = Rule("undated", "registry-override", "");
        var policy = BuildActionPolicyService.Evaluate(Ruleset(rule), rule, Now);
        Assert.False(policy.MutationAllowed);
        Assert.True(policy.Stale);
    }

    // --- Integration against the real bundled windows_build_rules.json ---

    [Theory]
    [InlineData(26099, 999999, "pre-24h2", "none-known", false)]
    [InlineData(26100, 8105, "24h2-client-unverified", "none-known", false)]
    [InlineData(26100, 8106, "24h2-26100-8106-fallback", "vivetool-fallback", true)]
    [InlineData(26100, 8107, "24h2-client-unverified", "none-known", false)]
    [InlineData(26199, 999999, "24h2-client-unverified", "none-known", false)]
    [InlineData(26200, 0, "25h2-vivetool-new-ids", "vivetool-fallback", true)]
    [InlineData(26200, 8523, "25h2-vivetool-new-ids", "vivetool-fallback", true)]
    [InlineData(26200, 8524, "26200-bind-blocked", "none-known", false)]
    [InlineData(26200, 8737, "26200-bind-blocked", "none-known", false)]
    [InlineData(26201, 0, "post-26200-trains-bind-blocked", "none-known", false)]
    [InlineData(26300, 8772, "26300-feature-flags-page", "none-known", false)]
    public void BundledRules_EnforceEvidenceConservativeClientBoundaries(
        int build, int ubr, string expectedId, string expectedPath, bool mutationAllowed)
    {
        var ruleset = WindowsBuildRulesService.LoadRuleset(AppContext.BaseDirectory);
        Assert.NotEmpty(ruleset.Rules);
        var rule = WindowsBuildRulesService.Match(ruleset, build, ubr, isServer: false);
        Assert.NotNull(rule);
        Assert.Equal(expectedId, rule!.Id);
        Assert.Equal(expectedPath, rule.ExpectedPath);

        // Use a clock near the rules' review dates so this asserts the path classification, not staleness.
        var policy = BuildActionPolicyService.Evaluate(ruleset, rule, new DateTime(2026, 6, 20, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(mutationAllowed, policy.MutationAllowed);
    }
}
