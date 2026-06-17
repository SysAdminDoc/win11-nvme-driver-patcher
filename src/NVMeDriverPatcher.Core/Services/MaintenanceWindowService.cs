using System.IO;
using System.Text.Json;
using NVMeDriverPatcher.Models;

namespace NVMeDriverPatcher.Services;

public class MaintenanceWindow
{
    public bool Enabled { get; set; }
    // Local-time window — most sysadmins think in business hours, not UTC.
    public int StartHour { get; set; } = 22;   // 10pm
    public int EndHour { get; set; } = 6;      // 6am next day
    public List<DayOfWeek> ActiveDays { get; set; } = new()
    {
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
        DayOfWeek.Thursday, DayOfWeek.Friday
    };
}

// User-definable maintenance window gating auto-revert and scheduled actions. Purpose:
// don't yank the NVMe driver out from under a CEO giving a demo at 2pm on Tuesday because
// the post-patch watchdog crossed a threshold — wait for the 10pm → 6am window.
public static class MaintenanceWindowService
{
    private const string WindowFile = "maintenance_window.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string WindowPath(AppConfig config) => Path.Combine(
        string.IsNullOrWhiteSpace(config.WorkingDir) ? AppConfig.GetWorkingDir() : config.WorkingDir,
        WindowFile);

    public static MaintenanceWindow Load(AppConfig config)
    {
        try
        {
            var path = WindowPath(config);
            if (!File.Exists(path)) return new MaintenanceWindow();
            var json = File.ReadAllText(path);
            return string.IsNullOrWhiteSpace(json)
                ? new MaintenanceWindow()
                : JsonSerializer.Deserialize<MaintenanceWindow>(json, JsonOptions) ?? new MaintenanceWindow();
        }
        catch { return new MaintenanceWindow(); }
    }

    public static void Save(AppConfig config, MaintenanceWindow window)
    {
        try
        {
            var path = WindowPath(config);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(window, JsonOptions));
            File.Move(tmp, path, overwrite: true);
        }
        catch { }
    }

    /// <summary>
    /// Returns true when NOW is inside the maintenance window (or no window is set).
    /// Auto-revert / scheduled actions gate on this: if the window is disabled, they run
    /// freely; if the window is set, they wait until we're inside it.
    /// </summary>
    public static bool IsInWindow(MaintenanceWindow window, DateTime? now = null)
    {
        if (!window.Enabled) return true;
        if (window.StartHour == window.EndHour) return false;

        var local = now ?? DateTime.Now;
        int hour = local.Hour;

        // Same-day window (start < end): must be an active day, within [start, end).
        if (window.StartHour < window.EndHour)
        {
            return window.ActiveDays.Contains(local.DayOfWeek)
                && hour >= window.StartHour && hour < window.EndHour;
        }

        // Overnight window (start > end, e.g. 22:00 → 06:00) belongs to the calendar day it
        // OPENED, not the instant being tested. Evaluating active-day membership against the
        // current day mis-gates the tail: Saturday 02:00 (the tail of the Friday-night window)
        // would be wrongly rejected, and Monday 02:00 (no window opened Sunday night) wrongly
        // accepted. Split on which side of midnight we're on:
        if (hour >= window.StartHour)                                    // evening part — opened today
            return window.ActiveDays.Contains(local.DayOfWeek);
        if (hour < window.EndHour)                                       // morning tail — opened yesterday
            return window.ActiveDays.Contains(local.AddDays(-1).DayOfWeek);
        return false;                                                    // daytime gap between windows
    }

    public static string Summarize(MaintenanceWindow window)
    {
        if (!window.Enabled) return "Maintenance window disabled — actions run any time.";
        var days = string.Join(", ", window.ActiveDays.Select(d => d.ToString()[..3]));
        return $"Maintenance window: {window.StartHour:00}:00–{window.EndHour:00}:00 on {days}.";
    }
}
