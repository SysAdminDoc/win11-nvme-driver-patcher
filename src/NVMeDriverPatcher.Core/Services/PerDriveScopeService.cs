using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using NVMeDriverPatcher.Models;

namespace NVMeDriverPatcher.Services;

public class PerDriveScopeConfig
{
    // Legacy wire fields retained only so old drive_scope.json files can be detected and their
    // unenforced intent explained. They never authorize a per-device binding change.
    public List<string> ExcludedSerials { get; set; } = new();

    // Legacy model-pattern intent retained for detection only. It never narrows the global
    // mutation scope or changes device binding.
    public List<string> ExcludedModelPatterns { get; set; } = new();

    public bool Enabled { get; set; }

    [JsonIgnore] public bool LegacyFilePresent { get; set; }
    [JsonIgnore] public string? LoadError { get; set; }
}

public class PerDriveDecision
{
    public string Serial { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public bool Include { get; set; } = true;
    public bool LegacyExclusionRequested { get; set; }
    public string Reason { get; set; } = string.Empty;
}

// Legacy compatibility reader. Earlier releases described drive_scope.json as a per-drive
// exclusion mechanism, but no call path ever changed driver selection: the feature/registry
// mutation is machine-wide. Preserve and report old intent without inventing an unsupported
// UpperFilters or forced-binding workaround.
public static class PerDriveScopeService
{
    private const string ScopeFile = "drive_scope.json";

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
            var scope = string.IsNullOrWhiteSpace(json)
                ? new PerDriveScopeConfig()
                : JsonSerializer.Deserialize<PerDriveScopeConfig>(json, JsonOptions) ?? new PerDriveScopeConfig();
            scope.LegacyFilePresent = true;
            return scope;
        }
        catch (Exception ex)
        {
            return new PerDriveScopeConfig
            {
                LegacyFilePresent = true,
                LoadError = $"{ex.GetType().Name}: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Reports legacy exclusion matches while keeping every drive in the machine-wide scope.
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
                Reason = "Global feature/driver selection includes this drive."
            };
            if (!scope.Enabled)
            {
                decisions.Add(decision);
                continue;
            }
            if (!string.IsNullOrWhiteSpace(serial) &&
                scope.ExcludedSerials.Any(s => string.Equals(s, serial, StringComparison.OrdinalIgnoreCase)))
            {
                decision.LegacyExclusionRequested = true;
                decision.Reason = $"Legacy drive_scope.json requested exclusion by serial ({serial}), but that preference was never enforced; this drive remains in the global swap.";
            }
            else if (!string.IsNullOrWhiteSpace(model) &&
                     scope.ExcludedModelPatterns.Any(p => !string.IsNullOrWhiteSpace(p) && model.Contains(p, StringComparison.OrdinalIgnoreCase)))
            {
                decision.LegacyExclusionRequested = true;
                var matched = scope.ExcludedModelPatterns.First(p => model.Contains(p, StringComparison.OrdinalIgnoreCase));
                decision.Reason = $"Legacy drive_scope.json requested exclusion by model pattern ({matched}), but that preference was never enforced; this drive remains in the global swap.";
            }
            decisions.Add(decision);
        }
        return decisions;
    }

    /// <summary>
    /// Returns an honest global-scope summary and warns when a legacy file requested exclusions.
    /// </summary>
    public static string Summarize(List<PerDriveDecision> decisions, PerDriveScopeConfig? scope = null)
    {
        int total = decisions.Count;
        int requested = decisions.Count(d => d.LegacyExclusionRequested);
        if (scope?.LegacyFilePresent == true && !string.IsNullOrWhiteSpace(scope.LoadError))
            return $"WARNING: legacy drive_scope.json was detected but could not be read ({scope.LoadError}). It is not enforced; the mutation remains machine-wide across all {total} detected NVMe drive(s).";
        if (requested == 0)
            return scope?.LegacyFilePresent == true
                ? $"NOTICE: legacy drive_scope.json was detected, but it is not enforced. The mutation is machine-wide and all {total} detected NVMe drive(s) remain in scope."
                : $"Global scope: all {total} detected NVMe drive(s) are eligible for the same Windows driver selection; no per-drive exclusion is enforced.";
        return $"WARNING: drive_scope.json requested {requested} per-drive exclusion(s), but they were never enforced. The mutation is machine-wide and all {total} detected NVMe drive(s) remain in scope.";
    }
}
