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

        var local = now ?? DateTime.Now;
        if (!window.ActiveDays.Contains(local.DayOfWeek)) return false;

        int hour = local.Hour;
        if (window.StartHour == window.EndHour) return false;

        // Normal case: start < end (same-day window).
        if (window.StartHour < window.EndHour)
            return hour >= window.StartHour && hour < window.EndHour;

        // Overnight case: start > end (e.g. 22 → 06 wraps midnight).
        return hour >= window.StartHour || hour < window.EndHour;
    }

    public static string Summarize(MaintenanceWindow window)
    {
        if (!window.Enabled) return "Maintenance window disabled — actions run any time.";
        var days = string.Join(", ", window.ActiveDays.Select(d => d.ToString()[..3]));
        return $"Maintenance window: {window.StartHour:00}:00–{window.EndHour:00}:00 on {days}.";
    }
}
