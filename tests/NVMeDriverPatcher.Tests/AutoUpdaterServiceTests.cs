using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

// AutoUpdaterService.StageUpdateAsync host-allowlist + filename guards — both run pre-network
// so these tests exercise rejection paths without any I/O. The happy path is covered
// manually since it requires a live GitHub release.
public sealed class AutoUpdaterServiceTests
{
    [Fact]
    public async Task NonHttpsUrl_Rejected()
    {
        var result = await AutoUpdaterService.StageUpdateAsync(
            "http://github.com/foo/bar/releases/download/asset.exe",
            "asset.exe",
            Path.GetTempPath());
        Assert.False(result.Success);
        Assert.Contains("https", result.Summary);
    }

    [Fact]
    public async Task UrlOnUnknownHost_Rejected()
    {
        var result = await AutoUpdaterService.StageUpdateAsync(
            "https://evil.example.com/foo.exe",
            "asset.exe",
            Path.GetTempPath());
        Assert.False(result.Success);
        Assert.Contains("not in the allowlist", result.Summary);
    }

    [Fact]
    public async Task AssetNameWithPathTraversal_Rejected()
    {
        var result = await AutoUpdaterService.StageUpdateAsync(
            "https://github.com/foo/bar/releases/download/asset.exe",
            "..\\evil.exe",
            Path.GetTempPath());
        Assert.False(result.Success);
        Assert.Contains("invalid", result.Summary);
    }

    [Fact]
    public async Task MalformedUri_Rejected()
    {
        var result = await AutoUpdaterService.StageUpdateAsync(
            "not a url at all",
            "asset.exe",
            Path.GetTempPath());
        Assert.False(result.Success);
    }
}
