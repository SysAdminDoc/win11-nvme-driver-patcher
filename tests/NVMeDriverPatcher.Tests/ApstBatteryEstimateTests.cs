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
}
