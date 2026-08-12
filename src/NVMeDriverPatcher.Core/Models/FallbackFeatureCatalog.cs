namespace NVMeDriverPatcher.Models;

/// <summary>A named set of ViVeTool/FeatureStore fallback feature IDs with provenance.</summary>
public sealed record FallbackIdSet(
    string Name,
    IReadOnlyList<int> Ids,
    string AppliesTo,
    string Confidence)
{
    /// <summary>Prose form for dialogs: "55369237, 48433719 and 49453572".</summary>
    public string IdsDisplay =>
        Ids.Count <= 1 ? string.Join("", Ids)
        : string.Join(", ", Ids.Take(Ids.Count - 1)) + " and " + Ids[^1];
}

public sealed record RegistryOverrideFeatureAssessment(
    string RegistryId,
    string FeatureName,
    int? KnownBranchId,
    bool MatchesKnownFeature)
{
    public string Detail => KnownBranchId is null
        ? $"{RegistryId} ({FeatureName}): UNKNOWN — no curated branch ID is available."
        : MatchesKnownFeature
            ? $"{RegistryId} ({FeatureName}): MATCH — this is the known branch ID."
            : $"{RegistryId} ({FeatureName}): MISMATCH — this branch lists {KnownBranchId} for {FeatureName}.";
}

public sealed record RegistryOverrideAssessment(
    int BuildNumber,
    int Ubr,
    string Branch,
    bool BranchKnown,
    IReadOnlyList<RegistryOverrideFeatureAssessment> Features)
{
    public bool HasMismatch => BranchKnown && Features.Any(f =>
        f.KnownBranchId is not null && !f.MatchesKnownFeature);

    public int MismatchCount => Features.Count(f =>
        f.KnownBranchId is not null && !f.MatchesKnownFeature);

    public string Summary
    {
        get
        {
            if (!BranchKnown)
                return $"Registry override IDs: Windows build unavailable; cannot compare {Features.Count} ID(s) to a known branch.";

            var verdict = HasMismatch
                ? $"{MismatchCount} MISMATCH(es)"
                : "all curated IDs match";
            return $"Registry override IDs for Windows build {BuildNumber}.{Ubr} ({Branch}): {verdict}.";
        }
    }
}

/// <summary>
/// Single source of truth for every known fallback feature-ID set. Microsoft has rotated
/// these once already (the March 2026 block) and community reports show newer 25H2 builds
/// moved again — every UI string, CLI message, ViVeTool invocation, and FeatureStore
/// evidence probe must derive from here instead of hardcoding IDs (previously duplicated
/// across 8+ files).
/// </summary>
public static class FallbackFeatureCatalog
{
    // These are the feature names/IDs observed in the sampled velocity dumps. They are used
    // for status and dry-run disclosure only; the registry payload remains AppConfig's legacy
    // write set until the live-hardware question in RESEARCH.md is resolved.
    private static readonly IReadOnlyDictionary<string, int> Pre26200RegistryFeatureIds =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["NativeNVMeStackForGeClient"] = 60786016,
            ["UxAccOptimization"] = 48433719,
            ["Standalone_Future"] = 49453572,
        };

    private static readonly IReadOnlyDictionary<string, int> Post26200RegistryFeatureIds =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["NativeNVMeStackForGeClient"] = 55369237,
            ["UxAccOptimization"] = 48433719,
            ["Standalone_Future"] = 49453572,
        };

    /// <summary>The set the community adopted after the Feb/Mar 2026 registry-override
    /// block. Verified working on 24H2 and early-25H2 builds (Tom's Hardware /
    /// HotHardware, Mar 2026; still confirmed early June 2026).</summary>
    public static FallbackIdSet PostBlockMarch2026 { get; } = new(
        "post-block-2026-03",
        new[] { 60786016, 48433719 },
        "Windows 11 builds below 26200",
        "verified");

    /// <summary>Newer 25H2 (26200.x) applied set: 55369237 ("Native NVMe Stack") reportedly
    /// REPLACES 60786016 — one community report says 60786016 no longer exists on recent
    /// stable builds — used with 48433719 ("UX Acceleration"). 49453572 ("Standalone_Future")
    /// is always enabled in the sampled branches and remains probe-only. Community-reported
    /// (elevenforum 46678, windowsforum 406833); needs live validation on a 26200.8xxx system.</summary>
    public static FallbackIdSet NativeNvmeStack25H2 { get; } = new(
        "native-nvme-stack-25h2",
        new[] { 55369237, 48433719 },
        "Windows 11 builds 26200 and later",
        "community-reported");

    /// <summary>IDs recognized by evidence probes but deliberately excluded from every
    /// applied set because the sampled velocity dumps mark them Always Enabled.</summary>
    public static IReadOnlyList<int> ProbeOnlyIds { get; } = [49453572];

    public static IReadOnlyList<FallbackIdSet> All { get; } =
        new[] { PostBlockMarch2026, NativeNvmeStack25H2 };

    /// <summary>Union of every applied set plus probe-only IDs — what evidence probes must
    /// scan so a fallback applied by ANY known set (or by the user running ViVeTool by hand
    /// from a forum guide) is still recognized.</summary>
    public static IReadOnlyList<int> AllKnownIds { get; } =
        All.SelectMany(s => s.Ids)
            .Concat(ProbeOnlyIds)
            .Distinct()
            .OrderBy(i => i)
            .ToArray();

    /// <summary>Returns the sampled velocity-dump name-to-ID map for a build branch.</summary>
    public static IReadOnlyDictionary<string, int> GetKnownRegistryFeatureIdsForBuild(int buildNumber) =>
        buildNumber >= 26200 ? Post26200RegistryFeatureIds : Pre26200RegistryFeatureIds;

    /// <summary>
    /// Compares the IDs the registry route would write with the current sampled feature names
    /// for the detected branch. This is deliberately diagnostic: it does not change the IDs in
    /// <see cref="AppConfig"/> or authorize a different mutation payload.
    /// </summary>
    public static RegistryOverrideAssessment AssessRegistryOverrides(
        WindowsBuildDetails? buildDetails,
        IEnumerable<string> registryIds)
    {
        var ids = registryIds.Distinct(StringComparer.Ordinal).ToArray();
        if (buildDetails is null || buildDetails.BuildNumber <= 0)
        {
            return new RegistryOverrideAssessment(
                0,
                0,
                "unknown branch",
                false,
                ids.Select(id => new RegistryOverrideFeatureAssessment(
                    id,
                    GetFeatureName(id),
                    null,
                    false)).ToArray());
        }

        var knownIds = GetKnownRegistryFeatureIdsForBuild(buildDetails.BuildNumber);
        var features = ids.Select(id =>
        {
            var name = GetFeatureName(id);
            var hasKnownId = knownIds.TryGetValue(name, out var knownId);
            var matches = hasKnownId && id == knownId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return new RegistryOverrideFeatureAssessment(
                id,
                name,
                hasKnownId ? knownId : null,
                matches);
        }).ToArray();

        var branch = buildDetails.BuildNumber >= 26200
            ? "26200+ sampled branch"
            : "pre-26200 sampled branch";
        return new RegistryOverrideAssessment(
            buildDetails.BuildNumber,
            buildDetails.UBR,
            branch,
            true,
            features);
    }

    private static string GetFeatureName(string id)
    {
        if (!AppConfig.FeatureNames.TryGetValue(id, out var displayName))
            return "Unknown feature";
        var descriptionStart = displayName.IndexOf(" (", StringComparison.Ordinal);
        return descriptionStart > 0 ? displayName[..descriptionStart] : displayName;
    }

    /// <summary>Build-gated selection: which set to APPLY on a given build.</summary>
    public static FallbackIdSet SelectForBuild(int buildNumber) =>
        buildNumber >= 26200 ? NativeNvmeStack25H2 : PostBlockMarch2026;
}
