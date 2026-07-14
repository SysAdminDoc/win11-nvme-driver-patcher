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
    [InlineData(26200, 8737)] // issue #13 build — matches 26200-bind-blocked (none-known)
    [InlineData(26300, 8772)] // matches 26300-feature-flags-page (none-known)
    public void BundledRules_ClassifyBlockedClientBuilds_AsVerifyRollbackOnly(int build, int ubr)
    {
        var ruleset = WindowsBuildRulesService.LoadRuleset(AppContext.BaseDirectory);
        Assert.NotEmpty(ruleset.Rules);
        var rule = WindowsBuildRulesService.Match(ruleset, build, ubr, isServer: false);
        Assert.NotNull(rule);
        Assert.Equal("none-known", rule!.ExpectedPath);

        // Use a clock near the rules' review dates so this asserts the path classification, not staleness.
        var policy = BuildActionPolicyService.Evaluate(ruleset, rule, new DateTime(2026, 6, 20, 0, 0, 0, DateTimeKind.Utc));
        Assert.False(policy.MutationAllowed);
    }

    [Fact]
    public void BundledRules_24H2PreBlock_AllowsMutation_WhenFresh()
    {
        var ruleset = WindowsBuildRulesService.LoadRuleset(AppContext.BaseDirectory);
        var rule = WindowsBuildRulesService.Match(ruleset, 26100, 1000, isServer: false);
        Assert.NotNull(rule);
        Assert.Equal("registry-override", rule!.ExpectedPath);
        var policy = BuildActionPolicyService.Evaluate(ruleset, rule, new DateTime(2026, 6, 20, 0, 0, 0, DateTimeKind.Utc));
        Assert.True(policy.MutationAllowed);
    }
}
