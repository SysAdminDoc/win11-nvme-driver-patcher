using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class CodeIntegrityEventServiceTests
{
    [Fact]
    public void TryCreateBackupDriverEvent_RecognizesEnforcedPsmounterexBlock()
    {
        var timestamp = new DateTime(2026, 4, 15, 12, 0, 0, DateTimeKind.Utc);
        var ev = CodeIntegrityEventService.TryCreateBackupDriverEvent(
            3077,
            timestamp,
            @"Code Integrity determined that a process (\Device\HarddiskVolume3\Windows\System32\drivers\psmounterex.sys) attempted to load psmounterex.sys from C:\Users\alice\Downloads.");

        Assert.NotNull(ev);
        Assert.Equal("psmounterex.sys", ev!.DriverFile);
        Assert.Equal("Enforced block", ev.Mode);
        Assert.Equal(timestamp, ev.TimestampUtc);
        Assert.Contains("Macrium Reflect", ev.AffectedProducts);
        Assert.Contains("NinjaOne", ev.AffectedProducts);
        Assert.Contains("psmounterex.sys", ev.Evidence);
        Assert.DoesNotContain(@"C:\Users\alice", ev.Evidence, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryCreateBackupDriverEvent_RecognizesAuditMode()
    {
        var ev = CodeIntegrityEventService.TryCreateBackupDriverEvent(
            3076,
            DateTime.UtcNow,
            "Audit: blocked driver psmounter.sys would have been denied.");

        Assert.NotNull(ev);
        Assert.Equal("Audit block", ev!.Mode);
        Assert.Equal("psmounter.sys", ev.DriverFile);
    }

    [Fact]
    public void TryCreateBackupDriverEvent_IgnoresUnknownDriversAndOtherEventIds()
    {
        Assert.Null(CodeIntegrityEventService.TryCreateBackupDriverEvent(
            3077,
            DateTime.UtcNow,
            "Blocked driver unrelated.sys"));
        Assert.Null(CodeIntegrityEventService.TryCreateBackupDriverEvent(
            1000,
            DateTime.UtcNow,
            "Blocked driver psmounterex.sys"));
    }

    [Fact]
    public void DescribeForPreflight_SummarizesProductsAndDrivers()
    {
        var ev = CodeIntegrityEventService.TryCreateBackupDriverEvent(
            3077,
            DateTime.UtcNow,
            "Blocked psmounterex.sys");

        var summary = CodeIntegrityEventService.DescribeForPreflight([ev!]);

        Assert.Contains("psmounterex.sys", summary);
        Assert.Contains("Macrium Reflect", summary);
        Assert.Contains("CodeIntegrity", summary);
    }
}
