using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class WindowsBuildRulesServiceTests
{
    [Fact]
    public void BundledRuleset_LoadsAndIsInternallyConsistent()
    {
        var rs = WindowsBuildRulesService.LoadRuleset();
        Assert.True(rs.Rules.Count >= 5, "bundled ruleset missing");
        foreach (var r in rs.Rules)
        {
            Assert.False(string.IsNullOrWhiteSpace(r.Id), "rule id missing");
            Assert.False(string.IsNullOrWhiteSpace(r.Summary), $"{r.Id}: summary missing");
            Assert.False(string.IsNullOrWhiteSpace(r.SourceUrl), $"{r.Id}: source missing");
            Assert.False(string.IsNullOrWhiteSpace(r.Confidence), $"{r.Id}: confidence missing");
            Assert.True(r.MinBuild <= r.MaxBuild, $"{r.Id}: build range inverted");
            Assert.True(r.MinUbr <= r.MaxUbr, $"{r.Id}: UBR range inverted");
            Assert.Contains(r.ExpectedPath, new[] { "registry-override", "vivetool-fallback", "none-known", "official-optin" });
        }
    }

    [Theory]
    // (build, ubr, isServer) → expected rule id from the bundled file.
    [InlineData(26100, 8655, true, "server-2025")]
    [InlineData(26200, 8655, false, "26200-bind-blocked")]
    [InlineData(26250, 1, false, "post-26200-trains-bind-blocked")]    // 26201-26299 band still routes to the block
    [InlineData(26300, 8155, false, "26300-feature-flags-page")]       // native Feature flags page boundary
    [InlineData(28020, 1, false, "26300-feature-flags-page")]          // newer trains (>=26300) have the page too
    [InlineData(26200, 8246, false, "25h2-vivetool-new-ids")]
    [InlineData(26100, 8106, false, "24h2-26100-8106-fallback")]
    [InlineData(26100, 8246, false, "24h2-client-unverified")]
    [InlineData(26100, 2000, false, "24h2-client-unverified")]
    [InlineData(22631, 4000, false, "pre-24h2")]
    public void Match_BundledRules_RouteKnownBuildsCorrectly(int build, int ubr, bool server, string expectedId)
    {
        var rs = WindowsBuildRulesService.LoadRuleset();
        var rule = WindowsBuildRulesService.Match(rs, build, ubr, server);
        Assert.NotNull(rule);
        Assert.Equal(expectedId, rule!.Id);
    }

    [Fact]
    public void Match_NoMatch_ReturnsNull_AndDescribeIsConservative()
    {
        var empty = new WindowsBuildRuleset();
        Assert.Null(WindowsBuildRulesService.Match(empty, 26100, 1, false));
        var text = WindowsBuildRulesService.Describe(null);
        Assert.Contains("unknown", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("works", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Match_FallbackSetNames_ResolveInTheCatalog()
    {
        // A rule pointing at a fallback set that doesn't exist in FallbackFeatureCatalog
        // would route users to nothing — pin the join.
        var rs = WindowsBuildRulesService.LoadRuleset();
        var known = Models.FallbackFeatureCatalog.All.Select(s => s.Name).ToHashSet();
        foreach (var r in rs.Rules.Where(r => !string.IsNullOrEmpty(r.FallbackSet)))
            Assert.Contains(r.FallbackSet!, known);
    }
}
