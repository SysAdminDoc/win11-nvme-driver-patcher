using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

// Exhaustive table for the watchdog's core decision (extracted DeriveVerdict) plus the
// maintenance-window evaluation that gates auto-revert — the two pure inputs to
// AutoRevertService's "should we yank the driver right now" call.
public sealed class WatchdogVerdictDerivationTests
{
    [Theory]
    // Window open: under warn → Healthy; at/over warn → Warning; at/over revert → Unstable.
    [InlineData(0, 3, 6, false, WatchdogVerdict.Healthy)]
    [InlineData(2, 3, 6, false, WatchdogVerdict.Healthy)]
    [InlineData(3, 3, 6, false, WatchdogVerdict.Warning)]
    [InlineData(5, 3, 6, false, WatchdogVerdict.Warning)]
    [InlineData(6, 3, 6, false, WatchdogVerdict.Unstable)]
    [InlineData(99, 3, 6, false, WatchdogVerdict.Unstable)]
    // Window expired: same threshold behavior, but under-warn becomes Completed
    // ("patch deemed stable") — the boundary the original nested if/else made fragile.
    [InlineData(0, 3, 6, true, WatchdogVerdict.Completed)]
    [InlineData(2, 3, 6, true, WatchdogVerdict.Completed)]
    [InlineData(3, 3, 6, true, WatchdogVerdict.Warning)]
    [InlineData(6, 3, 6, true, WatchdogVerdict.Unstable)]
    // Degenerate thresholds: revert == warn means warning band is empty.
    [InlineData(4, 4, 4, false, WatchdogVerdict.Unstable)]
    [InlineData(3, 4, 4, false, WatchdogVerdict.Healthy)]
    public void DeriveVerdict_FullTable(int events, int warn, int revert, bool expired, WatchdogVerdict expected)
    {
        Assert.Equal(expected, EventLogWatchdogService.DeriveVerdict(events, warn, revert, expired));
    }

    // --- Maintenance window gating (injectable clock) ---

    private static MaintenanceWindow OvernightWeekdays() => new()
    {
        Enabled = true,
        StartHour = 22,
        EndHour = 6,
        ActiveDays = new List<DayOfWeek>
        {
            DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
            DayOfWeek.Thursday, DayOfWeek.Friday
        },
    };

    [Theory]
    [InlineData(2026, 6, 8, 23, true)]   // Monday 23:00 — inside overnight window
    [InlineData(2026, 6, 9, 5, true)]    // Tuesday 05:00 — tail of Monday-started window
    [InlineData(2026, 6, 8, 12, false)]  // Monday noon — outside
    [InlineData(2026, 6, 8, 6, false)]   // Monday 06:00 — boundary: window closed
    public void IsInWindow_OvernightWindow_EvaluatesLocalHours(int y, int m, int d, int hour, bool expected)
    {
        var now = new DateTime(y, m, d, hour, 0, 0, DateTimeKind.Local);
        Assert.Equal(expected, MaintenanceWindowService.IsInWindow(OvernightWeekdays(), now));
    }

    [Fact]
    public void IsInWindow_SaturdayNight_OutsideWeekdayWindow()
    {
        // 2026-06-13 is a Saturday — not in ActiveDays.
        var saturdayNight = new DateTime(2026, 6, 13, 23, 0, 0, DateTimeKind.Local);
        Assert.False(MaintenanceWindowService.IsInWindow(OvernightWeekdays(), saturdayNight));
    }
}
