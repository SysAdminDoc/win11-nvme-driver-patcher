using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

// The curated data files are published loose beside each single-file exe, so the bare exe that
// winget, Scoop, Chocolatey and a direct download hand out arrives without them. These tests pin
// the embedded copy that keeps such an install on the shipped rules, IDs and compat list.
public sealed class BundledDataFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"NVMeDriverPatcher.BundledData.Tests.{Guid.NewGuid():N}");
    private readonly string _emptyAppDir;
    private readonly string _emptyWorkDir;

    public BundledDataFileTests()
    {
        _emptyAppDir = Path.Combine(_dir, "bare-exe");
        _emptyWorkDir = Path.Combine(_dir, "state");
        Directory.CreateDirectory(_emptyAppDir);
        Directory.CreateDirectory(_emptyWorkDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static string RepoRoot([CallerFilePath] string sourceFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));

    public static TheoryData<string> DataFiles => new()
    {
        "feature_ids.json",
        "windows_build_rules.json",
        "compat.json",
    };

    [Theory]
    [MemberData(nameof(DataFiles))]
    public void CoreAssembly_EmbedsEveryShippedDataFile(string fileName)
    {
        var names = typeof(FeatureIdCatalogService).Assembly.GetManifestResourceNames();
        Assert.Contains("NVMeDriverPatcher." + fileName, names);
    }

    [Theory]
    [MemberData(nameof(DataFiles))]
    public void EmbeddedCopy_IsByteForByteTheReviewedSourceFile(string fileName)
    {
        var source = File.ReadAllBytes(Path.Combine(RepoRoot(), "src", "NVMeDriverPatcher.Core", fileName));
        Assert.Equal(source, BundledDataFileService.ReadEmbeddedBytes(fileName));
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(source)).ToLowerInvariant(),
            BundledDataFileService.EmbeddedSha256(fileName));
    }

    [Fact]
    public void FeatureCatalog_WithNoLooseCopy_LoadsTheEmbeddedCatalog()
    {
        // An empty catalog left PostBlockFeatureIds empty, so the FeatureStore evidence probe had
        // nothing to look for and a surviving fallback read as a clean removal.
        Assert.NotEmpty(FeatureIdCatalogService.LoadBundledCatalog(_emptyAppDir).Branches);
        Assert.NotEmpty(FeatureIdCatalogService.LoadCatalog(_emptyWorkDir, _emptyAppDir).Branches);
        Assert.NotEmpty(FeatureIdCatalogService.GetKnownIds(FeatureIdCatalogService.LoadBundledCatalog(_emptyAppDir)));
    }

    [Fact]
    public void FeatureCatalog_ValidationToolingNeverFallsBackToTheEmbeddedCopy()
    {
        Assert.Empty(FeatureIdCatalogService.LoadFromPath(Path.Combine(_emptyAppDir, "feature_ids.json")).Branches);
    }

    [Fact]
    public void BuildRules_WithNoLooseCopy_LoadTheEmbeddedRuleset()
    {
        // An empty ruleset matches no build, which turns every build verify/rollback-only.
        Assert.NotEmpty(WindowsBuildRulesService.LoadRuleset(_emptyWorkDir, _emptyAppDir).Rules);
    }

    [Fact]
    public void CompatDb_WithNoLooseCopy_LoadsTheEmbeddedList()
    {
        Assert.NotEmpty(FirmwareCompatService.LoadDatabase(_emptyWorkDir, _emptyAppDir).Entries);
    }

    [Fact]
    public void CompatDb_AdminOverrideStillWinsOverTheEmbeddedCopy()
    {
        File.WriteAllText(Path.Combine(_emptyWorkDir, "compat.json"), """
            { "schemaVersion": 1, "updated": "2026-10-01", "entries": [
              { "controller": "Override Controller", "firmware": "*", "level": "Bad", "note": "override" } ] }
            """);

        var db = FirmwareCompatService.LoadDatabase(_emptyWorkDir, _emptyAppDir);

        var entry = Assert.Single(db.Entries);
        Assert.Equal("override", entry.Note);
    }

    [Fact]
    public void Provenance_WithNoLooseCopy_ReportsTheEmbeddedCopyInsteadOfMissing()
    {
        var result = DataFileProvenanceService.Inspect(
            "Curated feature ID catalog",
            "feature_ids.json",
            workingDir: null,
            shippedDir: _emptyAppDir,
            staleAfterDays: 30);

        Assert.True(result.Exists);
        Assert.Equal(BundledDataFileService.EmbeddedSourceKind, result.SourceKind);
        Assert.Equal(BundledDataFileService.EmbeddedSha256("feature_ids.json"), result.Sha256);
        Assert.False(result.IsCustomized);
        Assert.True(result.SchemaVersion >= 1);
        Assert.DoesNotContain("missing", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Provenance_LooseCopyStillReportsAsBundledDefault()
    {
        File.Copy(
            Path.Combine(RepoRoot(), "src", "NVMeDriverPatcher.Core", "compat.json"),
            Path.Combine(_emptyAppDir, "compat.json"));

        var result = DataFileProvenanceService.Inspect("Firmware compatibility DB", "compat.json", null, _emptyAppDir, 30);

        Assert.Equal("bundled default", result.SourceKind);
        Assert.Equal(Path.Combine(_emptyAppDir, "compat.json"), result.ActivePath);
    }

    [Fact]
    public void CompatChecksum_WithNoLooseCopy_MatchesTheEmbeddedDefault()
    {
        var r = CompatChecksumService.Verify(
            Path.Combine(_emptyWorkDir, "compat.json"),
            Path.Combine(_emptyAppDir, "compat.json"),
            embeddedFileName: "compat.json");

        Assert.True(r.ShippedDefault);
        Assert.Equal(BundledDataFileService.EmbeddedSha256("compat.json"), r.Sha256);
    }

    [Fact]
    public void FallbackEvidence_WithAnEmptyCatalog_IsUnknownNotClean()
    {
        // Removal and verification treat an exception from the probe as unverified residue.
        var ex = Assert.Throws<InvalidOperationException>(() =>
            FeatureStoreWriterService.HasFallbackEvidence(Array.Empty<int>()));
        Assert.Contains("catalog", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
