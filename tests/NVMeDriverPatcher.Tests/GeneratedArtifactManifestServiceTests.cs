using System.IO.Compression;
using System.Text.Json;
using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class GeneratedArtifactManifestServiceTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(), $"NVMeDriverPatcher.ArtifactManifest.Tests.{Guid.NewGuid():N}");

    public GeneratedArtifactManifestServiceTests() => Directory.CreateDirectory(_tempRoot);

    [Fact]
    public void PublishDirectoryManifest_RecordsVersionRoleLengthAndHash_ThenVerifies()
    {
        Write("scripts/remove.cmd", "@echo off\r\nexit /b 0\r\n");
        Write("README.txt", "recovery instructions");

        var manifest = GeneratedArtifactManifestService.PublishDirectoryManifest(
            _tempRoot,
            "recovery-kit",
            path => path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ? "mutation" : "documentation");

        Assert.Equal(1, manifest.SchemaVersion);
        Assert.Equal(AppConfig.AppVersion, manifest.ToolVersion);
        Assert.Equal("recovery-kit", manifest.PayloadType);
        Assert.Equal(2, manifest.Files.Count);
        Assert.All(manifest.Files, file =>
        {
            Assert.True(file.Required);
            Assert.True(file.ByteLength > 0);
            Assert.Matches("^[0-9a-f]{64}$", file.Sha256);
            Assert.DoesNotContain('\\', file.RelativePath);
        });
        Assert.Equal("mutation", manifest.Files.Single(f => f.RelativePath == "scripts/remove.cmd").Role);

        var result = GeneratedArtifactManifestService.VerifyDirectory(_tempRoot);
        Assert.True(result.Success, result.Summary + Environment.NewLine +
            string.Join(Environment.NewLine, result.Issues.Select(i => i.Detail)));
    }

    [Fact]
    public void VerifyDirectory_IdentifiesMissingUnexpectedTruncatedAndModifiedFilesExactly()
    {
        Write("missing.txt", "present during publication");
        Write("truncated.txt", "this file will become shorter");
        Write("modified.txt", "AAAA");
        GeneratedArtifactManifestService.PublishDirectoryManifest(_tempRoot, "test-payload");

        File.Delete(Path.Combine(_tempRoot, "missing.txt"));
        File.WriteAllText(Path.Combine(_tempRoot, "truncated.txt"), "x");
        File.WriteAllText(Path.Combine(_tempRoot, "modified.txt"), "BBBB"); // same length, different hash
        File.WriteAllText(Path.Combine(_tempRoot, "unexpected.txt"), "surprise");

        var result = GeneratedArtifactManifestService.VerifyDirectory(_tempRoot);

        Assert.False(result.Success);
        Assert.Contains(result.Issues, i => i.Kind == ArtifactIntegrityIssueKind.Missing && i.RelativePath == "missing.txt");
        Assert.Contains(result.Issues, i => i.Kind == ArtifactIntegrityIssueKind.LengthMismatch && i.RelativePath == "truncated.txt");
        Assert.Contains(result.Issues, i => i.Kind == ArtifactIntegrityIssueKind.HashMismatch && i.RelativePath == "modified.txt");
        Assert.Contains(result.Issues, i => i.Kind == ArtifactIntegrityIssueKind.Unexpected && i.RelativePath == "unexpected.txt");
    }

    [Fact]
    public void VerifyDirectory_RejectsManifestTraversalWithoutReadingOutsideRoot()
    {
        Write("inside.txt", "inside");
        GeneratedArtifactManifestService.PublishDirectoryManifest(_tempRoot, "test-payload");
        var manifestPath = Path.Combine(_tempRoot, GeneratedArtifactManifestService.ManifestFileName);
        var json = File.ReadAllText(manifestPath).Replace("inside.txt", "../outside.txt", StringComparison.Ordinal);
        File.WriteAllText(manifestPath, json);

        var result = GeneratedArtifactManifestService.VerifyDirectory(_tempRoot);

        Assert.False(result.Success);
        Assert.Contains(result.Issues, i => i.Kind == ArtifactIntegrityIssueKind.ManifestInvalid &&
                                            i.RelativePath == "../outside.txt");
    }

    [Fact]
    public void PublishZipManifest_CommitsFinalEntryAndDetectsArchiveTampering()
    {
        var zipPath = Path.Combine(_tempRoot, "bundle.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("diagnostics.txt");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("healthy");
        }

        GeneratedArtifactManifestService.PublishZipManifest(zipPath, "support-bundle",
            _ => "diagnostics");
        var initial = GeneratedArtifactManifestService.VerifyZip(zipPath);
        Assert.True(initial.Success, initial.Summary);

        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Update))
        {
            var entry = zip.GetEntry("diagnostics.txt")!;
            entry.Delete();
            entry = zip.CreateEntry("diagnostics.txt");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("tampered");
        }

        var tampered = GeneratedArtifactManifestService.VerifyZip(zipPath);
        Assert.False(tampered.Success);
        Assert.Contains(tampered.Issues, i => i.RelativePath == "diagnostics.txt" &&
            i.Kind is ArtifactIntegrityIssueKind.LengthMismatch or ArtifactIntegrityIssueKind.HashMismatch);
    }

    [Fact]
    public void VerifyZip_RejectsDuplicateCaseInsensitivePaths()
    {
        var zipPath = Path.Combine(_tempRoot, "duplicates.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            WriteEntry(zip, "data.txt", "one");
            WriteEntry(zip, "DATA.txt", "two");
        }

        Assert.Throws<InvalidDataException>(() =>
            GeneratedArtifactManifestService.PublishZipManifest(zipPath, "bad-payload"));
    }

    [Fact]
    public void PublishDirectoryManifest_LeavesNoTemporaryManifestSidecar()
    {
        Write("payload.bin", "bytes");

        GeneratedArtifactManifestService.PublishDirectoryManifest(_tempRoot, "test-payload");

        Assert.Empty(Directory.GetFiles(_tempRoot, "ARTIFACT-MANIFEST.json.*.tmp"));
        using var document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(_tempRoot, GeneratedArtifactManifestService.ManifestFileName)));
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
    }

    private void Write(string relativePath, string content)
    {
        var path = Path.Combine(_tempRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static void WriteEntry(ZipArchive zip, string path, string content)
    {
        var entry = zip.CreateEntry(path);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }
}
