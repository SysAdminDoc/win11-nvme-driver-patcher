using System.Text.Json;
using NVMeDriverPatcher.Models;

namespace NVMeDriverPatcher.Services;

/// <summary>
/// Loads and resolves the reviewed feature-name/ID/default-state catalog. The velocity-list
/// repository is a source for transcription only; the product ships this curated copy and never
/// downloads feature data at runtime.
/// </summary>
public static class FeatureIdCatalogService
{
    public const string BundledCatalogFile = "feature_ids.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static FeatureIdCatalog LoadCatalog(string? workingDir = null)
    {
        var candidates = new List<string>();
        // A null workingDir means "use the shipped catalog". Runtime callers that support a
        // local override pass the resolved state directory explicitly (ViVeToolService does so
        // after it has already resolved the application working directory).
        var workDir = workingDir;
        if (!string.IsNullOrWhiteSpace(workDir))
        {
            if (AppConfig.IsRuntimeWorkingDirectory(workDir))
            {
                try
                {
                    var access = PrivilegedStateSecurityService.EnsureForMutation(workDir);
                    var localPath = Path.Combine(access.Directory, BundledCatalogFile);
                    if (access.Success && File.Exists(localPath) &&
                        PrivilegedStateSecurityService.ValidateCriticalFile(
                            localPath, StateDirectoryRole.Privileged).Success)
                        candidates.Add(localPath);
                }
                catch { /* fall through to the bundled catalog */ }
            }
            else
            {
                candidates.Add(Path.Combine(workDir, BundledCatalogFile));
            }
        }

        candidates.Add(BundledPath());
        return LoadFirstUsable(candidates);
    }

    /// <summary>Loads only the immutable catalog shipped beside the application.</summary>
    public static FeatureIdCatalog LoadBundledCatalog() => LoadFirstUsable([BundledPath()]);

    /// <summary>Loads a catalog fixture or operator-supplied file for validation tooling.</summary>
    public static FeatureIdCatalog LoadFromPath(string path) =>
        LoadFirstUsable([path]);

    public static FeatureIdBranch? ResolveBranch(
        FeatureIdCatalog catalog,
        int buildNumber,
        int? ubr = null)
    {
        if (catalog is null || buildNumber < 0)
            return null;

        return catalog.Branches
            .Where(branch =>
                buildNumber >= branch.MinBuild && buildNumber <= branch.MaxBuild &&
                (!ubr.HasValue || (ubr.Value >= branch.MinUbr && ubr.Value <= branch.MaxUbr)))
            // Exact sampled builds must win over the compatibility bands. This makes the
            // resolution data-driven while retaining a conservative nearest-branch fallback
            // for builds not present in the three transcribed velocity dumps.
            .OrderBy(branch => BuildRangeWidth(branch))
            .ThenBy(branch => UbrRangeWidth(branch))
            .ThenByDescending(branch => branch.MinBuild)
            .FirstOrDefault();
    }

    public static FallbackIdSet SelectAppliedSet(
        int buildNumber,
        int? ubr = null,
        string? workingDir = null)
    {
        var catalog = LoadCatalog(workingDir);
        var branch = ResolveBranch(catalog, buildNumber, ubr);
        return branch is null
            ? EmptySet("No curated feature branch matches this Windows build")
            : ToAppliedSet(branch);
    }

    public static FallbackIdSet GetAppliedSetByName(FeatureIdCatalog catalog, string setName)
    {
        var branch = catalog.Branches.FirstOrDefault(b =>
            string.Equals(b.FallbackSet, setName, StringComparison.Ordinal));
        return branch is null
            ? EmptySet($"Curated fallback set '{setName}' is unavailable")
            : ToAppliedSet(branch);
    }

    public static IReadOnlyList<FallbackIdSet> GetAppliedSets(FeatureIdCatalog catalog) =>
        catalog.Branches
            .Where(branch => !string.IsNullOrWhiteSpace(branch.FallbackSet))
            .GroupBy(branch => branch.FallbackSet, StringComparer.Ordinal)
            .Select(group => ToAppliedSet(group.First()))
            .ToArray();

    public static IReadOnlyList<int> GetKnownIds(FeatureIdCatalog catalog) =>
        catalog.Branches
            .SelectMany(branch => branch.Features)
            .Where(feature => !feature.Candidate && (feature.Apply || feature.ProbeOnly))
            .Select(feature => feature.Id)
            .Where(id => id > 0)
            .Distinct()
            .OrderBy(id => id)
            .ToArray();

    public static IReadOnlyList<int> GetProbeOnlyIds(FeatureIdCatalog catalog) =>
        catalog.Branches
            .SelectMany(branch => branch.Features)
            .Where(feature => !feature.Candidate && feature.ProbeOnly)
            .Select(feature => feature.Id)
            .Where(id => id > 0)
            .Distinct()
            .OrderBy(id => id)
            .ToArray();

    public static IReadOnlyList<int> GetCandidateIds(FeatureIdCatalog catalog) =>
        catalog.Branches
            .SelectMany(branch => branch.Features)
            .Where(feature => feature.Candidate)
            .Select(feature => feature.Id)
            .Where(id => id > 0)
            .Distinct()
            .OrderBy(id => id)
            .ToArray();

    public static IReadOnlyDictionary<string, int> GetKnownFeatureIds(
        FeatureIdCatalog catalog,
        int buildNumber,
        int? ubr = null)
    {
        var branch = ResolveBranch(catalog, buildNumber, ubr);
        return branch?.Features
            .Where(feature => !string.IsNullOrWhiteSpace(feature.Name) && feature.Id > 0)
            .GroupBy(feature => feature.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Id, StringComparer.Ordinal)
            ?? new Dictionary<string, int>(StringComparer.Ordinal);
    }

    public static FeatureRouteAssessment AssessFeature(
        FeatureIdCatalog catalog,
        int buildNumber,
        int? ubr,
        string featureName)
    {
        var branch = ResolveBranch(catalog, buildNumber, ubr);
        if (branch is null)
        {
            return new FeatureRouteAssessment(
                featureName,
                "unknown branch",
                false,
                false,
                null,
                "Unknown",
                string.Empty);
        }

        var feature = branch.Features.FirstOrDefault(f =>
            string.Equals(f.Name, featureName, StringComparison.Ordinal));
        return feature is null
            ? new FeatureRouteAssessment(
                featureName,
                branch.AppliesTo,
                true,
                false,
                null,
                "Unknown",
                branch.SourceUrl)
            : new FeatureRouteAssessment(
                feature.Name,
                branch.AppliesTo,
                true,
                true,
                feature.Id,
                feature.DefaultStateLabel,
                branch.SourceUrl);
    }

    public static FeatureRouteAssessment AssessFeature(
        WindowsBuildDetails? buildDetails,
        string featureName,
        string? workingDir = null)
    {
        if (buildDetails is null || buildDetails.BuildNumber < 0)
            return AssessFeature(new FeatureIdCatalog(), 0, null, featureName);

        return AssessFeature(
            LoadCatalog(workingDir),
            buildDetails.BuildNumber,
            buildDetails.UBR,
            featureName);
    }

    private static FallbackIdSet ToAppliedSet(FeatureIdBranch branch)
    {
        var ids = branch.Features
            .Where(feature => feature.Apply && !feature.IsAlwaysDisabled && feature.Id > 0)
            .Select(feature => feature.Id)
            .Distinct()
            .ToArray();

        return new FallbackIdSet(
            branch.FallbackSet,
            ids,
            branch.AppliesTo,
            branch.Confidence);
    }

    private static FeatureIdCatalog LoadFirstUsable(IEnumerable<string> paths)
    {
        foreach (var path in paths.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!File.Exists(path)) continue;
                var json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json)) continue;
                var catalog = JsonSerializer.Deserialize<FeatureIdCatalog>(json, JsonOptions);
                if (catalog is not null && catalog.SchemaVersion == 1 && catalog.Branches.Count > 0)
                    return catalog;
            }
            catch { /* try the next candidate */ }
        }

        return new FeatureIdCatalog();
    }

    private static string BundledPath() =>
        Path.Combine(AppContext.BaseDirectory, BundledCatalogFile);

    private static FallbackIdSet EmptySet(string appliesTo) =>
        new("catalog-unavailable", Array.Empty<int>(), appliesTo, "unknown");

    private static long BuildRangeWidth(FeatureIdBranch branch) =>
        (long)branch.MaxBuild - branch.MinBuild;

    private static long UbrRangeWidth(FeatureIdBranch branch) =>
        (long)branch.MaxUbr - branch.MinUbr;
}
