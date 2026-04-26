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
    /// <summary>Name of the verification signal that ran: "sha256", "authenticode", or "none".</summary>
    public string VerificationMethod { get; set; } = "none";
}

// Downloads a GitHub release asset into a staging folder beside the running exe, verifies
// the allowlisted download host + SHA-256 sidecar, and emits a swap script. The swap itself
// happens after the running exe exits — the CLI prints the PowerShell one-liner to the user.
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
        string stagingDir,
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
            Directory.CreateDirectory(stagingDir);
            var stagedPath = Path.Combine(stagingDir, targetAssetName);

            var policy = new VerifiedDownloader.DownloadPolicy
            {
                AllowedHosts = AllowedHosts,
                MinBytes = 1_048_576,     // 1 MB — below this is almost certainly a 404 page
                MaxBytes = 262_144_000,   // 250 MB — above this is out of scope
                MaxRedirects = 6,
                RequireIntegrity = true,             // never stage an unverified exe
                AllowAuthenticodeFallback = true
            };

            var download = await VerifiedDownloader
                .DownloadAsync(Http, uri, stagedPath, policy, cancellationToken)
                .ConfigureAwait(false);

            if (!download.Success)
            {
                result.Summary = download.Summary;
                return result;
            }

            result.Success = true;
            result.StagedPath = download.Path;
            result.ContentVerified = download.Signal != VerifiedDownloader.IntegritySignal.None;
            result.VerificationMethod = download.Signal switch
            {
                VerifiedDownloader.IntegritySignal.Sha256Sidecar => "sha256",
                VerifiedDownloader.IntegritySignal.Authenticode => "authenticode",
                _ => "none"
            };

            var currentExe = Environment.ProcessPath ?? "NVMeDriverPatcher.exe";
            result.RestartCommand = BuildRestartCommand(download.Path!, currentExe);
            result.Summary =
                $"Update staged ({result.VerificationMethod} verified). Run the printed RestartCommand in a separate PowerShell window, then exit the app.";
        }
        catch (Exception ex)
        {
            result.Summary = $"Staging failed: {ex.GetType().Name}: {ex.Message}";
        }
        return result;
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

    internal static string BuildRestartCommand(string stagedPath, string currentExe)
    {
        // PowerShell single-quoted strings treat '' as a literal apostrophe. Escape any
        // apostrophes in the paths so a pathological install path cannot break the command.
        var staged = stagedPath.Replace("'", "''");
        var current = currentExe.Replace("'", "''");
        return
            $"Start-Sleep -Seconds 2; " +
            $"Copy-Item -LiteralPath '{staged}' -Destination '{current}' -Force; " +
            $"Start-Process -FilePath '{current}'";
    }

    /// <summary>
    /// Convenience: query GitHub for the latest release and pick the first .exe asset. Returns
    /// null on any failure — caller shows a "update check failed" notice.
    /// </summary>
    public static async Task<(string? Url, string? Name, string? Tag)> FetchLatestAssetAsync(
        string apiReleasesUrl, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!Uri.TryCreate(apiReleasesUrl, UriKind.Absolute, out var uri))
                return (null, null, null);

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
                    return (null, null, null);

                using var req = new HttpRequestMessage(HttpMethod.Get, uri);
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
                using var resp = await Http.SendAsync(req, cancellationToken).ConfigureAwait(false);

                var status = (int)resp.StatusCode;
                if (status is >= 300 and <= 399)
                {
                    var location = resp.Headers.Location;
                    if (location is null) return (null, null, null);
                    uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                    continue;
                }

                if (!resp.IsSuccessStatusCode) return (null, null, null);
                var json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                var tag = doc.RootElement.TryGetProperty("tag_name", out var tn) ? tn.GetString() : null;
                if (!doc.RootElement.TryGetProperty("assets", out var assets)) return (null, null, tag);
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(name) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        return (url, name, tag);
                }
                return (null, null, tag);
            }
            return (null, null, null);
        }
        catch { return (null, null, null); }
    }
}
