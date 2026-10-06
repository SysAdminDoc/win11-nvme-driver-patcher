using System.Text;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class WinReDriverInjectionServiceTests
{
    [Fact]
    public void BuildPlan_ProducesMountAddDriverCommitInOrder()
    {
        var plan = WinReDriverInjectionService.BuildPlan(
            @"C:\Recovery\WindowsRE\winre.wim", @"C:\Temp\mount", @"C:\Windows\INF\stornvme.inf");

        Assert.Equal(3, plan.Steps.Count);
        Assert.Contains("/Mount-Image", plan.Steps[0].CommandLine);
        Assert.Contains("winre.wim", plan.Steps[0].CommandLine);
        Assert.Contains("/Add-Driver", plan.Steps[1].CommandLine);
        Assert.Contains("stornvme.inf", plan.Steps[1].CommandLine);
        Assert.Contains("/Unmount-Image", plan.Steps[2].CommandLine);
        Assert.Contains("/Commit", plan.Steps[2].CommandLine);
    }

    [Fact]
    public void BuildPlan_HealthyInputs_AreExecutableWithBlastRadiusWarning()
    {
        var plan = WinReDriverInjectionService.BuildPlan(
            @"C:\Recovery\WindowsRE\winre.wim", @"C:\Temp\mount", @"C:\Windows\INF\stornvme.inf");

        Assert.True(plan.IsExecutable);
        Assert.Contains(plan.Warnings, w => w.Contains("BLAST RADIUS", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.Warnings, w => w.Contains("boot into WinRE", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildPlan_MissingImageOrDriver_IsNotExecutableAndExplainsWhy()
    {
        var plan = WinReDriverInjectionService.BuildPlan(
            "(unknown)", @"C:\Temp\mount", @"C:\Windows\INF\stornvme.inf",
            imageMissing: true, driverInfMissing: true);

        Assert.False(plan.IsExecutable);
        Assert.Contains(plan.Warnings, w => w.Contains("WinRE image not found", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.Warnings, w => w.Contains("Driver package not found", StringComparison.OrdinalIgnoreCase)
                                            && w.Contains("DriverStore", StringComparison.Ordinal));
    }

    // --- Which stornvme.inf DISM can inject ---

    private static string StagePackage(string repository, string folder, string? driverVer, bool withSys, Encoding? encoding = null)
    {
        var dir = Directory.CreateDirectory(Path.Combine(repository, folder)).FullName;
        var inf = Path.Combine(dir, "stornvme.inf");
        var text = "[Version]\r\nSignature=\"$WINDOWS NT$\"\r\nClass=SCSIAdapter\r\n" +
                   (driverVer is null ? "" : $"DriverVer = 06/21/2006,{driverVer}\r\n") +
                   "\r\n[SourceDisksFiles]\r\nstornvme.sys = 3426\r\n";
        File.WriteAllText(inf, text, encoding ?? new UTF8Encoding(false));
        if (withSys) File.WriteAllBytes(Path.Combine(dir, "stornvme.sys"), new byte[] { 0x4D, 0x5A });
        return inf;
    }

    [Fact]
    public void FindDriverStorePackage_PicksTheNewestFolderThatHoldsTheDriverToo()
    {
        // Seen on the 24H2 rig: DISM refused %WINDIR%\INF\stornvme.inf with 0x80070002 because the
        // .sys isn't beside it, and installed the Driver Store copy without complaint.
        var root = CreateTempDir();
        try
        {
            StagePackage(root, "stornvme.inf_amd64_0000000000000001", "10.0.26100.1", withSys: true);
            var newest = StagePackage(root, "stornvme.inf_amd64_0000000000000002", "10.0.26100.9549", withSys: true, new UnicodeEncoding(false, true));
            StagePackage(root, "stornvme.inf_amd64_0000000000000003", "10.0.26200.1", withSys: false);
            StagePackage(root, "storahci.inf_amd64_0000000000000004", "10.0.26200.1", withSys: true);
            File.WriteAllText(Path.Combine(root, "stornvme.inf"), "a stray copy, not a package");

            Assert.Equal(newest, WinReDriverInjectionService.FindDriverStorePackage(root, "stornvme.inf"));
        }
        finally
        {
            TryDeleteDir(root);
        }
    }

    [Fact]
    public void FindDriverStorePackage_NoVersionLine_StillCountsAsAPackage()
    {
        var root = CreateTempDir();
        try
        {
            var only = StagePackage(root, "stornvme.inf_amd64_0000000000000001", driverVer: null, withSys: true);
            Assert.Equal(only, WinReDriverInjectionService.FindDriverStorePackage(root, "stornvme.inf"));
        }
        finally
        {
            TryDeleteDir(root);
        }
    }

    [Fact]
    public void FindDriverStorePackage_NothingStaged_IsNull()
    {
        var root = CreateTempDir();
        try
        {
            Assert.Null(WinReDriverInjectionService.FindDriverStorePackage(root, "stornvme.inf"));
            Assert.Null(WinReDriverInjectionService.FindDriverStorePackage(Path.Combine(root, "missing"), "stornvme.inf"));
        }
        finally
        {
            TryDeleteDir(root);
        }
    }

    [Fact]
    public void DefaultStornvmeInf_OnThisMachine_IsTheDriverStoreCopyBesideItsDriver()
    {
        // Every Windows 10/11 install stages the inbox stornvme package in the Driver Store.
        var inf = WinReDriverInjectionService.DefaultStornvmeInf();

        Assert.Contains(@"\DriverStore\FileRepository\stornvme.inf_", inf, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(inf), inf);
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(inf)!, "stornvme.sys")));
    }

    [Theory]
    [InlineData("DriverVer = 06/21/2006,10.0.26100.9549", "10.0.26100.9549")]
    [InlineData("DriverVer=06/21/2006,10.0.26100.1 ; trailing comment", "10.0.26100.1")]
    [InlineData("  driverver = 01/02/2024,2.5", "2.5")]
    public void ReadDriverVersion_ParsesTheVersionHalf(string line, string expected)
    {
        var root = CreateTempDir();
        try
        {
            var inf = Path.Combine(root, "x.inf");
            File.WriteAllText(inf, "[Version]\r\n" + line + "\r\n");
            Assert.Equal(Version.Parse(expected), WinReDriverInjectionService.ReadDriverVersion(inf));
        }
        finally
        {
            TryDeleteDir(root);
        }
    }

    [Fact]
    public void ReadDriverVersion_NoLineOrNoFile_IsNull()
    {
        var root = CreateTempDir();
        try
        {
            var inf = Path.Combine(root, "x.inf");
            File.WriteAllText(inf, "[Version]\r\nClass=SCSIAdapter\r\n");
            Assert.Null(WinReDriverInjectionService.ReadDriverVersion(inf));
            Assert.Null(WinReDriverInjectionService.ReadDriverVersion(Path.Combine(root, "missing.inf")));
        }
        finally
        {
            TryDeleteDir(root);
        }
    }

    [Fact]
    public void RenderPlan_IncludesCommandsAndRunnableVerdict()
    {
        var plan = WinReDriverInjectionService.BuildPlan(
            @"C:\Recovery\WindowsRE\winre.wim", @"C:\Temp\mount", @"C:\Windows\INF\stornvme.inf");
        var text = WinReDriverInjectionService.RenderPlan(plan);

        Assert.Contains("PLANNED DISM operations", text);
        Assert.Contains("/Add-Driver", text);
        Assert.Contains("runnable", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("preview only", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RenderPlan_NotExecutable_SaysNotRunnable()
    {
        var plan = WinReDriverInjectionService.BuildPlan("(unknown)", @"C:\m", "", imageMissing: true, driverInfMissing: true);
        var text = WinReDriverInjectionService.RenderPlan(plan);
        Assert.Contains("NOT runnable", text);
    }

    [Fact]
    public async Task ApplyAsync_Success_BacksUpImageRunsDismAndLogsHashes()
    {
        var root = CreateTempDir();
        try
        {
            var image = Path.Combine(root, "winre.wim");
            var inf = Path.Combine(root, "stornvme.inf");
            var mount = Path.Combine(root, "mount");
            File.WriteAllText(image, "fake winre image");
            File.WriteAllText(inf, "fake driver inf");
            var plan = WinReDriverInjectionService.BuildPlan(image, mount, inf);
            var commands = new List<string>();

            Task Runner(string exe, string[] args, int timeoutSeconds, CancellationToken cancellationToken)
            {
                commands.Add(string.Join(" ", args));
                return Task.CompletedTask;
            }

            var result = await WinReDriverInjectionService.ApplyAsync(plan, root, Runner);

            Assert.True(result.Success, result.Summary);
            Assert.True(File.Exists(result.BackupPath));
            Assert.Equal(result.OriginalSha256, result.BackupSha256);
            Assert.False(string.IsNullOrWhiteSpace(result.FinalSha256));
            Assert.Contains(commands, c => c.Contains("/Mount-Image"));
            Assert.Contains(commands, c => c.Contains("/Add-Driver"));
            Assert.Contains(commands, c => c.Contains("/Unmount-Image") && c.Contains("/Commit"));
            Assert.DoesNotContain(commands, c => c.Contains("/Discard"));
            Assert.DoesNotContain(commands, c => c.Contains("/Cleanup-Mountpoints"));
            Assert.False(Directory.Exists(mount));
        }
        finally
        {
            TryDeleteDir(root);
        }
    }

    [Fact]
    public async Task ApplyAsync_BackupOfTheHiddenSystemImage_IsAPlainVisibleFile()
    {
        // The real Winre.wim carries Hidden and System, and File.Copy keeps them on the copy. Seen
        // on the 24H2 rig: every backup in ProgramData was invisible in Explorer, while the
        // clean-data summary told people to delete the kept one by hand.
        var root = CreateTempDir();
        try
        {
            var image = Path.Combine(root, "winre.wim");
            var inf = Path.Combine(root, "stornvme.inf");
            File.WriteAllText(image, "fake winre image");
            File.WriteAllText(inf, "fake driver inf");
            File.SetAttributes(image, FileAttributes.Hidden | FileAttributes.System | FileAttributes.NotContentIndexed);
            var plan = WinReDriverInjectionService.BuildPlan(image, Path.Combine(root, "mount"), inf);

            var result = await WinReDriverInjectionService.ApplyAsync(plan, root,
                (_, _, _, _) => Task.CompletedTask);

            Assert.True(result.Success, result.Summary);
            var attributes = File.GetAttributes(result.BackupPath!);
            Assert.Equal((FileAttributes)0, attributes & (FileAttributes.Hidden | FileAttributes.System));
            Assert.Equal(FileAttributes.Hidden | FileAttributes.System | FileAttributes.NotContentIndexed,
                File.GetAttributes(image) & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.NotContentIndexed));
        }
        finally
        {
            TryDeleteDir(root);
        }
    }

    [Fact]
    public async Task ApplyAsync_AddDriverFailure_DiscardsAndRunsCleanupMountpoints()
    {
        var root = CreateTempDir();
        try
        {
            var image = Path.Combine(root, "winre.wim");
            var inf = Path.Combine(root, "stornvme.inf");
            var mount = Path.Combine(root, "mount");
            File.WriteAllText(image, "fake winre image");
            File.WriteAllText(inf, "fake driver inf");
            var plan = WinReDriverInjectionService.BuildPlan(image, mount, inf);
            var commands = new List<string>();

            Task Runner(string exe, string[] args, int timeoutSeconds, CancellationToken cancellationToken)
            {
                var command = string.Join(" ", args);
                commands.Add(command);
                if (command.Contains("/Add-Driver"))
                    throw new InvalidOperationException("add failed");
                return Task.CompletedTask;
            }

            var result = await WinReDriverInjectionService.ApplyAsync(plan, root, Runner);

            Assert.False(result.Success);
            Assert.Contains("add failed", result.Summary);
            Assert.True(File.Exists(result.BackupPath));
            Assert.Contains(commands, c => c.Contains("/Unmount-Image") && c.Contains("/Discard"));
            Assert.Contains(commands, c => c.Contains("/Cleanup-Mountpoints"));
            Assert.False(Directory.Exists(mount));
        }
        finally
        {
            TryDeleteDir(root);
        }
    }

    [Fact]
    public async Task ApplyAsync_ThirdApply_LeavesTheTwoNewestBackups()
    {
        // Every --apply used to add another full copy of winre.wim (0.5 to 1 GB) with no cap.
        var root = CreateTempDir();
        try
        {
            var image = Path.Combine(root, "winre.wim");
            var inf = Path.Combine(root, "stornvme.inf");
            File.WriteAllText(image, "fake winre image");
            File.WriteAllText(inf, "fake driver inf");
            var backups = Directory.CreateDirectory(Path.Combine(root, "backups")).FullName;
            var first = Path.Combine(backups, "winre.wim.20260101-000000.bak");
            var second = Path.Combine(backups, "winre.wim.20260201-000000.bak");
            var otherImage = Path.Combine(backups, "custom.wim.20250101-000000.bak");
            var notOurs = Path.Combine(backups, "winre.wim.bak");
            foreach (var path in new[] { first, second, otherImage, notOurs })
                File.WriteAllText(path, "older");
            var plan = WinReDriverInjectionService.BuildPlan(image, Path.Combine(root, "mount"), inf);
            var log = new List<string>();

            var result = await WinReDriverInjectionService.ApplyAsync(
                plan, root, (_, _, _, _) => Task.CompletedTask, log.Add);

            Assert.True(result.Success, result.Summary);
            Assert.False(File.Exists(first));
            Assert.True(File.Exists(second));
            Assert.True(File.Exists(result.BackupPath));
            Assert.Equal(2, WinReDriverInjectionService.ListBackups(backups).Count(b => b.ImageName == "winre.wim"));
            // Another image's backup and a file that only looks similar aren't this run's to delete.
            Assert.True(File.Exists(otherImage));
            Assert.True(File.Exists(notOurs));
            var removed = Assert.Single(log, line => line.Contains("Removed older WinRE backup", StringComparison.Ordinal));
            Assert.Contains(first, removed, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDir(root);
        }
    }

    [Fact]
    public async Task ApplyAsync_FailedInjection_StillCapsTheBackups()
    {
        // A scheduled retry of a failing injection mustn't add a copy per attempt either.
        var root = CreateTempDir();
        try
        {
            var image = Path.Combine(root, "winre.wim");
            var inf = Path.Combine(root, "stornvme.inf");
            File.WriteAllText(image, "fake winre image");
            File.WriteAllText(inf, "fake driver inf");
            var backups = Directory.CreateDirectory(Path.Combine(root, "backups")).FullName;
            File.WriteAllText(Path.Combine(backups, "winre.wim.20260101-000000.bak"), "older");
            File.WriteAllText(Path.Combine(backups, "winre.wim.20260201-000000.bak"), "older");
            var plan = WinReDriverInjectionService.BuildPlan(image, Path.Combine(root, "mount"), inf);

            var result = await WinReDriverInjectionService.ApplyAsync(plan, root, (_, args, _, _) =>
                args.Any(a => a.Contains("/Add-Driver", StringComparison.Ordinal))
                    ? throw new InvalidOperationException("add failed")
                    : Task.CompletedTask);

            Assert.False(result.Success);
            Assert.True(File.Exists(result.BackupPath));
            Assert.Equal(2, WinReDriverInjectionService.ListBackups(backups).Count);
        }
        finally
        {
            TryDeleteDir(root);
        }
    }

    [Fact]
    public void PruneBackups_KeepsTheBackupJustMade_EvenWhenTheClockWentBackwards()
    {
        // A dead CMOS battery can stamp the new backup years before the ones already there.
        var root = CreateTempDir();
        try
        {
            var justMade = Path.Combine(root, "winre.wim.20190101-000000.bak");
            var newest = Path.Combine(root, "winre.wim.20260301-000000.bak");
            var middle = Path.Combine(root, "winre.wim.20260201-000000.bak");
            foreach (var path in new[] { justMade, newest, middle })
                File.WriteAllText(path, "x");

            int removed = WinReDriverInjectionService.PruneBackups(root, @"C:\Recovery\WindowsRE\Winre.wim", keep: 2, justMade);

            Assert.Equal(1, removed);
            Assert.True(File.Exists(justMade));
            Assert.True(File.Exists(newest));
            Assert.False(File.Exists(middle));
        }
        finally
        {
            TryDeleteDir(root);
        }
    }

    [Fact]
    public void PruneBackups_FileThatCannotBeDeleted_IsAWarningAndTheRestStillGo()
    {
        var root = CreateTempDir();
        try
        {
            var locked = Path.Combine(root, "winre.wim.20260101-000000.bak");
            var old = Path.Combine(root, "winre.wim.20260102-000000.bak");
            var kept = Path.Combine(root, "winre.wim.20260301-000000.bak");
            foreach (var path in new[] { locked, old, kept })
                File.WriteAllText(path, "x");
            var log = new List<string>();

            int removed;
            using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
                removed = WinReDriverInjectionService.PruneBackups(root, "winre.wim", keep: 1, justMade: null, log.Add);

            Assert.Equal(1, removed);
            Assert.True(File.Exists(locked));
            Assert.False(File.Exists(old));
            Assert.True(File.Exists(kept));
            var warning = Assert.Single(log, line => line.StartsWith("[WARN]", StringComparison.Ordinal));
            Assert.Contains(locked, warning, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDir(root);
        }
    }

    [Theory]
    [InlineData("winre.wim.20261005-203000.bak", true, "winre.wim")]
    [InlineData("Winre.wim.20261005-203000.BAK", true, "Winre.wim")]
    [InlineData("my.custom.image.wim.20261005-203000.bak", true, "my.custom.image.wim")]
    [InlineData("winre.wim.bak", false, "")]                      // no stamp
    [InlineData("winre.wim.20261305-203000.bak", false, "")]      // month 13
    [InlineData("winre.wim.2026100-5203000.bak", false, "")]      // stamp in the wrong shape
    [InlineData("winre.wim.20261005-203000.bak.tmp", false, "")]
    [InlineData(".20261005-203000.bak", false, "")]               // no image name
    [InlineData("20261005-203000.bak", false, "")]
    public void TryParseBackupName_OnlyAcceptsNamesThisToolWrites(string fileName, bool expected, string image)
    {
        Assert.Equal(expected, WinReDriverInjectionService.TryParseBackupName(fileName, out var imageName, out var takenUtc));
        Assert.Equal(image, imageName);
        if (expected)
            Assert.Equal(new DateTime(2026, 10, 5, 20, 30, 0, DateTimeKind.Utc), takenUtc);
    }

    [Fact]
    public void BackupNames_RoundTripThroughTheParser()
    {
        var taken = new DateTimeOffset(2026, 10, 5, 20, 30, 0, TimeSpan.Zero);
        var path = WinReDriverInjectionService.BuildBackupPath(@"C:\data\backups", @"C:\Recovery\WindowsRE\Winre.wim", taken);

        Assert.True(WinReDriverInjectionService.TryParseBackupName(Path.GetFileName(path), out var imageName, out var takenUtc));
        Assert.Equal("Winre.wim", imageName);
        Assert.Equal(taken.UtcDateTime, takenUtc);
        Assert.Equal(DateTimeKind.Utc, takenUtc.Kind);
    }

    [Fact]
    public void ListBackups_MissingDirectory_IsEmpty()
    {
        Assert.Empty(WinReDriverInjectionService.ListBackups(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));
    }

    [Fact]
    public void DiscardUnverifiedBackup_DeletesACopyThatDidNotMatchTheImage()
    {
        var root = CreateTempDir();
        try
        {
            var bad = Path.Combine(root, "winre.wim.20261005-203000.bak");
            File.WriteAllText(bad, "truncated copy");
            var log = new List<string>();

            Assert.True(WinReDriverInjectionService.DiscardUnverifiedBackup(bad, log.Add));

            Assert.False(File.Exists(bad));
            Assert.Contains(bad, Assert.Single(log), StringComparison.Ordinal);
            Assert.False(WinReDriverInjectionService.DiscardUnverifiedBackup(null));
        }
        finally
        {
            TryDeleteDir(root);
        }
    }

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), $"NVMePatcher.WinReInject.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteDir(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch { }
    }
}
