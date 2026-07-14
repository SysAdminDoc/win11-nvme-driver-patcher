using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class ViVeToolServiceTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(), $"NVMeDriverPatcher.ViVeTool.Tests.{Guid.NewGuid():N}");

    public ViVeToolServiceTests() => Directory.CreateDirectory(_tempRoot);

    [Theory]
    [InlineData("https://github.com/thebookisclosed/ViVe/releases/download/v0.3.4/ViVeTool-v0.3.4-IntelAmd.zip", true)]
    [InlineData("https://objects.githubusercontent.com/github-production-release-asset/example", true)]
    [InlineData("http://github.com/thebookisclosed/ViVe/releases/download/v0.3.4/ViVeTool.zip", false)]
    [InlineData("https://example.com/ViVeTool.zip", false)]
    public void IsTrustedAssetUri_OnlyAllowsHttpsKnownAssetHosts(string rawUri, bool expected)
    {
        Assert.Equal(expected, ViVeToolService.IsTrustedAssetUri(new Uri(rawUri)));
    }

    [Fact]
    public void EmbeddedTrustManifest_IsValidAndPinsBothPublishedArchitectures()
    {
        var manifest = ViVeToolService.LoadTrustManifest();

        Assert.True(ViVeToolService.ValidateTrustManifest(manifest).Success);
        var release = Assert.Single(manifest.Releases);
        Assert.Equal("v0.3.4", release.Tag);
        Assert.Equal(new[] { "Arm64", "X64" }, release.Assets.Select(a => a.Architecture).Order().ToArray());

        var x64 = Assert.Single(release.Assets, a => a.Architecture == "X64");
        Assert.Equal(538_555, x64.ArchiveSize);
        Assert.Equal("cc27f073f3fe5dd2c3d947faf558fd4b2f8e34454f812689b0d65ee8a52e4147", x64.ArchiveSha256);
        Assert.Equal((ushort)0x014c, x64.ExecutablePeMachine);

        var arm64 = Assert.Single(release.Assets, a => a.Architecture == "Arm64");
        Assert.Equal(538_467, arm64.ArchiveSize);
        Assert.Equal("30ad9a4912686355bfce60e1d7bef608735475b7e2160d67418eed8f5e3ba8c7", arm64.ArchiveSha256);
        Assert.Equal((ushort)0xaa64, arm64.ExecutablePeMachine);
        Assert.All(release.Assets, asset => Assert.Equal(4, asset.Members.Count));
    }

    [Fact]
    public void ValidateTrustManifest_RejectsNestedMemberAndArchitectureMismatch()
    {
        var nested = CreateManifest(CreateAsset(
            "X64", 0x014c,
            new Dictionary<string, byte[]> { ["folder/ViVeTool.exe"] = CreatePe(0x014c) }));
        Assert.False(ViVeToolService.ValidateTrustManifest(nested).Success);

        var wrongMachine = CreateManifest(CreateAsset(
            "Arm64", 0x014c,
            new Dictionary<string, byte[]> { ["ViVeTool.exe"] = CreatePe(0x014c) }));
        Assert.False(ViVeToolService.ValidateTrustManifest(wrongMachine).Success);
    }

    [Fact]
    public void SelectReleaseAsset_RequiresExactAllowlistedTagNameSizeUrlAndArchitecture()
    {
        var manifest = ViVeToolService.LoadTrustManifest();
        var release = Assert.Single(manifest.Releases);
        var x64 = Assert.Single(release.Assets, a => a.Architecture == "X64");
        var candidate = OfficialCandidate(release.Tag, x64);

        Assert.True(ViVeToolService.TrySelectReleaseAsset(
            manifest, release.Tag, new[] { candidate }, Architecture.X64,
            out var selectedAsset, out var selectedCandidate, out var error), error);
        Assert.Same(x64, selectedAsset);
        Assert.Equal(candidate, selectedCandidate);

        Assert.False(ViVeToolService.TrySelectReleaseAsset(
            manifest, "v9.9.9", new[] { candidate }, Architecture.X64,
            out _, out _, out _));
        Assert.False(ViVeToolService.TrySelectReleaseAsset(
            manifest, release.Tag, new[] { candidate with { Size = x64.ArchiveSize + 1 } }, Architecture.X64,
            out _, out _, out _));
        Assert.False(ViVeToolService.TrySelectReleaseAsset(
            manifest, release.Tag, new[] { candidate with { Url = "https://example.com/ViVeTool.zip" } }, Architecture.X64,
            out _, out _, out _));
        Assert.False(ViVeToolService.TrySelectReleaseAsset(
            manifest, release.Tag, new[] { candidate }, Architecture.Arm64,
            out _, out _, out _));
        Assert.False(ViVeToolService.TrySelectReleaseAsset(
            manifest, release.Tag, new[] { candidate }, Architecture.X86,
            out _, out _, out _));
    }

    [Fact]
    public void SelectReleaseAsset_Arm64UsesDistinctPublishedPayload()
    {
        var manifest = ViVeToolService.LoadTrustManifest();
        var release = Assert.Single(manifest.Releases);
        var arm64 = Assert.Single(release.Assets, a => a.Architecture == "Arm64");
        var candidate = OfficialCandidate(release.Tag, arm64);

        Assert.True(ViVeToolService.TrySelectReleaseAsset(
            manifest, release.Tag, new[] { candidate }, Architecture.Arm64,
            out var selected, out _, out var error), error);
        Assert.Equal("ViVeTool-v0.3.4-SnapdragonArm64.zip", selected!.Name);
        Assert.Equal((ushort)0xaa64, selected.ExecutablePeMachine);
    }

    [Fact]
    public void ValidateArchive_AcceptsExactManifest()
    {
        var expected = CreatePayload(0x014c);
        var zip = CreateArchive("exact.zip", expected.Select(kv => (kv.Key, kv.Value)));
        var asset = CreateAssetFromArchive(zip, "X64", 0x014c, expected);

        var validation = ViVeToolService.ValidateArchive(zip, asset);

        Assert.True(validation.Success, validation.Summary);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("modified")]
    [InlineData("nested")]
    [InlineData("duplicate")]
    public void ValidateArchive_FailsClosedOnInventoryOrContentDrift(string mutation)
    {
        var expected = CreatePayload(0x014c);
        var entries = expected.Select(kv => (kv.Key, kv.Value)).ToList();
        switch (mutation)
        {
            case "missing":
                entries.RemoveAt(0);
                break;
            case "extra":
                entries.Add(("unexpected.dll", new byte[] { 1, 2, 3 }));
                break;
            case "modified":
                entries[0] = (entries[0].Key, entries[0].Value.Concat(new byte[] { 0xff }).ToArray());
                break;
            case "nested":
                entries[0] = ($"nested/{entries[0].Key}", entries[0].Value);
                break;
            case "duplicate":
                entries.Add(entries[0]);
                break;
        }
        var zip = CreateArchive($"{mutation}.zip", entries);
        var asset = CreateAssetFromArchive(zip, "X64", 0x014c, expected);

        var validation = ViVeToolService.ValidateArchive(zip, asset);

        Assert.False(validation.Success);
    }

    [Fact]
    public void ValidateArchive_RejectsModifiedArchiveHashBeforeExtraction()
    {
        var expected = CreatePayload(0x014c);
        var zip = CreateArchive("hash.zip", expected.Select(kv => (kv.Key, kv.Value)));
        var asset = CreateAssetFromArchive(zip, "X64", 0x014c, expected);
        asset.ArchiveSha256 = new string('0', 64);

        var validation = ViVeToolService.ValidateArchive(zip, asset);

        Assert.False(validation.Success);
        Assert.Contains("archive SHA-256", validation.Summary);
    }

    [Fact]
    public void ValidatePayloadDirectory_AcceptsExactManifestAndPeArchitecture()
    {
        var expected = CreatePayload(0xaa64);
        WritePayload(_tempRoot, expected);
        var asset = CreateAsset("Arm64", 0xaa64, expected);

        var validation = ViVeToolService.ValidatePayloadDirectory(_tempRoot, asset);

        Assert.True(validation.Success, validation.Summary);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("modified")]
    [InlineData("nested")]
    [InlineData("architecture")]
    public void ValidatePayloadDirectory_FailsClosedOnAnyPayloadDrift(string mutation)
    {
        var expected = CreatePayload(0x014c);
        WritePayload(_tempRoot, expected);
        var asset = CreateAsset("X64", 0x014c, expected);
        switch (mutation)
        {
            case "missing":
                File.Delete(Path.Combine(_tempRoot, expected.Keys.First()));
                break;
            case "extra":
                File.WriteAllText(Path.Combine(_tempRoot, "extra.txt"), "unexpected");
                break;
            case "modified":
                File.AppendAllText(Path.Combine(_tempRoot, expected.Keys.First()), "tamper");
                break;
            case "nested":
                Directory.CreateDirectory(Path.Combine(_tempRoot, "nested"));
                break;
            case "architecture":
                asset.ExecutablePeMachine = 0xaa64;
                break;
        }

        var validation = ViVeToolService.ValidatePayloadDirectory(_tempRoot, asset);

        Assert.False(validation.Success);
    }

    [Fact]
    public void IsInstalled_RejectsLegacyRootExeWithoutCompletePayload()
    {
        Directory.CreateDirectory(ViVeToolService.ToolsDir(_tempRoot));
        File.WriteAllBytes(
            Path.Combine(ViVeToolService.ToolsDir(_tempRoot), "ViVeTool.exe"), CreatePe(0x014c));

        Assert.False(ViVeToolService.IsInstalled(_tempRoot));
    }

    [Theory]
    [InlineData(0x014c)]
    [InlineData(0xaa64)]
    public void TryReadPeMachine_ReportsPublishedArchitectures(int expectedMachine)
    {
        var path = Path.Combine(_tempRoot, $"{expectedMachine}.exe");
        File.WriteAllBytes(path, CreatePe((ushort)expectedMachine));

        Assert.True(ViVeToolService.TryReadPeMachine(path, out var actual));
        Assert.Equal((ushort)expectedMachine, actual);
    }

    [Fact]
    public void PromotePayloadDirectory_RestoresExistingPayloadIfFinalValidationFails()
    {
        var destination = Path.Combine(_tempRoot, "vivetool");
        var staging = Path.Combine(_tempRoot, "staging");
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(destination, "old.txt"), "keep me");
        var expected = CreatePayload(0x014c);
        WritePayload(staging, expected);
        var asset = CreateAsset("X64", 0x014c, expected);
        asset.Members[0].Sha256 = new string('0', 64);

        var result = ViVeToolService.PromotePayloadDirectory(staging, destination, asset);

        Assert.False(result.Success);
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(destination, "old.txt")));
        Assert.False(File.Exists(Path.Combine(destination, "ViVeTool.exe")));
    }

    [Fact]
    public void ComputeSha256_MatchesKnownVector()
    {
        var path = Path.Combine(_tempRoot, "vec.bin");
        File.WriteAllText(path, "abc");
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            ViVeToolService.ComputeSha256(path));
    }

    private static ViVeToolService.ReleaseAssetCandidate OfficialCandidate(
        string tag, ViVeToolTrustedAsset asset) =>
        new(asset.Name,
            $"https://github.com/thebookisclosed/ViVe/releases/download/{tag}/{asset.Name}",
            asset.ArchiveSize);

    private static ViVeToolTrustManifest CreateManifest(ViVeToolTrustedAsset asset) => new()
    {
        SchemaVersion = 1,
        Source = "https://github.com/thebookisclosed/ViVe/releases/tag/v0.0.0",
        LastReviewed = "2026-07-14",
        Releases =
        {
            new ViVeToolTrustedRelease { Tag = "v0.0.0", Assets = { asset } }
        }
    };

    private static Dictionary<string, byte[]> CreatePayload(ushort machine) => new(StringComparer.Ordinal)
    {
        ["Albacore.ViVe.dll"] = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray(),
        ["FeatureDictionary.pfs"] = Enumerable.Range(0, 80).Select(i => (byte)(i + 1)).ToArray(),
        ["Newtonsoft.Json.dll"] = Enumerable.Range(0, 96).Select(i => (byte)(255 - i)).ToArray(),
        ["ViVeTool.exe"] = CreatePe(machine)
    };

    private static byte[] CreatePe(ushort machine)
    {
        var bytes = new byte[512];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(bytes, 0x3c);
        bytes[0x80] = (byte)'P';
        bytes[0x81] = (byte)'E';
        BitConverter.GetBytes(machine).CopyTo(bytes, 0x84);
        return bytes;
    }

    private static void WritePayload(string directory, IReadOnlyDictionary<string, byte[]> payload)
    {
        Directory.CreateDirectory(directory);
        foreach (var (name, bytes) in payload)
            File.WriteAllBytes(Path.Combine(directory, name), bytes);
    }

    private string CreateArchive(string name, IEnumerable<(string Name, byte[] Bytes)> entries)
    {
        var path = Path.Combine(_tempRoot, name);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entryName, bytes) in entries)
        {
            var entry = archive.CreateEntry(entryName, CompressionLevel.NoCompression);
            using var output = entry.Open();
            output.Write(bytes);
        }
        return path;
    }

    private static ViVeToolTrustedAsset CreateAssetFromArchive(
        string archivePath,
        string architecture,
        ushort machine,
        IReadOnlyDictionary<string, byte[]> expected)
    {
        var asset = CreateAsset(architecture, machine, expected);
        var info = new FileInfo(archivePath);
        asset.ArchiveSize = info.Length;
        asset.ArchiveSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archivePath))).ToLowerInvariant();
        return asset;
    }

    private static ViVeToolTrustedAsset CreateAsset(
        string architecture,
        ushort machine,
        IReadOnlyDictionary<string, byte[]> expected) => new()
    {
        Architecture = architecture,
        Name = $"ViVeTool-v0.0.0-{architecture}.zip",
        ArchiveSize = 20_000,
        ArchiveSha256 = new string('a', 64),
        ExecutablePeMachine = machine,
        Members = expected.Select(kv => new ViVeToolTrustedMember
        {
            Path = kv.Key,
            Size = kv.Value.Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(kv.Value)).ToLowerInvariant()
        }).ToList()
    };

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true);
        }
        catch { }
    }
}
