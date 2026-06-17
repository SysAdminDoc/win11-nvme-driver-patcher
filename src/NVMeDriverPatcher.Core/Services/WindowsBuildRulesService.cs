using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using NVMeDriverPatcher.Models;

namespace NVMeDriverPatcher.Services;

public class WindowsBuildRule
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    // null = applies to both client and server SKUs.
    [JsonPropertyName("server")]
    public bool? Server { get; set; }

    [JsonPropertyName("minBuild")]
    public int MinBuild { get; set; }

    [JsonPropertyName("maxBuild")]
    public int MaxBuild { get; set; } = int.MaxValue;

    [JsonPropertyName("minUbr")]
    public int MinUbr { get; set; }

    [JsonPropertyName("maxUbr")]
    public int MaxUbr { get; set; } = int.MaxValue;

    // registry-override | vivetool-fallback | none-known | official-optin
    [JsonPropertyName("expectedPath")]
    public string ExpectedPath { get; set; } = string.Empty;

    // Optional FallbackFeatureCatalog set name this rule's fallback expects.
    [JsonPropertyName("fallbackSet")]
    public string? FallbackSet { get; set; }

    [JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;

    [JsonPropertyName("sourceUrl")]
    public string SourceUrl { get; set; } = string.Empty;

    [JsonPropertyName("confidence")]
    public string Confidence { get; set; } = string.Empty;

    [JsonPropertyName("lastReviewed")]
    public string LastReviewed { get; set; } = string.Empty;
}

public class WindowsBuildRuleset
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 1;

    [JsonPropertyName("updated")]
    public string Updated { get; set; } = string.Empty;

    [JsonPropertyName("rules")]
    public List<WindowsBuildRule> Rules { get; set; } = new();
}

// Per-build enablement intelligence (AR-2026-006). Microsoft changes the native-NVMe
// client gating out-of-band — registry route neutered Feb/Mar 2026, fallback IDs rotated
// on 25H2, bind path removed on 26200.8524+ — so the expected behavior lives in an
// updatable data file (windows_build_rules.json) instead of scattered hardcoded copy.
// First matching rule wins; rules are ordered most-specific first in the file. A
// user-edited copy in the working dir takes precedence over the bundled default,
// mirroring compat.json.
public static class WindowsBuildRulesService
{
    private const string BundledRulesFile = "windows_build_rules.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static WindowsBuildRuleset LoadRuleset(string? workingDir = null)
    {
        var candidates = new List<string>();
        var workDir = workingDir ?? AppConfig.GetWorkingDir();
        if (!string.IsNullOrEmpty(workDir)) candidates.Add(Path.Combine(workDir, BundledRulesFile));
        try
        {
            var appDir = AppContext.BaseDirectory;
            if (!string.IsNullOrEmpty(appDir)) candidates.Add(Path.Combine(appDir, BundledRulesFile));
        }
        catch { }

        foreach (var path in candidates)
        {
            try
            {
                if (!File.Exists(path)) continue;
                var json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json)) continue;
                var rules = JsonSerializer.Deserialize<WindowsBuildRuleset>(json, JsonOptions);
                if (rules is not null && rules.Rules.Count > 0) return rules;
            }
            catch { /* try next candidate */ }
        }

        return new WindowsBuildRuleset();
    }

    /// <summary>
    /// First matching rule for (build, ubr, server-ness), or null when no rule matches —
    /// callers must render conservative "unknown build behavior" guidance for null, never
    /// false confidence.
    /// </summary>
    public static WindowsBuildRule? Match(WindowsBuildRuleset ruleset, int buildNumber, int ubr, bool isServer)
    {
        foreach (var rule in ruleset.Rules)
        {
            if (rule.Server is bool srv && srv != isServer) continue;
            if (buildNumber < rule.MinBuild || buildNumber > rule.MaxBuild) continue;
            if (ubr < rule.MinUbr || ubr > rule.MaxUbr) continue;
            return rule;
        }
        return null;
    }

    /// <summary>Convenience: match against the live system.</summary>
    public static WindowsBuildRule? MatchCurrent(string? workingDir = null)
    {
        try
        {
            var build = DriveService.GetWindowsBuildDetails();
            if (build is null) return null;
            bool isServer = (build.Caption ?? string.Empty).Contains("Server", StringComparison.OrdinalIgnoreCase);
            return Match(LoadRuleset(workingDir), build.BuildNumber, build.UBR, isServer);
        }
        catch { return null; }
    }

    public static string Describe(WindowsBuildRule? rule) =>
        rule is null
            ? "No rule matches this Windows build — behavior unknown. Proceed conservatively and share diagnostics."
            : $"[{rule.Id}] {rule.Summary} (expected path: {rule.ExpectedPath}; {rule.Confidence}, reviewed {rule.LastReviewed})";
}
