using NVMeDriverPatcher.Services;

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

/// <summary>
/// One registry override value shown beside the FeatureStore ID the curated catalog lists for the
/// same feature name on this branch. The two are separate numbering schemes: the Policies override
/// value names (735209102 and the rest, plus Microsoft's own Server key 1176759950) appear in no
/// velocity dump on any branch, and the registry route enabled the driver on 26100 builds whose
/// FeatureStore ID for the same feature was 60786016. They are never expected to match.
/// </summary>
public sealed record RegistryOverrideFeatureAssessment(
    string RegistryId,
    string FeatureName,
    int? KnownBranchId)
{
    public string Detail => KnownBranchId is null
        ? $"{RegistryId} ({FeatureName}): registry override ID; no FeatureStore ID is listed for this feature on this branch."
        : $"{RegistryId} ({FeatureName}): registry override ID; FeatureStore ID on this branch is {KnownBranchId}.";
}

public sealed record RegistryOverrideAssessment(
    int BuildNumber,
    int Ubr,
    string Branch,
    bool BranchKnown,
    IReadOnlyList<RegistryOverrideFeatureAssessment> Features)
{
    public string Summary => !BranchKnown
        ? $"Registry override IDs: Windows build unavailable, so this branch's FeatureStore IDs can't be listed beside the {Features.Count} registry ID(s)."
        : $"Registry override IDs for Windows build {BuildNumber}.{Ubr} ({Branch}): {Features.Count} listed beside this branch's FeatureStore IDs. The two use separate numbering, so they don't match by design.";
}

/// <summary>
/// Single source of truth for every known fallback feature-ID set. Microsoft has rotated
/// these once already (the March 2026 block) and community reports show newer 25H2 builds
/// moved again — every UI string, CLI message, ViVeTool invocation, and FeatureStore
/// evidence probe must derive from the reviewed feature_ids.json catalog instead of a
/// build-number heuristic or duplicated IDs.
/// </summary>
public static class FallbackFeatureCatalog
{
    public const int CandidateSecondGateId = 48613417;
    public const string CandidateSecondGateName = "NativeNVMeStackEnableForClientOS";
    public const string CandidateSecondGateSourceUrl =
        "https://github.com/phantomofearth/windows-velocity-feature-lists";

    private const string PostBlockSetName = "post-block-2026-03";
    private const string NativeNvmeSetName = "native-nvme-stack-25h2";
    private static readonly FeatureIdCatalog BundledCatalog = FeatureIdCatalogService.LoadBundledCatalog();

    /// <summary>The set the community adopted after the Feb/Mar 2026 registry-override
    /// block. Verified on the sampled 26100.8687 branch.</summary>
    public static FallbackIdSet PostBlockMarch2026 { get; } =
        FeatureIdCatalogService.GetAppliedSetByName(BundledCatalog, PostBlockSetName);

    /// <summary>Newer 25H2 and later applied set, resolved from the sampled 26404.5000 and
    /// 29531.1000 branches. 49453572 is Always Enabled in those branches and remains probe-only.</summary>
    public static FallbackIdSet NativeNvmeStack25H2 { get; } =
        FeatureIdCatalogService.GetAppliedSetByName(BundledCatalog, NativeNvmeSetName);

    /// <summary>IDs recognized by evidence probes but deliberately excluded from every
    /// applied set because the sampled velocity dumps mark them Always Enabled.</summary>
    public static IReadOnlyList<int> ProbeOnlyIds { get; } =
        FeatureIdCatalogService.GetProbeOnlyIds(BundledCatalog);

    /// <summary>Candidate IDs queried for diagnostics only. These are not fallback evidence
    /// IDs and must never be passed to an apply/reset operation without live validation.</summary>
    public static IReadOnlyList<int> CandidateProbeIds { get; } =
        FeatureIdCatalogService.GetCandidateIds(BundledCatalog);

    public static IReadOnlyList<FallbackIdSet> All { get; } =
        FeatureIdCatalogService.GetAppliedSets(BundledCatalog);

    /// <summary>Union of every applied set plus probe-only IDs — what evidence probes must
    /// scan so a fallback applied by ANY known set (or by the user running ViVeTool by hand
    /// from a forum guide) is still recognized. Candidate IDs remain a separate diagnostic set.</summary>
    public static IReadOnlyList<int> AllKnownIds { get; } =
        FeatureIdCatalogService.GetKnownIds(BundledCatalog);

    /// <summary>Returns the curated velocity-dump name-to-ID map for a build branch.</summary>
    public static IReadOnlyDictionary<string, int> GetKnownRegistryFeatureIdsForBuild(
        int buildNumber,
        int? ubr = null) =>
        FeatureIdCatalogService.GetKnownFeatureIds(BundledCatalog, buildNumber, ubr);

    /// <summary>
    /// Lists the IDs the registry route would write beside the curated FeatureStore IDs for the
    /// same feature names on the detected branch. Information only: the two are separate numbering
    /// schemes, and this never changes the IDs in <see cref="AppConfig"/> or the mutation payload.
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
                    null)).ToArray());
        }

        var knownIds = GetKnownRegistryFeatureIdsForBuild(buildDetails.BuildNumber, buildDetails.UBR);
        var features = ids.Select(id =>
        {
            var name = GetFeatureName(id);
            var hasKnownId = knownIds.TryGetValue(name, out var knownId);
            return new RegistryOverrideFeatureAssessment(
                id,
                name,
                hasKnownId ? knownId : null);
        }).ToArray();

        var branch = FeatureIdCatalogService.ResolveBranch(
            BundledCatalog,
            buildDetails.BuildNumber,
            buildDetails.UBR);
        return new RegistryOverrideAssessment(
            buildDetails.BuildNumber,
            buildDetails.UBR,
            branch?.AppliesTo ?? "unknown branch",
            branch is not null,
            features);
    }

    public static FeatureRouteAssessment AssessFeatureRoute(
        WindowsBuildDetails? buildDetails,
        string featureName) =>
        FeatureIdCatalogService.AssessFeature(BundledCatalog, buildDetails?.BuildNumber ?? -1,
            buildDetails?.UBR, featureName);

    private static string GetFeatureName(string id)
    {
        if (!AppConfig.FeatureNames.TryGetValue(id, out var displayName))
            return "Unknown feature";
        var descriptionStart = displayName.IndexOf(" (", StringComparison.Ordinal);
        return descriptionStart > 0 ? displayName[..descriptionStart] : displayName;
    }

    /// <summary>Build-gated selection resolved from the curated branch ranges and UBR rows.</summary>
    public static FallbackIdSet SelectForBuild(
        int buildNumber,
        int? ubr = null,
        string? workingDir = null)
    {
        var selected = FeatureIdCatalogService.SelectAppliedSet(buildNumber, ubr, workingDir);
        return selected.Ids.Count > 0 ? selected : PostBlockMarch2026;
    }
}
