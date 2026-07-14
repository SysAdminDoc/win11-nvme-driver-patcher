using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using NVMeDriverPatcher.Models;

namespace NVMeDriverPatcher.Services;

// Secondary ViVeTool fallback path — the normal GUI/CLI fallback first writes the same
// build-specific IDs through the in-process Rtl FeatureStore API. If native both-store
// verification fails, this service downloads ViVeTool from its official GitHub releases,
// caches it in the working dir, then shells out.
//
// Source: https://github.com/thebookisclosed/ViVe (permissive, MIT-style license)
// Feature IDs come from Models/FallbackFeatureCatalog (build-gated; Microsoft rotates them).
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

    /// <summary>
    /// The fallback ID set to apply on THIS machine, selected by Windows build from the
    /// FallbackFeatureCatalog (builds >= 26200 use the newer "Native NVMe Stack" set).
    /// Falls back to the verified March-2026 set when the build can't be read.
    /// </summary>
    public static FallbackIdSet SelectFallbackSet()
    {
        try
        {
            var build = DriveService.GetWindowsBuildDetails();
            if (build is not null) return FallbackFeatureCatalog.SelectForBuild(build.BuildNumber);
        }
        catch { }
        return FallbackFeatureCatalog.PostBlockMarch2026;
    }

    public static IReadOnlyList<string> FallbackFeatureIDs =>
        SelectFallbackSet().Ids.Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();

    public static string ToolsDir(string workingDir) => Path.Combine(workingDir, "tools");
    public static string CachedExePath(string workingDir) => Path.Combine(ToolsDir(workingDir), "ViVeTool.exe");

    // Repo-pinned SHA-256 allowlist of known-good ViVeTool.exe builds. ViVeTool ships no upstream
    // .sha256 sidecar and we execute it ELEVATED, so this pin is the load-bearing supply-chain
    // control: a compromised release, repo transfer, or same-sized MITM payload cannot be run.
    // Fail-closed — an exe whose hash is not in this set is refused (extract AND execute). To adopt
    // a new ViVeTool release, download its zip, hash the extracted ViVeTool.exe, and add it here.
    //   v0.3.4 (IntelAmd + SnapdragonArm64 ship the same ViVeTool.exe):
    private static readonly HashSet<string> PinnedExeSha256 = new(StringComparer.OrdinalIgnoreCase)
    {
        "d3b69c982622a26ad0b37c65b8f006b5139e50aeb45fda68734a33ca28706dea", // v0.3.4
    };

    internal static string ComputeSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        using var sha = System.Security.Cryptography.SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    /// <summary>True when the file's SHA-256 is in the repo-pinned known-good allowlist.</summary>
    internal static bool IsPinnedExecutable(string exePath)
    {
        try { return File.Exists(exePath) && PinnedExeSha256.Contains(ComputeSha256(exePath)); }
        catch { return false; }
    }

    internal readonly record struct ReleaseAssetCandidate(string Name, string? Url, long? Size);

    /// <summary>
    /// Architecture-aware release asset selection. ViVe v0.3.4+ ships split per-architecture
    /// zips (ViVeTool-vX.Y.Z-IntelAmd.zip / ViVeTool-vX.Y.Z-SnapdragonArm64.zip); earlier
    /// releases shipped a single un-suffixed ViVeTool-vX.Y.Z.zip. Selecting "the first zip"
    /// made the staged binary depend on upload order — an ARM64 binary on an x64 machine
    /// breaks the only post-block fallback path. Rules:
    ///   x64/x86 → prefer "-IntelAmd", accept a legacy un-suffixed zip, never an ARM asset.
    ///   ARM64   → only an ARM-marked asset.
    ///   no match → null (fail closed; caller reports it).
    /// </summary>
    internal static ReleaseAssetCandidate? SelectReleaseAsset(
        IReadOnlyList<ReleaseAssetCandidate> assets,
        System.Runtime.InteropServices.Architecture arch)
    {
        static bool IsViVeZip(string n) =>
            n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
            n.StartsWith("ViVeTool", StringComparison.OrdinalIgnoreCase);
        static bool IsArm(string n) =>
            n.Contains("Arm64", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("Snapdragon", StringComparison.OrdinalIgnoreCase);
        static bool IsIntelAmd(string n) => n.Contains("IntelAmd", StringComparison.OrdinalIgnoreCase);

        if (arch is System.Runtime.InteropServices.Architecture.X64
                 or System.Runtime.InteropServices.Architecture.X86)
        {
            foreach (var a in assets)
                if (IsViVeZip(a.Name) && IsIntelAmd(a.Name)) return a;
            foreach (var a in assets)
                if (IsViVeZip(a.Name) && !IsArm(a.Name) && !IsIntelAmd(a.Name)) return a;
            return null;
        }

        if (arch == System.Runtime.InteropServices.Architecture.Arm64)
        {
            foreach (var a in assets)
                if (IsViVeZip(a.Name) && IsArm(a.Name)) return a;
            return null;
        }

        return null;
    }

    // A 0-byte cache file from a previously-failed extraction would silently fool IsInstalled
    // into reporting success. Require a non-trivial file size AND a pinned known-good hash before
    // we trust the cached copy — otherwise a stale/tampered cache would block re-download and then
    // be refused at execution, dead-ending the fallback. A non-pinned cache is treated as "not
    // installed" so EnsureInstalledAsync re-downloads a verified build.
    public static bool IsInstalled(string workingDir)
    {
        try
        {
            var path = CachedExePath(workingDir);
            if (!File.Exists(path)) return false;
            var fi = new FileInfo(path);
            if (fi.Length < MinAssetBytes) return false;
            return IsPinnedExecutable(path);
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
        /// <summary>
        /// Level of content-integrity verification the staged ViVeTool archive passed.
        /// "sha256" means an upstream .sha256 sidecar was published and matched;
        /// "weak" means size + host allowlist + API size cross-check only (upstream has no
        /// hashes). Operators auditing an unattended install can gate on this value.
        /// </summary>
        public string IntegritySignal { get; set; } = "weak";
    }

    public static async Task<ViVeToolResult> EnsureInstalledAsync(string workingDir, Action<string>? log = null, CancellationToken ct = default)
    {
        // Guard against concurrent invocations (e.g. user double-click on the fallback badge).
        // With the 2-minute timeout, even if the current holder is stuck on a slow download,
        // a second caller eventually gets a clean "something else is installing" error instead
        // of racing into the same tools folder.
        if (!await _installLock.WaitAsync(TimeSpan.FromMinutes(2), ct).ConfigureAwait(false))
        {
            return new ViVeToolResult
            {
                Message = "Another ViVeTool install is already in progress. Try again in a moment.",
                ExePath = CachedExePath(workingDir)
            };
        }
        try
        {
            return await EnsureInstalledInnerAsync(workingDir, log, ct).ConfigureAwait(false);
        }
        finally
        {
            _installLock.Release();
        }
    }

    private static async Task<ViVeToolResult> EnsureInstalledInnerAsync(string workingDir, Action<string>? log, CancellationToken ct)
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
            var json = await client.GetStringAsync(ViVeToolLatestApi, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("tag_name", out var tagProp))
                tagName = tagProp.GetString();

            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                // Collect every asset, then select by process architecture — ViVe v0.3.4+
                // publishes split IntelAmd / SnapdragonArm64 zips and the order is not stable.
                var candidates = new List<ReleaseAssetCandidate>();
                foreach (var asset in assets.EnumerateArray())
                {
                    if (!asset.TryGetProperty("name", out var nameProp)) continue;
                    var name = nameProp.GetString() ?? string.Empty;
                    string? url = asset.TryGetProperty("browser_download_url", out var urlProp)
                        ? urlProp.GetString() : null;
                    long? size = null;
                    if (asset.TryGetProperty("size", out var sizeProp) &&
                        sizeProp.ValueKind == JsonValueKind.Number &&
                        sizeProp.TryGetInt64(out var sz))
                        size = sz;
                    candidates.Add(new ReleaseAssetCandidate(name, url, size));
                }

                var selected = SelectReleaseAsset(
                    candidates, System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture);
                if (selected is { } sel)
                {
                    log?.Invoke($"Selected release asset: {sel.Name}");
                    assetUrl = sel.Url;
                    expectedSize = sel.Size;
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
            result.Message = "No ViVeTool release asset matching this machine's architecture " +
                $"({System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}) was found on GitHub.";
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

            // Delegate to VerifiedDownloader so the redirect-walking, host-allowlist re-check,
            // size-cap enforcement, `.part` staging, SHA-256 sidecar verification, and atomic
            // promote all live in one place. Upstream ViVeTool doesn't publish sidecars today,
            // and we don't sign its binary — RequireIntegrity is false (the archive's own
            // zip-slip defense + size cap substitute), but if upstream ever adds a .sha256
            // sidecar it's verified automatically. AllowAuthenticodeFallback is false: the
            // downloaded zip isn't Authenticode-signed, so falling back to signtool would only
            // produce noise.
            var downloadPolicy = new VerifiedDownloader.DownloadPolicy
            {
                AllowedHosts = AllowedAssetHosts,
                MinBytes = MinAssetBytes,
                MaxBytes = MaxAssetBytes,
                MaxRedirects = 6,
                RequireIntegrity = false,
                AllowAuthenticodeFallback = false
            };
            var download = await VerifiedDownloader
                .DownloadAsync(client, assetUri, tempZip, downloadPolicy, ct)
                .ConfigureAwait(false);
            if (!download.Success)
            {
                result.Message = download.Summary;
                return result;
            }

            result.IntegritySignal = download.Signal == VerifiedDownloader.IntegritySignal.Sha256Sidecar
                ? "sha256"
                : "weak";
            log?.Invoke(download.Signal == VerifiedDownloader.IntegritySignal.Sha256Sidecar
                ? "ViVeTool SHA-256 sidecar verified."
                : "ViVeTool release has no .sha256 sidecar — relying on size + host allowlist checks.");

            // Cross-check the downloaded size against what GitHub's API advertised. The
            // size-range check already ran inside VerifiedDownloader, but the API-reported
            // size is an independent signal: if the CDN served a different payload than the
            // release record claims, we want to notice before extracting.
            var actualSize = new FileInfo(tempZip).Length;
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

            // Verify the extracted ViVeTool.exe against the repo-pinned SHA-256 allowlist BEFORE it
            // is promoted into tools/ or ever executed. This is the load-bearing check: we run this
            // binary elevated, so an unrecognized hash (tamper, new/unknown release) is refused.
            var stagedExe = Path.Combine(payloadRoot, "ViVeTool.exe");
            if (!IsPinnedExecutable(stagedExe))
            {
                result.Message =
                    $"Refusing ViVeTool {(tagName ?? "latest")}: extracted ViVeTool.exe SHA-256 " +
                    $"({(File.Exists(stagedExe) ? ComputeSha256(stagedExe) : "missing")}) is not in the pinned known-good allowlist. " +
                    "This blocks executing an unverified elevated binary. If this is a legitimate new release, add its hash to ViVeToolService.PinnedExeSha256.";
                log?.Invoke("[SECURITY] " + result.Message);
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

    // The download + redirect + size-cap loop previously lived here; every call site now
    // delegates to VerifiedDownloader.DownloadAsync. Keeping IsTrustedAssetUri because it's
    // also used by the JSON-asset-URL validation at EnsureInstalledInnerAsync line above, and
    // is covered by its own unit tests.

    // Runs ViVeTool.exe /enable for each supplied feature ID. Returns on first failure —
    // caller surfaces the concatenated output. We deliberately run one ID at a time so a
    // partial failure is obvious in the log, even if ViVeTool supports batching.
    public static async Task<ViVeToolResult> ApplyFallbackAsync(
        string workingDir,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        var result = new ViVeToolResult();

        var install = await EnsureInstalledAsync(workingDir, log, ct).ConfigureAwait(false);
        if (!install.Success)
        {
            result.Message = install.Message;
            return result;
        }
        result.ExePath = install.ExePath;
        result.Version = install.Version;
        result.IntegritySignal = install.IntegritySignal;

        var idSet = SelectFallbackSet();
        log?.Invoke($"Using fallback ID set '{idSet.Name}' ({idSet.AppliesTo}; {idSet.Confidence}): {string.Join(", ", idSet.Ids)}");
        foreach (var id in idSet.Ids.Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)))
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
                result.Message = $"ViVeTool rejected feature ID {id}. " +
                    (result.AppliedIDs.Count > 0
                        ? $"IDs already applied before failure: {string.Join(", ", result.AppliedIDs)}. These remain enabled and may need manual reset."
                        : "See the activity log for details.");
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
        // Re-verify the pinned hash at the moment of execution — this also covers a CACHED exe from
        // a previous run and closes any window between staging and launch.
        if (!IsPinnedExecutable(exePath))
        {
            log?.Invoke($"[SECURITY] Refusing to execute ViVeTool: {exePath} does not match the pinned known-good SHA-256 allowlist.");
            return false;
        }
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

            // WaitForExitAsync with a linked timeout CTS is the clean replacement for the
            // old `await Task.Run(() => proc.WaitForExit(30000))` pattern, which blocked a
            // threadpool thread just to shim a synchronous wait onto the async path.
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
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
