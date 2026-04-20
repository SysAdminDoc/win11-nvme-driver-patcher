using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class FeatureStoreWriterServiceTests
{
    [Fact]
    public void IndexOfBytes_FindsNeedleInMiddle()
    {
        byte[] hay = { 1, 2, 3, 4, 5, 6, 7, 8 };
        byte[] needle = { 4, 5 };
        Assert.Equal(3, FeatureStoreWriterService.IndexOfBytes(hay, needle));
    }

    [Fact]
    public void IndexOfBytes_ReturnsNegativeOneWhenMissing()
    {
        byte[] hay = { 1, 2, 3 };
        byte[] needle = { 9, 9 };
        Assert.Equal(-1, FeatureStoreWriterService.IndexOfBytes(hay, needle));
    }

    [Fact]
    public void IndexOfBytes_EmptyNeedleReturnsNegativeOne()
    {
        byte[] hay = { 1, 2, 3 };
        Assert.Equal(-1, FeatureStoreWriterService.IndexOfBytes(hay, Array.Empty<byte>()));
    }

    [Fact]
    public void WriteOverrides_ReturnsNotImplementedStub()
    {
        // The stub is the contract. When the native writer lands, this test flips to verify
        // the actual encoder. Having the assertion in place prevents a silent behavior change.
        var result = FeatureStoreWriterService.WriteOverrides(new[] { 60786016, 48433719 });
        Assert.False(result.Success);
        Assert.Contains("not yet implemented", result.Summary);
    }

    [Fact]
    public void PostBlockFeatureIds_MatchesPublishedIds()
    {
        // Tom's Hardware / HotHardware / gamegpu community tracking: 60786016 + 48433719 are
        // the two IDs the ViVeTool fallback writes. Pinning here so a typo doesn't silently
        // invalidate everyone's fallback evidence check.
        Assert.Contains(60786016, FeatureStoreWriterService.PostBlockFeatureIds);
        Assert.Contains(48433719, FeatureStoreWriterService.PostBlockFeatureIds);
    }
}
