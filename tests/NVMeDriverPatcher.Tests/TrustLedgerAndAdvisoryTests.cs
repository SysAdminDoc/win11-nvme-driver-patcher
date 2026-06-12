using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class TrustLedgerAndAdvisoryTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"NVMeDriverPatcher.TrustLedger.Tests.{Guid.NewGuid():N}");

    public TrustLedgerAndAdvisoryTests()
    {
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public void BuildTrustLedger_AlwaysEmitsCoreSections_AndNeverThrows()
    {
        // Every probe is best-effort; on any machine (admin or not, ViVeTool cached or not)
        // the ledger must render its core lines rather than throw.
        var ledger = DiagnosticsService.BuildTrustLedger(_tempRoot);
        Assert.Contains("Trust ledger:", ledger);
        Assert.Contains("App version:", ledger);
        Assert.Contains("Install channel:", ledger);
        Assert.Contains("ViVeTool cache:", ledger);
        Assert.Contains("FeatureStore blob:", ledger);
        Assert.Contains("Fallback ID set:", ledger);
        Assert.Contains("Data files:", ledger);
        Assert.Contains("windows_build_rules.json", ledger);
        Assert.Contains("compat.json", ledger);
        Assert.Contains("sha256", ledger);
    }

    [Fact]
    public void BuildTrustLedger_HashesCachedViVeTool()
    {
        var toolsDir = Path.Combine(_tempRoot, "tools");
        Directory.CreateDirectory(toolsDir);
        File.WriteAllBytes(Path.Combine(toolsDir, "ViVeTool.exe"), new byte[] { 1, 2, 3, 4 });

        var ledger = DiagnosticsService.BuildTrustLedger(_tempRoot);
        // SHA-256 of 01 02 03 04
        Assert.Contains("9f64a747e1b97f131fabb6b447296c9b6f0201e79fb3c5356e6c77e89b6a806a", ledger);
        Assert.Contains("4 bytes", ledger);
    }

    [Fact]
    public void CompatDatabase_LoadsCveAdvisories_WithApplicabilityBoundary()
    {
        var db = FirmwareCompatService.LoadDatabase();
        var advisory = db.CveAdvisories.FirstOrDefault(a => a.Cve == "CVE-2026-34332");
        Assert.NotNull(advisory);
        // The documented boundary: Server 2025 NVMe-oF only — must never alarm client users.
        Assert.True(advisory!.AffectsServer);
        Assert.False(advisory.AffectsClient);
        Assert.Equal("KB5087539", advisory.FixedBy);
        Assert.False(string.IsNullOrWhiteSpace(advisory.Description));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }
        catch
        {
            // Best-effort temp cleanup only.
        }
    }
}
