using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

// The signed update manifest: what the updater accepts, what it refuses, and the download gate
// that stops an exe swapped together with its .sha256 sidecar.
public sealed class UpdateManifestTests : IDisposable
{
    private const string Release = "https://github.com/SysAdminDoc/win11-nvme-driver-patcher/releases/download/v9.0.0/";
    private static readonly Version Installed = new(5, 7, 0);
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    private readonly ECDsa _primary = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _next = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"NVMeDriverPatcher.UpdateManifest.Tests.{Guid.NewGuid():N}");

    private IReadOnlyList<string> Trusted => [Spki(_primary), Spki(_next)];

    public void Dispose()
    {
        _primary.Dispose();
        _next.Dispose();
        _stranger.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void EitherTrustedKey_Verifies_SoTheSigningKeyCanRotate()
    {
        var bytes = Manifest();

        var byPrimary = UpdateManifestService.Verify(bytes, Sign(_primary, bytes), Trusted, Installed, Now);
        var byNext = UpdateManifestService.Verify(bytes, Sign(_next, bytes), Trusted, Installed, Now);

        Assert.True(byPrimary.Success, byPrimary.Summary);
        Assert.True(byNext.Success, byNext.Summary);
        Assert.Equal(new Version(9, 0, 0), byPrimary.Manifest!.Version);
        Assert.Equal(GenuineHash, byPrimary.Manifest.Sha256For("NVMeDriverPatcher.exe"));
    }

    [Fact]
    public void UnknownKey_IsRefused()
    {
        var bytes = Manifest();

        var check = UpdateManifestService.Verify(bytes, Sign(_stranger, bytes), Trusted, Installed, Now);

        Assert.False(check.Success);
        Assert.Contains("doesn't match a key this version trusts", check.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ManifestEditedAfterSigning_IsRefused()
    {
        var bytes = Manifest();
        var signature = Sign(_primary, bytes);
        var edited = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace(GenuineHash, AttackerHash, StringComparison.Ordinal));

        var check = UpdateManifestService.Verify(edited, signature, Trusted, Installed, Now);

        Assert.False(check.Success);
        Assert.Null(check.Manifest);
    }

    [Fact]
    public void ExpiredManifest_IsRefused()
    {
        var bytes = Manifest(expires: Now.AddMinutes(-1));

        var check = UpdateManifestService.Verify(bytes, Sign(_primary, bytes), Trusted, Installed, Now);

        Assert.False(check.Success);
        Assert.Contains("expired", check.Summary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("5.6.0")]
    [InlineData("5.7.0")]
    public void ManifestNamingAnOlderOrTheSameVersion_IsRefused(string version)
    {
        var bytes = Manifest(version: version);

        var check = UpdateManifestService.Verify(bytes, Sign(_primary, bytes), Trusted, Installed, Now);

        Assert.False(check.Success);
        Assert.Contains("isn't newer than the installed 5.7.0", check.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallOlderThanTheManifestMinimum_IsSentToTheReleasePage()
    {
        var bytes = Manifest(minimum: "5.8.0");

        var check = UpdateManifestService.Verify(bytes, Sign(_primary, bytes), Trusted, Installed, Now);

        Assert.False(check.Success);
        Assert.Contains("needs 5.8.0 or later installed first", check.Summary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not base64 at all")]
    [InlineData("AAAA")]
    [InlineData("")]
    public void MalformedSignature_IsRefused(string signature)
    {
        var check = UpdateManifestService.Verify(Manifest(), signature, Trusted, Installed, Now);

        Assert.False(check.Success);
    }

    [Fact]
    public void SignedButUnreadableManifest_IsRefusedWithoutThrowing()
    {
        var bytes = Encoding.UTF8.GetBytes("""{"schema":1,"product":"NVMeDriverPatcher","version":"9.0.0"}""");

        var check = UpdateManifestService.Verify(bytes, Sign(_primary, bytes), Trusted, Installed, Now);

        Assert.False(check.Success);
        Assert.Contains("couldn't be read", check.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FetchedManifest_PinsTheAssetsHash()
    {
        var bytes = Manifest();
        using var client = ReleaseClient(bytes, Sign(_primary, bytes));

        var (summary, expected) = await AutoUpdaterService.FetchVerifiedManifestAsync(
            client, new Uri(Release + "NVMeDriverPatcher.exe"), Trusted, Installed, Now, CancellationToken.None);

        Assert.Equal(GenuineHash, expected);
        Assert.Contains("verified", summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManifestFromAnotherRelease_IsRefused()
    {
        var bytes = Manifest(version: "9.1.0");
        using var client = ReleaseClient(bytes, Sign(_primary, bytes));

        var (summary, expected) = await AutoUpdaterService.FetchVerifiedManifestAsync(
            client, new Uri(Release + "NVMeDriverPatcher.exe"), Trusted, Installed, Now, CancellationToken.None);

        Assert.Null(expected);
        Assert.Contains("download is from release v9.0.0", summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReleaseWithoutAManifest_IsRefused()
    {
        using var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));

        var (summary, expected) = await AutoUpdaterService.FetchVerifiedManifestAsync(
            client, new Uri(Release + "NVMeDriverPatcher.exe"), Trusted, Installed, Now, CancellationToken.None);

        Assert.Null(expected);
        Assert.Contains("has no signed update manifest", summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AssetTheManifestDoesntList_IsRefused()
    {
        var bytes = Manifest();
        using var client = ReleaseClient(bytes, Sign(_primary, bytes));

        var (summary, expected) = await AutoUpdaterService.FetchVerifiedManifestAsync(
            client, new Uri(Release + "Other.exe"), Trusted, Installed, Now, CancellationToken.None);

        Assert.Null(expected);
        Assert.Contains("doesn't list Other.exe", summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReplacedExeWithAMatchingReplacedSidecar_IsRefusedAndDeleted()
    {
        var attackerExe = Encoding.ASCII.GetBytes("MZ attacker payload");
        var attackerSidecar = $"{Sha256(attackerExe)}  NVMeDriverPatcher.exe";
        using var client = AssetClient(attackerExe, attackerSidecar);
        var staged = Path.Combine(_tempDir, "NVMeDriverPatcher.exe");

        var result = await AutoUpdaterService.DownloadPinnedAsync(
            client, new Uri(Release + "NVMeDriverPatcher.exe"), staged, TestPolicy, GenuineHash, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("not the signed update manifest", result.Summary, StringComparison.Ordinal);
        Assert.False(File.Exists(staged));
    }

    [Fact]
    public async Task GenuineExe_PassesBothTheSidecarAndTheManifest()
    {
        using var client = AssetClient(GenuineExe, $"{GenuineHash}  NVMeDriverPatcher.exe");
        var staged = Path.Combine(_tempDir, "NVMeDriverPatcher.exe");

        var result = await AutoUpdaterService.DownloadPinnedAsync(
            client, new Uri(Release + "NVMeDriverPatcher.exe"), staged, TestPolicy, GenuineHash, CancellationToken.None);

        Assert.True(result.Success, result.Summary);
        Assert.Equal(GenuineHash, result.VerifiedSha256);
        Assert.True(File.Exists(staged));
    }

    [Fact]
    public void ShippedKeys_AreTwoDistinctP256Keys_ThatTheReleaseValidatorCanFind()
    {
        Assert.Equal(2, UpdateManifestService.TrustedPublicKeys.Count);
        Assert.Equal(2, UpdateManifestService.TrustedPublicKeys.Distinct(StringComparer.Ordinal).Count());
        foreach (var key in UpdateManifestService.TrustedPublicKeys)
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(key), out _);
            Assert.Equal(256, ecdsa.KeySize);
        }

        // Validate-ReleaseAssets.ps1 takes every quoted string between the markers, in order.
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "NVMeDriverPatcher.Core", "Services", "UpdateManifestService.cs"));
        var start = source.IndexOf("// update-manifest-keys:start", StringComparison.Ordinal);
        var end = source.IndexOf("// update-manifest-keys:end", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var quoted = System.Text.RegularExpressions.Regex.Matches(source[start..end], "\"([A-Za-z0-9+/=]{80,})\"")
            .Select(m => m.Groups[1].Value)
            .ToArray();
        Assert.Equal(UpdateManifestService.TrustedPublicKeys, quoted);
    }

    private static readonly byte[] GenuineExe = Encoding.ASCII.GetBytes("MZ genuine release payload");
    private static readonly string GenuineHash = Sha256(GenuineExe);
    private static readonly string AttackerHash = new('a', 64);

    private static VerifiedDownloader.DownloadPolicy TestPolicy => new()
    {
        AllowedHosts = ["github.com"],
        MinBytes = 1,
        RequireIntegrity = true,
        AllowAuthenticodeFallback = false
    };

    private static byte[] Manifest(string version = "9.0.0", string? minimum = "5.0.0", DateTimeOffset? expires = null)
    {
        var minimumJson = minimum is null ? string.Empty : $"\"minimumVersion\": \"{minimum}\",\n";
        var json = $$"""
            {
              "schema": 1,
              "product": "NVMeDriverPatcher",
              "version": "{{version}}",
              {{minimumJson}}  "issuedUtc": "2026-10-01T00:00:00Z",
              "expiresUtc": "{{(expires ?? Now.AddDays(365)).UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}}",
              "assets": {
                "NVMeDriverPatcher.exe": "{{GenuineHash}}",
                "NVMeDriverPatcher-9.0.0.msi": "{{new string('b', 64)}}"
              }
            }
            """;
        return Encoding.UTF8.GetBytes(json);
    }

    private static string Sign(ECDsa key, byte[] bytes) =>
        Convert.ToBase64String(key.SignData(bytes, HashAlgorithmName.SHA256));

    private static string Spki(ECDsa key) => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static HttpClient ReleaseClient(byte[] manifest, string signature) =>
        new(new StubHandler(request => request.RequestUri!.AbsolutePath switch
        {
            var path when path.EndsWith("/" + UpdateManifestService.ManifestFileName, StringComparison.Ordinal) =>
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(manifest) },
            var path when path.EndsWith("/" + UpdateManifestService.SignatureFileName, StringComparison.Ordinal) =>
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(signature) },
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        }));

    private static HttpClient AssetClient(byte[] exe, string sidecar) =>
        new(new StubHandler(request => request.RequestUri!.AbsolutePath.EndsWith(".sha256", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sidecar) }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(exe) }));

    private static string RepoRoot([CallerFilePath] string sourceFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
}
