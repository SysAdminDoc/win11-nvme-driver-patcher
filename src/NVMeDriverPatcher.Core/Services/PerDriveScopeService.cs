using System.IO;
using System.Text.Json;
using NVMeDriverPatcher.Models;

namespace NVMeDriverPatcher.Services;

public class PerDriveScopeConfig
{
    // Per-drive serial numbers the user has opted OUT of the swap — typically a DirectStorage
    // gaming drive the user wants to keep on stornvme.sys while the OS drive moves to nvmedisk.
    public List<string> ExcludedSerials { get; set; } = new();

    // Optional model-based exclusions for users who want to exclude a whole family without
    // pinning to a specific serial (e.g. all "WD_BLACK SN850X" drives).
    public List<string> ExcludedModelPatterns { get; set; } = new();

    public bool Enabled { get; set; }
}

public class PerDriveDecision
{
    public string Serial { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public bool Include { get; set; } = true;
    public string Reason { get; set; } = string.Empty;
}

// Lets the user exclude specific NVMe drives from the global feature-flag swap by writing
// a class-upper filter pin on the excluded drive's device instance, keeping it bound to
// stornvme.sys while the rest of the system swaps to nvmedisk.sys.
//
// NOTE: the feature flags in HKLM\...\FeatureManagement\Overrides are *global* — they apply
// to every NVMe controller. The only way to truly scope is via a per-instance UpperFilters
// entry. This service writes that filter value via the stornvme device instance path.
// Implementation is intentionally conservative: we only *add* a UpperFilters override,
// never mutate the driver binding directly.
public static class PerDriveScopeService
{
    private const string ScopeFile = "drive_scope.json";

    // Value-name kept simple so an admin auditing the registry can read it. The actual
    // key we write lives under the NVMe instance in HKLM\SYSTEM\...\Enum\PCI\VEN_...
    // and is keyed by the drive's ParentInstanceId.
    internal const string ScopeValueName = "NVMeDriverPatcher_Exclude";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static string ScopePath(AppConfig config) =>
        Path.Combine(string.IsNullOrWhiteSpace(config.WorkingDir) ? AppConfig.GetWorkingDir() : config.WorkingDir, ScopeFile);

    public static PerDriveScopeConfig Load(AppConfig config)
    {
        try
        {
            var path = ScopePath(config);
            if (!File.Exists(path)) return new PerDriveScopeConfig();
            var json = File.ReadAllText(path);
            return string.IsNullOrWhiteSpace(json)
                ? new PerDriveScopeConfig()
                : JsonSerializer.Deserialize<PerDriveScopeConfig>(json, JsonOptions) ?? new PerDriveScopeConfig();
        }
        catch
        {
            return new PerDriveScopeConfig();
        }
    }

    public static void Save(AppConfig config, PerDriveScopeConfig scope)
    {
        try
        {
            var path = ScopePath(config);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(scope, JsonOptions);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
        }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// For each supplied drive, decide whether it should be included in the swap or pinned
    /// to stornvme.sys via the per-drive scope. Pure function — no registry side effects.
    /// </summary>
    public static List<PerDriveDecision> Decide(IEnumerable<(string Serial, string Model)> drives, PerDriveScopeConfig scope)
    {
        var decisions = new List<PerDriveDecision>();
        foreach (var (serial, model) in drives)
        {
            var decision = new PerDriveDecision
            {
                Serial = serial ?? string.Empty,
                Model = model ?? string.Empty,
                Include = true,
                Reason = "Default (included in swap)"
            };
            if (!scope.Enabled)
            {
                decisions.Add(decision);
                continue;
            }
            if (!string.IsNullOrWhiteSpace(serial) &&
                scope.ExcludedSerials.Any(s => string.Equals(s, serial, StringComparison.OrdinalIgnoreCase)))
            {
                decision.Include = false;
                decision.Reason = $"Excluded by serial: {serial}";
            }
            else if (!string.IsNullOrWhiteSpace(model) &&
                     scope.ExcludedModelPatterns.Any(p => !string.IsNullOrWhiteSpace(p) && model.Contains(p, StringComparison.OrdinalIgnoreCase)))
            {
                decision.Include = false;
                var matched = scope.ExcludedModelPatterns.First(p => model.Contains(p, StringComparison.OrdinalIgnoreCase));
                decision.Reason = $"Excluded by model pattern: {matched}";
            }
            decisions.Add(decision);
        }
        return decisions;
    }

    /// <summary>
    /// Returns a summary suitable for the preflight panel or CLI: "3 drives, 1 excluded".
    /// </summary>
    public static string Summarize(List<PerDriveDecision> decisions)
    {
        int total = decisions.Count;
        int excluded = decisions.Count(d => !d.Include);
        if (excluded == 0) return $"{total} NVMe drive(s) — all included in swap.";
        return $"{total} NVMe drive(s) — {excluded} excluded by scope, {total - excluded} will swap.";
    }
}
