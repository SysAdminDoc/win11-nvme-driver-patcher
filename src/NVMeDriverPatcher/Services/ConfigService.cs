using System.IO;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
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
            new LenientPatchProfileJsonConverter(),
            new LenientThemeModeJsonConverter(),
            new JsonStringEnumConverter()
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
            config.ThemeMode = Enum.IsDefined(typeof(AppThemeMode), saved.ThemeMode)
                ? saved.ThemeMode
                : AppThemeMode.System;
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
            config.LastDiagnosticsPath = ExistingFileWithExtension(saved.LastDiagnosticsPath, ".txt");
            config.LastSupportBundlePath = ExistingFileWithExtension(saved.LastSupportBundlePath, ".zip");
            config.LastVerificationScriptPath = ExistingFile(saved.LastVerificationScriptPath);
            config.PendingVerificationSince = saved.PendingVerificationSince;
            config.PendingVerificationProfile = saved.PendingVerificationProfile;
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
    {
        if (!IsUsableAbsolutePath(path))
            return null;

        return Directory.Exists(path) ? Path.GetFullPath(path) : null;
    }

    private static string? ExistingFile(string? path)
    {
        if (!IsUsableAbsolutePath(path))
            return null;

        return File.Exists(path) ? Path.GetFullPath(path) : null;
    }

    internal static bool IsUsableAbsolutePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            return Path.IsPathFullyQualified(path)
                && path.IndexOfAny(Path.GetInvalidPathChars()) < 0;
        }
        catch
        {
            return false;
        }
    }

    internal static string? ExistingFileWithExtension(string? path, string extension)
    {
        var existing = ExistingFile(path);
        if (existing is null)
            return null;

        return string.Equals(Path.GetExtension(existing), extension, StringComparison.OrdinalIgnoreCase)
            ? existing
            : null;
    }

    public static void Save(AppConfig config)
    {
        if (string.IsNullOrEmpty(config.ConfigFile))
            return;

        var tempFile = config.ConfigFile + ".tmp";
        Exception? lastException = null;

        // Retry up to 5 times with a short backoff. The common transient failure is an AV
        // scanner holding an exclusive handle on the target during File.Move — that usually
        // clears within a few hundred milliseconds. A persistent IOException is a real
        // problem worth logging, not silently swallowing.
        for (int attempt = 0; attempt < 5; attempt++)
        {
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
                    config.ThemeMode,
                    config.PatchProfile,
                    config.ConfigVersion,
                    config.LastRecoveryKitPath,
                    config.LastDiagnosticsPath,
                    config.LastSupportBundlePath,
                    config.LastVerificationScriptPath,
                    config.PendingVerificationSince,
                    config.PendingVerificationProfile,
                    config.LastVerifiedProfile,
                    config.LastVerificationResult
                };
                var json = JsonSerializer.Serialize(toSave, JsonOptions);

                // Atomic write: write+flush to a temp file, then rename. The Flush(true) is
                // what makes this crash-safe — without it, File.WriteAllText returns before
                // the data hits the disk and a hard reset can leave a zero-byte config behind.
                using (var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var sw = new StreamWriter(fs, new System.Text.UTF8Encoding(false)))
                {
                    sw.Write(json);
                    sw.Flush();
                    fs.Flush(flushToDisk: true);
                }
                File.Move(tempFile, config.ConfigFile, overwrite: true);
                return;
            }
            catch (IOException ex)
            {
                lastException = ex;
                // Antivirus / file-explorer locks are transient — brief backoff and retry.
                try { System.Threading.Thread.Sleep(100); } catch { }
            }
            catch (UnauthorizedAccessException ex)
            {
                lastException = ex;
                try { System.Threading.Thread.Sleep(100); } catch { }
            }
            catch (Exception ex)
            {
                // Non-IO failures (serialization, invalid config) won't improve with retry.
                lastException = ex;
                break;
            }
        }

        // Clean up any stale temp file so the workspace doesn't accumulate .tmp files.
        try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }

        // Surface the failure to the Application event log so users / support can see that
        // a save actually failed instead of wondering why their settings disappeared. Guarded
        // so a misconfigured event source can't cascade into an exception here.
        try
        {
            EventLogService.Write(
                $"Failed to save config.json after 5 attempts: {lastException?.GetType().Name}: {lastException?.Message}",
                System.Diagnostics.EventLogEntryType.Warning,
                3010);
        }
        catch { }
    }

    internal sealed class LenientPatchProfileJsonConverter : JsonConverter<PatchProfile>
    {
        public override PatchProfile Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                var text = reader.GetString();
                if (Enum.TryParse<PatchProfile>(text, ignoreCase: true, out var parsed) &&
                    Enum.IsDefined(typeof(PatchProfile), parsed))
                    return parsed;

                if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric) &&
                    Enum.IsDefined(typeof(PatchProfile), numeric))
                    return (PatchProfile)numeric;

                return PatchProfile.Safe;
            }

            if (reader.TokenType == JsonTokenType.Number &&
                reader.TryGetInt32(out var value) &&
                Enum.IsDefined(typeof(PatchProfile), value))
                return (PatchProfile)value;

            try { reader.Skip(); } catch { }
            return PatchProfile.Safe;
        }

        public override void Write(
            Utf8JsonWriter writer,
            PatchProfile value,
            JsonSerializerOptions options)
        {
            writer.WriteStringValue(Enum.IsDefined(typeof(PatchProfile), value)
                ? value.ToString()
                : PatchProfile.Safe.ToString());
        }
    }

    internal sealed class LenientThemeModeJsonConverter : JsonConverter<AppThemeMode>
    {
        public override AppThemeMode Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                var text = reader.GetString();
                if (TryParseThemeMode(text, out var parsed))
                    return parsed;

                if (Enum.TryParse<AppThemeMode>(text, ignoreCase: true, out parsed) &&
                    Enum.IsDefined(typeof(AppThemeMode), parsed))
                    return parsed;

                if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric) &&
                    Enum.IsDefined(typeof(AppThemeMode), numeric))
                    return (AppThemeMode)numeric;

                return AppThemeMode.System;
            }

            if (reader.TokenType == JsonTokenType.Number &&
                reader.TryGetInt32(out var value) &&
                Enum.IsDefined(typeof(AppThemeMode), value))
                return (AppThemeMode)value;

            try { reader.Skip(); } catch { }
            return AppThemeMode.System;
        }

        public override void Write(
            Utf8JsonWriter writer,
            AppThemeMode value,
            JsonSerializerOptions options)
        {
            writer.WriteStringValue(Enum.IsDefined(typeof(AppThemeMode), value)
                ? value.ToString()
                : AppThemeMode.System.ToString());
        }

        private static bool TryParseThemeMode(string? text, out AppThemeMode mode)
        {
            mode = AppThemeMode.System;
            if (string.IsNullOrWhiteSpace(text))
                return false;

            var normalized = string.Concat(text.Where(char.IsLetterOrDigit));
            if (Enum.TryParse<AppThemeMode>(normalized, ignoreCase: true, out var parsed) &&
                Enum.IsDefined(typeof(AppThemeMode), parsed))
            {
                mode = parsed;
                return true;
            }

            if (normalized.Equals("AccessibleContrast", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("Contrast", StringComparison.OrdinalIgnoreCase))
            {
                mode = AppThemeMode.HighContrast;
                return true;
            }

            return false;
        }
    }
}
