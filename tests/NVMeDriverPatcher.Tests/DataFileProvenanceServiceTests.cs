using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class DataFileProvenanceServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"NVMeDriverPatcher.Provenance.Tests.{Guid.NewGuid():N}");

    public DataFileProvenanceServiceTests()
    {
        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public void Inspect_ReportsLocalOverrideChecksumSchemaAndFreshness()
    {
        var shippedDir = Path.Combine(_dir, "app");
        var localDir = Path.Combine(_dir, "local");
        Directory.CreateDirectory(shippedDir);
        Directory.CreateDirectory(localDir);
        File.WriteAllText(Path.Combine(shippedDir, "compat.json"), Payload("2026-06-10", "2026-06-10", "shipped"));
        File.WriteAllText(Path.Combine(localDir, "compat.json"), Payload("2026-06-11", "2026-06-11", "local"));

        var result = DataFileProvenanceService.Inspect("Firmware compatibility DB", "compat.json", localDir, shippedDir, staleAfterDays: 30);

        Assert.True(result.Exists);
        Assert.Equal("local override", result.SourceKind);
        Assert.True(result.IsCustomized);
        Assert.Equal(1, result.SchemaVersion);
        Assert.Equal("2026-06-11", result.Updated);
        Assert.Equal("2026-06-11", result.NewestLastReviewed);
        Assert.False(result.IsStale);
        Assert.False(string.IsNullOrWhiteSpace(result.Sha256));
        Assert.False(string.IsNullOrWhiteSpace(result.ShippedSha256));
    }

    [Fact]
    public void Inspect_FlagsStaleDataWhenReviewedDateIsOld()
    {
        var shippedDir = Path.Combine(_dir, "app-old");
        Directory.CreateDirectory(shippedDir);
        File.WriteAllText(Path.Combine(shippedDir, "windows_build_rules.json"), Payload("2000-01-01", "2000-01-02", "old"));

        var result = DataFileProvenanceService.Inspect("Windows build rules", "windows_build_rules.json", null, shippedDir, staleAfterDays: 30);

        Assert.True(result.IsStale);
        Assert.Contains("STALE", result.Summary);
        Assert.Equal("2000-01-02", result.NewestLastReviewed);
    }

    [Fact]
    public void DescribeForPreflight_UsesWarningTextForStaleFiles()
    {
        var summary = DataFileProvenanceService.DescribeForPreflight(
        [
            new()
            {
                FileName = "compat.json",
                SourceKind = "bundled default",
                SchemaVersion = 1,
                NewestLastReviewed = "2000-01-02",
                IsStale = true,
                StaleAfterDays = 30,
                Sha256 = "abcdef0123456789",
                Summary = "compat.json: STALE test."
            }
        ]);

        Assert.Contains("STALE", summary);
        Assert.Contains("compat.json", summary);
    }

    [Fact]
    public void BundledDataFiles_HaveProvenance()
    {
        var all = DataFileProvenanceService.InspectAll();

        Assert.Contains(all, f => f.FileName == "windows_build_rules.json" && f.Exists && f.SchemaVersion >= 1);
        Assert.Contains(all, f => f.FileName == "compat.json" && f.Exists && f.SchemaVersion >= 1);
        Assert.All(all, f => Assert.False(string.IsNullOrWhiteSpace(f.Sha256)));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static string Payload(string updated, string reviewed, string note)
    {
        return $$"""
            {
              "schemaVersion": 1,
              "updated": "{{updated}}",
              "entries": [
                {
                  "controller": "{{note}}",
                  "firmware": "*",
                  "level": "Good",
                  "note": "{{note}}",
                  "lastReviewed": "{{reviewed}}"
                }
              ]
            }
            """;
    }
}
