using System.IO;

namespace NVMeDriverPatcher.Services;

// Portable mode — when `portable.flag` sits next to the exe OR `--portable` is passed,
// redirect all writable state to a `Data\` folder beside the exe. Lets sysadmins drop the
// binary on a USB stick and keep config/watchdog/etl/backups self-contained.
public static class PortableModeService
{
    private const string FlagFile = "portable.flag";
    private const string PortableDataDir = "Data";

    public static bool IsPortable()
    {
        try
        {
            var exeDir = AppContext.BaseDirectory;
            if (string.IsNullOrEmpty(exeDir)) return false;
            return File.Exists(Path.Combine(exeDir, FlagFile));
        }
        catch { return false; }
    }

    public static string? PortableDataPath()
    {
        try
        {
            var exeDir = AppContext.BaseDirectory;
            if (string.IsNullOrEmpty(exeDir)) return null;
            var dir = Path.Combine(exeDir, PortableDataDir);
            Directory.CreateDirectory(dir);
            return dir;
        }
        catch { return null; }
    }

    /// <summary>
    /// Creates portable.flag beside the exe. After the next launch the app writes state to
    /// `Data\` beside the exe instead of %LocalAppData%.
    /// </summary>
    public static bool Enable(Action<string>? log = null)
    {
        try
        {
            var exeDir = AppContext.BaseDirectory;
            var flag = Path.Combine(exeDir, FlagFile);
            File.WriteAllText(flag, $"Portable mode enabled at {DateTime.UtcNow:O}. Delete this file to return to per-user mode.");
            log?.Invoke($"[OK] Portable mode enabled. Data directory: {Path.Combine(exeDir, PortableDataDir)}");
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke($"[ERROR] Could not enable portable mode: {ex.Message}");
            return false;
        }
    }

    public static bool Disable(Action<string>? log = null)
    {
        try
        {
            var exeDir = AppContext.BaseDirectory;
            var flag = Path.Combine(exeDir, FlagFile);
            if (File.Exists(flag)) File.Delete(flag);
            log?.Invoke("[OK] Portable mode disabled. Data returns to %LocalAppData%\\NVMePatcher on next launch.");
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke($"[ERROR] Could not disable portable mode: {ex.Message}");
            return false;
        }
    }
}
