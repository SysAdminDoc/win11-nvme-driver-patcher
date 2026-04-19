using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using NVMeDriverPatcher.Models;

namespace NVMeDriverPatcher.Services;

// ViVeTool fallback path — when the FeatureManagement\Overrides route is blocked
// (Microsoft's Feb/Mar 2026 Insider change), the community solution is ViVeTool, which
// writes the new feature IDs to a different store entirely. We download ViVeTool from
// its official GitHub releases, cache it in the working dir, then shell out.
//
// Source: https://github.com/thebookisclosed/ViVe (permissive, MIT-style license)
// Relevant feature IDs on post-block builds: 60786016 + 48433719.
//
// We explicitly do NOT bundle vivetool.exe in the installer — that would drag a binary
// we don't sign into our release. Auto-download on demand keeps the dependency honest
// and always current.
public static class ViVeToolService
{
    public const string ViVeToolRepo = "thebookisclosed/ViVe";
    public const string ViVeToolLatestApi = "https://api.github.com/repos/thebookisclosed/ViVe/releases/latest";
    public const string ViVeToolProjectUrl = "https://github.com/thebookisclosed/ViVe";

    // Only download from these hosts. GitHub redirects release assets to
    // objects.githubusercontent.com (and, during rollouts, release-assets.githubusercontent.com).
    // Anything else — including a future release with a tampered `browser_download_url` that
    // points off-GitHub — is refused before we hit the network a second time.
    private static readonly string[] AllowedAssetHosts =
    {
        "github.com",
        "api.github.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com",
        "codeload.github.com"
    };

    // Sanity bounds on the downloaded archive. ViVeTool is a tiny .NET exe (~60 KB zipped at
    // the time of writing). If a release ever exceeds 32 MB that's almost certainly not the
    // tool we expect, so we refuse rather than extract it blindly.
    private const long MinAssetBytes = 10 * 1024;
    private const long MaxAssetBytes = 32 * 1024 * 1024;

    // Serializes concurrent EnsureInstalledAsync calls so a double-click can't start two
    // parallel downloads that race on the tools folder.
    private static readonly SemaphoreSlim _installLock = new(1, 1);

    // The two feature IDs the community moved to after the Feb/Mar 2026 block.
    // Verified via HotHardware / Tom's Hardware / gamegpu.com reporting.
    public static IReadOnlyList<string> FallbackFeatureIDs { get; } = ["60786016", "48433719"];

    public static string ToolsDir(string workingDir) => Path.Combine(workingDir, "tools");
    public static string CachedExePath(string workingDir) => Path.Combine(ToolsDir(workingDir), "ViVeTool.exe");

    // A 0-byte cache file from a previously-failed extraction would silently fool IsInstalled
    // into reporting success. Require a non-trivial file size before we trust the cached copy.
    public static bool IsInstalled(string workingDir)
    {
        try
        {
            var path = CachedExePath(workingDir);
            if (!File.Exists(path)) return false;
            var fi = new FileInfo(path);
            return fi.Length >= MinAssetBytes;
        }
        catch { return false; }
    }

    public class ViVeToolResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public string? ExePath { get; set; }
        public string? Version { get; set; }
        public List<string> AppliedIDs { get; set; } = [];
    }

    public static async Task<ViVeToolResult> EnsureInstalledAsync(string workingDir, Action<string>? log = null)
    {
        // Guard against concurrent invocations (e.g. user double-click on the fallback badge).
        // With the 2-minute timeout, even if the current holder is stuck on a slow download,
        // a second caller eventually gets a clean "something else is installing" error instead
        // of racing into the same tools folder.
        if (!await _installLock.WaitAsync(TimeSpan.FromMinutes(2)).ConfigureAwait(false))
        {
            return new ViVeToolResult
            {
                Message = "Another ViVeTool install is already in progress. Try again in a moment.",
                ExePath = CachedExePath(workingDir)
            };
        }
        try
        {
            return await EnsureInstalledInnerAsync(workingDir, log).ConfigureAwait(false);
        }
        finally
        {
            _installLock.Release();
        }
    }

    private static async Task<ViVeToolResult> EnsureInstalledInnerAsync(string workingDir, Action<string>? log)
    {
        var result = new ViVeToolResult { ExePath = CachedExePath(workingDir) };

        if (IsInstalled(workingDir))
        {
            log?.Invoke($"ViVeTool already cached at {result.ExePath}");
            result.Success = true;
            result.Message = "Cached copy in use";
            return result;
        }

        try
        {
            Directory.CreateDirectory(ToolsDir(workingDir));
        }
        catch (Exception ex)
        {
            result.Message = $"Could not create tools directory: {ex.Message}";
            return result;
        }

        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(90) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"NVMeDriverPatcher/{AppConfig.AppVersion}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        string? assetUrl = null;
        string? tagName = null;
        long? expectedSize = null;
        try
        {
            log?.Invoke($"Querying GitHub for latest ViVeTool release ({ViVeToolRepo})...");
            var json = await client.GetStringAsync(ViVeToolLatestApi).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("tag_name", out var tagProp))
                tagName = tagProp.GetString();

            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    if (!asset.TryGetProperty("name", out var nameProp)) continue;
                    var name = nameProp.GetString() ?? string.Empty;
                    // The canonical release asset is ViVeTool-vX.Y.Z.zip — anything else is skipped.
                    if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!name.StartsWith("ViVeTool", StringComparison.OrdinalIgnoreCase)) continue;
                    if (asset.TryGetProperty("browser_download_url", out var urlProp))
                    {
                        assetUrl = urlProp.GetString();
                        if (asset.TryGetProperty("size", out var sizeProp) && sizeProp.ValueKind == JsonValueKind.Number)
                        {
                            if (sizeProp.TryGetInt64(out var sz)) expectedSize = sz;
                        }
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            result.Message = $"GitHub API unreachable: {ex.Message}";
            return result;
        }

        if (string.IsNullOrEmpty(assetUrl))
        {
            result.Message = "No ViVeTool release asset found on GitHub.";
            return result;
        }

        // Host whitelist. If a future release's JSON points `browser_download_url` at a host
        // we don't trust (compromise, typo, repo transfer), we refuse before fetching anything.
        if (!Uri.TryCreate(assetUrl, UriKind.Absolute, out var assetUri) || !IsTrustedAssetUri(assetUri))
        {
            result.Message = $"Asset URL is not a trusted HTTPS GitHub asset URL: {assetUrl}";
            return result;
        }

        // Size sanity check before we even download (from the API's reported size).
        if (expectedSize is { } sz0 && (sz0 < MinAssetBytes || sz0 > MaxAssetBytes))
        {
            result.Message = $"Asset size {sz0} bytes is outside expected range ({MinAssetBytes}..{MaxAssetBytes}); refusing download.";
            return result;
        }

        var tempZip = Path.Combine(ToolsDir(workingDir), $"vivetool-download-{Guid.NewGuid():N}.zip");
        // Extract into a fresh staging folder, promote atomically. If extraction crashes
        // midway, the cached exe path stays empty rather than half-populated.
        var stagingDir = Path.Combine(ToolsDir(workingDir), $"staging-{Guid.NewGuid():N}");
        try
        {
            log?.Invoke($"Downloading ViVeTool {(tagName ?? "latest")} from {assetUri}");
            await DownloadTrustedAssetAsync(client, assetUri, tempZip).ConfigureAwait(false);

            // Verify the downloaded file size matches what the API told us, and fits our bounds.
            var actualSize = new FileInfo(tempZip).Length;
            if (actualSize < MinAssetBytes || actualSize > MaxAssetBytes)
            {
                result.Message = $"Downloaded file size {actualSize} bytes is outside expected range ({MinAssetBytes}..{MaxAssetBytes}); refusing extraction.";
                return result;
            }
            if (expectedSize is { } esz && actualSize != esz)
            {
                result.Message = $"Downloaded file size {actualSize} doesn't match the API's reported {esz}; refusing extraction.";
                return result;
            }

            log?.Invoke("Extracting vivetool payload...");
            Directory.CreateDirectory(stagingDir);
            // Trailing separator appended so the zip-slip prefix check isn't fooled by a
            // sibling folder whose name starts with the staging-dir name (e.g. "staging-abc_evil").
            var stagingPrefix = Path.GetFullPath(stagingDir);
            if (!stagingPrefix.EndsWith(Path.DirectorySeparatorChar))
                stagingPrefix += Path.DirectorySeparatorChar;

            using (var archive = ZipFile.OpenRead(tempZip))
            {
                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue;
                    // Defense against zip-slip: refuse entries whose full path escapes stagingDir.
                    var targetPath = Path.GetFullPath(Path.Combine(stagingDir, entry.FullName));
                    if (!targetPath.StartsWith(stagingPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        log?.Invoke($"[WARNING] Skipping zip entry with suspicious path: {entry.FullName}");
                        continue;
                    }
                    var targetDir = Path.GetDirectoryName(targetPath);
                    if (!string.IsNullOrEmpty(targetDir)) Directory.CreateDirectory(targetDir);
                    entry.ExtractToFile(targetPath, overwrite: true);
                }
            }

            var payloadRoot = FindViVeToolPayloadRoot(stagingDir);
            if (payloadRoot is null)
            {
                result.Message = "ViVeTool.exe missing from the extracted archive.";
                return result;
            }

            // Promote every staged file into the final tools/ folder. ViVeTool.exe expects its
            // sibling DLLs alongside it; we preserve the layout the archive shipped with.
            foreach (var src in Directory.EnumerateFiles(payloadRoot, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(payloadRoot, src);
                var dst = Path.Combine(ToolsDir(workingDir), rel);
                var dstDir = Path.GetDirectoryName(dst);
                if (!string.IsNullOrEmpty(dstDir)) Directory.CreateDirectory(dstDir);
                try { if (File.Exists(dst)) File.Delete(dst); } catch { }
                File.Move(src, dst);
            }
        }
        catch (Exception ex)
        {
            result.Message = $"Download or extraction failed: {ex.Message}";
            return result;
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
            try { if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, recursive: true); } catch { }
        }

        if (!IsInstalled(workingDir))
        {
            result.Message = "ViVeTool.exe missing or too small after extraction.";
            return result;
        }

        result.Success = true;
        result.Version = tagName;
        result.Message = $"Installed ViVeTool {(tagName ?? "latest")}";
        log?.Invoke(result.Message);
        return result;
    }

    internal static bool IsTrustedAssetUri(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps &&
        AllowedAssetHosts.Any(h => uri.Host.Equals(h, StringComparison.OrdinalIgnoreCase));

    internal static string? FindViVeToolPayloadRoot(string stagingDir)
    {
        try
        {
            return Directory
                .EnumerateFiles(stagingDir, "ViVeTool.exe", SearchOption.AllDirectories)
                .Select(path => new
                {
                    Path = path,
                    Depth = System.IO.Path.GetRelativePath(stagingDir, path)
                        .Count(c => c == System.IO.Path.DirectorySeparatorChar || c == System.IO.Path.AltDirectorySeparatorChar)
                })
                .OrderBy(candidate => candidate.Depth)
                .Select(candidate => System.IO.Path.GetDirectoryName(candidate.Path))
                .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
        }
        catch
        {
            return null;
        }
    }

    private static async Task DownloadTrustedAssetAsync(HttpClient client, Uri initialUri, string destination)
    {
        var current = initialUri;
        for (int redirectCount = 0; redirectCount < 6; redirectCount++)
        {
            if (!IsTrustedAssetUri(current))
                throw new InvalidOperationException($"Refusing untrusted download URL: {current}");

            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            using var response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead)
                .ConfigureAwait(false);

            if (IsRedirect(response.StatusCode))
            {
                var location = response.Headers.Location
                    ?? throw new InvalidOperationException("Download redirect did not include a Location header.");
                var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                if (!IsTrustedAssetUri(next))
                    throw new InvalidOperationException($"Refusing redirect to untrusted host: {next.Host}");

                current = next;
                continue;
            }

            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } length &&
                (length < MinAssetBytes || length > MaxAssetBytes))
            {
                throw new InvalidOperationException(
                    $"Download Content-Length {length} bytes is outside expected range ({MinAssetBytes}..{MaxAssetBytes}).");
            }

            await using var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
            var buffer = new byte[81920];
            long total = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer).ConfigureAwait(false);
                if (read == 0) break;
                total += read;
                if (total > MaxAssetBytes)
                    throw new InvalidOperationException($"Download exceeded maximum size ({MaxAssetBytes} bytes).");
                await output.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
            }

            if (total < MinAssetBytes)
                throw new InvalidOperationException($"Download was too small ({total} bytes).");
            return;
        }

        throw new InvalidOperationException("Download followed too many redirects.");
    }

    private static bool IsRedirect(System.Net.HttpStatusCode statusCode)
    {
        var code = (int)statusCode;
        return code is >= 300 and <= 399;
    }

    // Runs ViVeTool.exe /enable for each supplied feature ID. Returns on first failure —
    // caller surfaces the concatenated output. We deliberately run one ID at a time so a
    // partial failure is obvious in the log, even if ViVeTool supports batching.
    public static async Task<ViVeToolResult> ApplyFallbackAsync(
        string workingDir,
        Action<string>? log = null)
    {
        var result = new ViVeToolResult();

        var install = await EnsureInstalledAsync(workingDir, log).ConfigureAwait(false);
        if (!install.Success)
        {
            result.Message = install.Message;
            return result;
        }
        result.ExePath = install.ExePath;
        result.Version = install.Version;

        foreach (var id in FallbackFeatureIDs)
        {
            // Defense in depth — the IDs are static constants today, but validate the digit-only
            // shape so a future editor can't accidentally smuggle an argument injection.
            if (string.IsNullOrEmpty(id) || !id.All(char.IsDigit))
            {
                result.Message = $"Refusing to invoke ViVeTool with non-numeric feature ID '{id}'.";
                result.AppliedIDs.Clear();
                return result;
            }
            var stepOk = await RunViVeToolAsync(install.ExePath!, new[] { "/enable", $"/id:{id}" }, log).ConfigureAwait(false);
            if (!stepOk)
            {
                result.Message = $"ViVeTool rejected feature ID {id}. See the activity log for details.";
                result.AppliedIDs.Clear();
                return result;
            }
            result.AppliedIDs.Add(id);
        }

        result.Success = true;
        result.Message = $"Applied {result.AppliedIDs.Count} fallback feature ID(s). Restart required.";
        EventLogService.Write(
            $"ViVeTool fallback applied ({string.Join(", ", result.AppliedIDs)})");
        return result;
    }

    private static async Task<bool> RunViVeToolAsync(string exePath, string[] args, Action<string>? log)
    {
        try
        {
            // ArgumentList handles per-argument quoting so we can't smuggle separators by
            // accident. Avoids the Windows command-line-parsing surprises you get from the
            // single-string ProcessStartInfo constructor.
            var psi = new ProcessStartInfo(exePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            var argsDisplay = string.Join(' ', args);
            using var proc = Process.Start(psi);
            if (proc is null)
            {
                log?.Invoke($"[ERROR] Could not start ViVeTool.exe {argsDisplay}");
                return false;
            }
            var stdout = proc.StandardOutput.ReadToEndAsync();
            var stderr = proc.StandardError.ReadToEndAsync();
            if (!await Task.Run(() => proc.WaitForExit(30000)).ConfigureAwait(false))
            {
                try { proc.Kill(true); } catch { }
                log?.Invoke($"[ERROR] ViVeTool.exe {argsDisplay} timed out after 30s");
                return false;
            }
            var outText = (await stdout.ConfigureAwait(false))?.Trim();
            var errText = (await stderr.ConfigureAwait(false))?.Trim();
            if (!string.IsNullOrWhiteSpace(outText)) log?.Invoke($"  vivetool: {outText}");
            if (!string.IsNullOrWhiteSpace(errText)) log?.Invoke($"  vivetool[err]: {errText}");
            if (proc.ExitCode != 0)
            {
                log?.Invoke($"[ERROR] ViVeTool.exe {argsDisplay} exit {proc.ExitCode}");
                return false;
            }
            log?.Invoke($"  [OK] ViVeTool {argsDisplay}");
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke($"[ERROR] ViVeTool launch failed: {ex.Message}");
            return false;
        }
    }
}
