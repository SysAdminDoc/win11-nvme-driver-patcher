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
    public static UpdateInfo? Check()
    {
        try
        {
            // Run on thread pool to avoid deadlock when called from UI synchronization context
            return Task.Run(CheckAsync).GetAwaiter().GetResult();
        }
        catch { return null; }
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

        // Some upstream tags include pre-release labels (e.g. "4.1.0-rc1"); strip those so
        // Version.TryParse doesn't reject otherwise-valid newer releases.
        int dashIdx = tagName.IndexOf('-');
        var versionPart = dashIdx > 0 ? tagName[..dashIdx] : tagName;
        if (!Version.TryParse(versionPart, out var latest)) return null;
        if (!Version.TryParse(AppConfig.AppVersion, out var current)) return null;
        if (latest <= current) return null;

        // Skip drafts and pre-releases by default — users who opt into pre-releases can use the
        // GitHub UI directly, but the in-app prompt should only point at stable.
        if (root.TryGetProperty("draft", out var draftProp) && draftProp.ValueKind == JsonValueKind.True)
            return null;
        if (root.TryGetProperty("prerelease", out var preProp) && preProp.ValueKind == JsonValueKind.True)
            return null;

        var htmlUrl = root.TryGetProperty("html_url", out var urlProp) ? urlProp.GetString() ?? "" : "";
        var body = root.TryGetProperty("body", out var bodyProp) ? bodyProp.GetString() ?? "" : "";
        if (body.Length > 200) body = body[..200] + "...";

        return new UpdateInfo { Version = tagName, URL = htmlUrl, Notes = body };
    }
}
