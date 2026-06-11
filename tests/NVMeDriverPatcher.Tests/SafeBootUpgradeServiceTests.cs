using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class SafeBootUpgradeServiceTests
{
    [Theory]
    // No GUID entries at all → nothing to upgrade, regardless of service-name state.
    [InlineData(false, false, false, false, false)]
    [InlineData(false, false, true, true, false)]
    // GUID entries present + complete service-name pair → current, no upgrade.
    [InlineData(true, true, true, true, false)]
    // GUID entries present + missing or partial service-name pair → upgrade needed.
    [InlineData(true, true, false, false, true)]
    [InlineData(true, true, true, false, true)]
    [InlineData(true, true, false, true, true)]
    // Even a single surviving GUID entry (partial old patch) still warrants the upgrade.
    [InlineData(true, false, false, false, true)]
    [InlineData(false, true, false, false, true)]
    public void Classify_TruthTable(bool guidMin, bool guidNet, bool svcMin, bool svcNet, bool expectUpgrade)
    {
        var report = SafeBootUpgradeService.Classify(guidMin, guidNet, svcMin, svcNet);
        Assert.Equal(expectUpgrade, report.UpgradeNeeded);
        Assert.False(string.IsNullOrWhiteSpace(report.Summary));
    }

    [Fact]
    public void Classify_UpgradeNeeded_SummaryNamesTheServiceEntries()
    {
        var report = SafeBootUpgradeService.Classify(guidMin: true, guidNet: true, svcMin: false, svcNet: false);
        Assert.Contains(@"SafeBoot\Minimal\nvmedisk", report.Summary);
        Assert.Contains(@"SafeBoot\Network\nvmedisk", report.Summary);
        Assert.Contains("KB5079391", report.Summary);
    }
}
