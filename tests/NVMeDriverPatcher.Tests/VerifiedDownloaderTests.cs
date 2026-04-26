using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

// VerifiedDownloader.DownloadAsync requires a live HttpClient + network, so it's exercised
// indirectly via the AutoUpdaterService integration path. These tests target the pure helpers
// the downloader (and its downstream consumers) use for integrity verification. Parsing
// bugs here would silently void the supply-chain integrity story.
public sealed class VerifiedDownloaderTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("abc", null)]                                                                 // too short
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdeZ", null)]    // non-hex
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
                "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF",
                "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    public void ExtractSha256_HandlesBareHashVariants(string? input, string? expected)
    {
        Assert.Equal(expected, VerifiedDownloader.ExtractSha256(input!));
    }

    [Fact]
    public void ExtractSha256_AcceptsSha256SumFormat()
    {
        const string body = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855  NVMeDriverPatcher.exe";
        Assert.Equal(
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            VerifiedDownloader.ExtractSha256(body));
    }

    [Fact]
    public void ExtractSha256_TakesFirstLineOnly()
    {
        // A `.sha256` file that accidentally contains a second entry (or trailing line)
        // must not confuse the parser into returning the second hash.
        const string body =
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855  file.exe\n" +
            "deadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeef  other.exe";
        Assert.Equal(
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            VerifiedDownloader.ExtractSha256(body));
    }

    [Fact]
    public void ExtractSha256_IgnoresLeadingWhitespace()
    {
        const string body = "   e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855  f.exe";
        Assert.Equal(
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            VerifiedDownloader.ExtractSha256(body));
    }

    [Fact]
    public async Task ComputeSha256Async_EmptyFile_HashesEmptyString()
    {
        // Canonical SHA-256 of empty input. Locks in the algorithm + encoding (lowercase
        // hex, no separators) so a refactor that subtly changes either side is caught.
        var tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(tmp, Array.Empty<byte>());
            var hash = await VerifiedDownloader.ComputeSha256Async(tmp, CancellationToken.None);
            Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", hash);
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    [Fact]
    public async Task ComputeSha256Async_KnownBytes_MatchesReferenceHash()
    {
        // SHA-256("abc") per RFC 6234 / NIST test vector.
        var tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(tmp, System.Text.Encoding.ASCII.GetBytes("abc"));
            var hash = await VerifiedDownloader.ComputeSha256Async(tmp, CancellationToken.None);
            Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", hash);
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    [Fact]
    public async Task TryFetchSidecarHashAsync_RejectsSidecarOnDisallowedHost()
    {
        using var client = new System.Net.Http.HttpClient();
        var allowlist = new[] { "github.com", "objects.githubusercontent.com" };
        var result = await VerifiedDownloader.TryFetchSidecarHashAsync(
            client, new Uri("https://evil.example.com/asset.exe"), allowlist, CancellationToken.None);
        Assert.Null(result);
    }
}
