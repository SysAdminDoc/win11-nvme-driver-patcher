namespace NVMeDriverPatcher.Models;

public sealed class GeneratedArtifactManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string ToolVersion { get; set; } = string.Empty;
    public string PayloadType { get; set; } = string.Empty;
    public DateTimeOffset GeneratedAtUtc { get; set; }
    public List<GeneratedArtifactFile> Files { get; set; } = [];
}

public sealed class GeneratedArtifactFile
{
    public string RelativePath { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public long ByteLength { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public bool Required { get; set; } = true;
}

public enum ArtifactIntegrityIssueKind
{
    ManifestMissing,
    ManifestInvalid,
    Missing,
    Unexpected,
    LengthMismatch,
    HashMismatch,
    ReadFailure
}

public sealed record ArtifactIntegrityIssue(
    ArtifactIntegrityIssueKind Kind,
    string RelativePath,
    string Detail);

public sealed class ArtifactIntegrityResult
{
    public string PayloadPath { get; init; } = string.Empty;
    public string? PayloadType { get; init; }
    public int? SchemaVersion { get; init; }
    public List<ArtifactIntegrityIssue> Issues { get; init; } = [];
    public bool Success => Issues.Count == 0;

    public string Summary => Success
        ? $"Payload integrity verified ({PayloadType ?? "unknown"}, schema {SchemaVersion?.ToString() ?? "?"})."
        : $"Payload integrity verification failed with {Issues.Count} issue(s).";
}
