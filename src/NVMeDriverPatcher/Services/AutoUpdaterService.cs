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
}

// Downloads a GitHub release asset into a staging folder beside the running exe, verifies
// the allowlisted download host, and emits a swap script. The swap itself has to happen
// after the running exe exits — the CLI prints the PowerShell one-liner to the user.
//
// Host whitelist mirrors ViVeToolService's — only github.com-adjacent hosts allowed.
public static class AutoUpdaterService
{
    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "github.com",
        "api.github.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com",
        "codeload.github.com"
    };

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

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
        if (!AllowedHosts.Contains(uri.Host))
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
            using (var req = new HttpRequestMessage(HttpMethod.Get, uri))
            {
                req.Headers.UserAgent.Add(new ProductInfoHeaderValue("NVMeDriverPatcher", Models.AppConfig.AppVersion));
                using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                resp.EnsureSuccessStatusCode();
                using var outFs = File.Create(stagedPath);
                await resp.Content.CopyToAsync(outFs, cancellationToken);
            }

            // Sanity-check the download: 1 MB minimum, 250 MB maximum. Anything outside is
            // either a 404 page or a supply-chain bomb.
            var info = new FileInfo(stagedPath);
            if (info.Length < 1_048_576 || info.Length > 262_144_000)
            {
                File.Delete(stagedPath);
                result.Summary = $"Staged file size out of range ({info.Length} bytes).";
                return result;
            }

            result.Success = true;
            result.StagedPath = stagedPath;
            // The caller exits; a short PowerShell one-liner swaps the file in after exit.
            var currentExe = Environment.ProcessPath ?? "NVMeDriverPatcher.exe";
            result.RestartCommand =
                $"Start-Sleep -Seconds 2; Copy-Item -Force \"{stagedPath}\" \"{currentExe}\"; Start-Process \"{currentExe}\"";
            result.Summary = "Update staged. Run the printed RestartCommand in a separate PowerShell window, then exit the app.";
        }
        catch (Exception ex)
        {
            result.Summary = $"Staging failed: {ex.GetType().Name}: {ex.Message}";
        }
        return result;
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
            using var req = new HttpRequestMessage(HttpMethod.Get, apiReleasesUrl);
            req.Headers.UserAgent.Add(new ProductInfoHeaderValue("NVMeDriverPatcher", Models.AppConfig.AppVersion));
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var resp = await Http.SendAsync(req, cancellationToken);
            if (!resp.IsSuccessStatusCode) return (null, null, null);
            var json = await resp.Content.ReadAsStringAsync(cancellationToken);
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
        catch { return (null, null, null); }
    }
}
