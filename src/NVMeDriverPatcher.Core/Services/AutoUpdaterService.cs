using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace NVMeDriverPatcher.Services;

public class AutoUpdateResult
{
    public bool Success { get; set; }
    public string Summary { get; set; } = string.Empty;
    public string? StagedPath { get; set; }
    public string? RestartCommand { get; set; }
    /// <summary>True when content-level verification (SHA-256 sidecar or Authenticode) passed.</summary>
    public bool ContentVerified { get; set; }
    /// <summary>Name of the verification signal that ran: "signed-manifest", "sha256", "authenticode", or "none".</summary>
    public string VerificationMethod { get; set; } = "none";
    /// <summary>Digest embedded into the post-exit swap command for a second verification.</summary>
    public string? ExpectedSha256 { get; set; }
}

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

// Downloads a GitHub release asset into an Administrators/SYSTEM-only ProgramData folder,
// verifies the allowlisted download host, the SHA-256 sidecar and the release's signed update
// manifest (UpdateManifestService), and emits a swap script. The swap
// itself happens after the running exe exits, so that script re-hashes before copying and again
// before launching the installed target.
//
// Heavy lifting (host allowlist, redirect handling, .part staging, size caps, SHA-256 +
// Authenticode verification, atomic promote) lives in VerifiedDownloader. This service stays
// focused on GitHub-API-asset discovery and the restart-command ergonomics.
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
        // AllowAutoRedirect=false is load-bearing: VerifiedDownloader drives redirects manually
        // so every hop is re-checked against AllowedHosts. If the client were to auto-follow
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

    public static async Task<AutoUpdateResult> StageUpdateAsync(
        string browserDownloadUrl,
        string targetAssetName,
        CancellationToken cancellationToken = default)
    {
        var result = new AutoUpdateResult();
        if (!Uri.TryCreate(browserDownloadUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            result.Summary = "Download URL must be an absolute https:// URL.";
            return result;
        }
        if (!AllowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
        {
            result.Summary = $"Download host '{uri.Host}' is not in the allowlist.";
            return result;
        }
        if (string.IsNullOrWhiteSpace(targetAssetName) || targetAssetName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            result.Summary = "Target asset name is invalid.";
            return result;
        }

        try
        {
            if (!UpdateService.TryParseComparableVersion(Models.AppConfig.AppVersion, out var installed))
            {
                result.Summary = "The installed version couldn't be read, so the update manifest can't be checked.";
                return result;
            }
            // The signed manifest comes first: without it nothing is downloaded or staged.
            var manifest = await FetchVerifiedManifestAsync(
                Http, uri, UpdateManifestService.TrustedPublicKeys, installed, DateTimeOffset.UtcNow, cancellationToken)
                .ConfigureAwait(false);
            if (manifest.ExpectedSha256 is null)
            {
                result.Summary = manifest.Summary;
                return result;
            }

            var stagingAccess = PrivilegedStateSecurityService.EnsureForUpdates();
            if (!stagingAccess.Success)
            {
                result.Summary = "Protected update staging is unavailable: " + stagingAccess.Summary;
                return result;
            }
            var stagingDir = stagingAccess.Directory;
            var stagedPath = Path.Combine(stagingDir, targetAssetName);

            var policy = new VerifiedDownloader.DownloadPolicy
            {
                AllowedHosts = AllowedHosts,
                MinBytes = 1_048_576,     // 1 MB — below this is almost certainly a 404 page
                MaxBytes = 262_144_000,   // 250 MB — above this is out of scope
                MaxRedirects = 6,
                RequireIntegrity = true,             // never stage an unverified exe
                // Authenticode fallback verifies signature VALIDITY, not signer IDENTITY, so it would
                // accept any validly-signed binary at the asset URL. This project ships UNSIGNED and
                // every release carries a SHA-256 sidecar, so require the sidecar and disable the
                // fallback rather than accept an unpinned signer for the in-place self-replace.
                AllowAuthenticodeFallback = false
            };

            var download = await DownloadPinnedAsync(
                Http, uri, stagedPath, policy, manifest.ExpectedSha256, cancellationToken).ConfigureAwait(false);
            if (!download.Success)
            {
                result.Summary = download.Summary;
                return result;
            }

            var protectedFile = PrivilegedStateSecurityService.ProtectCriticalFile(
                download.Path!, StateDirectoryRole.Privileged);
            if (!protectedFile.Success)
            {
                TryDelete(download.Path);
                result.Summary = "Staged update metadata is not trusted: " + protectedFile.Summary;
                return result;
            }

            // Re-read only after the file has an admin-only DACL and trusted reparse/hard-link
            // metadata. This must still match the digest VerifiedDownloader compared to the
            // release sidecar; otherwise no post-exit command is exposed.
            var protectedHash = await ComputeSha256Async(download.Path!, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(protectedHash, download.VerifiedSha256, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(download.Path);
                result.Summary = "Staged update changed while its protected metadata was established; aborting.";
                return result;
            }

            result.Success = true;
            result.StagedPath = download.Path;
            result.ContentVerified = true;
            result.ExpectedSha256 = protectedHash;
            result.VerificationMethod = "signed-manifest";

            var currentExe = Environment.ProcessPath ?? "NVMeDriverPatcher.exe";
            result.RestartCommand = BuildRestartCommand(download.Path!, currentExe, protectedHash);
            result.Summary =
                $"Update staged in protected ProgramData storage ({result.VerificationMethod} verified). Run the printed RestartCommand in a separate PowerShell window, then exit the app; it re-verifies SHA-256 before copy and launch.";
            if (SmartAppControlService.DownloadNote(SmartAppControlService.Read()) is string sacNote)
                result.Summary += " " + sacNote;
        }
        catch (Exception ex)
        {
            result.Summary = $"Staging failed: {ex.GetType().Name}: {ex.Message}";
        }
        return result;
    }

    /// <summary>
    /// Downloads through <see cref="VerifiedDownloader"/> (which checks the release's .sha256
    /// sidecar) and then requires the result to match the SHA-256 the signed manifest pins. A
    /// sidecar replaced along with the exe still matches it; the manifest doesn't. The staged
    /// file is deleted on any failure.
    /// </summary>
    internal static async Task<VerifiedDownloader.DownloadResult> DownloadPinnedAsync(
        HttpClient client,
        Uri uri,
        string stagedPath,
        VerifiedDownloader.DownloadPolicy policy,
        string manifestSha256,
        CancellationToken cancellationToken)
    {
        var download = await VerifiedDownloader
            .DownloadAsync(client, uri, stagedPath, policy, cancellationToken)
            .ConfigureAwait(false);
        if (!download.Success)
            return download;

        if (download.Signal != VerifiedDownloader.IntegritySignal.Sha256Sidecar ||
            string.IsNullOrWhiteSpace(download.VerifiedSha256))
        {
            TryDelete(download.Path);
            return new VerifiedDownloader.DownloadResult
            {
                Summary = "Update staging did not retain a sidecar-verified SHA-256; refusing to emit a swap command."
            };
        }

        if (!string.Equals(download.VerifiedSha256, manifestSha256, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(download.Path);
            return new VerifiedDownloader.DownloadResult
            {
                Summary = "The downloaded update matches its .sha256 file but not the signed update manifest; refusing it."
            };
        }
        return download;
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

    internal static Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken) =>
        VerifiedDownloader.ComputeSha256Async(path, cancellationToken);

    internal static bool VerifyAuthenticode(string path) =>
        VerifiedDownloader.VerifyAuthenticode(path);

    internal static string BuildRestartCommand(string stagedPath, string currentExe, string expectedSha256)
    {
        // PowerShell single-quoted strings treat '' as a literal apostrophe. Escape any
        // apostrophes in the paths so a pathological install path cannot break the command.
        var staged = stagedPath.Replace("'", "''");
        var current = currentExe.Replace("'", "''");
        var expected = ExtractSha256(expectedSha256)
            ?? throw new ArgumentException("A 64-character SHA-256 is required.", nameof(expectedSha256));
        return
            $"$expected='{expected}'; Start-Sleep -Seconds 2; " +
            $"$actual=(Get-FileHash -LiteralPath '{staged}' -Algorithm SHA256).Hash.ToLowerInvariant(); " +
            "if ($actual -ne $expected) { throw 'Staged update SHA-256 changed; refusing replacement.' }; " +
            $"Copy-Item -LiteralPath '{staged}' -Destination '{current}' -Force; " +
            $"$installed=(Get-FileHash -LiteralPath '{current}' -Algorithm SHA256).Hash.ToLowerInvariant(); " +
            "if ($installed -ne $expected) { throw 'Installed update SHA-256 does not match; refusing launch.' }; " +
            $"Start-Process -FilePath '{current}'";
    }

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try { File.Delete(path); } catch { }
    }

    // The only asset name the auto-updater may stage. Releases also upload CLI, tray,
    // watchdog, and MSI binaries — selecting "any .exe" made the update payload depend on
    // upload order. Exact match + fail-closed: if the GUI asset is absent, no update.
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
