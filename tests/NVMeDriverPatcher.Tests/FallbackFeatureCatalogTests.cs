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
}
