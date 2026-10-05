using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class SchedulerServiceTests
{
    [Fact]
    public void BootVerifyArgs_RunAsSystemHighest_OnStart_WatchdogAutoRevert()
    {
        var args = SchedulerService.BuildBootVerifyArgs(@"C:\Tools\NVMeDriverPatcher.Cli.exe");

        Assert.Equal("/Create", args[0]);
        AssertPair(args, "/RU", "SYSTEM");
        AssertPair(args, "/RL", "HIGHEST");
        AssertPair(args, "/TN", SchedulerService.BootTaskName);
        AssertPair(args, "/SC", "ONSTART");
        AssertPair(args, "/TR", "\"C:\\Tools\\NVMeDriverPatcher.Cli.exe\" watchdog --auto-revert");
    }

    [Fact]
    public void WatchdogSweepArgs_ClampsIntervalAndUsesMinuteSchedule()
    {
        var tooSmall = SchedulerService.BuildWatchdogSweepArgs(@"cli.exe", 1);
        AssertPair(tooSmall, "/SC", "MINUTE");
        AssertPair(tooSmall, "/MO", "5");   // clamped up to the 5-minute floor

        var inRange = SchedulerService.BuildWatchdogSweepArgs(@"cli.exe", 30);
        AssertPair(inRange, "/MO", "30");
        AssertPair(inRange, "/TR", "\"cli.exe\" watchdog");

        var justUnderADay = SchedulerService.BuildWatchdogSweepArgs(@"cli.exe", 1439);
        AssertPair(justUnderADay, "/SC", "MINUTE");
        AssertPair(justUnderADay, "/MO", "1439");
    }

    /// <summary>
    /// schtasks.exe documents /SC MINUTE /MO as 1 - 1439. The 24-hour ceiling used to be emitted
    /// as "/SC MINUTE /MO 1440", which schtasks rejects ("The /MO value is invalid"), so asking
    /// for a daily sweep failed outright. A day or more is now a plain daily schedule.
    /// </summary>
    [Theory]
    [InlineData(1440)]
    [InlineData(1441)]
    [InlineData(99999)]
    [InlineData(int.MaxValue)]
    public void WatchdogSweepArgs_ADayOrMore_UsesDailySchedule(int intervalMinutes)
    {
        var args = SchedulerService.BuildWatchdogSweepArgs(@"cli.exe", intervalMinutes);

        AssertPair(args, "/SC", "DAILY");
        Assert.DoesNotContain("/MO", args);
        AssertPair(args, "/TN", SchedulerService.WatchdogTaskName);
        AssertPair(args, "/TR", "\"cli.exe\" watchdog");
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(60)]
    [InlineData(1439)]
    [InlineData(1440)]
    [InlineData(int.MaxValue)]
    public void WatchdogSweepArgs_NeverEmitAMinuteModifierSchtasksRejects(int intervalMinutes)
    {
        var args = SchedulerService.BuildWatchdogSweepArgs(@"cli.exe", intervalMinutes);
        var schedule = args[Array.IndexOf(args, "/SC") + 1];
        if (schedule != "MINUTE") return;

        var modifier = int.Parse(args[Array.IndexOf(args, "/MO") + 1], System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(modifier, 1, 1439);
    }

    [Fact]
    public void UnregisterArgs_DeletesNamedTask()
    {
        var args = SchedulerService.BuildUnregisterArgs(SchedulerService.WatchdogTaskName);
        Assert.Equal("/Delete", args[0]);
        Assert.Contains("/F", args);
        AssertPair(args, "/TN", SchedulerService.WatchdogTaskName);
    }

    private static void AssertPair(string[] args, string flag, string expectedValue)
    {
        var idx = Array.IndexOf(args, flag);
        Assert.True(idx >= 0 && idx + 1 < args.Length, $"flag {flag} not found with a value");
        Assert.Equal(expectedValue, args[idx + 1]);
    }
}
