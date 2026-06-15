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

    // --- Verify gate: success REQUIRES the service-name pair, not just "GUID present" ---

    [Fact]
    public void VerifyUpgrade_ServiceEntriesComplete_Succeeds()
    {
        var after = SafeBootUpgradeService.Classify(guidMin: false, guidNet: false, svcMin: true, svcNet: true);
        var (success, _) = SafeBootUpgradeService.VerifyUpgrade(after);
        Assert.True(success);
    }

    [Fact]
    public void VerifyUpgrade_SilentWriteNoOp_Fails()
    {
        // The regression: a CreateSubKey no-op leaves NO service entries (and no GUID entries on
        // a fresh machine). The old gate reported success here; VerifyUpgrade must fail.
        var after = SafeBootUpgradeService.Classify(guidMin: false, guidNet: false, svcMin: false, svcNet: false);
        var (success, message) = SafeBootUpgradeService.VerifyUpgrade(after);
        Assert.False(success);
        Assert.False(string.IsNullOrWhiteSpace(message));
    }

    [Fact]
    public void VerifyUpgrade_GuidPresentButServiceMissing_Fails()
    {
        var after = SafeBootUpgradeService.Classify(guidMin: true, guidNet: true, svcMin: false, svcNet: false);
        var (success, _) = SafeBootUpgradeService.VerifyUpgrade(after);
        Assert.False(success);
    }
}
