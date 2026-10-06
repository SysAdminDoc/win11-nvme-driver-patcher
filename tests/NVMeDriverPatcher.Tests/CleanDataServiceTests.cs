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
    public void Backups_SweepOlderWinReImagesAndKeepTheNewest()
    {
        // `winre-inject --apply` leaves full copies of winre.wim under backups\, the largest files
        // the app writes. clean-data never looked there.
        var backups = Directory.CreateDirectory(Path.Combine(_dir, "backups")).FullName;
        var oldest = Path.Combine(backups, "winre.wim.20260101-000000.bak");
        var middle = Path.Combine(backups, "winre.wim.20260201-000000.bak");
        var newest = Path.Combine(backups, "winre.wim.20260301-000000.bak");
        var unrelated = Path.Combine(backups, "notes.txt");
        File.WriteAllText(oldest, "12345");
        File.WriteAllText(middle, "1234567");
        File.WriteAllText(newest, "123");
        File.WriteAllText(unrelated, "keep me");

        var result = CleanDataService.Clean(_config);

        Assert.True(result.Success);
        Assert.False(File.Exists(oldest));
        Assert.False(File.Exists(middle));
        Assert.True(File.Exists(newest));
        Assert.True(File.Exists(unrelated));
        Assert.Equal(2, result.FilesRemoved);
        Assert.Equal(12, result.BytesFreed);
        Assert.Equal([newest], result.Kept);
        // The summary used to say everything was gone while gigabytes stayed behind.
        Assert.Contains("Kept the newest WinRE image backup", result.Summary, StringComparison.Ordinal);
        Assert.Contains(newest, result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Backups_KeepTheNewestOfEachImage()
    {
        var backups = Directory.CreateDirectory(Path.Combine(_dir, "backups")).FullName;
        File.WriteAllText(Path.Combine(backups, "winre.wim.20260101-000000.bak"), "x");
        File.WriteAllText(Path.Combine(backups, "winre.wim.20260301-000000.bak"), "x");
        File.WriteAllText(Path.Combine(backups, "custom.wim.20250101-000000.bak"), "x");

        var result = CleanDataService.Clean(_config, new[] { "backups" });

        Assert.Equal(1, result.FilesRemoved);
        Assert.Equal(
            new[] { "custom.wim.20250101-000000.bak", "winre.wim.20260301-000000.bak" },
            result.Kept.Select(Path.GetFileName).Order());
        Assert.Contains("Kept the newest backup of each WinRE image", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Backups_NotSelected_LeavesWinReImagesAlone()
    {
        var backups = Directory.CreateDirectory(Path.Combine(_dir, "backups")).FullName;
        File.WriteAllText(Path.Combine(backups, "winre.wim.20260101-000000.bak"), "x");
        File.WriteAllText(Path.Combine(backups, "winre.wim.20260301-000000.bak"), "x");

        var result = CleanDataService.Clean(_config, new[] { "logs" });

        Assert.Equal(2, Directory.GetFiles(backups).Length);
        Assert.Empty(result.Kept);
        Assert.DoesNotContain("Kept", result.Summary, StringComparison.Ordinal);
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
    public void IsSafeCleanRoot_AllowsProgramDataLocalAppDataAndPortableStyleDirs()
    {
        var programData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NVMePatcher");
        var localAppData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NVMePatcher");
        Assert.True(CleanDataService.IsSafeCleanRoot(programData, out _));
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
        // The legacy fallback app dir lives under LocalAppData (itself under the user profile)
        // and must still pass for migrated installs.
        var localAppData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NVMePatcher");
        Assert.True(CleanDataService.IsSafeCleanRoot(localAppData, out _));
    }
}
