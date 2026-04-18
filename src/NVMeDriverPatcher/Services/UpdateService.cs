using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using NVMeDriverPatcher.Models;

namespace NVMeDriverPatcher.Services;

public class UpdateInfo
{
    public string Version { get; set; } = string.Empty;
    public string URL { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
}

public static class UpdateService
{
    private static readonly object CacheLock = new();
    private static UpdateInfo? _cachedResult;
    private static DateTime _lastCheckUtc;
    private static bool _hasCachedCheck;
    private static bool _lastCheckFaulted;
    private static readonly TimeSpan SuccessCacheLifetime = TimeSpan.FromHours(6);
    private static readonly TimeSpan FailureCacheLifetime = TimeSpan.FromMinutes(15);

    public static UpdateInfo? Check()
    {
        try
        {
            lock (CacheLock)
            {
                var age = DateTime.UtcNow - _lastCheckUtc;
                var cacheLifetime = _lastCheckFaulted ? FailureCacheLifetime : SuccessCacheLifetime;
                if (_hasCachedCheck && _lastCheckUtc != default && age >= TimeSpan.Zero && age < cacheLifetime)
                    return _cachedResult;
            }

            // CheckAsync uses ConfigureAwait(false), so a synchronous wait here won't deadlock the UI thread.
            var result = CheckAsync().GetAwaiter().GetResult();

            lock (CacheLock)
            {
                _cachedResult = result;
                _lastCheckUtc = DateTime.UtcNow;
                _hasCachedCheck = true;
                _lastCheckFaulted = false;
            }

            return result;
        }
        catch
        {
            lock (CacheLock)
            {
                _cachedResult = null;
                _lastCheckUtc = DateTime.UtcNow;
                _hasCachedCheck = true;
                _lastCheckFaulted = true;
            }
            return null;
        }
    }

    private static async Task<UpdateInfo?> CheckAsync()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"NVMeDriverPatcher/{AppConfig.AppVersion}");
        // GitHub's API expects "Accept: application/vnd.github+json"; sending it sticks us to the
        // documented schema even if they roll out a default change later.
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        var response = await client.GetStringAsync(AppConfig.GitHubApiReleasesUrl).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(response);
        var root = doc.RootElement;

        if (!root.TryGetProperty("tag_name", out var tagProp)) return null;
        var tagName = tagProp.GetString()?.TrimStart('v', 'V').Trim() ?? "";
        if (string.IsNullOrEmpty(tagName)) return null;

        if (!TryParseComparableVersion(tagName, out var latest)) return null;
        if (!TryParseComparableVersion(AppConfig.AppVersion, out var current)) return null;
        if (latest <= current) return null;

        // Skip drafts and pre-releases by default — users who opt into pre-releases can use the
        // GitHub UI directly, but the in-app prompt should only point at stable.
        if (root.TryGetProperty("draft", out var draftProp) && draftProp.ValueKind == JsonValueKind.True)
            return null;
        if (root.TryGetProperty("prerelease", out var preProp) && preProp.ValueKind == JsonValueKind.True)
            return null;

        var htmlUrl = root.TryGetProperty("html_url", out var urlProp)
            ? SanitizeReleaseUrl(urlProp.GetString())
            : "";
        if (string.IsNullOrEmpty(htmlUrl))
            htmlUrl = AppConfig.GitHubURL;
        var body = root.TryGetProperty("body", out var bodyProp) ? bodyProp.GetString() ?? "" : "";
        if (body.Length > 200) body = body[..200] + "...";

        return new UpdateInfo { Version = tagName, URL = htmlUrl, Notes = body };
    }

    internal static bool TryParseComparableVersion(string? rawVersion, out Version version)
    {
        version = new Version();
        var normalized = NormalizeVersionString(rawVersion);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        if (!Version.TryParse(normalized, out var parsed) || parsed is null)
            return false;

        version = parsed;
        return true;
    }

    internal static string NormalizeVersionString(string? rawVersion)
    {
        if (string.IsNullOrWhiteSpace(rawVersion))
            return string.Empty;

        var normalized = rawVersion.Trim().TrimStart('v', 'V');
        int suffixStart = normalized.IndexOfAny(['-', '+']);
        if (suffixStart > 0)
            normalized = normalized[..suffixStart];
        return normalized.Trim();
    }

    internal static string SanitizeReleaseUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return string.Empty;

        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            return string.Empty;

        return uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            ? uri.ToString()
            : string.Empty;
    }
}
