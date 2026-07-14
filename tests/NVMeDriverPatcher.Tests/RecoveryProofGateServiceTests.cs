using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class RecoveryProofGateServiceTests
{
    [Fact]
    public void Evaluate_ReturnsExpectedItemCount()
    {
        var config = new AppConfig();
        var report = RecoveryProofGateService.Evaluate(config);
        Assert.Equal(5, report.TotalCount);
    }

    [Fact]
    public void Evaluate_AllItemsHaveLabelsAndDetails()
    {
        var config = new AppConfig();
        var report = RecoveryProofGateService.Evaluate(config);
        foreach (var item in report.Items)
        {
            Assert.False(string.IsNullOrWhiteSpace(item.Label), "item label missing");
            Assert.False(string.IsNullOrWhiteSpace(item.Detail), "item detail missing");
        }
    }

    [Fact]
    public void Evaluate_MissingRecoveryKit_FailsRecoveryKitCheck()
    {
        var config = new AppConfig { LastRecoveryKitPath = null };
        var report = RecoveryProofGateService.Evaluate(config);
        var kitItem = report.Items.First(i => i.Label == "Recovery kit");
        Assert.False(kitItem.Passed);
        Assert.Contains("No recovery kit", kitItem.Detail);
    }

    [Fact]
    public void Evaluate_NonexistentKitPath_FailsRecoveryKitCheck()
    {
        var config = new AppConfig { LastRecoveryKitPath = @"C:\NONEXISTENT_PATH_99999" };
        var report = RecoveryProofGateService.Evaluate(config);
        var kitItem = report.Items.First(i => i.Label == "Recovery kit");
        Assert.False(kitItem.Passed);
    }

    [Fact]
    public void Summary_ContainsPassedAndTotal()
    {
        var config = new AppConfig();
        var report = RecoveryProofGateService.Evaluate(config);
        Assert.Contains($"{report.PassedCount}/{report.TotalCount}", report.Summary);
    }

    [Fact]
    public void AllPassed_IsFalse_WhenAnyItemFails()
    {
        var report = new RecoveryProofReport();
        report.Items.Add(new() { Label = "A", Passed = true, Detail = "ok" });
        report.Items.Add(new() { Label = "B", Passed = false, Detail = "fail" });
        Assert.False(report.AllPassed);
        Assert.Equal(1, report.PassedCount);
    }

    [Fact]
    public void AllPassed_IsTrue_WhenAllItemsPass()
    {
        var report = new RecoveryProofReport();
        report.Items.Add(new() { Label = "A", Passed = true, Detail = "ok" });
        report.Items.Add(new() { Label = "B", Passed = true, Detail = "ok" });
        Assert.True(report.AllPassed);
    }

    [Fact]
    public void Summary_WhenAllPassed_DoesNotContainNotReady()
    {
        var report = new RecoveryProofReport();
        report.Items.Add(new() { Label = "A", Passed = true, Detail = "ok" });
        Assert.DoesNotContain("not ready", report.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Summary_WhenFailing_ListsFailingLabels()
    {
        var report = new RecoveryProofReport();
        report.Items.Add(new() { Label = "Recovery kit", Passed = false, Detail = "missing" });
        report.Items.Add(new() { Label = "SafeBoot", Passed = true, Detail = "ok" });
        Assert.Contains("Recovery kit", report.Summary);
    }

    [Fact]
    public void EvaluateSafeBootEntries_DoesNotThrow()
    {
        var item = RecoveryProofGateService.EvaluateSafeBootEntries();
        Assert.NotNull(item);
        Assert.Equal("SafeBoot entries", item.Label);
        Assert.False(string.IsNullOrWhiteSpace(item.Detail));
    }

    [Fact]
    public void EvaluateBitLockerRecovery_UsesAuthoritativeProofVerdict()
    {
        var blockedProof = new BitLockerRecoveryProof(
            new BitLockerVolumeEvidence
            {
                ProbeSucceeded = true,
                SystemVolumePresent = true,
                MountPoint = "C:",
                ConversionStatus = 1,
                ProtectionStatus = 1,
                RecoveryProtectorIds = []
            },
            new DirectoryJoinEvidence(true, DirectoryJoinKind.None));

        var item = RecoveryProofGateService.EvaluateBitLockerRecovery(blockedProof);

        Assert.False(item.Passed);
        Assert.Equal("BitLocker recovery", item.Label);
        Assert.Contains("no numerical-password", item.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EvaluateWinReInjectionPlan_PassesOnlyExecutablePlan()
    {
        var ready = WinReDriverInjectionService.BuildPlan(
            @"C:\Recovery\WindowsRE\winre.wim",
            @"C:\ProgramData\NVMePatcher\WinREMount",
            @"C:\Windows\INF\stornvme.inf");
        var blocked = WinReDriverInjectionService.BuildPlan(
            "(unknown)",
            @"C:\ProgramData\NVMePatcher\WinREMount",
            "",
            imageMissing: true,
            driverInfMissing: true);

        var readyItem = RecoveryProofGateService.EvaluateWinReInjectionPlan(ready);
        var blockedItem = RecoveryProofGateService.EvaluateWinReInjectionPlan(blocked);

        Assert.True(readyItem.Passed);
        Assert.Contains("back up", readyItem.Detail);
        Assert.False(blockedItem.Passed);
        Assert.Contains("not found", blockedItem.Detail, StringComparison.OrdinalIgnoreCase);
    }

    // --- Restore-point capability: protection state, not RPSessionInterval, decides ---

    [Fact]
    public void ClassifyRestoreCapability_GloballyDisabled_Fails()
    {
        // Even with the system drive "protected", a global DisableSR=1 wins.
        var (passed, detail) = RecoveryProofGateService.ClassifyRestoreCapability(
            globallyDisabled: true, systemDriveProtected: true);
        Assert.False(passed);
        Assert.Contains("disabled", detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ClassifyRestoreCapability_SystemDriveUnprotected_Fails()
    {
        // The RPSessionInterval-non-zero-but-protection-off case the old proxy got wrong.
        var (passed, detail) = RecoveryProofGateService.ClassifyRestoreCapability(
            globallyDisabled: false, systemDriveProtected: false);
        Assert.False(passed);
        Assert.Contains("no-op", detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ClassifyRestoreCapability_ProtectedAndEnabled_Passes()
    {
        var (passed, detail) = RecoveryProofGateService.ClassifyRestoreCapability(
            globallyDisabled: false, systemDriveProtected: true);
        Assert.True(passed);
        Assert.Contains("restore point", detail, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(@"\\?\Volume{1a2b3c4d-0000-0000-0000-000000000000}\", "Volume{1a2b3c4d-0000-0000-0000-000000000000}")]
    [InlineData(@"\\?\GLOBALROOT\Device\HarddiskVolume3", null)]
    [InlineData(null, null)]
    public void ExtractVolumeGuid_PullsGuidToken(string? deviceId, string? expected)
    {
        Assert.Equal(expected, RecoveryProofGateService.ExtractVolumeGuid(deviceId));
    }
}
