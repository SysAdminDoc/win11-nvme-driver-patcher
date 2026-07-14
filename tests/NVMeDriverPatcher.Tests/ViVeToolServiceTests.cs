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

    // --- Pinned-hash supply-chain control (elevated execution) ---

    [Fact]
    public void ComputeSha256_MatchesKnownVector()
    {
        var f = Path.Combine(_tempRoot, "vec.bin");
        File.WriteAllText(f, "abc");
        // SHA-256("abc")
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            ViVeToolService.ComputeSha256(f));
    }

    [Fact]
    public void IsPinnedExecutable_RejectsUnknownHash()
    {
        var f = Path.Combine(_tempRoot, "ViVeTool.exe");
        File.WriteAllText(f, "not the real vivetool");
        Assert.False(ViVeToolService.IsPinnedExecutable(f));
    }

    [Fact]
    public void IsPinnedExecutable_RejectsMissingFile()
    {
        Assert.False(ViVeToolService.IsPinnedExecutable(Path.Combine(_tempRoot, "nope.exe")));
    }

    [Fact]
    public void IsInstalled_RejectsLargeButUnpinnedCachedExe()
    {
        // A big cached file that is NOT a pinned build must NOT be treated as installed, so the
        // fallback re-downloads a verified copy instead of dead-ending at execution.
        var tools = ViVeToolService.ToolsDir(_tempRoot);
        Directory.CreateDirectory(tools);
        File.WriteAllBytes(ViVeToolService.CachedExePath(_tempRoot), new byte[64 * 1024]);
        Assert.False(ViVeToolService.IsInstalled(_tempRoot));
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
