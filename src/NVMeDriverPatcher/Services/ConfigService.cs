using System.IO;
using System.Text.Json;
using NVMeDriverPatcher.Models;

namespace NVMeDriverPatcher.Services;

public static class ConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters =
        {
            // Persist enums like PatchProfile as "Safe"/"Full" strings — config.json is a file
            // sysadmins open by hand, and numeric enum values would be opaque.
            new System.Text.Json.Serialization.JsonStringEnumConverter()
        }
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
            if (string.IsNullOrWhiteSpace(json)) return config;

            var saved = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
            if (saved is null) return config;

            config.AutoSaveLog = saved.AutoSaveLog;
            config.EnableToasts = saved.EnableToasts;
            config.WriteEventLog = saved.WriteEventLog;
            config.RestartDelay = saved.RestartDelay;       // setter clamps 0..3600
            config.IncludeServerKey = saved.IncludeServerKey;
            config.SkipWarnings = saved.SkipWarnings;
            // Unknown or out-of-range enum -> keep the default (Safe). Same defensive
            // stance we take for RestartDelay via the clamp setter.
            config.PatchProfile = Enum.IsDefined(typeof(PatchProfile), saved.PatchProfile)
                ? saved.PatchProfile
                : PatchProfile.Safe;
            // Future migrations can branch on ConfigVersion; for now just carry it forward
            // unless the saved file predates the field (deserializes to 0).
            config.ConfigVersion = saved.ConfigVersion == 0 ? 2 : saved.ConfigVersion;
            // Drop stale recovery/diagnostics paths whose targets no longer exist —
            // otherwise the workspace shows a "ready" status pointing at missing files.
            config.LastRecoveryKitPath = ExistingDir(saved.LastRecoveryKitPath);
            config.LastDiagnosticsPath = ExistingFile(saved.LastDiagnosticsPath);
            config.LastVerificationScriptPath = ExistingFile(saved.LastVerificationScriptPath);
            config.PendingVerificationSince = saved.PendingVerificationSince;
            config.LastVerifiedProfile = saved.LastVerifiedProfile;
            config.LastVerificationResult = saved.LastVerificationResult;
        }
        catch
        {
            // Corrupt config: rename so the next save starts fresh and the user can recover values.
            try
            {
                var corrupt = config.ConfigFile + ".corrupt";
                if (File.Exists(corrupt)) File.Delete(corrupt);
                File.Move(config.ConfigFile, corrupt);
            }
            catch { /* Best-effort */ }
        }

        return config;
    }

    private static string? ExistingDir(string? path)
        => string.IsNullOrWhiteSpace(path) || !Directory.Exists(path) ? null : path;

    private static string? ExistingFile(string? path)
        => string.IsNullOrWhiteSpace(path) || !File.Exists(path) ? null : path;

    public static void Save(AppConfig config)
    {
        if (string.IsNullOrEmpty(config.ConfigFile))
            return;

        try
        {
            // Make sure the working folder still exists — the user could have deleted it.
            var dir = Path.GetDirectoryName(config.ConfigFile);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var toSave = new
            {
                config.AutoSaveLog,
                config.EnableToasts,
                config.WriteEventLog,
                config.RestartDelay,
                config.IncludeServerKey,
                config.SkipWarnings,
                config.PatchProfile,
                config.ConfigVersion,
                config.LastRecoveryKitPath,
                config.LastDiagnosticsPath,
                config.LastVerificationScriptPath,
                config.PendingVerificationSince,
                config.LastVerifiedProfile,
                config.LastVerificationResult,
                LastRun = DateTime.Now.ToString("o")
            };
            var json = JsonSerializer.Serialize(toSave, JsonOptions);

            // Atomic write: write+flush to a temp file, then rename. The Flush(true) is what
            // makes this crash-safe — without it, File.WriteAllText returns before the data
            // hits the disk and a hard reset can leave a zero-byte config behind.
            var tempFile = config.ConfigFile + ".tmp";
            using (var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var sw = new StreamWriter(fs, new System.Text.UTF8Encoding(false)))
            {
                sw.Write(json);
                sw.Flush();
                fs.Flush(flushToDisk: true);
            }
            File.Move(tempFile, config.ConfigFile, overwrite: true);
        }
        catch { /* Config save best-effort */ }
    }
}
