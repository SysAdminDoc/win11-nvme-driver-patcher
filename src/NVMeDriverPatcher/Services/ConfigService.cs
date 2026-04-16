using System.IO;
using System.Text.Json;
using NVMeDriverPatcher.Models;

namespace NVMeDriverPatcher.Services;

public static class ConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static AppConfig Load()
    {
        var config = new AppConfig
        {
            WorkingDir = AppConfig.GetWorkingDir()
        };
        config.ConfigFile = Path.Combine(config.WorkingDir, "config.json");

        if (!File.Exists(config.ConfigFile)) return config;

        try
        {
            var json = File.ReadAllText(config.ConfigFile);
            var saved = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
            if (saved is null) return config;

            config.AutoSaveLog = saved.AutoSaveLog;
            config.EnableToasts = saved.EnableToasts;
            config.WriteEventLog = saved.WriteEventLog;
            config.RestartDelay = saved.RestartDelay;
            config.IncludeServerKey = saved.IncludeServerKey;
            config.SkipWarnings = saved.SkipWarnings;
            config.LastRecoveryKitPath = saved.LastRecoveryKitPath;
            config.LastDiagnosticsPath = saved.LastDiagnosticsPath;
            config.LastVerificationScriptPath = saved.LastVerificationScriptPath;
        }
        catch { /* Config load best-effort */ }

        return config;
    }

    public static void Save(AppConfig config)
    {
        try
        {
            var toSave = new
            {
                config.AutoSaveLog,
                config.EnableToasts,
                config.WriteEventLog,
                config.RestartDelay,
                config.IncludeServerKey,
                config.SkipWarnings,
                config.LastRecoveryKitPath,
                config.LastDiagnosticsPath,
                config.LastVerificationScriptPath,
                LastRun = DateTime.Now.ToString("o")
            };
            var json = JsonSerializer.Serialize(toSave, JsonOptions);
            File.WriteAllText(config.ConfigFile, json);
        }
        catch { /* Config save best-effort */ }
    }
}
