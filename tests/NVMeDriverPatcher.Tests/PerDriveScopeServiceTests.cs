using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class PerDriveScopeServiceTests
{
    [Fact]
    public void ScopeDisabled_IncludesEverything()
    {
        var scope = new PerDriveScopeConfig { Enabled = false };
        var decisions = PerDriveScopeService.Decide(
            new[] { ("SN-1", "Samsung 990 PRO"), ("SN-2", "WD SN850X") },
            scope);
        Assert.All(decisions, d => Assert.True(d.Include));
    }

    [Fact]
    public void SerialMatch_ExcludesTheDrive()
    {
        var scope = new PerDriveScopeConfig { Enabled = true, ExcludedSerials = { "SN-1" } };
        var decisions = PerDriveScopeService.Decide(
            new[] { ("SN-1", "Samsung 990 PRO"), ("SN-2", "WD SN850X") },
            scope);
        Assert.False(decisions[0].Include);
        Assert.True(decisions[1].Include);
    }

    [Fact]
    public void ModelPattern_ExcludesMatchingDrive()
    {
        var scope = new PerDriveScopeConfig { Enabled = true, ExcludedModelPatterns = { "WD" } };
        var decisions = PerDriveScopeService.Decide(
            new[] { ("SN-1", "Samsung 990 PRO"), ("SN-2", "WD_BLACK SN850X") },
            scope);
        Assert.True(decisions[0].Include);
        Assert.False(decisions[1].Include);
    }

    [Fact]
    public void SerialTakesPrecedenceOverModelPattern()
    {
        // Drive that matches a model pattern (WD) AND has its serial explicitly excluded.
        // The decision reason should cite the serial exclusion (more specific).
        var scope = new PerDriveScopeConfig
        {
            Enabled = true,
            ExcludedSerials = { "SN-2" },
            ExcludedModelPatterns = { "WD" }
        };
        var decisions = PerDriveScopeService.Decide(new[] { ("SN-2", "WD SN850X") }, scope);
        Assert.False(decisions[0].Include);
        Assert.Contains("serial", decisions[0].Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SummarizeReports_CountsAndExclusions()
    {
        var scope = new PerDriveScopeConfig { Enabled = true, ExcludedSerials = { "SN-1" } };
        var decisions = PerDriveScopeService.Decide(
            new[] { ("SN-1", "A"), ("SN-2", "B"), ("SN-3", "C") },
            scope);
        var summary = PerDriveScopeService.Summarize(decisions);
        Assert.Contains("3 NVMe drive(s)", summary);
        Assert.Contains("1 excluded", summary);
        Assert.Contains("2 will swap", summary);
    }
}
