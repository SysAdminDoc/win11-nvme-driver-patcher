using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class MaintenanceWindowServiceTests
{
    [Fact]
    public void DisabledWindow_AlwaysInWindow()
    {
        var w = new MaintenanceWindow { Enabled = false };
        Assert.True(MaintenanceWindowService.IsInWindow(w, new DateTime(2026, 4, 20, 14, 0, 0)));
    }

    [Fact]
    public void SameDayWindow_InsideHours_IsTrue()
    {
        var w = new MaintenanceWindow
        {
            Enabled = true, StartHour = 9, EndHour = 17,
            ActiveDays = new() { DayOfWeek.Monday }
        };
        Assert.True(MaintenanceWindowService.IsInWindow(w, new DateTime(2026, 4, 20, 12, 0, 0)));   // Monday noon
        Assert.False(MaintenanceWindowService.IsInWindow(w, new DateTime(2026, 4, 20, 17, 0, 0)));  // 5pm is OUT (< end)
    }

    [Fact]
    public void OvernightWindow_WrapsMidnight()
    {
        var w = new MaintenanceWindow
        {
            Enabled = true, StartHour = 22, EndHour = 6,
            ActiveDays = new() { DayOfWeek.Monday, DayOfWeek.Tuesday }
        };
        Assert.True(MaintenanceWindowService.IsInWindow(w, new DateTime(2026, 4, 20, 23, 30, 0))); // 11:30pm Mon
        Assert.True(MaintenanceWindowService.IsInWindow(w, new DateTime(2026, 4, 21, 5, 0, 0)));   // 5am Tue
        Assert.False(MaintenanceWindowService.IsInWindow(w, new DateTime(2026, 4, 20, 15, 0, 0))); // 3pm Mon
    }

    [Fact]
    public void InactiveDay_NeverInWindow()
    {
        var w = new MaintenanceWindow
        {
            Enabled = true, StartHour = 0, EndHour = 23,
            ActiveDays = new() { DayOfWeek.Saturday }
        };
        // Monday — not in ActiveDays — should be out regardless of hour.
        Assert.False(MaintenanceWindowService.IsInWindow(w, new DateTime(2026, 4, 20, 12, 0, 0)));
    }

    [Fact]
    public void ZeroWidthWindow_IsNeverTrue()
    {
        var w = new MaintenanceWindow
        {
            Enabled = true, StartHour = 12, EndHour = 12,
            ActiveDays = new() { DayOfWeek.Monday }
        };
        Assert.False(MaintenanceWindowService.IsInWindow(w, new DateTime(2026, 4, 20, 12, 0, 0)));
    }
}
