using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

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

    [Fact]
    public async Task DownloadAsync_UsesOriginalReleaseSidecarBeforeRedirectTarget()
    {
        var payload = Encoding.ASCII.GetBytes(new string('A', 2048));
        var hash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var original = new Uri("https://github.com/owner/repo/releases/download/v1/NVMeDriverPatcher.exe");
        var final = new Uri("https://release-assets.githubusercontent.com/github-production-release-asset/1/asset.exe?sp=r");
        var originalSidecar = BuildSidecarUri(original);
        var finalSidecar = BuildSidecarUri(final);
        var handler = new RedirectSidecarHandler(
            original,
            final,
            payload,
            originalSidecarHash: hash,
            finalSidecarHash: new string('0', 64));
        using var client = new HttpClient(handler);
        var destination = Path.Combine(Path.GetTempPath(), $"nvme-patcher-test-{Guid.NewGuid():N}.exe");

        try
        {
            var result = await VerifiedDownloader.DownloadAsync(
                client,
                original,
                destination,
                TestDownloadPolicy(),
                CancellationToken.None);

            Assert.True(result.Success, result.Summary);
            Assert.Equal(VerifiedDownloader.IntegritySignal.Sha256Sidecar, result.Signal);
            Assert.Equal(payload, File.ReadAllBytes(destination));

            Assert.Contains(originalSidecar.AbsoluteUri, handler.Requests);
            Assert.DoesNotContain(finalSidecar.AbsoluteUri, handler.Requests);
        }
        finally
        {
            TryDelete(destination);
            TryDelete(destination + ".part");
        }
    }

    [Fact]
    public async Task DownloadAsync_FallsBackToRedirectTargetSidecarWhenOriginalSidecarMissing()
    {
        var payload = Encoding.ASCII.GetBytes(new string('B', 2048));
        var hash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var original = new Uri("https://github.com/owner/repo/releases/download/v1/NVMeDriverPatcher.exe");
        var final = new Uri("https://release-assets.githubusercontent.com/github-production-release-asset/1/asset.exe?sp=r");
        var originalSidecar = BuildSidecarUri(original);
        var finalSidecar = BuildSidecarUri(final);
        var handler = new RedirectSidecarHandler(
            original,
            final,
            payload,
            originalSidecarHash: null,
            finalSidecarHash: hash);
        using var client = new HttpClient(handler);
        var destination = Path.Combine(Path.GetTempPath(), $"nvme-patcher-test-{Guid.NewGuid():N}.exe");

        try
        {
            var result = await VerifiedDownloader.DownloadAsync(
                client,
                original,
                destination,
                TestDownloadPolicy(),
                CancellationToken.None);

            Assert.True(result.Success, result.Summary);
            Assert.Equal(VerifiedDownloader.IntegritySignal.Sha256Sidecar, result.Signal);
            Assert.Contains(originalSidecar.AbsoluteUri, handler.Requests);
            Assert.Contains(finalSidecar.AbsoluteUri, handler.Requests);
        }
        finally
        {
            TryDelete(destination);
            TryDelete(destination + ".part");
        }
    }

    private static VerifiedDownloader.DownloadPolicy TestDownloadPolicy() => new()
    {
        AllowedHosts = new[] { "github.com", "release-assets.githubusercontent.com" },
        MinBytes = 1,
        MaxBytes = 4096,
        RequireIntegrity = true,
        AllowAuthenticodeFallback = false
    };

    private static Uri BuildSidecarUri(Uri assetUri) =>
        new UriBuilder(assetUri) { Path = assetUri.AbsolutePath + ".sha256" }.Uri;

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    private sealed class RedirectSidecarHandler : HttpMessageHandler
    {
        private readonly Uri _original;
        private readonly Uri _final;
        private readonly byte[] _payload;
        private readonly string? _originalSidecarHash;
        private readonly string? _finalSidecarHash;

        public RedirectSidecarHandler(
            Uri original,
            Uri final,
            byte[] payload,
            string? originalSidecarHash,
            string? finalSidecarHash)
        {
            _original = original;
            _final = final;
            _payload = payload;
            _originalSidecarHash = originalSidecarHash;
            _finalSidecarHash = finalSidecarHash;
        }

        public List<string> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var requestUri = request.RequestUri ?? throw new InvalidOperationException("Missing request URI.");
            Requests.Add(requestUri.AbsoluteUri);

            if (SameUri(requestUri, _original))
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
                redirect.Headers.Location = _final;
                return Task.FromResult(redirect);
            }

            if (SameUri(requestUri, _final))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(_payload)
                });
            }

            if (SameUri(requestUri, BuildSidecarUri(_original)))
                return Task.FromResult(SidecarResponse(_originalSidecarHash));

            if (SameUri(requestUri, BuildSidecarUri(_final)))
                return Task.FromResult(SidecarResponse(_finalSidecarHash));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static bool SameUri(Uri left, Uri right) =>
            string.Equals(left.AbsoluteUri, right.AbsoluteUri, StringComparison.Ordinal);

        private static HttpResponseMessage SidecarResponse(string? hash)
        {
            if (hash is null)
                return new HttpResponseMessage(HttpStatusCode.NotFound);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"{hash}  NVMeDriverPatcher.exe", Encoding.ASCII)
            };
        }
    }
}
