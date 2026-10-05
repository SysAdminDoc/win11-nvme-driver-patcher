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
        Assert.DoesNotContain(49453572, set.Ids);
        // The 26200+ set is 55369237 + 48433719. A lone 26200.8116 report of 60786016 binding
        // isn't enough to widen it, so 60786016 stays out until it's verified on a device.
        Assert.DoesNotContain(60786016, set.Ids);
    }

    [Fact]
    public void AllKnownIds_IsTheDistinctUnion_AndFeedsTheEvidenceProbe()
    {
        Assert.Equal(new[] { 48433719, 49453572, 55369237, 60786016 },
            FallbackFeatureCatalog.AllKnownIds);
        Assert.Equal(new[] { 55369237, 48433719 },
            FallbackFeatureCatalog.NativeNvmeStack25H2.Ids);
        Assert.Contains(49453572, FallbackFeatureCatalog.ProbeOnlyIds);
        Assert.Contains(FallbackFeatureCatalog.CandidateSecondGateId, FallbackFeatureCatalog.CandidateProbeIds);
        Assert.Equal("NativeNVMeStackEnableForClientOS", FallbackFeatureCatalog.CandidateSecondGateName);
        Assert.Contains("windows-velocity-feature-lists", FallbackFeatureCatalog.CandidateSecondGateSourceUrl);
        // The FeatureStore evidence probe must recognize evidence from ANY known set,
        // including ViVeTool runs the user did by hand from a forum guide. The applied set is
        // intentionally smaller: 49453572 is Always Enabled in every sampled branch, so it is
        // probe-only and must not widen the native write or its mutation obligation.
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
        Assert.Equal("55369237 and 48433719", FallbackFeatureCatalog.NativeNvmeStack25H2.IdsDisplay);
    }

    [Fact]
    public void RegistryOverrideIds_AreASeparateNumberingFromEveryCuratedFeatureStoreId()
    {
        // The Policies override value names never appear in any sampled velocity dump, so a
        // comparison between the two sets can only ever say "different". That is expected.
        var catalogIds = FeatureIdCatalogService.LoadBundledCatalog().Branches
            .SelectMany(branch => branch.Features)
            .Select(feature => feature.Id.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain(AppConfig.OwnedOverrideValueNames, catalogIds.Contains);
    }

    [Theory]
    [InlineData(26100, 8687)]
    [InlineData(26404, 5000)]
    public void RegistryOverrideAssessment_ListsBothIdSetsWithoutAMismatchVerdict(int build, int ubr)
    {
        var assessment = FallbackFeatureCatalog.AssessRegistryOverrides(
            new WindowsBuildDetails { BuildNumber = build, UBR = ubr },
            AppConfig.FeatureIDs);

        Assert.True(assessment.BranchKnown);
        Assert.DoesNotContain("MISMATCH", assessment.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.All(assessment.Features, feature =>
        {
            Assert.DoesNotContain("MISMATCH", feature.Detail, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(feature.RegistryId, feature.Detail, StringComparison.Ordinal);
            Assert.Contains(feature.KnownBranchId!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), feature.Detail, StringComparison.Ordinal);
        });
        Assert.Contains("separate numbering", assessment.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RegistryOverrideAssessment_Pre26200_ListsTheBranchFeatureStoreIds()
    {
        var assessment = FallbackFeatureCatalog.AssessRegistryOverrides(
            new WindowsBuildDetails { BuildNumber = 26100, UBR = 8687 },
            AppConfig.FeatureIDs);

        Assert.Equal("pre-26200 sampled branch", assessment.Branch);
        Assert.Equal(60786016, assessment.Features[0].KnownBranchId);
        Assert.Equal("NativeNVMeStackForGeClient", assessment.Features[0].FeatureName);
    }

    [Fact]
    public void RegistryOverrideAssessment_Post26200_ListsTheRotatedFeatureStoreIds()
    {
        var assessment = FallbackFeatureCatalog.AssessRegistryOverrides(
            new WindowsBuildDetails { BuildNumber = 26404, UBR = 5000 },
            AppConfig.FeatureIDs);

        Assert.Equal(55369237, assessment.Features.Single(f => f.FeatureName == "NativeNVMeStackForGeClient").KnownBranchId);
        Assert.Equal(48433719, assessment.Features.Single(f => f.FeatureName == "UxAccOptimization").KnownBranchId);
        Assert.Equal(49453572, assessment.Features.Single(f => f.FeatureName == "Standalone_Future").KnownBranchId);
    }

    [Fact]
    public void RegistryOverrideAssessment_WithoutBuild_SaysTheBranchIsUnknown()
    {
        var assessment = FallbackFeatureCatalog.AssessRegistryOverrides(null, AppConfig.FeatureIDs);

        Assert.False(assessment.BranchKnown);
        Assert.Contains("build unavailable", assessment.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Null(assessment.Features[0].KnownBranchId);
        Assert.Contains("no FeatureStore ID", assessment.Features[0].Detail, StringComparison.Ordinal);
    }
}
