using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using NVMeDriverPatcher.Models;

namespace NVMeDriverPatcher.Services;

public static partial class GeneratedArtifactManifestService
{
    public const string ManifestFileName = "ARTIFACT-MANIFEST.json";
    public const int CurrentSchemaVersion = 1;
    private const int MaxManifestBytes = 4 * 1024 * 1024;
    private const int MaxManifestFiles = 100_000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly EnumerationOptions DirectoryEnumerationOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = false,
        AttributesToSkip = FileAttributes.ReparsePoint,
        ReturnSpecialDirectories = false
    };

    public static GeneratedArtifactManifest PublishDirectoryManifest(
        string payloadRoot,
        string payloadType,
        Func<string, string>? roleResolver = null)
    {
        var root = RequireDirectory(payloadRoot);
        ValidatePayloadType(payloadType);

        var manifest = NewManifest(payloadType);
        foreach (var file in EnumeratePayloadFiles(root))
        {
            var relativePath = NormalizeRelativePath(Path.GetRelativePath(root, file));
            manifest.Files.Add(CreateFileRecord(file, relativePath, ResolveRole(roleResolver, relativePath)));
        }
        SortAndValidateRecords(manifest.Files);

        var finalPath = Path.Combine(root, ManifestFileName);
        var tempPath = Path.Combine(root, $"{ManifestFileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            WriteManifestDurably(tempPath, manifest);
            _ = ReadManifestFromFile(tempPath);
            File.Move(tempPath, finalPath, overwrite: true);
            return manifest;
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            throw;
        }
    }

    public static GeneratedArtifactManifest PublishZipManifest(
        string zipPath,
        string payloadType,
        Func<string, string>? roleResolver = null)
    {
        if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath))
            throw new FileNotFoundException("Payload ZIP was not found.", zipPath);
        ValidatePayloadType(payloadType);

        var manifest = NewManifest(payloadType);
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Update))
        {
            foreach (var prior in zip.Entries
                         .Where(e => e.FullName.Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase))
                         .ToArray())
                prior.Delete();

            foreach (var entry in zip.Entries.Where(IsPayloadEntry))
            {
                var relativePath = NormalizeRelativePath(entry.FullName);
                var byteLength = entry.Length;
                using var stream = entry.Open();
                manifest.Files.Add(new GeneratedArtifactFile
                {
                    RelativePath = relativePath,
                    Role = ResolveRole(roleResolver, relativePath),
                    ByteLength = byteLength,
                    Sha256 = ComputeSha256(stream),
                    Required = true
                });
            }
            SortAndValidateRecords(manifest.Files);

            var manifestEntry = zip.CreateEntry(ManifestFileName, CompressionLevel.Optimal);
            using var output = manifestEntry.Open();
            JsonSerializer.Serialize(output, manifest, JsonOptions);
        }

        // Re-open and parse what was actually committed into the archive before the caller
        // atomically promotes its temporary ZIP to the user-visible path.
        _ = ReadManifestFromZip(zipPath);
        return manifest;
    }

    public static ArtifactIntegrityResult Verify(string payloadPath)
    {
        if (Directory.Exists(payloadPath))
            return VerifyDirectory(payloadPath);
        if (File.Exists(payloadPath))
            return VerifyZip(payloadPath);

        return Failed(payloadPath, ArtifactIntegrityIssueKind.ReadFailure, "",
            "Payload path does not exist.");
    }

    public static ArtifactIntegrityResult VerifyDirectory(string payloadRoot)
    {
        string root;
        try { root = RequireDirectory(payloadRoot); }
        catch (Exception ex)
        {
            return Failed(payloadRoot, ArtifactIntegrityIssueKind.ReadFailure, "", ex.Message);
        }

        GeneratedArtifactManifest manifest;
        try
        {
            var manifestPath = Path.Combine(root, ManifestFileName);
            if (!File.Exists(manifestPath))
                return Failed(root, ArtifactIntegrityIssueKind.ManifestMissing, ManifestFileName,
                    "Required artifact manifest is missing.");
            manifest = ReadManifestFromFile(manifestPath);
        }
        catch (Exception ex)
        {
            return Failed(root, ArtifactIntegrityIssueKind.ManifestInvalid, ManifestFileName, ex.Message);
        }

        var issues = new List<ArtifactIntegrityIssue>();
        var records = ValidateManifest(manifest, issues);
        if (records is null)
            return Result(root, manifest, issues);

        Dictionary<string, string> actual;
        try
        {
            actual = EnumeratePayloadFiles(root).ToDictionary(
                file => NormalizeRelativePath(Path.GetRelativePath(root, file)),
                file => file,
                StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            issues.Add(new(ArtifactIntegrityIssueKind.ReadFailure, "", $"Could not enumerate payload: {ex.Message}"));
            return Result(root, manifest, issues);
        }

        foreach (var record in records.Values.OrderBy(r => r.RelativePath, StringComparer.Ordinal))
        {
            if (!actual.TryGetValue(record.RelativePath, out var path))
            {
                if (record.Required)
                    issues.Add(new(ArtifactIntegrityIssueKind.Missing, record.RelativePath, "Required file is missing."));
                continue;
            }

            actual.Remove(record.RelativePath);
            VerifyFile(path, record, issues);
        }

        foreach (var unexpected in actual.Keys.OrderBy(k => k, StringComparer.Ordinal))
            issues.Add(new(ArtifactIntegrityIssueKind.Unexpected, unexpected, "File is not declared by the manifest."));

        return Result(root, manifest, issues);
    }

    public static ArtifactIntegrityResult VerifyZip(string zipPath)
    {
        if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath))
            return Failed(zipPath, ArtifactIntegrityIssueKind.ReadFailure, "", "Payload ZIP does not exist.");

        GeneratedArtifactManifest manifest;
        try { manifest = ReadManifestFromZip(zipPath); }
        catch (FileNotFoundException ex)
        {
            return Failed(zipPath, ArtifactIntegrityIssueKind.ManifestMissing, ManifestFileName, ex.Message);
        }
        catch (Exception ex)
        {
            return Failed(zipPath, ArtifactIntegrityIssueKind.ManifestInvalid, ManifestFileName, ex.Message);
        }

        var issues = new List<ArtifactIntegrityIssue>();
        var records = ValidateManifest(manifest, issues);
        if (records is null)
            return Result(zipPath, manifest, issues);

        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var actual = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in zip.Entries.Where(IsPayloadEntry))
            {
                string relativePath;
                try { relativePath = NormalizeRelativePath(entry.FullName); }
                catch (Exception ex)
                {
                    issues.Add(new(ArtifactIntegrityIssueKind.Unexpected, entry.FullName,
                        $"Archive entry has an unsafe path: {ex.Message}"));
                    continue;
                }
                if (!actual.TryAdd(relativePath, entry))
                    issues.Add(new(ArtifactIntegrityIssueKind.Unexpected, relativePath,
                        "Archive contains duplicate case-insensitive paths."));
            }

            foreach (var record in records.Values.OrderBy(r => r.RelativePath, StringComparer.Ordinal))
            {
                if (!actual.TryGetValue(record.RelativePath, out var entry))
                {
                    if (record.Required)
                        issues.Add(new(ArtifactIntegrityIssueKind.Missing, record.RelativePath, "Required file is missing."));
                    continue;
                }
                actual.Remove(record.RelativePath);
                if (entry.Length != record.ByteLength)
                {
                    issues.Add(new(ArtifactIntegrityIssueKind.LengthMismatch, record.RelativePath,
                        $"Expected {record.ByteLength} bytes; found {entry.Length}."));
                    continue;
                }
                try
                {
                    using var stream = entry.Open();
                    var hash = ComputeSha256(stream);
                    if (!hash.Equals(record.Sha256, StringComparison.OrdinalIgnoreCase))
                        issues.Add(new(ArtifactIntegrityIssueKind.HashMismatch, record.RelativePath,
                            $"Expected SHA-256 {record.Sha256}; found {hash}."));
                }
                catch (Exception ex)
                {
                    issues.Add(new(ArtifactIntegrityIssueKind.ReadFailure, record.RelativePath, ex.Message));
                }
            }

            foreach (var unexpected in actual.Keys.OrderBy(k => k, StringComparer.Ordinal))
                issues.Add(new(ArtifactIntegrityIssueKind.Unexpected, unexpected, "File is not declared by the manifest."));
        }
        catch (Exception ex)
        {
            issues.Add(new(ArtifactIntegrityIssueKind.ReadFailure, "", $"Could not inspect ZIP payload: {ex.Message}"));
        }

        return Result(zipPath, manifest, issues);
    }

    public static string ComputeSha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return ComputeSha256(stream);
    }

    private static GeneratedArtifactManifest NewManifest(string payloadType) => new()
    {
        SchemaVersion = CurrentSchemaVersion,
        ToolVersion = AppConfig.AppVersion,
        PayloadType = payloadType,
        GeneratedAtUtc = DateTimeOffset.UtcNow
    };

    private static GeneratedArtifactFile CreateFileRecord(string path, string relativePath, string role)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return new GeneratedArtifactFile
        {
            RelativePath = relativePath,
            Role = role,
            ByteLength = stream.Length,
            Sha256 = ComputeSha256(stream),
            Required = true
        };
    }

    private static void VerifyFile(
        string path,
        GeneratedArtifactFile record,
        ICollection<ArtifactIntegrityIssue> issues)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length != record.ByteLength)
            {
                issues.Add(new(ArtifactIntegrityIssueKind.LengthMismatch, record.RelativePath,
                    $"Expected {record.ByteLength} bytes; found {stream.Length}."));
                return;
            }
            var hash = ComputeSha256(stream);
            if (!hash.Equals(record.Sha256, StringComparison.OrdinalIgnoreCase))
                issues.Add(new(ArtifactIntegrityIssueKind.HashMismatch, record.RelativePath,
                    $"Expected SHA-256 {record.Sha256}; found {hash}."));
        }
        catch (Exception ex)
        {
            issues.Add(new(ArtifactIntegrityIssueKind.ReadFailure, record.RelativePath, ex.Message));
        }
    }

    private static Dictionary<string, GeneratedArtifactFile>? ValidateManifest(
        GeneratedArtifactManifest manifest,
        ICollection<ArtifactIntegrityIssue> issues)
    {
        if (manifest.SchemaVersion != CurrentSchemaVersion)
            issues.Add(new(ArtifactIntegrityIssueKind.ManifestInvalid, ManifestFileName,
                $"Unsupported schema version {manifest.SchemaVersion}."));
        if (string.IsNullOrWhiteSpace(manifest.ToolVersion))
            issues.Add(new(ArtifactIntegrityIssueKind.ManifestInvalid, ManifestFileName, "toolVersion is required."));
        if (string.IsNullOrWhiteSpace(manifest.PayloadType))
            issues.Add(new(ArtifactIntegrityIssueKind.ManifestInvalid, ManifestFileName, "payloadType is required."));
        if (manifest.Files is null || manifest.Files.Count > MaxManifestFiles)
            issues.Add(new(ArtifactIntegrityIssueKind.ManifestInvalid, ManifestFileName,
                $"files must contain at most {MaxManifestFiles} entries."));
        if (issues.Count > 0 || manifest.Files is null) return null;

        var records = new Dictionary<string, GeneratedArtifactFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in manifest.Files)
        {
            try
            {
                var rawPath = record.RelativePath ?? string.Empty;
                var path = NormalizeRelativePath(rawPath);
                if (!path.Equals(rawPath, StringComparison.Ordinal))
                    throw new InvalidDataException("relativePath is not normalized.");
                if (path.Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The manifest cannot declare itself.");
                if (string.IsNullOrWhiteSpace(record.Role))
                    throw new InvalidDataException("role is required.");
                if (record.ByteLength < 0)
                    throw new InvalidDataException("byteLength cannot be negative.");
                if (!Sha256Regex().IsMatch(record.Sha256))
                    throw new InvalidDataException("sha256 must be 64 lowercase hexadecimal characters.");
                if (!records.TryAdd(path, record))
                    throw new InvalidDataException("duplicate case-insensitive relativePath.");
            }
            catch (Exception ex)
            {
                issues.Add(new(ArtifactIntegrityIssueKind.ManifestInvalid,
                    record.RelativePath ?? "", ex.Message));
            }
        }
        return issues.Count == 0 ? records : null;
    }

    private static GeneratedArtifactManifest ReadManifestFromFile(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > MaxManifestBytes)
            throw new InvalidDataException($"Manifest exceeds {MaxManifestBytes} bytes.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return DeserializeManifest(stream);
    }

    private static GeneratedArtifactManifest ReadManifestFromZip(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var matches = zip.Entries
            .Where(e => e.FullName.Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length == 0)
            throw new FileNotFoundException($"{ManifestFileName} is missing from the ZIP.");
        if (matches.Length != 1)
            throw new InvalidDataException($"ZIP contains {matches.Length} artifact manifests.");
        if (matches[0].Length > MaxManifestBytes)
            throw new InvalidDataException($"Manifest exceeds {MaxManifestBytes} bytes.");
        using var stream = matches[0].Open();
        return DeserializeManifest(stream);
    }

    private static GeneratedArtifactManifest DeserializeManifest(Stream stream) =>
        JsonSerializer.Deserialize<GeneratedArtifactManifest>(stream, JsonOptions)
        ?? throw new InvalidDataException("Manifest is empty.");

    private static void WriteManifestDurably(string path, GeneratedArtifactManifest manifest)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            4096, FileOptions.WriteThrough);
        JsonSerializer.Serialize(stream, manifest, JsonOptions);
        stream.WriteByte((byte)'\n');
        stream.Flush(flushToDisk: true);
    }

    private static IEnumerable<string> EnumeratePayloadFiles(string root) =>
        Directory.EnumerateFiles(root, "*", DirectoryEnumerationOptions)
            .Where(path => !Path.GetRelativePath(root, path)
                .Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase));

    private static bool IsPayloadEntry(ZipArchiveEntry entry) =>
        !string.IsNullOrEmpty(entry.Name) &&
        !entry.FullName.Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase);

    private static string NormalizeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.IndexOf('\0') >= 0)
            throw new InvalidDataException("Path must be a non-empty relative path.");
        var normalized = path.Replace('\\', '/');
        var segments = normalized.Split('/');
        if (segments.Any(s => s.Length == 0 || s is "." or ".." || s.Contains(':')))
            throw new InvalidDataException("Path contains an empty, rooted, or traversal segment.");
        return string.Join('/', segments);
    }

    private static string RequireDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Payload root is required.", nameof(path));
        var root = Path.GetFullPath(path);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Payload directory does not exist: {root}");
        return root;
    }

    private static void ValidatePayloadType(string payloadType)
    {
        if (string.IsNullOrWhiteSpace(payloadType) || payloadType.Length > 100)
            throw new ArgumentException("Payload type must be between 1 and 100 characters.", nameof(payloadType));
    }

    private static string ResolveRole(Func<string, string>? resolver, string path)
    {
        var role = resolver?.Invoke(path) ?? "payload";
        if (string.IsNullOrWhiteSpace(role) || role.Length > 100)
            throw new InvalidDataException($"Invalid role for '{path}'.");
        return role;
    }

    private static void SortAndValidateRecords(List<GeneratedArtifactFile> records)
    {
        records.Sort((a, b) => StringComparer.Ordinal.Compare(a.RelativePath, b.RelativePath));
        var duplicate = records.GroupBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new InvalidDataException($"Payload contains duplicate case-insensitive path '{duplicate.Key}'.");
    }

    private static string ComputeSha256(Stream stream) =>
        Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();

    private static ArtifactIntegrityResult Failed(
        string path,
        ArtifactIntegrityIssueKind kind,
        string relativePath,
        string detail) => new()
    {
        PayloadPath = path ?? string.Empty,
        Issues = [new(kind, relativePath, detail)]
    };

    private static ArtifactIntegrityResult Result(
        string path,
        GeneratedArtifactManifest manifest,
        List<ArtifactIntegrityIssue> issues) => new()
    {
        PayloadPath = path,
        PayloadType = manifest.PayloadType,
        SchemaVersion = manifest.SchemaVersion,
        Issues = issues
    };

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Regex();
}
