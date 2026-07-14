using System.Runtime.CompilerServices;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class DocsServiceTests
{
    [Theory]
    [InlineData("overview")]
    [InlineData("profiles")]
    [InlineData("recovery")]
    [InlineData("watchdog")]
    [InlineData("bypassio")]
    [InlineData("vivetool")]
    [InlineData("buildrules")]
    [InlineData("firmware")]
    [InlineData("gpo")]
    [InlineData("portable")]
    [InlineData("telemetry")]
    [InlineData("featureflags")]
    [InlineData("uninstall")]
    public void EveryDocumentedTopic_HasContent(string topic)
    {
        var text = DocsService.Render(topic);
        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.Contains("# " + topic, text);
    }

    [Fact]
    public void UnknownTopic_FallsBackToIndex()
    {
        var text = DocsService.Render("nonexistent");
        Assert.Contains("Unknown topic", text);
        Assert.Contains("Available topics", text);
    }

    [Fact]
    public void EmptyTopic_RendersIndex()
    {
        var text = DocsService.Render("");
        Assert.Contains("Available topics", text);
    }

    [Fact]
    public void TopicNamesAreCaseInsensitive()
    {
        var upper = DocsService.Render("WATCHDOG");
        Assert.Contains("watchdog", upper, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WatchdogTopic_ExplainsStorport129CommandTimeout()
    {
        var text = DocsService.Render("watchdog");

        Assert.Contains("command timeout (Storport 129)", text);
        Assert.Contains("revert", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildRulesTopic_DistinguishesFallbackBlockedAndFeatureFlagsBuilds()
    {
        var text = DocsService.Render("buildrules");

        Assert.Contains("24H2 26100.8106", text);
        Assert.Contains("Other 24H2 26100/26101-26199", text);
        Assert.Contains("25H2 26200.0-26200.8523", text);
        Assert.Contains("25H2 26200.8524+", text);
        Assert.Contains("verify/monitor/rollback only", text);
        Assert.Contains("26300+", text);
        Assert.Contains("Feature flags", text);
        Assert.Contains("Pre-24H2 client builds", text);
    }

    [Fact]
    public void ReadmeCompatibilityMatrix_MatchesBundledBuildRuleBuckets()
    {
        var readme = File.ReadAllText(Path.Combine(RepoRoot(), "README.md"));

        Assert.DoesNotContain("| 25H2 | 26200+ | Full support", readme);
        Assert.DoesNotContain("| 24H2 | 26100 | Full support", readme);
        Assert.Contains("25H2 pre-26200.8524", readme);
        Assert.Contains("25H2 26200.8524+", readme);
        Assert.Contains("Verify / monitor / rollback only", readme);
        Assert.Contains("24H2 evidenced fallback", readme);
        Assert.Contains("Other 24H2 builds", readme);
        Assert.Contains("Pre-24H2 client", readme);
        Assert.Contains("26300+ Insider", readme);
        Assert.Contains("Feature flags page", readme);
        Assert.Contains("windows_build_rules.json", readme);
    }

    private static string RepoRoot([CallerFilePath] string sourceFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));
}
