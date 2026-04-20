using System.IO;
using System.Text;
using System.Text.Json;
using NVMeDriverPatcher.Models;

namespace NVMeDriverPatcher.Services;

// Export a portable config bundle (AppConfig JSON + drive_scope.json + watchdog.json +
// active tuning profile) and import one on another machine. Separate from the support-bundle
// ZIP — that one is for diagnostics, this one is for fleet cloning.
public static class ConfigImportExportService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public class Bundle
    {
        public int SchemaVersion { get; set; } = 1;
        public string ExportedAt { get; set; } = DateTime.UtcNow.ToString("o");
        public AppConfig? Config { get; set; }
        public PerDriveScopeConfig? DriveScope { get; set; }
        public WatchdogState? Watchdog { get; set; }
    }

    public static string Export(AppConfig config, string outputPath)
    {
        var bundle = new Bundle
        {
            Config = config,
            DriveScope = PerDriveScopeService.Load(config),
            Watchdog = EventLogWatchdogService.LoadState(config)
        };
        var json = JsonSerializer.Serialize(bundle, JsonOptions);
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
        var tmp = outputPath + ".tmp";
        File.WriteAllText(tmp, json, new UTF8Encoding(false));
        File.Move(tmp, outputPath, overwrite: true);
        return outputPath;
    }

    public static (bool Success, string Summary) Import(string inputPath, AppConfig config)
    {
        try
        {
            if (!File.Exists(inputPath)) return (false, "Bundle not found.");
            var json = File.ReadAllText(inputPath);
            var bundle = JsonSerializer.Deserialize<Bundle>(json);
            if (bundle is null) return (false, "Bundle could not be parsed.");
            if (bundle.Config is not null)
            {
                // Carry over only user-safe fields. Working dir / config file path stay local.
                config.AutoSaveLog = bundle.Config.AutoSaveLog;
                config.EnableToasts = bundle.Config.EnableToasts;
                config.WriteEventLog = bundle.Config.WriteEventLog;
                config.RestartDelay = bundle.Config.RestartDelay;
                config.IncludeServerKey = bundle.Config.IncludeServerKey;
                config.SkipWarnings = bundle.Config.SkipWarnings;
                config.PatchProfile = bundle.Config.PatchProfile;
                ConfigService.Save(config);
            }
            if (bundle.DriveScope is not null)
                PerDriveScopeService.Save(config, bundle.DriveScope);
            if (bundle.Watchdog is not null)
                EventLogWatchdogService.SaveState(config, bundle.Watchdog);
            return (true, "Config bundle imported.");
        }
        catch (Exception ex)
        {
            return (false, $"Import failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
