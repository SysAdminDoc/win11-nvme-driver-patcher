using System.Net;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

// AutoUpdaterService: the latest-release lookup update-check uses, and the sidecar parsing it
// shares with VerifiedDownloader. The signed manifest check is in UpdateManifestTests.
public sealed class AutoUpdaterServiceTests
{
    [Theory]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("   ", null)]
    [InlineData("abc", null)]                                                            // too short
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdeZ", null)] // non-hex char
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
                "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF",
                "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]      // normalized to lower
    public void ExtractSha256_AcceptsBareHash(string? input, string? expected)
    {
        Assert.Equal(expected, AutoUpdaterService.ExtractSha256(input!));
    }

    [Fact]
    public void ExtractSha256_AcceptsSha256SumFormat()
    {
        // sha256sum output is `<hash>  <filename>`; we take the first whitespace-delimited token.
        const string body = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855  NVMeDriverPatcher.exe";
        Assert.Equal(
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            AutoUpdaterService.ExtractSha256(body));
    }

    [Fact]
    public void ExtractSha256_IgnoresLinesAfterFirst()
    {
        // A .sha256 file may include a trailing newline or a second entry; we only care
        // about the leading hash so the parser stays tolerant of formatting variations.
        const string body =
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855  file.exe\n" +
            "abc123  unrelated.exe\n";
        Assert.Equal(
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            AutoUpdaterService.ExtractSha256(body));
    }

    [Fact]
    public async Task SidecarLookup_RejectsSidecarsOnUntrustedHost()
    {
        // The sidecar fetch reuses the same host allowlist as the asset download — a release
        // that somehow points at an off-GitHub host must NOT be silently "integrity-verified"
        // against an attacker-supplied sha256 file.
        var evil = new Uri("https://evil.example.com/asset.exe");
        var result = await AutoUpdaterService.TryFetchSidecarHashAsync(evil, CancellationToken.None);
        Assert.Null(result);
    }

    // --- GUI asset selection (exact-name, fail-closed) ---

    private static readonly (string? Name, string? Url)[] FullReleaseAssets =
    {
        ("NVMeDriverPatcher.Cli.exe", "https://example/cli"),
        ("NVMeDriverPatcher.Tray.exe", "https://example/tray"),
        ("NVMeDriverPatcher.Watchdog.exe", "https://example/watchdog"),
        ("NVMeDriverPatcher-win-arm64.exe", "https://example/gui-arm64"),
        ("NVMeDriverPatcher-4.6.2.msi", "https://example/msi"),
        ("SHA256SUMS.txt", "https://example/sums"),
        ("NVMeDriverPatcher.exe", "https://example/gui"),
        ("NVMe_Driver_Patcher.ps1", "https://example/ps1"),
    };

    [Fact]
    public void SelectGuiAsset_PicksExactGuiName_RegardlessOfOrder()
    {
        // Exhaustive rotations stand in for "upload order is not stable".
        for (int shift = 0; shift < FullReleaseAssets.Length; shift++)
        {
            var rotated = FullReleaseAssets.Skip(shift).Concat(FullReleaseAssets.Take(shift)).ToArray();
            var (url, name) = AutoUpdaterService.SelectGuiAsset(rotated);
            Assert.Equal("NVMeDriverPatcher.exe", name);
            Assert.Equal("https://example/gui", url);
        }
    }

    [Fact]
    public void SelectGuiAsset_FailsClosed_WhenGuiAssetMissing()
    {
        // CLI/tray/watchdog executables must NEVER be reported as the GUI update.
        var withoutGui = FullReleaseAssets.Where(a => a.Name != "NVMeDriverPatcher.exe").ToArray();
        var (url, name) = AutoUpdaterService.SelectGuiAsset(withoutGui);
        Assert.Null(url);
        Assert.Null(name);
    }

    [Fact]
    public void SelectGuiAsset_MatchesCaseInsensitively()
    {
        var (url, name) = AutoUpdaterService.SelectGuiAsset(new (string?, string?)[]
        {
            ("nvmedriverpatcher.exe", "https://example/gui-lower"),
        });
        Assert.Equal("nvmedriverpatcher.exe", name);
        Assert.Equal("https://example/gui-lower", url);
    }

    [Fact]
    public void SelectGuiAsset_IgnoresNullAndEmptyNames()
    {
        var (url, name) = AutoUpdaterService.SelectGuiAsset(new (string?, string?)[]
        {
            (null, "https://example/null"),
            ("", "https://example/empty"),
            ("NVMeDriverPatcher.exe", "https://example/gui"),
        });
        Assert.Equal("https://example/gui", url);
        Assert.Equal("NVMeDriverPatcher.exe", name);
    }

    [Fact]
    public async Task FetchLatestAsset_ClassifiesRateLimitSeparately()
    {
        using var client = ClientReturning(HttpStatusCode.Forbidden, "{}");

        var result = await AutoUpdaterService.FetchLatestAssetAsync(
            "https://api.github.com/repos/example/project/releases/latest",
            client);

        Assert.Equal(ReleaseAssetFetchStatus.RateLimited, result.Status);
        Assert.Equal(403, result.HttpStatusCode);
        Assert.Contains("rate limit", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FetchLatestAsset_ClassifiesMalformedJsonSeparately()
    {
        using var client = ClientReturning(HttpStatusCode.OK, "{not-json");

        var result = await AutoUpdaterService.FetchLatestAssetAsync(
            "https://api.github.com/repos/example/project/releases/latest",
            client);

        Assert.Equal(ReleaseAssetFetchStatus.InvalidResponse, result.Status);
        Assert.Contains("invalid JSON", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FetchLatestAsset_ClassifiesTransportFailureSeparately()
    {
        using var client = new HttpClient(new StubHttpMessageHandler(
            _ => throw new HttpRequestException("offline")));

        var result = await AutoUpdaterService.FetchLatestAssetAsync(
            "https://api.github.com/repos/example/project/releases/latest",
            client);

        Assert.Equal(ReleaseAssetFetchStatus.NetworkError, result.Status);
        Assert.Contains("offline", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FetchLatestAsset_ReportsValidReleaseWithoutGuiAsset()
    {
        using var client = ClientReturning(
            HttpStatusCode.OK,
            """{"tag_name":"v5.1.0","assets":[{"name":"NVMeDriverPatcher.Cli.exe","browser_download_url":"https://github.com/example/cli"}]}""");

        var result = await AutoUpdaterService.FetchLatestAssetAsync(
            "https://api.github.com/repos/example/project/releases/latest",
            client);

        Assert.Equal(ReleaseAssetFetchStatus.NoSuitableAsset, result.Status);
        Assert.Equal("v5.1.0", result.Tag);
    }

    [Fact]
    public async Task FetchLatestAsset_ReturnsExactGuiAsset()
    {
        const string url = "https://github.com/example/gui";
        using var client = ClientReturning(
            HttpStatusCode.OK,
            $$"""{"tag_name":"v5.1.0","assets":[{"name":"NVMeDriverPatcher.exe","browser_download_url":"{{url}}"}]}""");

        var result = await AutoUpdaterService.FetchLatestAssetAsync(
            "https://api.github.com/repos/example/project/releases/latest",
            client);

        Assert.True(result.IsAvailable);
        Assert.Equal(ReleaseAssetFetchStatus.Available, result.Status);
        Assert.Equal("v5.1.0", result.Tag);
        Assert.Equal(AutoUpdaterService.GuiAssetName, result.Name);
        Assert.Equal(url, result.Url);
    }

    private static HttpClient ClientReturning(HttpStatusCode statusCode, string content) =>
        new(new StubHttpMessageHandler(
            _ => new HttpResponseMessage(statusCode) { Content = new StringContent(content) }));

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }

}
