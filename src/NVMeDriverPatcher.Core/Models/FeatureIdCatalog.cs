using System.Text.Json.Serialization;

namespace NVMeDriverPatcher.Models;

public enum CuratedFeatureDefaultState
{
    Unknown,
    AlwaysEnabled,
    EnabledByDefault,
    DisabledByDefault,
    AlwaysDisabled,
}

public sealed class FeatureIdCatalog
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 1;

    [JsonPropertyName("updated")]
    public string Updated { get; set; } = string.Empty;

    [JsonPropertyName("sourceUrl")]
    public string SourceUrl { get; set; } = string.Empty;

    [JsonPropertyName("branches")]
    public List<FeatureIdBranch> Branches { get; set; } = [];
}

public sealed class FeatureIdBranch
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("minBuild")]
    public int MinBuild { get; set; }

    [JsonPropertyName("maxBuild")]
    public int MaxBuild { get; set; } = int.MaxValue;

    [JsonPropertyName("minUbr")]
    public int MinUbr { get; set; }

    [JsonPropertyName("maxUbr")]
    public int MaxUbr { get; set; } = int.MaxValue;

    [JsonPropertyName("appliesTo")]
    public string AppliesTo { get; set; } = string.Empty;

    [JsonPropertyName("fallbackSet")]
    public string FallbackSet { get; set; } = string.Empty;

    [JsonPropertyName("confidence")]
    public string Confidence { get; set; } = string.Empty;

    [JsonPropertyName("sourceUrl")]
    public string SourceUrl { get; set; } = string.Empty;

    [JsonPropertyName("lastReviewed")]
    public string LastReviewed { get; set; } = string.Empty;

    [JsonPropertyName("features")]
    public List<CuratedFeature> Features { get; set; } = [];
}

public sealed class CuratedFeature
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("id")]
    public int Id { get; set; }

    // Kept as the source's human-readable class so the JSON remains auditable without
    // requiring a reader to decode enum integers.
    [JsonPropertyName("defaultState")]
    public string DefaultState { get; set; } = string.Empty;

    // Apply is an explicit product decision: an overridable feature may still be probe-only
    // until live validation proves that writing it is safe and useful.
    [JsonPropertyName("apply")]
    public bool Apply { get; set; }

    [JsonPropertyName("probeOnly")]
    public bool ProbeOnly { get; set; }

    [JsonPropertyName("candidate")]
    public bool Candidate { get; set; }

    [JsonIgnore]
    public CuratedFeatureDefaultState ParsedDefaultState => ParseDefaultState(DefaultState);

    [JsonIgnore]
    public bool IsAlwaysDisabled => ParsedDefaultState == CuratedFeatureDefaultState.AlwaysDisabled;

    [JsonIgnore]
    public string DefaultStateLabel => ParsedDefaultState switch
    {
        CuratedFeatureDefaultState.AlwaysEnabled => "Always Enabled",
        CuratedFeatureDefaultState.EnabledByDefault => "Enabled By Default",
        CuratedFeatureDefaultState.DisabledByDefault => "Disabled By Default",
        CuratedFeatureDefaultState.AlwaysDisabled => "Always Disabled",
        _ => "Unknown",
    };

    public static CuratedFeatureDefaultState ParseDefaultState(string? value)
    {
        var normalized = new string((value ?? string.Empty)
            .Where(char.IsLetter)
            .ToArray())
            .ToLowerInvariant();

        return normalized switch
        {
            "alwaysenabled" => CuratedFeatureDefaultState.AlwaysEnabled,
            "enabledbydefault" => CuratedFeatureDefaultState.EnabledByDefault,
            "disabledbydefault" => CuratedFeatureDefaultState.DisabledByDefault,
            "alwaysdisabled" => CuratedFeatureDefaultState.AlwaysDisabled,
            _ => CuratedFeatureDefaultState.Unknown,
        };
    }
}

public sealed record FeatureRouteAssessment(
    string FeatureName,
    string Branch,
    bool BranchKnown,
    bool FeatureKnown,
    int? FeatureId,
    string DefaultState,
    string SourceUrl)
{
    public bool IsAlwaysDisabled => FeatureKnown &&
        string.Equals(DefaultState, "Always Disabled", StringComparison.Ordinal);

    public string Detail
    {
        get
        {
            if (!BranchKnown)
                return $"{FeatureName}: UNKNOWN — no curated feature branch is available.";

            if (!FeatureKnown)
                return $"{FeatureName}: UNKNOWN — the feature is absent from curated branch '{Branch}'.";

            if (IsAlwaysDisabled)
                return $"{FeatureName} ({FeatureId}): ALWAYS DISABLED on curated branch '{Branch}' — no override route is known.";

            return $"{FeatureName} ({FeatureId}): {DefaultState} on curated branch '{Branch}'.";
        }
    }
}
