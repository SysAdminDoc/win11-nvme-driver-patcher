using System.IO;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using NVMeDriverPatcher.Models;

namespace NVMeDriverPatcher.Services;

public static partial class ConfigService
{
    private const string LegacyMigrationMarker = "localappdata_migrated.flag";

    private static readonly string[] LegacyMigrationFiles =
    [
        "config.json",
        "config.json.bak",
        "config.json.corrupt",
        "drive_scope.json",
        "maintenance_window.json",
        "firmware_update_pending.json",
        "baseline.json",
        "benchmark_results.json",
        "compat.json",
        "compat_report.json",
        "anon_id.txt",
        "nvmepatcher.db",
        "nvmepatcher.db-wal",
        "nvmepatcher.db-shm"
    ];

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


    internal static int MigrateLegacyWorkingDirIfNeeded(string targetDir, string? legacyDir)
    {
        if (string.IsNullOrWhiteSpace(targetDir) ||
            string.IsNullOrWhiteSpace(legacyDir) ||
            AppConfig.PathsEqual(targetDir, legacyDir) ||
            !Directory.Exists(legacyDir))
        {
            return 0;
        }

        var marker = Path.Combine(targetDir, LegacyMigrationMarker);
        if (File.Exists(marker))
            return 0;

        int copied = 0;
        foreach (var fileName in LegacyMigrationFiles)
        {
            try
            {
                var source = Path.Combine(legacyDir, fileName);
                var destination = Path.Combine(targetDir, fileName);
                if (!File.Exists(source) || File.Exists(destination))
                    continue;

                Directory.CreateDirectory(targetDir);
                File.Copy(source, destination, overwrite: false);
                copied++;
            }
            catch
            {
                // Best-effort compatibility bridge. A failed copy must not block startup.
            }
        }

        try
        {
            Directory.CreateDirectory(targetDir);
            File.WriteAllText(marker, $"Migrated legacy LocalAppData state at {DateTime.UtcNow:O}.");
        }
        catch
        {
            // Marker is advisory; failed marker writes should not block app startup.
        }

        return copied;
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
