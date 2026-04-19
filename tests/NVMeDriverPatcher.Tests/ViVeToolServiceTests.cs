using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class ViVeToolServiceTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"NVMeDriverPatcher.ViVeTool.Tests.{Guid.NewGuid():N}");

    public ViVeToolServiceTests()
    {
        Directory.CreateDirectory(_tempRoot);
    }

    [Theory]
    [InlineData("https://github.com/thebookisclosed/ViVe/releases/download/v0.3.3/ViVeTool-v0.3.3.zip", true)]
    [InlineData("https://objects.githubusercontent.com/github-production-release-asset/example", true)]
    [InlineData("http://github.com/thebookisclosed/ViVe/releases/download/v0.3.3/ViVeTool-v0.3.3.zip", false)]
    [InlineData("https://example.com/ViVeTool.zip", false)]
    public void IsTrustedAssetUri_OnlyAllowsHttpsKnownAssetHosts(string rawUri, bool expected)
    {
        Assert.Equal(expected, ViVeToolService.IsTrustedAssetUri(new Uri(rawUri)));
    }

    [Fact]
    public void FindViVeToolPayloadRoot_AcceptsRootLevelExecutable()
    {
        File.WriteAllText(Path.Combine(_tempRoot, "ViVeTool.exe"), "stub");

        var root = ViVeToolService.FindViVeToolPayloadRoot(_tempRoot);

        Assert.Equal(_tempRoot, root);
    }

    [Fact]
    public void FindViVeToolPayloadRoot_AcceptsNestedReleaseFolder()
    {
        var payloadDir = Path.Combine(_tempRoot, "ViVeTool-v0.3.3");
        Directory.CreateDirectory(payloadDir);
        File.WriteAllText(Path.Combine(payloadDir, "ViVeTool.exe"), "stub");

        var root = ViVeToolService.FindViVeToolPayloadRoot(_tempRoot);

        Assert.Equal(payloadDir, root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }
        catch
        {
            // Best-effort temp cleanup only.
        }
    }
}
