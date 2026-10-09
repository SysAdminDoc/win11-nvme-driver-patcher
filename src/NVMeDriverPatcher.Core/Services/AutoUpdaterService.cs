using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace NVMeDriverPatcher.Services;

public enum ReleaseAssetFetchStatus
{
    Available,
    NoSuitableAsset,
    InvalidRequest,
    UnsafeRedirect,
    RedirectLimitExceeded,
    RateLimited,
    HttpError,
    NetworkError,
    InvalidResponse
}

public sealed class ReleaseAssetFetchResult
{
    public ReleaseAssetFetchStatus Status { get; init; }
    public string Summary { get; init; } = string.Empty;
    public string? Url { get; init; }
    public string? Name { get; init; }
    public string? Tag { get; init; }
    public int? HttpStatusCode { get; init; }
    public bool IsAvailable => Status == ReleaseAssetFetchStatus.Available;
}

// Finds the latest GitHub release's GUI asset for `update-check` and checks that release's signed
// update manifest (UpdateManifestService). Only the release metadata, the manifest and its
// signature are fetched. The app never downloads or replaces itself: an in-place swap of the GUI
// exe alone would leave an MSI install with mismatched CLI, tray and watchdog binaries, and an MSI
// repair would put the old GUI back.
public static class AutoUpdaterService
{
    private static readonly IReadOnlyCollection<string> AllowedHosts = new[]
    {
        "github.com",
        "api.github.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com",
        "codeload.github.com"
    };

    private static readonly HttpClient Http = CreateSharedClient();

    private static HttpClient CreateSharedClient()
    {
        // AllowAutoRedirect=false is load-bearing: VerifiedDownloader and FetchLatestAssetAsync drive
        // redirects manually so every hop is re-checked against AllowedHosts. If the client were to auto-follow
        // redirects, our per-hop allowlist check would run once (on the final response) and
        // miss the intermediate hops entirely — a compromised CDN could then steer into an
        // unlisted host without us noticing until the end.
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("NVMeDriverPatcher", Models.AppConfig.AppVersion));
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        return client;
    }

    /// <summary>
    /// Whether the release behind <paramref name="assetUrl"/> carries a signed update manifest that
    /// verifies against the shipped keys, names that release and a newer version than this one, and
    /// pins a SHA-256 for the asset. Nothing but the manifest and its signature is downloaded.
    /// </summary>
    public static Task<(bool Verified, string Summary, string? Sha256)> CheckReleaseManifestAsync(
        string? assetUrl, CancellationToken cancellationToken = default)
    {
        if (!UpdateService.TryParseComparableVersion(Models.AppConfig.AppVersion, out var installed))
            return Task.FromResult<(bool, string, string?)>(
                (false, "The installed version couldn't be read, so the update manifest can't be checked.", null));
        return CheckReleaseManifestAsync(
            assetUrl, Http, UpdateManifestService.TrustedPublicKeys, installed, DateTimeOffset.UtcNow, cancellationToken);
    }

    internal static async Task<(bool Verified, string Summary, string? Sha256)> CheckReleaseManifestAsync(
        string? assetUrl,
        HttpClient client,
        IReadOnlyList<string> trustedKeys,
        Version installedVersion,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(assetUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !AllowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
        {
            return (false, "The release asset URL isn't an https GitHub address, so its manifest wasn't checked.", null);
        }
        try
        {
            var (summary, sha256) = await FetchVerifiedManifestAsync(
                client, uri, trustedKeys, installedVersion, nowUtc, cancellationToken).ConfigureAwait(false);
            return (sha256 is not null, summary, sha256);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException ||
                                   (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return (false, $"The signed update manifest couldn't be fetched: {ex.Message}", null);
        }
    }

    /// <summary>
    /// Fetches <see cref="UpdateManifestService.ManifestFileName"/> and its signature from the
    /// release folder beside <paramref name="assetUri"/>, verifies them, and checks the manifest
    /// names the release tag in the URL and pins a SHA-256 for this asset. ExpectedSha256 is null
    /// on any failure, with the reason in Summary.
    /// </summary>
    internal static async Task<(string Summary, string? ExpectedSha256)> FetchVerifiedManifestAsync(
        HttpClient client,
        Uri assetUri,
        IReadOnlyList<string> trustedKeys,
        Version installedVersion,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var segments = assetUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        int download = Array.FindIndex(segments, s => s.Equals("download", StringComparison.OrdinalIgnoreCase));
        if (download < 1 || !segments[download - 1].Equals("releases", StringComparison.OrdinalIgnoreCase) ||
            segments.Length != download + 3)
        {
            return ("The update URL isn't a GitHub release download, so its signed manifest can't be found.", null);
        }
        var tag = Uri.UnescapeDataString(segments[download + 1]);
        var assetName = Uri.UnescapeDataString(segments[download + 2]);

        var manifestBytes = await VerifiedDownloader.TryFetchSmallFileAsync(
            client, new Uri(assetUri, UpdateManifestService.ManifestFileName), AllowedHosts,
            UpdateManifestService.MaxManifestBytes, cancellationToken).ConfigureAwait(false);
        var signatureBytes = await VerifiedDownloader.TryFetchSmallFileAsync(
            client, new Uri(assetUri, UpdateManifestService.SignatureFileName), AllowedHosts,
            UpdateManifestService.MaxSignatureBytes, cancellationToken).ConfigureAwait(false);
        if (manifestBytes is null || signatureBytes is null)
            return ($"Release {tag} has no signed update manifest. Download it from the release page instead.", null);

        var check = UpdateManifestService.Verify(
            manifestBytes, System.Text.Encoding.ASCII.GetString(signatureBytes), trustedKeys, installedVersion, nowUtc);
        if (!check.Success || check.Manifest is null)
            return (check.Summary, null);

        if (!UpdateService.TryParseComparableVersion(tag, out var tagVersion) || tagVersion != check.Manifest.Version)
            return ($"The signed update manifest names {check.Manifest.Version}, but the download is from release {tag}.", null);

        var expected = check.Manifest.Sha256For(assetName);
        return expected is null
            ? ($"The signed update manifest for {check.Manifest.Version} doesn't list {assetName}.", null)
            : (check.Summary, expected);
    }

    // Test-facing surface. These delegate to VerifiedDownloader so the existing tests keep
    // exercising the real parsing/escape logic regardless of which service owns it.
    internal static Task<string?> TryFetchSidecarHashAsync(Uri assetUri, CancellationToken cancellationToken) =>
        VerifiedDownloader.TryFetchSidecarHashAsync(Http, assetUri, AllowedHosts, cancellationToken);

    internal static string? ExtractSha256(string? text) =>
        VerifiedDownloader.ExtractSha256(text);

    // The only asset update-check reports. Releases also upload CLI, tray, watchdog, and MSI
    // binaries; selecting "any .exe" made the answer depend on upload order. Exact match and
    // fail-closed: if the GUI asset is absent, there's no update to offer.
    internal const string GuiAssetName = "NVMeDriverPatcher.exe";

    /// <summary>
    /// Pure selection: returns the asset whose name is exactly <see cref="GuiAssetName"/>
    /// (case-insensitive), or (null, null) when the release carries no GUI payload.
    /// Never falls back to other executables.
    /// </summary>
    internal static (string? Url, string? Name) SelectGuiAsset(IEnumerable<(string? Name, string? Url)> assets)
    {
        foreach (var (name, url) in assets)
        {
            if (string.Equals(name, GuiAssetName, StringComparison.OrdinalIgnoreCase))
                return (url, name);
        }
        return (null, null);
    }

    /// <summary>
    /// Query GitHub for the latest release and pick the exact GUI asset
    /// (<see cref="GuiAssetName"/>). Operational, response, and valid-no-asset outcomes remain
    /// distinct so automation never reports an offline or rate-limited check as up to date.
    /// </summary>
    public static Task<ReleaseAssetFetchResult> FetchLatestAssetAsync(
        string apiReleasesUrl, CancellationToken cancellationToken = default) =>
        FetchLatestAssetAsync(apiReleasesUrl, Http, cancellationToken);

    internal static async Task<ReleaseAssetFetchResult> FetchLatestAssetAsync(
        string apiReleasesUrl,
        HttpClient client,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!Uri.TryCreate(apiReleasesUrl, UriKind.Absolute, out var uri))
                return Failure(ReleaseAssetFetchStatus.InvalidRequest, "Release API URL is not an absolute URI.");

            // The shared Http handler has AllowAutoRedirect=false (required for
            // VerifiedDownloader's per-hop allowlist enforcement on the download path). The
            // GitHub /releases/latest endpoint is historically non-redirecting, but GitHub has
            // shifted the underlying URL shape before — any future move that introduces a 30x
            // would silently turn this method into a no-op that returns (null,null,null) and
            // confuses users with a permanent "no updates found" state. Walk up to 5 hops
            // manually, keeping every host inside the same allowlist the downloader uses.
            for (int hops = 0; hops < 5; hops++)
            {
                if (!AllowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
                    return Failure(
                        ReleaseAssetFetchStatus.UnsafeRedirect,
                        $"Release API host '{uri.Host}' is not allowlisted.");

                using var req = new HttpRequestMessage(HttpMethod.Get, uri);
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
                using var resp = await client.SendAsync(req, cancellationToken).ConfigureAwait(false);

                var status = (int)resp.StatusCode;
                if (status is >= 300 and <= 399)
                {
                    var location = resp.Headers.Location;
                    if (location is null)
                        return Failure(
                            ReleaseAssetFetchStatus.InvalidResponse,
                            $"Release API redirect HTTP {status} omitted Location.",
                            status);
                    try { uri = location.IsAbsoluteUri ? location : new Uri(uri, location); }
                    catch (UriFormatException)
                    {
                        return Failure(
                            ReleaseAssetFetchStatus.InvalidResponse,
                            $"Release API redirect HTTP {status} supplied an invalid Location.",
                            status);
                    }
                    continue;
                }

                if (status is 403 or 429)
                    return Failure(
                        ReleaseAssetFetchStatus.RateLimited,
                        $"GitHub release API rate limit returned HTTP {status}.",
                        status);
                if (!resp.IsSuccessStatusCode)
                    return Failure(
                        ReleaseAssetFetchStatus.HttpError,
                        $"GitHub release API returned HTTP {status}.",
                        status);

                var json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    return Failure(ReleaseAssetFetchStatus.InvalidResponse, "Release API response root is not an object.");

                var tag = doc.RootElement.TryGetProperty("tag_name", out var tn) &&
                          tn.ValueKind == JsonValueKind.String
                    ? tn.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(tag))
                    return Failure(ReleaseAssetFetchStatus.InvalidResponse, "Release API response omitted tag_name.");
                if (!doc.RootElement.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                    return Failure(ReleaseAssetFetchStatus.InvalidResponse, "Release API response omitted the assets array.");

                var candidates = new List<(string? Name, string? Url)>();
                foreach (var asset in assets.EnumerateArray())
                {
                    if (asset.ValueKind != JsonValueKind.Object)
                        return Failure(ReleaseAssetFetchStatus.InvalidResponse, "Release API assets contained a non-object entry.");
                    var name = asset.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                        ? n.GetString()
                        : null;
                    var url = asset.TryGetProperty("browser_download_url", out var u) &&
                              u.ValueKind == JsonValueKind.String
                        ? u.GetString()
                        : null;
                    candidates.Add((name, url));
                }
                var (selUrl, selName) = SelectGuiAsset(candidates);
                if (string.IsNullOrWhiteSpace(selUrl) || string.IsNullOrWhiteSpace(selName))
                {
                    return new ReleaseAssetFetchResult
                    {
                        Status = ReleaseAssetFetchStatus.NoSuitableAsset,
                        Summary = $"Release {tag} does not contain {GuiAssetName}.",
                        Tag = tag
                    };
                }

                return new ReleaseAssetFetchResult
                {
                    Status = ReleaseAssetFetchStatus.Available,
                    Summary = $"Release {tag} contains {selName}.",
                    Url = selUrl,
                    Name = selName,
                    Tag = tag
                };
            }
            return Failure(
                ReleaseAssetFetchStatus.RedirectLimitExceeded,
                "Release API exceeded the five-redirect limit.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(ReleaseAssetFetchStatus.NetworkError, "Release API request timed out.");
        }
        catch (HttpRequestException ex)
        {
            return Failure(ReleaseAssetFetchStatus.NetworkError, $"Release API request failed: {ex.Message}");
        }
        catch (JsonException ex)
        {
            return Failure(ReleaseAssetFetchStatus.InvalidResponse, $"Release API returned invalid JSON: {ex.Message}");
        }
    }

    private static ReleaseAssetFetchResult Failure(
        ReleaseAssetFetchStatus status,
        string summary,
        int? httpStatusCode = null) =>
        new()
        {
            Status = status,
            Summary = summary,
            HttpStatusCode = httpStatusCode
        };
}
