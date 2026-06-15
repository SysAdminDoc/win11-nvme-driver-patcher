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
}
