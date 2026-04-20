using System.IO;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class CompatChecksumServiceTests : IDisposable
{
    private readonly string _dir;

    public CompatChecksumServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "NVMePatcher_ChecksumTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void IdenticalFiles_ReportShippedDefault()
    {
        var shipped = Path.Combine(_dir, "shipped.json");
        var local = Path.Combine(_dir, "local.json");
        File.WriteAllText(shipped, "payload");
        File.WriteAllText(local, "payload");
        var r = CompatChecksumService.Verify(local, shipped);
        Assert.True(r.ShippedDefault);
        Assert.Equal(r.Sha256, r.ShippedSha256);
    }

    [Fact]
    public void DifferingFiles_ReportCustomized()
    {
        var shipped = Path.Combine(_dir, "shipped.json");
        var local = Path.Combine(_dir, "local.json");
        File.WriteAllText(shipped, "payload-A");
        File.WriteAllText(local, "payload-B");
        var r = CompatChecksumService.Verify(local, shipped);
        Assert.False(r.ShippedDefault);
        Assert.NotEqual(r.Sha256, r.ShippedSha256);
        Assert.Contains("customized", r.Summary);
    }

    [Fact]
    public void MissingLocal_FallsBackToShipped()
    {
        var shipped = Path.Combine(_dir, "shipped.json");
        var local = Path.Combine(_dir, "missing.json");
        File.WriteAllText(shipped, "payload");
        var r = CompatChecksumService.Verify(local, shipped);
        Assert.True(r.ShippedDefault);
    }

    [Fact]
    public void BothMissing_ReturnsNoCompatPresent()
    {
        var r = CompatChecksumService.Verify(null, Path.Combine(_dir, "nope.json"));
        Assert.False(r.ShippedDefault);
        Assert.Equal(string.Empty, r.Sha256);
    }
}
