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
        var response = await client.GetStringAsync(AppConfig.GitHubApiReleasesUrl).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(response);
        var root = doc.RootElement;

        var tagName = root.GetProperty("tag_name").GetString()?.TrimStart('v') ?? "";
        if (!Version.TryParse(tagName, out var latest)) return null;
        if (!Version.TryParse(AppConfig.AppVersion, out var current)) return null;
        if (latest <= current) return null;

        var htmlUrl = root.GetProperty("html_url").GetString() ?? "";
        var body = root.TryGetProperty("body", out var bodyProp) ? bodyProp.GetString() ?? "" : "";
        if (body.Length > 200) body = body[..200] + "...";

        return new UpdateInfo { Version = tagName, URL = htmlUrl, Notes = body };
    }
}
