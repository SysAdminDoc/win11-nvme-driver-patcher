using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class FallbackFeatureCatalogTests
{
    [Theory]
    [InlineData(22631)] // 23H2
    [InlineData(26100)] // 24H2
    [InlineData(26199)] // boundary: below 26200
    public void SelectForBuild_PreNewSetBuilds_UseVerifiedMarch2026Set(int build)
    {
        var set = FallbackFeatureCatalog.SelectForBuild(build);
        Assert.Equal("post-block-2026-03", set.Name);
        Assert.Equal(new[] { 60786016, 48433719 }, set.Ids);
    }

    [Theory]
    [InlineData(26200)] // 25H2
    [InlineData(28020)] // 26H1 train
    public void SelectForBuild_26200AndLater_UseNativeNvmeStackSet(int build)
    {
        var set = FallbackFeatureCatalog.SelectForBuild(build);
        Assert.Equal("native-nvme-stack-25h2", set.Name);
        Assert.Contains(55369237, set.Ids);
        Assert.Contains(48433719, set.Ids);
        Assert.Contains(49453572, set.Ids);
        // 60786016 reportedly no longer exists on these builds — never apply it there.
        Assert.DoesNotContain(60786016, set.Ids);
    }

    [Fact]
    public void AllKnownIds_IsTheDistinctUnion_AndFeedsTheEvidenceProbe()
    {
        Assert.Equal(new[] { 48433719, 49453572, 55369237, 60786016 },
            FallbackFeatureCatalog.AllKnownIds);
        // The FeatureStore evidence probe must recognize evidence from ANY known set,
        // including ViVeTool runs the user did by hand from a forum guide.
        Assert.Equal(FallbackFeatureCatalog.AllKnownIds, FeatureStoreWriterService.PostBlockFeatureIds);
    }

    [Fact]
    public void EverySet_HasProvenanceMetadata()
    {
        foreach (var set in FallbackFeatureCatalog.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(set.Name));
            Assert.False(string.IsNullOrWhiteSpace(set.AppliesTo));
            Assert.False(string.IsNullOrWhiteSpace(set.Confidence));
            Assert.NotEmpty(set.Ids);
        }
    }

    [Fact]
    public void IdsDisplay_RendersHumanReadableProse()
    {
        Assert.Equal("60786016 and 48433719", FallbackFeatureCatalog.PostBlockMarch2026.IdsDisplay);
        Assert.Equal("55369237, 48433719 and 49453572", FallbackFeatureCatalog.NativeNvmeStack25H2.IdsDisplay);
    }

    [Fact]
    public void RegistryOverrideAssessment_Pre26200_ReportsExplicitMismatches()
    {
        var assessment = FallbackFeatureCatalog.AssessRegistryOverrides(
            new WindowsBuildDetails { BuildNumber = 26100, UBR = 8687 },
            AppConfig.FeatureIDs);

        Assert.True(assessment.BranchKnown);
        Assert.Equal("pre-26200 sampled branch", assessment.Branch);
        Assert.Equal(3, assessment.MismatchCount);
        Assert.Equal(60786016, assessment.Features[0].KnownBranchId);
        Assert.Equal("NativeNVMeStackForGeClient", assessment.Features[0].FeatureName);
        Assert.All(assessment.Features, feature => Assert.False(feature.MatchesKnownFeature));
        Assert.Contains("MISMATCH", assessment.Features[0].Detail);
        Assert.Contains("3 MISMATCH(es)", assessment.Summary);
    }

    [Fact]
    public void RegistryOverrideAssessment_Post26200_UsesRotatedPrimaryId()
    {
        var assessment = FallbackFeatureCatalog.AssessRegistryOverrides(
            new WindowsBuildDetails { BuildNumber = 26404, UBR = 5000 },
            AppConfig.FeatureIDs);

        Assert.Equal(55369237, assessment.Features.Single(f => f.FeatureName == "NativeNVMeStackForGeClient").KnownBranchId);
        Assert.Equal(48433719, assessment.Features.Single(f => f.FeatureName == "UxAccOptimization").KnownBranchId);
        Assert.Equal(49453572, assessment.Features.Single(f => f.FeatureName == "Standalone_Future").KnownBranchId);
        Assert.True(assessment.HasMismatch);
    }

    [Fact]
    public void RegistryOverrideAssessment_WithoutBuild_IsExplicitlyUnknown()
    {
        var assessment = FallbackFeatureCatalog.AssessRegistryOverrides(null, AppConfig.FeatureIDs);

        Assert.False(assessment.BranchKnown);
        Assert.False(assessment.HasMismatch);
        Assert.Contains("build unavailable", assessment.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UNKNOWN", assessment.Features[0].Detail);
    }
}
