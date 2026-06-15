using System.IO;
using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class CleanDataServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly AppConfig _config;

    public CleanDataServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "NVMePatcher_CleanTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _config = new AppConfig { WorkingDir = _dir };
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void MissingWorkingDir_ReportsSuccess()
    {
        var config = new AppConfig { WorkingDir = Path.Combine(_dir, "nonexistent") };
        var result = CleanDataService.Clean(config);
        Assert.True(result.Success);
        Assert.Equal(0, result.FilesRemoved);
    }

    [Fact]
    public void CleansAllDefaultTargets()
    {
        // Seed each known target class with one file.
        File.WriteAllText(Path.Combine(_dir, "crash.log"), "x");
        File.WriteAllText(Path.Combine(_dir, "activity.log.1"), "x");
        Directory.CreateDirectory(Path.Combine(_dir, "etl"));
        File.WriteAllText(Path.Combine(_dir, "etl", "pre.etl"), "x");
        File.WriteAllText(Path.Combine(_dir, "Pre_Patch_Backup_20260420.reg"), "x");
        File.WriteAllText(Path.Combine(_dir, "nvmepatcher.db"), "x");
        File.WriteAllText(Path.Combine(_dir, "nvmepatcher.db-wal"), "x");
        File.WriteAllText(Path.Combine(_dir, "support_bundle_20260420.zip"), "x");
        File.WriteAllText(Path.Combine(_dir, "anon_id.txt"), "id");
        File.WriteAllText(Path.Combine(_dir, "compat_report.json"), "{}");

        var result = CleanDataService.Clean(_config);

        Assert.True(result.Success);
        Assert.True(result.FilesRemoved >= 9);
        Assert.False(File.Exists(Path.Combine(_dir, "crash.log")));
        Assert.False(File.Exists(Path.Combine(_dir, "etl", "pre.etl")));
        Assert.False(File.Exists(Path.Combine(_dir, "anon_id.txt")));
    }

    [Fact]
    public void SelectiveTargets_OnlyCleanRequested()
    {
        File.WriteAllText(Path.Combine(_dir, "crash.log"), "x");
        File.WriteAllText(Path.Combine(_dir, "nvmepatcher.db"), "x");
        var result = CleanDataService.Clean(_config, new[] { "logs" });
        Assert.False(File.Exists(Path.Combine(_dir, "crash.log")));
        Assert.True(File.Exists(Path.Combine(_dir, "nvmepatcher.db")));
    }

    [Fact]
    public void UnrelatedFiles_AreNotTouched()
    {
        File.WriteAllText(Path.Combine(_dir, "user_notes.md"), "keep me");
        var result = CleanDataService.Clean(_config);
        Assert.True(File.Exists(Path.Combine(_dir, "user_notes.md")));
    }
    // --- Scope guard: recursive deletion must never target system locations ---

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"C:")]
    [InlineData(@"D:\")]
    [InlineData("")]
    [InlineData("   ")]
    public void IsSafeCleanRoot_RefusesDriveRootsAndEmpty(string dir)
    {
        Assert.False(CleanDataService.IsSafeCleanRoot(dir, out var reason));
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void IsSafeCleanRoot_RefusesProtectedSystemLocations()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Assert.False(CleanDataService.IsSafeCleanRoot(windows, out _));
        Assert.False(CleanDataService.IsSafeCleanRoot(Path.Combine(windows, "System32"), out _));
        Assert.False(CleanDataService.IsSafeCleanRoot(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), out _));
    }

    [Fact]
    public void IsSafeCleanRoot_AllowsLocalAppDataAndPortableStyleDirs()
    {
        var localAppData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NVMePatcher");
        Assert.True(CleanDataService.IsSafeCleanRoot(localAppData, out _));
        Assert.True(CleanDataService.IsSafeCleanRoot(_dir, out _)); // temp-based test dir
    }

    [Fact]
    public void Clean_AgainstDriveRoot_RefusesAndDeletesNothing()
    {
        var cfg = new AppConfig { WorkingDir = @"C:\" };
        var result = CleanDataService.Clean(cfg);
        Assert.False(result.Success);
        Assert.Equal(0, result.FilesRemoved);
        Assert.Contains("Refusing to clean", result.Summary);
    }

    [Fact]
    public void IsSafeCleanRoot_RefusesSubtreesOfProgramFilesAndUserProfile()
    {
        // Defense-in-depth: a portable install dropped directly under a protected root must be
        // refused, not just the exact protected dir.
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Assert.False(CleanDataService.IsSafeCleanRoot(Path.Combine(programFiles, "NVMePatcher"), out var r1));
        Assert.Contains("protected", r1, StringComparison.OrdinalIgnoreCase);

        // A path directly under the user profile (NOT under the LocalAppData app zone) is refused.
        Assert.False(CleanDataService.IsSafeCleanRoot(Path.Combine(userProfile, "NVMePatcherStuff"), out _));
    }

    [Fact]
    public void IsSafeCleanRoot_AllowsAppManagedRootsUnderProtectedParents()
    {
        // The default app dir lives under LocalAppData (itself under the user profile) and must pass.
        var localAppData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NVMePatcher");
        Assert.True(CleanDataService.IsSafeCleanRoot(localAppData, out _));
    }
}
