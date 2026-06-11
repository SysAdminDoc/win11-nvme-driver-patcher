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
/// Single source of truth for every known fallback feature-ID set. Microsoft has rotated
/// these once already (the March 2026 block) and community reports show newer 25H2 builds
/// moved again — every UI string, CLI message, ViVeTool invocation, and FeatureStore
/// evidence probe must derive from here instead of hardcoding IDs (previously duplicated
/// across 8+ files).
/// </summary>
public static class FallbackFeatureCatalog
{
    /// <summary>The set the community adopted after the Feb/Mar 2026 registry-override
    /// block. Verified working on 24H2 and early-25H2 builds (Tom's Hardware /
    /// HotHardware, Mar 2026; still confirmed early June 2026).</summary>
    public static FallbackIdSet PostBlockMarch2026 { get; } = new(
        "post-block-2026-03",
        new[] { 60786016, 48433719 },
        "Windows 11 builds below 26200",
        "verified");

    /// <summary>Newer 25H2 (26200.x) set: 55369237 ("Native NVMe Stack") reportedly
    /// REPLACES 60786016 — one community report says 60786016 no longer exists on recent
    /// stable builds — used with 48433719 ("UX Acceleration") and 49453572 ("Standalone
    /// Future"). Community-reported (elevenforum 46678, windowsforum 406833); needs live
    /// validation on a 26200.8xxx system.</summary>
    public static FallbackIdSet NativeNvmeStack25H2 { get; } = new(
        "native-nvme-stack-25h2",
        new[] { 55369237, 48433719, 49453572 },
        "Windows 11 builds 26200 and later",
        "community-reported");

    public static IReadOnlyList<FallbackIdSet> All { get; } =
        new[] { PostBlockMarch2026, NativeNvmeStack25H2 };

    /// <summary>Union of every ID across all known sets — what evidence probes must scan
    /// for, so a fallback applied by ANY known set (or by the user running ViVeTool by
    /// hand from a forum guide) is still recognized.</summary>
    public static IReadOnlyList<int> AllKnownIds { get; } =
        All.SelectMany(s => s.Ids).Distinct().OrderBy(i => i).ToArray();

    /// <summary>Build-gated selection: which set to APPLY on a given build.</summary>
    public static FallbackIdSet SelectForBuild(int buildNumber) =>
        buildNumber >= 26200 ? NativeNvmeStack25H2 : PostBlockMarch2026;
}
