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
    public void ViVeToolTopic_DisclosesDormancyAndPrimaryNativeRoute()
    {
        var text = DocsService.Render("vivetool");

        Assert.Contains("Native FeatureStore", text);
        Assert.Contains("is primary", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2025-03-10", text);
        Assert.Contains("dormant", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secondary cross-check", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RecoveryTopic_DocumentsRegistryOwnershipRecovery()
    {
        var text = DocsService.Render("recovery");

        Assert.Contains("takeown", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reg delete", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SYSTEM.nvme-backup", text, StringComparison.Ordinal);
        Assert.Contains("FeatureManagement\\Overrides", text, StringComparison.Ordinal);
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
        Assert.Contains("26H2 (26300.x)", text);
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
        Assert.Contains("| 26H2 and newer | 26300+ |", readme);
        Assert.Contains("Feature flags page", readme);
        Assert.Contains("windows_build_rules.json", readme);
    }

    [Fact]
    public void ReadmeManualSafeBootRemoval_ClearsOnlyTheDefaultValue()
    {
        // Windows ships the SafeBoot storage-disk keys itself on current builds (issue #13), so a
        // manual step that deletes the whole key strips the OS's own Safe Mode registration.
        // Every documented SafeBoot delete has to match the Recovery Kit's /ve form.
        var readme = File.ReadAllText(Path.Combine(RepoRoot(), "README.md"));
        var safeBootDeletes = readme.Split('\n')
            .Where(line => line.Contains("reg delete", StringComparison.OrdinalIgnoreCase)
                        && line.Contains(@"\SafeBoot\", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.NotEmpty(safeBootDeletes);
        Assert.All(safeBootDeletes, line => Assert.Contains(" /ve /f", line));
    }

    [Fact]
    public void FullProfileDocs_WarnThatStandaloneFutureTripsDismScanHealth()
    {
        // Issue #19: on 24H2 26100.9550, override 156965516 alone made DISM /ScanHealth report
        // reverse-delta payloads as corrupt until it was removed. Full writes it only on request,
        // and the offline docs and README both have to say why before someone asks for it.
        var profiles = DocsService.Render("profiles");
        Assert.Contains("156965516 is a separate opt-in on top of Full", profiles);
        Assert.Contains("--standalone-future", profiles);
        Assert.Contains("DISM /ScanHealth", profiles);
        Assert.Contains("SFC stays clean", profiles);

        var readme = File.ReadAllText(Path.Combine(RepoRoot(), "README.md"));
        Assert.Contains("### DISM /ScanHealth reports component store corruption", readme);
        Assert.Contains("(#dism-scanhealth-reports-component-store-corruption)", readme);
    }

    private static string RepoRoot([CallerFilePath] string sourceFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));
}
