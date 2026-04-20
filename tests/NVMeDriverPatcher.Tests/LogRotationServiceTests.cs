using System.IO;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

// LogRotationService.RotateOne is pure once the file exists. These tests run against a
// per-test TEMP working dir so they don't interfere with the developer's %LocalAppData%.
public sealed class LogRotationServiceTests : IDisposable
{
    private readonly string _dir;

    public LogRotationServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "NVMePatcher_LogRotTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void SmallFile_BelowLimit_NotRotated()
    {
        var path = Path.Combine(_dir, "crash.log");
        File.WriteAllText(path, new string('x', 1024));  // 1 KB
        LogRotationService.RotateOne(path, maxBytesPerFile: 5 * 1024 * 1024, retain: 5);
        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".1"));
    }

    [Fact]
    public void OversizedFile_Rotates_To_Dot1()
    {
        var path = Path.Combine(_dir, "crash.log");
        File.WriteAllText(path, new string('x', 6000));  // 6 KB
        LogRotationService.RotateOne(path, maxBytesPerFile: 4000, retain: 5);
        Assert.False(File.Exists(path));
        Assert.True(File.Exists(path + ".1"));
    }

    [Fact]
    public void ExistingGenerations_ShiftUpByOne()
    {
        var path = Path.Combine(_dir, "crash.log");
        File.WriteAllText(path, "oversized content xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx");
        File.WriteAllText(path + ".1", "gen-1");
        File.WriteAllText(path + ".2", "gen-2");
        LogRotationService.RotateOne(path, maxBytesPerFile: 16, retain: 5);
        // New .1 = previous current, .2 = previous .1, .3 = previous .2.
        Assert.Equal("gen-1", File.ReadAllText(path + ".2"));
        Assert.Equal("gen-2", File.ReadAllText(path + ".3"));
    }

    [Fact]
    public void MissingFile_NoOp()
    {
        var path = Path.Combine(_dir, "never_existed.log");
        LogRotationService.RotateOne(path, maxBytesPerFile: 1, retain: 5);
        Assert.False(File.Exists(path));
    }
}
