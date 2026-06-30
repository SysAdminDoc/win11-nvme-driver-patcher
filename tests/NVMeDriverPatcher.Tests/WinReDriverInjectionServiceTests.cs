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
        Assert.Contains(plan.Warnings, w => w.Contains("Driver INF not found", StringComparison.OrdinalIgnoreCase));
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
