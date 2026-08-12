using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class FeatureIdCatalogServiceTests
{
    [Fact]
    public void BundledCatalog_ResolvesSampledBranchesAndCompatibilitySets()
    {
        var catalog = FeatureIdCatalogService.LoadBundledCatalog();

        var pre = FeatureIdCatalogService.ResolveBranch(catalog, 26100, 8687);
        Assert.NotNull(pre);
        Assert.Equal("ge-prerelease-im-26100.8687-amd64", pre!.Id);
        Assert.Equal(new[] { 60786016, 48433719 },
            FeatureIdCatalogService.SelectAppliedSet(26100, 8687).Ids);

        var post = FeatureIdCatalogService.ResolveBranch(catalog, 29531, 1000);
        Assert.NotNull(post);
        Assert.Equal("rs-prerelease-29531.1000-amd64", post!.Id);
        Assert.Equal(new[] { 55369237, 48433719 },
            FeatureIdCatalogService.SelectAppliedSet(29531, 1000).Ids);

        var candidate = FeatureIdCatalogService.GetKnownFeatureIds(catalog, 29531, 1000);
        Assert.Equal(48613417, candidate["NativeNVMeStackEnableForClientOS"]);
        Assert.DoesNotContain(48613417, FeatureIdCatalogService.GetKnownIds(catalog));
    }

    [Fact]
    public void AlwaysDisabled_IsDistinctFromUnknown()
    {
        var catalog = new FeatureIdCatalog
        {
            Branches =
            [
                new FeatureIdBranch
                {
                    Id = "fixture",
                    MinBuild = 100,
                    MaxBuild = 100,
                    AppliesTo = "fixture branch",
                    SourceUrl = "https://example.invalid/catalog",
                    Features =
                    [
                        new CuratedFeature
                        {
                            Name = "KnownDisabled",
                            Id = 123,
                            DefaultState = "Always Disabled",
                        }
                    ]
                }
            ]
        };

        var disabled = FeatureIdCatalogService.AssessFeature(catalog, 100, 0, "KnownDisabled");
        var unknown = FeatureIdCatalogService.AssessFeature(catalog, 100, 0, "MissingFeature");

        Assert.True(disabled.FeatureKnown);
        Assert.True(disabled.IsAlwaysDisabled);
        Assert.Contains("ALWAYS DISABLED", disabled.Detail);
        Assert.False(unknown.FeatureKnown);
        Assert.False(unknown.IsAlwaysDisabled);
        Assert.Contains("UNKNOWN", unknown.Detail);
    }

    [Fact]
    public void FallbackCatalog_UsesCuratedDefaultStateRoles()
    {
        var catalog = FeatureIdCatalogService.LoadBundledCatalog();
        var branch = FeatureIdCatalogService.ResolveBranch(catalog, 29531, 1000)!;

        var alwaysEnabled = branch.Features.Single(f => f.Name == "Standalone_Future");
        var candidate = branch.Features.Single(f => f.Name == "NativeNVMeStackEnableForClientOS");

        Assert.Equal(CuratedFeatureDefaultState.AlwaysEnabled, alwaysEnabled.ParsedDefaultState);
        Assert.True(alwaysEnabled.ProbeOnly);
        Assert.False(alwaysEnabled.Apply);
        Assert.Equal(CuratedFeatureDefaultState.DisabledByDefault, candidate.ParsedDefaultState);
        Assert.True(candidate.Candidate);
        Assert.False(candidate.Apply);
    }
}
