using System.IO;
using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class RecoveryKitFreshnessServiceTests : IDisposable
{
    private readonly string _dir;

    public RecoveryKitFreshnessServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "NVMePatcher_KitTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void NullPath_ReportsMissing()
    {
        var config = new AppConfig { LastRecoveryKitPath = null };
        var report = RecoveryKitFreshnessService.Evaluate(config);
        Assert.Equal(RecoveryKitFreshness.Missing, report.State);
        Assert.True(report.ShouldNag);
    }

    [Fact]
    public void PathDoesNotExist_ReportsMissing()
    {
        var config = new AppConfig { LastRecoveryKitPath = Path.Combine(_dir, "does_not_exist") };
        var report = RecoveryKitFreshnessService.Evaluate(config);
        Assert.Equal(RecoveryKitFreshness.Missing, report.State);
    }

    [Fact]
    public void EmptyFolder_ReportsUnknown()
    {
        var config = new AppConfig { LastRecoveryKitPath = _dir };
        var report = RecoveryKitFreshnessService.Evaluate(config);
        Assert.Equal(RecoveryKitFreshness.Unknown, report.State);
        Assert.False(report.ShouldNag);
    }

    [Fact]
    public void FreshFile_ReportsFresh()
    {
        var file = Path.Combine(_dir, "NVMe_Remove_Patch.reg");
        File.WriteAllText(file, "payload");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-5));
        var config = new AppConfig { LastRecoveryKitPath = _dir };
        var report = RecoveryKitFreshnessService.Evaluate(config);
        Assert.Equal(RecoveryKitFreshness.Fresh, report.State);
        Assert.False(report.ShouldNag);
    }

    [Fact]
    public void OldFile_ReportsStale()
    {
        var file = Path.Combine(_dir, "NVMe_Remove_Patch.reg");
        File.WriteAllText(file, "payload");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-45));
        var config = new AppConfig { LastRecoveryKitPath = _dir };
        var report = RecoveryKitFreshnessService.Evaluate(config);
        Assert.Equal(RecoveryKitFreshness.Stale, report.State);
        Assert.True(report.ShouldNag);
        Assert.Contains("45 day(s)", report.Summary);
    }
}
