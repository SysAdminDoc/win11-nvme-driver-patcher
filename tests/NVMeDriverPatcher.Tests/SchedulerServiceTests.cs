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

        var tooLarge = SchedulerService.BuildWatchdogSweepArgs(@"cli.exe", 99999);
        AssertPair(tooLarge, "/MO", "1440"); // clamped down to the 24h ceiling

        var inRange = SchedulerService.BuildWatchdogSweepArgs(@"cli.exe", 30);
        AssertPair(inRange, "/MO", "30");
        AssertPair(inRange, "/TR", "\"cli.exe\" watchdog");
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
