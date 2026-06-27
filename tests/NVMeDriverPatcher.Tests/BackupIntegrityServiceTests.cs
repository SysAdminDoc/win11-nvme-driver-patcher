using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class BackupIntegrityServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NVMePatcher_BackupTests_" + Guid.NewGuid().ToString("N"));

    public BackupIntegrityServiceTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Verify_ValidRegExportCountsSectionsAndValues()
    {
        var path = Path.Combine(_dir, "backup.reg");
        File.WriteAllText(path, """
            Windows Registry Editor Version 5.00

            [HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\nvmedisk]
            @="Storage Disks"
            "Start"=dword:00000000

            [HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\stornvme]
            "Start"=dword:00000003
            """);

        var result = BackupIntegrityService.Verify(path);

        Assert.True(result.Success);
        Assert.Equal(3, result.ValueCount);
        Assert.Contains("2 key section", result.Summary);
    }

    [Fact]
    public void Verify_InvalidHeaderFails()
    {
        var path = Path.Combine(_dir, "bad.reg");
        File.WriteAllText(path, "not a registry export");

        var result = BackupIntegrityService.Verify(path);

        Assert.False(result.Success);
        Assert.Contains("missing the Windows Registry Editor header", result.Summary);
    }

    [Fact]
    public void Verify_NoSectionsFails()
    {
        var path = Path.Combine(_dir, "empty.reg");
        File.WriteAllText(path, """
            REGEDIT4

            "Start"=dword:00000003
            """);

        var result = BackupIntegrityService.Verify(path);

        Assert.False(result.Success);
        Assert.Contains("no registry key sections", result.Summary);
    }
}
