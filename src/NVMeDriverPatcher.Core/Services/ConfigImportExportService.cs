using System.IO;
using System.Text;
using System.Text.Json;
using NVMeDriverPatcher.Models;

namespace NVMeDriverPatcher.Services;

// Export a portable config bundle (AppConfig JSON + watchdog.json) and import one on another
// machine. Legacy DriveScope members are accepted but ignored because driver selection is global.
// Separate from the support-bundle
// ZIP — that one is for diagnostics, this one is for fleet cloning.
public static class ConfigImportExportService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    // Highest bundle schema this build understands. A future bundle (higher number) is rejected
    // rather than silently partially-applied. RestartDelay bound matches the AppConfig clamp.
    internal const int CurrentSchemaVersion = 2;
    internal const int MaxRestartDelaySeconds = 3600;

    public class Bundle
    {
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        public string ExportedAt { get; set; } = DateTime.UtcNow.ToString("o");
        public AppConfig? Config { get; set; }
        public WatchdogState? Watchdog { get; set; }
    }

    public static string Export(AppConfig config, string outputPath)
    {
        var bundle = new Bundle
        {
            Config = config,
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

            // Validate the wire format BEFORE binding/mutating: an unknown schema, an
            // out-of-range RestartDelay (flows into `shutdown /r /t`), or an undefined
            // PatchProfile must fail cleanly without touching the live config.
            var (ok, error) = ValidateBundleJson(json);
            if (!ok) return (false, error);

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
            if (bundle.Watchdog is not null)
            {
                var watchdogSave = EventLogWatchdogService.SaveState(config, bundle.Watchdog);
                if (!watchdogSave.Success)
                    return (false, $"Config bundle was only partially imported: watchdog state was not persisted ({watchdogSave.Summary}).");
            }
            using var raw = JsonDocument.Parse(json);
            bool ignoredLegacyScope = raw.RootElement.TryGetProperty("DriveScope", out var legacyScope)
                                      && legacyScope.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined;
            return (true, ignoredLegacyScope
                ? "Config bundle imported. Legacy DriveScope preferences were ignored because the native NVMe mutation is machine-wide and no per-drive exclusion is enforced."
                : "Config bundle imported.");
        }
        catch (Exception ex)
        {
            return (false, $"Import failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Pure wire-format validation. Validates against the raw JSON (not the bound objects) so a
    /// negative RestartDelay is observable before the AppConfig setter clamps it. Rejects unknown
    /// schema versions, out-of-range RestartDelay, and undefined PatchProfile values.
    /// </summary>
    internal static (bool Ok, string Error) ValidateBundleJson(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { return (false, $"Bundle is not valid JSON: {ex.Message}"); }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return (false, "Bundle root is not a JSON object.");

            int schema = 1; // absent version means a legacy v1 bundle
            if (root.TryGetProperty("SchemaVersion", out var sv) && sv.ValueKind == JsonValueKind.Number)
                schema = sv.GetInt32();
            if (schema < 1 || schema > CurrentSchemaVersion)
                return (false, $"Unsupported bundle SchemaVersion {schema} (this build supports up to {CurrentSchemaVersion}).");

            if (root.TryGetProperty("Config", out var cfg) && cfg.ValueKind == JsonValueKind.Object)
            {
                if (cfg.TryGetProperty("RestartDelay", out var rd) && rd.ValueKind == JsonValueKind.Number)
                {
                    if (!rd.TryGetInt32(out var delay) || delay < 0 || delay > MaxRestartDelaySeconds)
                        return (false, $"Bundle RestartDelay is out of range (0-{MaxRestartDelaySeconds}s).");
                }

                if (cfg.TryGetProperty("PatchProfile", out var pp))
                {
                    if (pp.ValueKind == JsonValueKind.String)
                    {
                        if (!Enum.TryParse<PatchProfile>(pp.GetString(), ignoreCase: true, out var parsed)
                            || !Enum.IsDefined(typeof(PatchProfile), parsed))
                            return (false, $"Bundle PatchProfile '{pp.GetString()}' is not a recognized value.");
                    }
                    else if (pp.ValueKind == JsonValueKind.Number)
                    {
                        if (!pp.TryGetInt32(out var pv) || !Enum.IsDefined(typeof(PatchProfile), pv))
                            return (false, $"Bundle PatchProfile '{(pp.TryGetInt32(out var n) ? n.ToString() : "?")}' is not a recognized value.");
                    }
                }
            }

            return (true, string.Empty);
        }
    }
}
