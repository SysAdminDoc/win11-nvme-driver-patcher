using System.IO;
using NVMeDriverPatcher.Models;

namespace NVMeDriverPatcher.Services;

// Keeps %LocalAppData%\NVMePatcher\ from unbounded growth. Rotates crash.log and activity.log
// on startup if they've crossed the size cap. Five files × 5MB each by default — 25MB total
// headroom is enough to contain a noisy week of activity while staying cheap to zip into a
// support bundle.
public static class LogRotationService
{
    public const long DefaultMaxBytesPerFile = 5 * 1024 * 1024;
    public const int DefaultRetainCount = 5;

    private static readonly string[] ManagedLogs =
    {
        "crash.log",
        "activity.log",
        "watchdog.log",
        "diagnostics.log"
    };

    public static void RotateAll(AppConfig config, long maxBytesPerFile = DefaultMaxBytesPerFile, int retain = DefaultRetainCount)
    {
        var dir = string.IsNullOrWhiteSpace(config.WorkingDir) ? AppConfig.GetWorkingDir() : config.WorkingDir;
        foreach (var name in ManagedLogs)
        {
            var path = Path.Combine(dir, name);
            try { RotateOne(path, maxBytesPerFile, retain); }
            catch { /* rotation is best-effort — never let it break startup */ }
        }
    }

    internal static void RotateOne(string path, long maxBytesPerFile, int retain)
    {
        if (!File.Exists(path)) return;
        var info = new FileInfo(path);
        if (info.Length < maxBytesPerFile) return;

        // Shift older files: .N -> .N+1, deleting anything past `retain`.
        for (int i = retain - 1; i >= 1; i--)
        {
            var older = $"{path}.{i}";
            var newer = $"{path}.{i + 1}";
            if (!File.Exists(older)) continue;
            try
            {
                if (File.Exists(newer)) File.Delete(newer);
                File.Move(older, newer);
            }
            catch { /* if rename fails mid-chain, older file stays put — next rotation retries */ }
        }
        try
        {
            var archived = $"{path}.1";
            if (File.Exists(archived)) File.Delete(archived);
            File.Move(path, archived);
        }
        catch { /* live file in use — skip this cycle */ }
    }

    /// <summary>
    /// Returns the total bytes used by managed log files, for diagnostics display.
    /// </summary>
    public static long TotalManagedBytes(AppConfig config)
    {
        var dir = string.IsNullOrWhiteSpace(config.WorkingDir) ? AppConfig.GetWorkingDir() : config.WorkingDir;
        long total = 0;
        foreach (var name in ManagedLogs)
        {
            for (int i = 0; i <= DefaultRetainCount; i++)
            {
                var path = i == 0 ? Path.Combine(dir, name) : Path.Combine(dir, $"{name}.{i}");
                try
                {
                    var info = new FileInfo(path);
                    if (info.Exists) total += info.Length;
                }
                catch { }
            }
        }
        return total;
    }
}
