using System.Runtime.InteropServices;
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

    // --- Architecture-aware release asset selection (ViVe v0.3.4+ split-arch zips) ---

    private static readonly ViVeToolService.ReleaseAssetCandidate[] SplitArchAssets =
    {
        new("ViVeTool-v0.3.4-SnapdragonArm64.zip", "https://example/arm", 60_000),
        new("ViVeTool-v0.3.4-IntelAmd.zip", "https://example/x64", 60_000),
        new("Source code (zip)", "https://example/src", 1_000_000),
    };

    [Fact]
    public void SelectReleaseAsset_X64_PrefersIntelAmd_AcrossAllOrders()
    {
        for (int shift = 0; shift < SplitArchAssets.Length; shift++)
        {
            var rotated = SplitArchAssets.Skip(shift).Concat(SplitArchAssets.Take(shift)).ToArray();
            var sel = ViVeToolService.SelectReleaseAsset(rotated, Architecture.X64);
            Assert.NotNull(sel);
            Assert.Equal("ViVeTool-v0.3.4-IntelAmd.zip", sel!.Value.Name);
        }
    }

    [Fact]
    public void SelectReleaseAsset_X64_AcceptsLegacySingleZip()
    {
        // Pre-v0.3.4 releases shipped one un-suffixed zip — must remain installable.
        var legacy = new ViVeToolService.ReleaseAssetCandidate[]
        {
            new("ViVeTool-v0.3.3.zip", "https://example/legacy", 50_000),
        };
        var sel = ViVeToolService.SelectReleaseAsset(legacy, Architecture.X64);
        Assert.NotNull(sel);
        Assert.Equal("ViVeTool-v0.3.3.zip", sel!.Value.Name);
    }

    [Fact]
    public void SelectReleaseAsset_X64_NeverSelectsArmAssets_FailsClosed()
    {
        var armOnly = new ViVeToolService.ReleaseAssetCandidate[]
        {
            new("ViVeTool-v0.3.4-SnapdragonArm64.zip", "https://example/arm", 60_000),
            new("ViVeTool-v0.3.4-ARM64CLR.zip", "https://example/armclr", 60_000),
        };
        Assert.Null(ViVeToolService.SelectReleaseAsset(armOnly, Architecture.X64));
    }

    [Fact]
    public void SelectReleaseAsset_Arm64_SelectsArmAsset_AndRejectsIntelOnly()
    {
        var sel = ViVeToolService.SelectReleaseAsset(SplitArchAssets, Architecture.Arm64);
        Assert.NotNull(sel);
        Assert.Equal("ViVeTool-v0.3.4-SnapdragonArm64.zip", sel!.Value.Name);

        var intelOnly = new ViVeToolService.ReleaseAssetCandidate[]
        {
            new("ViVeTool-v0.3.4-IntelAmd.zip", "https://example/x64", 60_000),
        };
        Assert.Null(ViVeToolService.SelectReleaseAsset(intelOnly, Architecture.Arm64));
    }

    [Fact]
    public void SelectReleaseAsset_IgnoresNonViVeZipsAndSourceArchives()
    {
        var noise = new ViVeToolService.ReleaseAssetCandidate[]
        {
            new("Source code (zip)", "https://example/src", 1_000_000),
            new("readme.txt", "https://example/txt", 100),
            new("OtherTool-v1.0.zip", "https://example/other", 60_000),
        };
        Assert.Null(ViVeToolService.SelectReleaseAsset(noise, Architecture.X64));
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
