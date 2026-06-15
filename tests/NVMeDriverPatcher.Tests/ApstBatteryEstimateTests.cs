using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class ApstBatteryEstimateTests
{
    [Fact]
    public void EstimateBatteryImpact_EmptyReport_DoesNotThrow()
    {
        var report = new ApstInspectionReport();
        var est = ApstInspectorService.EstimateBatteryImpact(report);
        Assert.NotNull(est);
        Assert.False(string.IsNullOrWhiteSpace(est.Impact));
        Assert.False(string.IsNullOrWhiteSpace(est.Recommendation));
    }

    [Fact]
    public void EstimateBatteryImpact_ApstDisabled_ReportsNoAdditionalImpact()
    {
        var report = new ApstInspectionReport { ApstEnabled = false };
        var est = ApstInspectorService.EstimateBatteryImpact(report);
        Assert.False(est.ApstHonored);
        if (est.IsLaptop)
            Assert.Contains("already disabled", est.Impact, StringComparison.OrdinalIgnoreCase);
        else
            Assert.Contains("no battery impact", est.Impact, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EstimateBatteryImpact_WithPowerStates_CalculatesSavings()
    {
        var report = new ApstInspectionReport
        {
            ApstEnabled = true,
            NoLowPowerTransitions = false,
            States = new()
            {
                new ApstPowerState { PowerStateNumber = 0, MaxPowerWatts = 6.0, NonOperational = false },
                new ApstPowerState { PowerStateNumber = 3, MaxPowerWatts = 0.05, NonOperational = true, IdleTimeMicroseconds = 5000 },
                new ApstPowerState { PowerStateNumber = 4, MaxPowerWatts = 0.004, NonOperational = true, IdleTimeMicroseconds = 40000 },
            }
        };
        var est = ApstInspectorService.EstimateBatteryImpact(report);
        Assert.True(est.ApstHonored);
        Assert.Equal(6.0, est.ActivePowerWatts);
        Assert.Equal(0.004, est.LowestIdlePowerWatts);
        Assert.NotNull(est.EstimatedIdleSavingsWatts);
        Assert.True(est.EstimatedIdleSavingsWatts > 5.0);
    }

    [Fact]
    public void EstimateBatteryImpact_NoLowPowerTransitions_IsNotHonored()
    {
        var report = new ApstInspectionReport
        {
            ApstEnabled = true,
            NoLowPowerTransitions = true,
        };
        var est = ApstInspectorService.EstimateBatteryImpact(report);
        Assert.False(est.ApstHonored);
    }

    [Fact]
    public void Inspect_IncludesBatteryEstimate()
    {
        var report = ApstInspectorService.Inspect();
        Assert.NotNull(report.BatteryEstimate);
    }

    [Theory]
    [InlineData(false, false)] // desktop, no modern standby
    [InlineData(false, true)]  // desktop, modern standby (desktops aren't subject to this)
    [InlineData(true, false)]  // laptop, classic S3 sleep
    public void ModernStandbyApstWarning_OnlySurfacesForModernStandbyLaptops_Null(bool isLaptop, bool modernStandby)
    {
        Assert.Null(ApstInspectorService.ModernStandbyApstWarning(isLaptop, modernStandby));
    }

    [Fact]
    public void ModernStandbyApstWarning_ModernStandbyLaptop_WarnsWithMitigations()
    {
        var warn = ApstInspectorService.ModernStandbyApstWarning(isLaptop: true, modernStandby: true);
        Assert.NotNull(warn);
        Assert.Contains("Modern Standby", warn, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Fast Startup", warn, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PCIe", warn, StringComparison.OrdinalIgnoreCase);
    }
}
