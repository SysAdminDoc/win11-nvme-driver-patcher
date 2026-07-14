using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

// The full BuildAsync path shells out to ADK copype/MakeWinPEMedia + Dism (integration-only).
// What we pin here is the injection-target correctness: startnet.cmd must land INSIDE the boot
// image at \Windows\System32, not on the media's \sources folder where WinPE never reads it.
public sealed class WinPERecoveryBuilderServiceTests
{
    [Fact]
    public async Task BuildAsync_RequiresGeneratedRecoveryKitBeforeAdkWorkStarts()
    {
        var result = await WinPERecoveryBuilderService.BuildAsync(new WinPEBuildOptions
        {
            OutputDir = Path.GetTempPath(),
            RecoveryKitDir = Path.Combine(Path.GetTempPath(), $"missing-kit-{Guid.NewGuid():N}")
        });

        Assert.False(result.Success);
        Assert.Contains("self-verifying Recovery Kit is required", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void StartnetTargetPath_IsInsideImageWindowsSystem32_NotMediaSources()
    {
        var mount = Path.Combine(Path.GetTempPath(), "fakeMount");
        var target = WinPERecoveryBuilderService.StartnetTargetPath(mount);

        Assert.EndsWith(Path.Combine("Windows", "System32", "startnet.cmd"), target);
        // The old dead path — must NOT be where we write.
        Assert.DoesNotContain(Path.Combine("media", "sources"), target);
    }

    [Fact]
    public void BuildStartnetContent_AnnouncesRecoveryAndRunsWpeinit()
    {
        var content = WinPERecoveryBuilderService.BuildStartnetContent();
        Assert.Contains("wpeinit", content);
        Assert.Contains("Remove_NVMe_Patch.bat", content);
        Assert.Contains("NVMe_Recovery_Kit", content);
        Assert.Contains("Load_Controller_Drivers.bat", content);
    }

    [Fact]
    public void WriteStartnetToMount_WritesIntoMountedImageTree()
    {
        var mount = Path.Combine(Path.GetTempPath(), $"NVMeDriverPatcher.WinPE.Tests.{Guid.NewGuid():N}");
        try
        {
            WinPERecoveryBuilderService.WriteStartnetToMount(mount);

            var expected = Path.Combine(mount, "Windows", "System32", "startnet.cmd");
            Assert.True(File.Exists(expected), $"startnet.cmd should be injected at {expected}");
            // It must NOT be written to the media\sources location.
            Assert.False(File.Exists(Path.Combine(mount, "media", "sources", "startnet.cmd")));

            var written = File.ReadAllText(expected);
            Assert.Contains("wpeinit", written);
        }
        finally
        {
            try { Directory.Delete(mount, recursive: true); } catch { }
        }
    }

    [Fact]
    public void PublishMediaManifest_RecordsBootImageAndEmbeddedRecoveryKitThenVerifies()
    {
        var media = Path.Combine(Path.GetTempPath(), $"NVMeDriverPatcher.WinPE.Media.Tests.{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(media, "sources"));
            Directory.CreateDirectory(Path.Combine(media, "NVMe_Recovery_Kit"));
            File.WriteAllText(Path.Combine(media, "sources", "boot.wim"), "fake-wim");
            File.WriteAllText(Path.Combine(media, "NVMe_Recovery_Kit", "Remove_NVMe_Patch.bat"), "exit /b 0");

            var result = WinPERecoveryBuilderService.PublishMediaManifest(media);

            Assert.True(result.Success, result.Summary);
            Assert.Equal("winpe-recovery-media", result.PayloadType);
            var json = File.ReadAllText(Path.Combine(media, GeneratedArtifactManifestService.ManifestFileName));
            Assert.Contains("\"role\": \"boot-image\"", json, StringComparison.Ordinal);
            Assert.Contains("\"role\": \"embedded-recovery-kit\"", json, StringComparison.Ordinal);

            File.AppendAllText(Path.Combine(media, "sources", "boot.wim"), "tampered");
            var tampered = GeneratedArtifactManifestService.VerifyDirectory(media);
            Assert.False(tampered.Success);
            Assert.Contains(tampered.Issues, i => i.RelativePath == "sources/boot.wim");
        }
        finally
        {
            try { Directory.Delete(media, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ExportControllerPackages_ExportsEachOemInfOnceAndRetainsManualDrvloadCopy()
    {
        var media = Temp("export");
        try
        {
            Directory.CreateDirectory(media);
            var first = Controller("PCI\\ONE", "oem42.inf");
            var second = Controller("PCI\\TWO", "OEM42.INF");
            var inbox = Controller("PCI\\THREE", "stornvme.inf", WinPEControllerCoverage.Inbox);
            var inventory = Inventory(first, second, inbox);
            var calls = new List<string[]>();

            await WinPERecoveryBuilderService.ExportControllerPackagesAsync(
                media,
                inventory,
                (file, args, _, _) =>
                {
                    Assert.Equal("pnputil.exe", file);
                    calls.Add(args);
                    Directory.CreateDirectory(args[2]);
                    File.WriteAllText(Path.Combine(args[2], "iaStorVD.inf"), "[Version]\r\nSignature=\"$Windows NT$\"");
                    return Task.CompletedTask;
                });

            Assert.Single(calls);
            Assert.Equal(["/export-driver", "oem42.inf", calls[0][2]], calls[0]);
            Assert.All(new[] { first, second }, controller =>
            {
                Assert.Equal(WinPEControllerCoverage.PendingInjection, controller.Coverage);
                Assert.EndsWith("/Packages/oem42/iaStorVD.inf", controller.PackageRelativeInfPath,
                    StringComparison.OrdinalIgnoreCase);
                Assert.Matches("^[0-9a-f]{64}$", controller.PackageSha256!);
            });
            Assert.True(File.Exists(Path.Combine(media, WinPEMediaFreshnessService.ControllerDirectoryName,
                "Load_Controller_Drivers.bat")));
        }
        finally { try { Directory.Delete(media, recursive: true); } catch { } }
    }

    [Fact]
    public async Task ExportControllerPackages_FailureMarksEveryDependentControllerMissing()
    {
        var media = Temp("export-fail");
        try
        {
            Directory.CreateDirectory(media);
            var first = Controller("PCI\\ONE", "oem7.inf");
            var second = Controller("PCI\\TWO", "oem7.inf");

            await WinPERecoveryBuilderService.ExportControllerPackagesAsync(
                media,
                Inventory(first, second),
                (_, _, _, _) => throw new InvalidOperationException("PnPUtil access denied"));

            Assert.All(new[] { first, second }, controller =>
            {
                Assert.Equal(WinPEControllerCoverage.Missing, controller.Coverage);
                Assert.Contains("access denied", controller.Detail, StringComparison.OrdinalIgnoreCase);
            });
        }
        finally { try { Directory.Delete(media, recursive: true); } catch { } }
    }

    [Fact]
    public async Task CustomizeBootWim_InjectsWithoutForceUnsignedAndCommitsOnlyCompleteCoverage()
    {
        var tree = CreateFakeTreeWithExportedInf(out var relativeInf);
        try
        {
            var controller = Controller("PCI\\ONE", "oem42.inf");
            controller.PackageRelativeInfPath = relativeInf;
            controller.PackageSha256 = GeneratedArtifactManifestService.ComputeSha256(
                Path.Combine(tree, "media", relativeInf.Replace('/', Path.DirectorySeparatorChar)));
            var calls = new List<string[]>();
            bool startnetPresentAtCommit = false;

            await WinPERecoveryBuilderService.CustomizeBootWimAsync(
                tree,
                Inventory(controller),
                (file, args, _, _) =>
                {
                    Assert.Equal("dism.exe", file);
                    calls.Add(args);
                    if (args.Contains("/Commit"))
                        startnetPresentAtCommit = File.Exists(WinPERecoveryBuilderService.StartnetTargetPath(
                            Path.Combine(tree, "mount")));
                    return Task.CompletedTask;
                });

            Assert.Equal(WinPEControllerCoverage.Injected, controller.Coverage);
            Assert.True(startnetPresentAtCommit);
            Assert.Contains(calls, args => args.Contains("/Add-Driver"));
            Assert.Contains(calls, args => args.Contains("/Commit"));
            Assert.DoesNotContain(calls.SelectMany(args => args), arg =>
                arg.Equals("/ForceUnsigned", StringComparison.OrdinalIgnoreCase));
        }
        finally { try { Directory.Delete(tree, recursive: true); } catch { } }
    }

    [Fact]
    public async Task CustomizeBootWim_DismDriverFailureDiscardsMountAndNeverCommits()
    {
        var tree = CreateFakeTreeWithExportedInf(out var relativeInf);
        try
        {
            var controller = Controller("PCI\\ONE", "oem42.inf");
            controller.PackageRelativeInfPath = relativeInf;
            controller.PackageSha256 = GeneratedArtifactManifestService.ComputeSha256(
                Path.Combine(tree, "media", relativeInf.Replace('/', Path.DirectorySeparatorChar)));
            var calls = new List<string[]>();

            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                WinPERecoveryBuilderService.CustomizeBootWimAsync(
                    tree,
                    Inventory(controller),
                    (_, args, _, _) =>
                    {
                        calls.Add(args);
                        if (args.Contains("/Add-Driver"))
                            throw new InvalidOperationException("signature rejected");
                        return Task.CompletedTask;
                    }));

            Assert.Contains("coverage incomplete", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(WinPEControllerCoverage.Missing, controller.Coverage);
            Assert.Contains(calls, args => args.Contains("/Discard"));
            Assert.DoesNotContain(calls, args => args.Contains("/Commit"));
        }
        finally { try { Directory.Delete(tree, recursive: true); } catch { } }
    }

    [Fact]
    public async Task CustomizeBootWim_RejectsChangedExportBeforeDismInjection()
    {
        var tree = CreateFakeTreeWithExportedInf(out var relativeInf);
        try
        {
            var infPath = Path.Combine(tree, "media", relativeInf.Replace('/', Path.DirectorySeparatorChar));
            var controller = Controller("PCI\\ONE", "oem42.inf");
            controller.PackageRelativeInfPath = relativeInf;
            controller.PackageSha256 = GeneratedArtifactManifestService.ComputeSha256(infPath);
            File.AppendAllText(infPath, "tampered");
            var calls = new List<string[]>();

            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                WinPERecoveryBuilderService.CustomizeBootWimAsync(
                    tree,
                    Inventory(controller),
                    (_, args, _, _) =>
                    {
                        calls.Add(args);
                        return Task.CompletedTask;
                    }));

            Assert.Contains("coverage incomplete", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("hash changed", controller.Detail, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(calls, args => args.Contains("/Add-Driver"));
            Assert.Contains(calls, args => args.Contains("/Discard"));
        }
        finally { try { Directory.Delete(tree, recursive: true); } catch { } }
    }

    [Fact]
    public void ManualDrvloadScript_LoadsEveryRetainedInfAndFailsClosed()
    {
        var script = WinPERecoveryBuilderService.BuildManualDrvloadScript();
        Assert.Contains("for /r \"%~dp0Packages\" %%I in (*.inf)", script, StringComparison.Ordinal);
        Assert.Contains("drvload.exe \"%%I\"", script, StringComparison.Ordinal);
        Assert.Contains("No exported controller INF packages", script, StringComparison.Ordinal);
        Assert.Contains("exit /b 2", script, StringComparison.Ordinal);
    }

    [Fact]
    public void PromoteBuildOutputs_ReplacesTreeAndIsoAsOneLogicalPublication()
    {
        var root = Temp("promote");
        var stagingTree = Path.Combine(root, "tree.tmp");
        var finalTree = Path.Combine(root, "tree");
        var tempIso = Path.Combine(root, "image.tmp.iso");
        var finalIso = Path.Combine(root, "image.iso");
        try
        {
            Directory.CreateDirectory(stagingTree);
            Directory.CreateDirectory(finalTree);
            File.WriteAllText(Path.Combine(stagingTree, "version.txt"), "new");
            File.WriteAllText(Path.Combine(finalTree, "version.txt"), "old");
            File.WriteAllText(tempIso, "new iso");
            File.WriteAllText(finalIso, "old iso");

            WinPERecoveryBuilderService.PromoteBuildOutputs(stagingTree, finalTree, tempIso, finalIso);

            Assert.Equal("new", File.ReadAllText(Path.Combine(finalTree, "version.txt")));
            Assert.Equal("new iso", File.ReadAllText(finalIso));
            Assert.False(Directory.Exists(stagingTree));
            Assert.False(File.Exists(tempIso));
            Assert.Empty(Directory.GetFileSystemEntries(root, "*.bak"));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    public void PromoteBuildOutputs_IsoFailureRestoresPreviousTree()
    {
        var root = Temp("promote-rollback");
        var stagingTree = Path.Combine(root, "tree.tmp");
        var finalTree = Path.Combine(root, "tree");
        var tempIso = Path.Combine(root, "image.tmp.iso");
        var invalidFinalIso = Path.Combine(root, "image.iso");
        try
        {
            Directory.CreateDirectory(stagingTree);
            Directory.CreateDirectory(finalTree);
            Directory.CreateDirectory(invalidFinalIso); // File.Move cannot replace a directory.
            File.WriteAllText(Path.Combine(stagingTree, "version.txt"), "new");
            File.WriteAllText(Path.Combine(finalTree, "version.txt"), "old");
            File.WriteAllText(tempIso, "new iso");

            Assert.ThrowsAny<IOException>(() =>
                WinPERecoveryBuilderService.PromoteBuildOutputs(
                    stagingTree, finalTree, tempIso, invalidFinalIso));

            Assert.Equal("old", File.ReadAllText(Path.Combine(finalTree, "version.txt")));
            Assert.True(File.Exists(tempIso));
            Assert.True(Directory.Exists(invalidFinalIso));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    private static string Temp(string label) => Path.Combine(
        Path.GetTempPath(), $"NVMeDriverPatcher.WinPE.{label}.{Guid.NewGuid():N}");

    private static string CreateFakeTreeWithExportedInf(out string relativeInf)
    {
        var tree = Temp("customize");
        var media = Path.Combine(tree, "media");
        Directory.CreateDirectory(Path.Combine(media, "sources"));
        File.WriteAllText(Path.Combine(media, "sources", "boot.wim"), "fake wim");
        var inf = Path.Combine(media, WinPEMediaFreshnessService.ControllerDirectoryName,
            "Packages", "oem42", "iaStorVD.inf");
        Directory.CreateDirectory(Path.GetDirectoryName(inf)!);
        File.WriteAllText(inf, "[Version]");
        relativeInf = Path.GetRelativePath(media, inf).Replace('\\', '/');
        return tree;
    }

    private static BootStorageController Controller(
        string id,
        string inf,
        WinPEControllerCoverage coverage = WinPEControllerCoverage.PendingInjection) => new()
    {
        InstanceId = id,
        FriendlyName = id,
        DeviceClass = "SCSIAdapter",
        ServiceName = "iaStorVD",
        InfName = inf,
        DriverProvider = "Vendor",
        DriverVersion = "1.0",
        Coverage = coverage
    };

    private static BootStorageControllerInventory Inventory(params BootStorageController[] controllers) => new()
    {
        ProbeSucceeded = true,
        Controllers = controllers.ToList()
    };
}
