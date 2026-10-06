using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class ApstBatteryEstimateTests
{
    // Balanced plan defaults from Microsoft's StorNVMe power management page (AC / DC).
    private static readonly NvmeIdleSettings BalancedAc = new(200, 15, 2000, 100);
    private static readonly NvmeIdleSettings BalancedDc = new(100, 50, 1000, 100);

    // A drive like the ones on the test laptop: two operational states, then non-operational
    // PS3 (15 ms round trip) and PS4 (50 ms round trip).
    private static ApstInspectionReport ReportWithPowerTable()
    {
        var report = new ApstInspectionReport();
        ApstInspectorService.ApplyIdentifyPowerStates(report,
        [
            new NvmePowerStateDescriptor { Index = 0, MaxPowerWatts = 6.0, EntryLatencyUs = 0, ExitLatencyUs = 0 },
            new NvmePowerStateDescriptor { Index = 1, MaxPowerWatts = 4.0, EntryLatencyUs = 0, ExitLatencyUs = 0 },
            new NvmePowerStateDescriptor { Index = 3, MaxPowerWatts = 0.05, EntryLatencyUs = 5000, ExitLatencyUs = 10000, NonOperational = true },
            new NvmePowerStateDescriptor { Index = 4, MaxPowerWatts = 0.004, EntryLatencyUs = 5000, ExitLatencyUs = 45000, NonOperational = true }
        ]);
        return report;
    }

    [Fact]
    public void EstimateBatteryImpact_EmptyReport_DoesNotThrow()
    {
        var report = new ApstInspectionReport();
        var est = ApstInspectorService.EstimateBatteryImpact(report);
        Assert.NotNull(est);
        Assert.Null(est.IdleStatesUsed);
        Assert.False(string.IsNullOrWhiteSpace(est.Impact));
        Assert.False(string.IsNullOrWhiteSpace(est.Recommendation));
    }

    [Fact]
    public void EstimateBatteryImpact_IdleStatesNotUsed_ReportsNoAdditionalImpact()
    {
        var report = new ApstInspectionReport { IdleStatesUsed = false };
        var est = ApstInspectorService.EstimateBatteryImpact(report);
        Assert.False(est.IdleStatesUsed);
        if (est.IsLaptop)
            Assert.Contains("doesn't idle into a low-power state", est.Impact, StringComparison.Ordinal);
        else
            Assert.Contains("no battery impact", est.Impact, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EstimateBatteryImpact_NotReported_NeverReadsAsOff()
    {
        // The old inspector said "APST disabled" whenever a registry value was missing.
        var est = ApstInspectorService.EstimateBatteryImpact(new ApstInspectionReport { IdleStatesUsed = null });
        Assert.Null(est.IdleStatesUsed);
        Assert.DoesNotContain("doesn't idle", est.Impact, StringComparison.Ordinal);
        Assert.DoesNotContain("disabled", est.Impact, StringComparison.OrdinalIgnoreCase);
        if (est.IsLaptop)
            Assert.Contains("can't be estimated", est.Impact, StringComparison.Ordinal);
    }

    [Fact]
    public void EstimateBatteryImpact_WithPowerStates_CalculatesSavings()
    {
        var report = new ApstInspectionReport
        {
            IdleStatesUsed = true,
            States = new()
            {
                new ApstPowerState { PowerStateNumber = 0, MaxPowerWatts = 6.0, NonOperational = false },
                new ApstPowerState { PowerStateNumber = 3, MaxPowerWatts = 0.05, NonOperational = true, IdleTimeMicroseconds = 5000 },
                new ApstPowerState { PowerStateNumber = 4, MaxPowerWatts = 0.004, NonOperational = true, IdleTimeMicroseconds = 40000 },
            }
        };
        var est = ApstInspectorService.EstimateBatteryImpact(report);
        Assert.True(est.IdleStatesUsed);
        Assert.Equal(6.0, est.ActivePowerWatts);
        Assert.Equal(0.004, est.LowestIdlePowerWatts);
        Assert.NotNull(est.EstimatedIdleSavingsWatts);
        Assert.True(est.EstimatedIdleSavingsWatts > 5.0);
    }

    [Fact]
    public void ApplyIdentifyPowerStates_MapsMpsByPowerStateIndex()
    {
        var report = new ApstInspectionReport
        {
            States = new()
            {
                new ApstPowerState { PowerStateNumber = 0, NonOperational = false },
                new ApstPowerState { PowerStateNumber = 3, NonOperational = true },
                new ApstPowerState { PowerStateNumber = 4, NonOperational = true }
            }
        };

        ApstInspectorService.ApplyIdentifyPowerStates(report,
        [
            new NvmePowerStateDescriptor { Index = 0, MaxPowerWatts = 6.0 },
            new NvmePowerStateDescriptor { Index = 3, MaxPowerWatts = 0.05 },
            new NvmePowerStateDescriptor { Index = 4, MaxPowerWatts = 0.004 }
        ]);

        Assert.Equal(6.0, report.States[0].MaxPowerWatts);
        Assert.Equal(0.05, report.States[1].MaxPowerWatts);
        Assert.Equal(0.004, report.States[2].MaxPowerWatts);
        report.IdleStatesUsed = true;
        var estimate = ApstInspectorService.EstimateBatteryImpact(report);
        Assert.Equal(6.0, estimate.ActivePowerWatts);
        Assert.Equal(0.004, estimate.LowestIdlePowerWatts);
    }

    [Fact]
    public void ApplyIdentifyPowerStates_WithNoRegistryStates_ListsTheControllersTable()
    {
        // stornvme normally has no PowerState{i}_* values, which left the inspector empty even
        // though Identify returned the drive's power table.
        var report = new ApstInspectionReport();

        ApstInspectorService.ApplyIdentifyPowerStates(report,
        [
            new NvmePowerStateDescriptor { Index = 4, MaxPowerWatts = 0.005, EntryLatencyUs = 5000, ExitLatencyUs = 45000, NonOperational = true },
            new NvmePowerStateDescriptor { Index = 0, MaxPowerWatts = 3.25 },
            new NvmePowerStateDescriptor { Index = 2, MaxPowerWatts = 0 },   // 0 W is a reading, kept
            new NvmePowerStateDescriptor { Index = 5, MaxPowerWatts = -1 },  // impossible value: skipped
            new NvmePowerStateDescriptor { Index = 0, MaxPowerWatts = 9.9 }  // duplicate index: first wins
        ]);

        Assert.Equal([0, 2, 4], report.States.Select(s => s.PowerStateNumber));
        Assert.Equal(0, report.States[1].MaxPowerWatts);
        report.States.RemoveAt(1);
        Assert.Equal(3.25, report.States[0].MaxPowerWatts);
        Assert.Equal(false, report.States[0].NonOperational);
        Assert.Equal(5000, report.States[1].EntryLatencyUs);
        Assert.Equal(45000, report.States[1].ExitLatencyUs);
        Assert.Equal(true, report.States[1].NonOperational);
        Assert.Null(report.States[1].IdleTimeMicroseconds);

        var estimate = ApstInspectorService.EstimateBatteryImpact(report);
        Assert.Equal(3.25, estimate.ActivePowerWatts);
        Assert.Equal(0.005, estimate.LowestIdlePowerWatts);
    }

    [Theory]
    [InlineData(50, 1)]    // DC, not in Modern Standby: deepest state within 50 ms
    [InlineData(500, 2)]   // Modern Standby: 500 ms reaches PS2
    [InlineData(5, null)]  // nothing non-operational wakes within 5 ms
    public void PickIdleState_FollowsMicrosoftsExample(int toleranceMs, int? expected)
    {
        // The worked example on Microsoft's StorNVMe page: PS1 is 10 ms + 300 us, PS2 is 50 ms + 10 ms.
        var states = new List<ApstPowerState>
        {
            new() { PowerStateNumber = 0, MaxPowerWatts = 6.0, EntryLatencyUs = 5, ExitLatencyUs = 5, NonOperational = false },
            new() { PowerStateNumber = 1, MaxPowerWatts = 0.05, EntryLatencyUs = 10_000, ExitLatencyUs = 300, NonOperational = true },
            new() { PowerStateNumber = 2, MaxPowerWatts = 0.005, EntryLatencyUs = 50_000, ExitLatencyUs = 10_000, NonOperational = true }
        };

        Assert.Equal(expected, ApstInspectorService.PickIdleState(states, toleranceMs)?.PowerStateNumber);
    }

    [Fact]
    public void ApplyIdlePolicy_OnAc_UsesThePrimaryAndSecondaryTiers()
    {
        var report = ReportWithPowerTable();

        ApstInspectorService.ApplyIdlePolicy(report, BalancedAc, BalancedDc, onBattery: false);

        Assert.True(report.IdleStatesUsed);
        Assert.Equal(3, report.PrimaryIdleState);     // 15 ms fits the 15 ms primary tolerance
        Assert.Equal(4, report.SecondaryIdleState);   // 50 ms fits the 100 ms secondary tolerance
        Assert.Equal(200_000, report.States.Single(s => s.PowerStateNumber == 3).IdleTimeMicroseconds);
        Assert.Equal(2_000_000, report.States.Single(s => s.PowerStateNumber == 4).IdleTimeMicroseconds);
        Assert.Null(report.States.Single(s => s.PowerStateNumber == 0).IdleTimeMicroseconds);
        Assert.Contains("PS3 after 200 ms and PS4 after 2000 ms on AC power", report.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("disabled", report.Summary, StringComparison.OrdinalIgnoreCase);

        var est = ApstInspectorService.EstimateBatteryImpact(report);
        Assert.True(est.IdleStatesUsed);
        Assert.Equal(0.004, est.LowestIdlePowerWatts);
    }

    [Fact]
    public void ApplyIdlePolicy_OnBattery_UsesTheDcSettings()
    {
        var report = ReportWithPowerTable();

        ApstInspectorService.ApplyIdlePolicy(report, BalancedAc, BalancedDc, onBattery: true);

        // DC allows 50 ms on the primary tier, so the first step already reaches PS4.
        Assert.Equal(4, report.PrimaryIdleState);
        Assert.Equal(4, report.SecondaryIdleState);
        Assert.Equal(100_000, report.States.Single(s => s.PowerStateNumber == 4).IdleTimeMicroseconds);
        Assert.Contains("PS4 after 100 ms on battery", report.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyIdlePolicy_NoStateWakesFastEnough_StaysOperational()
    {
        var report = ReportWithPowerTable();

        ApstInspectorService.ApplyIdlePolicy(report, new NvmeIdleSettings(200, 0, 2000, 0), null, onBattery: false);

        Assert.False(report.IdleStatesUsed);
        Assert.Null(report.PrimaryIdleState);
        Assert.Contains("stays in an operational state", report.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyIdlePolicy_DriveWithOnlyOperationalStates_SaysSo()
    {
        var report = new ApstInspectionReport();
        ApstInspectorService.ApplyIdentifyPowerStates(report,
            [new NvmePowerStateDescriptor { Index = 0, MaxPowerWatts = 25.0, EntryLatencyUs = 16, ExitLatencyUs = 4 }]);

        ApstInspectorService.ApplyIdlePolicy(report, BalancedAc, BalancedDc, onBattery: false);

        Assert.False(report.IdleStatesUsed);
        Assert.Contains("reports no non-operational power states", report.Summary, StringComparison.Ordinal);
        Assert.Contains("1 power state.", report.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyIdlePolicy_PowerPlanUnreadable_IsNotReportedRatherThanOff()
    {
        var report = ReportWithPowerTable();

        ApstInspectorService.ApplyIdlePolicy(report, null, null, onBattery: false);

        Assert.Null(report.IdleStatesUsed);
        Assert.Contains("wasn't reported", report.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyIdlePolicy_NoPowerTable_IsNotReported()
    {
        var report = new ApstInspectionReport();

        ApstInspectorService.ApplyIdlePolicy(report, BalancedAc, BalancedDc, onBattery: false);

        Assert.Null(report.IdleStatesUsed);
        Assert.Contains("power table wasn't readable", report.Summary, StringComparison.Ordinal);
        Assert.Contains("200 ms", report.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyIdlePolicy_BothTimeoutsZero_IsNotReported()
    {
        var report = ReportWithPowerTable();

        ApstInspectorService.ApplyIdlePolicy(report, new NvmeIdleSettings(0, 15, 0, 100), null, onBattery: false);

        Assert.Null(report.IdleStatesUsed);
        Assert.Contains("0 ms", report.Summary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, null, "NoLowPowerTransitions=1")]
    [InlineData(false, false, "AutonomousPowerStateTransitionEnabled=0")]
    public void ApplyIdlePolicy_UndocumentedRegistryValue_IsReportedButThePowerPlanStillDecides(bool noLowPower, bool? apstOverride, string named)
    {
        // Microsoft documents neither value for stornvme and the driver isn't known to read them,
        // so they can't be trusted to turn idle off. The verdict stays with the power plan.
        var report = ReportWithPowerTable();
        report.NoLowPowerTransitions = noLowPower;
        report.ApstEnabledOverride = apstOverride;

        ApstInspectorService.ApplyIdlePolicy(report, BalancedAc, BalancedDc, onBattery: false);

        Assert.True(report.IdleStatesUsed);
        Assert.Equal(named, report.UndocumentedIdleOverride);
        Assert.Contains("PS3 after 200 ms and PS4 after 2000 ms on AC power", report.Summary, StringComparison.Ordinal);
        Assert.Contains($"{named} is set, but Microsoft doesn't document it", report.Summary, StringComparison.Ordinal);
        Assert.True(ApstInspectorService.EstimateBatteryImpact(report).IdleStatesUsed);
    }

    [Fact]
    public void ApplyIdlePolicy_UndocumentedValue_WithNoPowerPlan_StillSaysNotReported()
    {
        var report = ReportWithPowerTable();
        report.NoLowPowerTransitions = true;

        ApstInspectorService.ApplyIdlePolicy(report, ac: null, dc: null, onBattery: false);

        Assert.Null(report.IdleStatesUsed);
        Assert.StartsWith("Low-power idle wasn't reported", report.Summary, StringComparison.Ordinal);
        Assert.EndsWith("so it isn't counted on to turn idle off.", report.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyIdlePolicy_NoUndocumentedValue_LeavesTheSummaryAlone()
    {
        var report = ReportWithPowerTable();
        report.ApstEnabledOverride = true;   // its "on" value claims nothing

        ApstInspectorService.ApplyIdlePolicy(report, BalancedAc, BalancedDc, onBattery: false);

        Assert.Null(report.UndocumentedIdleOverride);
        Assert.DoesNotContain("doesn't document", report.Summary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, "~5.9W (lost after patching)")]
    [InlineData(null, "up to ~5.9W, if Windows idles this drive (not confirmed)")]
    public void IdleSavingsText_FollowsTheVerdict(bool? idleStatesUsed, string expected)
    {
        var est = new ApstBatteryEstimate { IdleStatesUsed = idleStatesUsed, EstimatedIdleSavingsWatts = 5.9 };
        Assert.Equal(expected, ApstInspectorService.IdleSavingsText(est));
    }

    [Fact]
    public void IdleSavingsText_NothingToSay_WhenWindowsNeverIdlesTheDriveOrTheWattsAreUnknown()
    {
        Assert.Null(ApstInspectorService.IdleSavingsText(new ApstBatteryEstimate { IdleStatesUsed = false, EstimatedIdleSavingsWatts = 5.9 }));
        Assert.Null(ApstInspectorService.IdleSavingsText(new ApstBatteryEstimate { IdleStatesUsed = true, EstimatedIdleSavingsWatts = null }));
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
