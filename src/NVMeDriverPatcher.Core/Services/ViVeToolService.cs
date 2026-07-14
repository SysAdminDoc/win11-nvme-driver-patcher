using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using NVMeDriverPatcher.Models;

namespace NVMeDriverPatcher.Services;

internal sealed class ViVeToolTrustManifest
{
    public int SchemaVersion { get; set; }
    public string Source { get; set; } = string.Empty;
    public string LastReviewed { get; set; } = string.Empty;
    public List<ViVeToolTrustedRelease> Releases { get; set; } = new();
}

internal sealed class ViVeToolTrustedRelease
{
    public string Tag { get; set; } = string.Empty;
    public List<ViVeToolTrustedAsset> Assets { get; set; } = new();
}

internal sealed class ViVeToolTrustedAsset
{
    public string Architecture { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public long ArchiveSize { get; set; }
    public string ArchiveSha256 { get; set; } = string.Empty;
    public ushort ExecutablePeMachine { get; set; }
    public List<ViVeToolTrustedMember> Members { get; set; } = new();
}

internal sealed class ViVeToolTrustedMember
{
    public string Path { get; set; } = string.Empty;
    public long Size { get; set; }
    public string Sha256 { get; set; } = string.Empty;
}

internal sealed record ViVeToolPayloadValidation(bool Success, string Summary)
{
    public static ViVeToolPayloadValidation Passed(string summary) => new(true, summary);
    public static ViVeToolPayloadValidation Failed(string summary) => new(false, summary);
}

// Secondary ViVeTool fallback path — the normal GUI/CLI fallback first writes the same
// build-specific IDs through the in-process Rtl FeatureStore API. If native both-store
// verification fails, this service downloads a specifically allowlisted ViVeTool release,
// validates the complete archive, and only then shells out.
//
// Source: https://github.com/thebookisclosed/ViVe (permissive, MIT-style license)
// Feature IDs come from Models/FallbackFeatureCatalog (build-gated; Microsoft rotates them).
//
// We explicitly do NOT bundle vivetool.exe in the installer. The signed application embeds
// the immutable release manifest instead, so a writable sidecar cannot redefine what elevated
// code is trusted.
public static class ViVeToolService
{
    public const string ViVeToolRepo = "thebookisclosed/ViVe";
    public const string ViVeToolLatestApi = "https://api.github.com/repos/thebookisclosed/ViVe/releases/latest";
    public const string ViVeToolProjectUrl = "https://github.com/thebookisclosed/ViVe";
    internal const string TrustManifestResourceName = "NVMeDriverPatcher.vivetool_trusted_releases.json";

    private static readonly string[] AllowedAssetHosts =
    {
        "github.com",
        "api.github.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com",
        "codeload.github.com"
    };

    private const long MinAssetBytes = 10 * 1024;
    private const long MaxAssetBytes = 32 * 1024 * 1024;
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
        SelectFallbackSet().Ids.Select(i => i.ToString(CultureInfo.InvariantCulture)).ToArray();

    public static string ToolsDir(string workingDir) => Path.Combine(workingDir, "tools");
    public static string PayloadDir(string workingDir) => Path.Combine(ToolsDir(workingDir), "vivetool");
    public static string CachedExePath(string workingDir) => Path.Combine(PayloadDir(workingDir), "ViVeTool.exe");

    internal static string ComputeSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return ComputeSha256(stream);
    }

    private static string ComputeSha256(Stream stream)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    internal readonly record struct ReleaseAssetCandidate(string Name, string? Url, long? Size);

    internal static ViVeToolTrustManifest LoadTrustManifest()
    {
        using var stream = typeof(ViVeToolService).Assembly.GetManifestResourceStream(TrustManifestResourceName)
            ?? throw new InvalidDataException($"Embedded ViVeTool trust manifest '{TrustManifestResourceName}' is missing.");
        var manifest = JsonSerializer.Deserialize<ViVeToolTrustManifest>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidDataException("Embedded ViVeTool trust manifest is empty.");

        var validation = ValidateTrustManifest(manifest);
        if (!validation.Success) throw new InvalidDataException(validation.Summary);
        return manifest;
    }

    internal static ViVeToolPayloadValidation ValidateTrustManifest(ViVeToolTrustManifest? manifest)
    {
        if (manifest is null) return ViVeToolPayloadValidation.Failed("ViVeTool trust manifest is null.");
        if (manifest.SchemaVersion != 1)
            return ViVeToolPayloadValidation.Failed($"Unsupported ViVeTool trust manifest schema {manifest.SchemaVersion}.");
        if (!Uri.TryCreate(manifest.Source, UriKind.Absolute, out var source) ||
            source.Scheme != Uri.UriSchemeHttps ||
            !source.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            !source.AbsolutePath.StartsWith($"/{ViVeToolRepo}/releases/", StringComparison.Ordinal))
            return ViVeToolPayloadValidation.Failed("ViVeTool trust manifest source is not the official HTTPS GitHub release path.");
        if (!DateOnly.TryParseExact(manifest.LastReviewed, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out _))
            return ViVeToolPayloadValidation.Failed("ViVeTool trust manifest lastReviewed must be an absolute YYYY-MM-DD date.");
        if (manifest.Releases.Count == 0)
            return ViVeToolPayloadValidation.Failed("ViVeTool trust manifest contains no releases.");

        var releaseTags = new HashSet<string>(StringComparer.Ordinal);
        foreach (var release in manifest.Releases)
        {
            if (!IsSafeManifestToken(release.Tag) || !releaseTags.Add(release.Tag))
                return ViVeToolPayloadValidation.Failed($"Invalid or duplicate trusted release tag '{release.Tag}'.");
            if (release.Assets.Count == 0)
                return ViVeToolPayloadValidation.Failed($"Trusted release '{release.Tag}' contains no assets.");

            var assetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var architectures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var asset in release.Assets)
            {
                if (asset.Architecture is not ("X64" or "Arm64") || !architectures.Add(asset.Architecture))
                    return ViVeToolPayloadValidation.Failed($"Release '{release.Tag}' has invalid or duplicate architecture '{asset.Architecture}'.");
                if (!IsSafeManifestToken(asset.Name) || !asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                    !assetNames.Add(asset.Name))
                    return ViVeToolPayloadValidation.Failed($"Release '{release.Tag}' has invalid or duplicate asset name '{asset.Name}'.");
                if (asset.ArchiveSize < MinAssetBytes || asset.ArchiveSize > MaxAssetBytes)
                    return ViVeToolPayloadValidation.Failed($"Trusted asset '{asset.Name}' has an out-of-range archive size.");
                if (!IsSha256(asset.ArchiveSha256))
                    return ViVeToolPayloadValidation.Failed($"Trusted asset '{asset.Name}' has an invalid archive SHA-256.");
                var expectedMachine = asset.Architecture == "X64" ? (ushort)0x014c : (ushort)0xaa64;
                if (asset.ExecutablePeMachine != expectedMachine)
                    return ViVeToolPayloadValidation.Failed($"Trusted asset '{asset.Name}' has PE machine {asset.ExecutablePeMachine}, expected {expectedMachine}.");
                if (asset.Members.Count == 0)
                    return ViVeToolPayloadValidation.Failed($"Trusted asset '{asset.Name}' contains no members.");

                var memberPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var member in asset.Members)
                {
                    if (!IsRootLevelMemberPath(member.Path) || !memberPaths.Add(member.Path))
                        return ViVeToolPayloadValidation.Failed($"Trusted asset '{asset.Name}' has invalid or duplicate member '{member.Path}'.");
                    if (member.Size <= 0 || member.Size > MaxAssetBytes)
                        return ViVeToolPayloadValidation.Failed($"Trusted member '{member.Path}' has an out-of-range size.");
                    if (!IsSha256(member.Sha256))
                        return ViVeToolPayloadValidation.Failed($"Trusted member '{member.Path}' has an invalid SHA-256.");
                }
                if (asset.Members.Count(m => m.Path.Equals("ViVeTool.exe", StringComparison.OrdinalIgnoreCase)) != 1)
                    return ViVeToolPayloadValidation.Failed($"Trusted asset '{asset.Name}' must contain exactly one root-level ViVeTool.exe.");
            }
        }
        return ViVeToolPayloadValidation.Passed("Embedded ViVeTool trust manifest is valid.");
    }

    private static bool IsSafeManifestToken(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.All(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-');

    private static bool IsRootLevelMemberPath(string path) =>
        IsSafeManifestToken(path) && path is not "." and not ".." &&
        !path.Contains('/') && !path.Contains('\\') && Path.GetFileName(path) == path;

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);

    internal static bool TrySelectReleaseAsset(
        ViVeToolTrustManifest manifest,
        string? tagName,
        IReadOnlyCollection<ReleaseAssetCandidate> candidates,
        Architecture architecture,
        out ViVeToolTrustedAsset? trustedAsset,
        out ReleaseAssetCandidate selectedCandidate,
        out string error)
    {
        trustedAsset = null;
        selectedCandidate = default;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(tagName))
        {
            error = "GitHub's latest release response did not include a tag_name.";
            return false;
        }
        var release = manifest.Releases.SingleOrDefault(r => r.Tag.Equals(tagName, StringComparison.Ordinal));
        if (release is null)
        {
            error = $"ViVeTool release '{tagName}' is not in the embedded allowlist.";
            return false;
        }
        var architectureName = architecture switch
        {
            Architecture.X64 => "X64",
            Architecture.Arm64 => "Arm64",
            _ => null
        };
        if (architectureName is null)
        {
            error = $"ViVeTool fallback is not published for process architecture {architecture}.";
            return false;
        }
        trustedAsset = release.Assets.SingleOrDefault(a => a.Architecture.Equals(architectureName, StringComparison.Ordinal));
        if (trustedAsset is null)
        {
            error = $"Release '{tagName}' has no allowlisted {architectureName} asset.";
            return false;
        }

        var selectedAsset = trustedAsset;
        var matches = candidates.Where(c => c.Name.Equals(selectedAsset.Name, StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1)
        {
            error = $"Release '{tagName}' must publish exactly one asset named '{trustedAsset.Name}'; found {matches.Length}.";
            return false;
        }
        selectedCandidate = matches[0];
        if (selectedCandidate.Size != trustedAsset.ArchiveSize)
        {
            error = $"GitHub reports {selectedCandidate.Size?.ToString(CultureInfo.InvariantCulture) ?? "no size"} bytes for '{trustedAsset.Name}', expected {trustedAsset.ArchiveSize}.";
            return false;
        }
        var expectedUri = BuildOfficialAssetUri(tagName, trustedAsset.Name);
        if (!Uri.TryCreate(selectedCandidate.Url, UriKind.Absolute, out var candidateUri) ||
            !candidateUri.AbsoluteUri.Equals(expectedUri.AbsoluteUri, StringComparison.Ordinal))
        {
            error = $"Release asset URL is not the exact official GitHub path '{expectedUri}'.";
            return false;
        }
        return true;
    }

    private static Uri BuildOfficialAssetUri(string tagName, string assetName) =>
        new($"https://github.com/{ViVeToolRepo}/releases/download/{Uri.EscapeDataString(tagName)}/{Uri.EscapeDataString(assetName)}");

    public static bool IsInstalled(string workingDir)
    {
        try
        {
            return TryValidateTrustedPayloadDirectory(
                PayloadDir(workingDir), RuntimeInformation.ProcessArchitecture,
                out _, out _, out _);
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
        /// <summary>Content-integrity verification applied to the complete ViVeTool payload.</summary>
        public string IntegritySignal { get; set; } = "none";
    }

    public static async Task<ViVeToolResult> EnsureInstalledAsync(
        string workingDir, Action<string>? log = null, CancellationToken ct = default)
    {
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

    private static async Task<ViVeToolResult> EnsureInstalledInnerAsync(
        string workingDir, Action<string>? log, CancellationToken ct)
    {
        var result = new ViVeToolResult { ExePath = CachedExePath(workingDir) };
        ViVeToolTrustManifest manifest;
        try
        {
            manifest = LoadTrustManifest();
        }
        catch (Exception ex)
        {
            result.Message = $"ViVeTool trust manifest failed validation: {ex.Message}";
            return result;
        }

        if (TryValidateTrustedPayloadDirectory(
                PayloadDir(workingDir), RuntimeInformation.ProcessArchitecture,
                out var cachedRelease, out _, out _))
        {
            log?.Invoke($"Authenticated ViVeTool payload already cached at {PayloadDir(workingDir)}");
            result.Success = true;
            result.Version = cachedRelease!.Tag;
            result.IntegritySignal = "manifest-sha256";
            result.Message = "Authenticated cached copy in use";
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

        string? tagName;
        List<ReleaseAssetCandidate> candidates = [];
        try
        {
            log?.Invoke($"Querying GitHub for latest ViVeTool release ({ViVeToolRepo})...");
            var json = await client.GetStringAsync(ViVeToolLatestApi, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            tagName = root.TryGetProperty("tag_name", out var tagProp) ? tagProp.GetString() : null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    if (!asset.TryGetProperty("name", out var nameProp)) continue;
                    var name = nameProp.GetString() ?? string.Empty;
                    var url = asset.TryGetProperty("browser_download_url", out var urlProp) ? urlProp.GetString() : null;
                    long? size = asset.TryGetProperty("size", out var sizeProp) &&
                        sizeProp.ValueKind == JsonValueKind.Number && sizeProp.TryGetInt64(out var parsedSize)
                        ? parsedSize : null;
                    candidates.Add(new ReleaseAssetCandidate(name, url, size));
                }
            }
        }
        catch (Exception ex)
        {
            result.Message = $"GitHub API unreachable: {ex.Message}";
            return result;
        }

        if (!TrySelectReleaseAsset(
                manifest, tagName, candidates, RuntimeInformation.ProcessArchitecture,
                out var trustedAsset, out var selectedCandidate, out var selectionError))
        {
            result.Message = selectionError;
            return result;
        }
        var selectedAsset = trustedAsset!;
        var assetUri = new Uri(selectedCandidate.Url!, UriKind.Absolute);
        log?.Invoke($"Selected allowlisted release asset: {selectedAsset.Name}");

        var tempZip = Path.Combine(ToolsDir(workingDir), $"vivetool-download-{Guid.NewGuid():N}.zip");
        var stagingDir = Path.Combine(ToolsDir(workingDir), $"vivetool-staging-{Guid.NewGuid():N}");
        try
        {
            log?.Invoke($"Downloading authenticated ViVeTool {tagName} from {assetUri}");
            var downloadPolicy = new VerifiedDownloader.DownloadPolicy
            {
                AllowedHosts = AllowedAssetHosts,
                MinBytes = selectedAsset.ArchiveSize,
                MaxBytes = selectedAsset.ArchiveSize,
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

            var archiveValidation = ValidateArchive(tempZip, selectedAsset);
            if (!archiveValidation.Success)
            {
                result.Message = archiveValidation.Summary;
                log?.Invoke("[SECURITY] " + result.Message);
                return result;
            }
            result.IntegritySignal = "manifest-sha256";
            log?.Invoke("ViVeTool archive hash and exact member manifest verified.");

            Directory.CreateDirectory(stagingDir);
            using (var archive = ZipFile.OpenRead(tempZip))
            {
                foreach (var member in selectedAsset.Members)
                {
                    var entry = archive.Entries.Single(e => e.FullName.Equals(member.Path, StringComparison.Ordinal));
                    entry.ExtractToFile(Path.Combine(stagingDir, member.Path), overwrite: false);
                }
            }

            var stagedValidation = ValidatePayloadDirectory(stagingDir, selectedAsset);
            if (!stagedValidation.Success)
            {
                result.Message = stagedValidation.Summary;
                log?.Invoke("[SECURITY] " + result.Message);
                return result;
            }

            var promotion = PromotePayloadDirectory(stagingDir, PayloadDir(workingDir), selectedAsset);
            if (!promotion.Success)
            {
                result.Message = promotion.Summary;
                return result;
            }
        }
        catch (Exception ex)
        {
            result.Message = $"Download or authenticated extraction failed: {ex.Message}";
            return result;
        }
        finally
        {
            TryDeleteFile(tempZip);
            TryDeleteDirectory(stagingDir);
        }

        var installedValidation = ValidatePayloadDirectory(PayloadDir(workingDir), selectedAsset);
        if (!installedValidation.Success)
        {
            result.Message = $"Promoted ViVeTool payload failed revalidation: {installedValidation.Summary}";
            return result;
        }

        result.Success = true;
        result.Version = tagName;
        result.IntegritySignal = "manifest-sha256";
        result.Message = $"Installed authenticated ViVeTool {tagName}";
        log?.Invoke(result.Message);
        return result;
    }

    internal static bool IsTrustedAssetUri(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps &&
        AllowedAssetHosts.Any(h => uri.Host.Equals(h, StringComparison.OrdinalIgnoreCase));

    internal static ViVeToolPayloadValidation ValidateArchive(string archivePath, ViVeToolTrustedAsset asset)
    {
        try
        {
            var archiveInfo = new FileInfo(archivePath);
            if (!archiveInfo.Exists)
                return ViVeToolPayloadValidation.Failed("ViVeTool archive is missing.");
            if (archiveInfo.Length != asset.ArchiveSize)
                return ViVeToolPayloadValidation.Failed($"ViVeTool archive size {archiveInfo.Length} does not match pinned size {asset.ArchiveSize}.");
            var archiveHash = ComputeSha256(archivePath);
            if (!archiveHash.Equals(asset.ArchiveSha256, StringComparison.OrdinalIgnoreCase))
                return ViVeToolPayloadValidation.Failed($"ViVeTool archive SHA-256 {archiveHash} does not match the embedded manifest.");

            using var archive = ZipFile.OpenRead(archivePath);
            if (archive.Entries.Count != asset.Members.Count)
                return ViVeToolPayloadValidation.Failed($"ViVeTool archive has {archive.Entries.Count} entries; expected exactly {asset.Members.Count}.");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in archive.Entries)
            {
                if (!IsRootLevelMemberPath(entry.FullName) || !entry.FullName.Equals(entry.Name, StringComparison.Ordinal) ||
                    !seen.Add(entry.FullName))
                    return ViVeToolPayloadValidation.Failed($"ViVeTool archive contains a nested, directory, or duplicate entry '{entry.FullName}'.");
                if (IsSymbolicLink(entry))
                    return ViVeToolPayloadValidation.Failed($"ViVeTool archive member '{entry.FullName}' is a symbolic link.");
                var member = asset.Members.SingleOrDefault(m => m.Path.Equals(entry.FullName, StringComparison.Ordinal));
                if (member is null)
                    return ViVeToolPayloadValidation.Failed($"ViVeTool archive contains unexpected member '{entry.FullName}'.");
                if (entry.Length != member.Size)
                    return ViVeToolPayloadValidation.Failed($"ViVeTool member '{entry.FullName}' size {entry.Length} does not match pinned size {member.Size}.");
                using var stream = entry.Open();
                var hash = ComputeSha256(stream);
                if (!hash.Equals(member.Sha256, StringComparison.OrdinalIgnoreCase))
                    return ViVeToolPayloadValidation.Failed($"ViVeTool member '{entry.FullName}' SHA-256 does not match the embedded manifest.");
            }
            if (seen.Count != asset.Members.Count)
                return ViVeToolPayloadValidation.Failed("ViVeTool archive is missing one or more required members.");
            return ViVeToolPayloadValidation.Passed("ViVeTool archive and exact member inventory verified.");
        }
        catch (Exception ex)
        {
            return ViVeToolPayloadValidation.Failed($"ViVeTool archive validation failed: {ex.Message}");
        }
    }

    private static bool IsSymbolicLink(ZipArchiveEntry entry) =>
        ((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000;

    internal static ViVeToolPayloadValidation ValidatePayloadDirectory(string payloadDir, ViVeToolTrustedAsset asset)
    {
        try
        {
            if (!Directory.Exists(payloadDir))
                return ViVeToolPayloadValidation.Failed("ViVeTool payload directory is missing.");
            if ((File.GetAttributes(payloadDir) & FileAttributes.ReparsePoint) != 0)
                return ViVeToolPayloadValidation.Failed("ViVeTool payload directory is a reparse point.");
            if (Directory.EnumerateDirectories(payloadDir, "*", SearchOption.AllDirectories).Any())
                return ViVeToolPayloadValidation.Failed("ViVeTool payload contains an unexpected nested directory.");

            var files = Directory.EnumerateFiles(payloadDir, "*", SearchOption.TopDirectoryOnly).ToArray();
            if (files.Length != asset.Members.Count)
                return ViVeToolPayloadValidation.Failed($"ViVeTool payload has {files.Length} files; expected exactly {asset.Members.Count}.");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                var name = Path.GetFileName(file);
                if (!seen.Add(name))
                    return ViVeToolPayloadValidation.Failed($"ViVeTool payload has duplicate member '{name}'.");
                var member = asset.Members.SingleOrDefault(m => m.Path.Equals(name, StringComparison.Ordinal));
                if (member is null)
                    return ViVeToolPayloadValidation.Failed($"ViVeTool payload contains unexpected member '{name}'.");
                var info = new FileInfo(file);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                    return ViVeToolPayloadValidation.Failed($"ViVeTool payload member '{name}' is a reparse point.");
                if (info.Length != member.Size)
                    return ViVeToolPayloadValidation.Failed($"ViVeTool payload member '{name}' size {info.Length} does not match pinned size {member.Size}.");
                if (!ComputeSha256(file).Equals(member.Sha256, StringComparison.OrdinalIgnoreCase))
                    return ViVeToolPayloadValidation.Failed($"ViVeTool payload member '{name}' SHA-256 does not match the embedded manifest.");
            }
            if (seen.Count != asset.Members.Count)
                return ViVeToolPayloadValidation.Failed("ViVeTool payload is missing one or more required members.");

            var exePath = Path.Combine(payloadDir, "ViVeTool.exe");
            if (!TryReadPeMachine(exePath, out var machine) || machine != asset.ExecutablePeMachine)
                return ViVeToolPayloadValidation.Failed($"ViVeTool.exe PE machine {machine} does not match pinned machine {asset.ExecutablePeMachine}.");
            return ViVeToolPayloadValidation.Passed("ViVeTool payload inventory, hashes, and architecture verified.");
        }
        catch (Exception ex)
        {
            return ViVeToolPayloadValidation.Failed($"ViVeTool payload validation failed: {ex.Message}");
        }
    }

    internal static bool TryReadPeMachine(string path, out ushort machine)
    {
        machine = 0;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream);
            if (stream.Length < 0x40 || reader.ReadUInt16() != 0x5a4d) return false;
            stream.Position = 0x3c;
            var peOffset = reader.ReadInt32();
            if (peOffset < 0x40 || peOffset > stream.Length - 6) return false;
            stream.Position = peOffset;
            if (reader.ReadUInt32() != 0x00004550) return false;
            machine = reader.ReadUInt16();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryValidateTrustedPayloadDirectory(
        string payloadDir,
        Architecture architecture,
        out ViVeToolTrustedRelease? matchedRelease,
        out ViVeToolTrustedAsset? matchedAsset,
        out string error)
    {
        matchedRelease = null;
        matchedAsset = null;
        error = string.Empty;
        ViVeToolTrustManifest manifest;
        try
        {
            manifest = LoadTrustManifest();
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }

        var architectureName = architecture switch
        {
            Architecture.X64 => "X64",
            Architecture.Arm64 => "Arm64",
            _ => null
        };
        if (architectureName is null)
        {
            error = $"Unsupported process architecture {architecture}.";
            return false;
        }

        var failures = new List<string>();
        foreach (var release in manifest.Releases)
        {
            foreach (var asset in release.Assets.Where(a => a.Architecture.Equals(architectureName, StringComparison.Ordinal)))
            {
                var validation = ValidatePayloadDirectory(payloadDir, asset);
                if (validation.Success)
                {
                    matchedRelease = release;
                    matchedAsset = asset;
                    return true;
                }
                failures.Add(validation.Summary);
            }
        }
        error = failures.FirstOrDefault() ?? $"No allowlisted ViVeTool payload exists for {architectureName}.";
        return false;
    }

    internal static ViVeToolPayloadValidation PromotePayloadDirectory(
        string stagingDir, string destinationDir, ViVeToolTrustedAsset asset)
    {
        var backupDir = destinationDir + $".backup-{Guid.NewGuid():N}";
        var hadExisting = Directory.Exists(destinationDir);
        try
        {
            if (hadExisting) Directory.Move(destinationDir, backupDir);
            try
            {
                Directory.Move(stagingDir, destinationDir);
            }
            catch
            {
                if (hadExisting && !Directory.Exists(destinationDir) && Directory.Exists(backupDir))
                    Directory.Move(backupDir, destinationDir);
                throw;
            }

            var validation = ValidatePayloadDirectory(destinationDir, asset);
            if (!validation.Success)
            {
                TryDeleteDirectory(destinationDir);
                if (hadExisting && Directory.Exists(backupDir)) Directory.Move(backupDir, destinationDir);
                return ViVeToolPayloadValidation.Failed($"Promoted payload failed validation and was rolled back: {validation.Summary}");
            }
            TryDeleteDirectory(backupDir);
            return ViVeToolPayloadValidation.Passed("Authenticated ViVeTool payload promoted atomically.");
        }
        catch (Exception ex)
        {
            return ViVeToolPayloadValidation.Failed($"Could not atomically promote ViVeTool payload: {ex.Message}");
        }
    }

    // Runs ViVeTool.exe /enable for each supplied feature ID. Returns on first failure.
    internal static async Task<ViVeToolResult> ApplyFallbackAsync(
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
        foreach (var id in idSet.Ids.Select(i => i.ToString(CultureInfo.InvariantCulture)))
        {
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
        EventLogService.Write($"ViVeTool fallback applied ({string.Join(", ", result.AppliedIDs)})");
        return result;
    }

    private static async Task<bool> RunViVeToolAsync(string exePath, string[] args, Action<string>? log)
    {
        var payloadDir = Path.GetDirectoryName(Path.GetFullPath(exePath)) ?? string.Empty;
        var validationError = "executable name is not ViVeTool.exe";
        var validName = Path.GetFileName(exePath).Equals("ViVeTool.exe", StringComparison.OrdinalIgnoreCase);
        var validPayload = validName && TryValidateTrustedPayloadDirectory(
            payloadDir, RuntimeInformation.ProcessArchitecture, out _, out _, out validationError);
        if (!validPayload)
        {
            log?.Invoke($"[SECURITY] Refusing to execute ViVeTool: complete payload authentication failed ({validationError}).");
            return false;
        }
        try
        {
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

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }
}
