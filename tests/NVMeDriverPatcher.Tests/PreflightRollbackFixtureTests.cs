using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class PreflightRollbackFixtureTests
{
    [Fact]
    public void ServiceFixture_IntelRstAndVmd_AreCriticalPreflightBlockers()
    {
        var findings = DriveService.DetectServiceIncompatibilities(["iaStorAC", "vmd"]);

        Assert.Contains(findings, f => f.Name == "Intel RST" && f.Severity == "Critical");
        Assert.Contains(findings, f => f.Name == "Intel VMD" && f.Severity == "Critical");

        var check = PreflightService.ClassifyCompatibility(findings);

        Assert.Equal(CheckStatus.Fail, check.Status);
        Assert.True(check.Critical);
        Assert.Contains("Intel RST", check.Message);
        Assert.Contains("Intel VMD", check.Message);
    }

    [Fact]
    public void ServiceFixture_BackupProducts_GetIndependentBlocklistNote()
    {
        var findings = DriveService.DetectServiceIncompatibilities(
            ["ReflectService", "UrBackupClientBackend", "NinjaRMMAgent"]);

        Assert.Contains(findings, f => f.Name == "Macrium Reflect");
        Assert.Contains(findings, f => f.Name == "UrBackup");
        Assert.Contains(findings, f => f.Name == "NinjaOne");
        Assert.Contains(findings, f => f.Name == "Backup software note" && f.Message.Contains("Unrelated to this patch"));

        var check = PreflightService.ClassifyCompatibility(findings);

        Assert.Equal(CheckStatus.Warning, check.Status);
        Assert.False(check.Critical);
    }

    [Fact]
    public void RollbackFixture_IncompleteRollbackRequiresManualRecoveryWarning()
    {
        var result = new PatchOperationResult
        {
            WasRolledBack = true,
            RollbackFullyReversed = false
        };

        Assert.True(PatchService.RequiresManualRecoveryWarning(result));
        result.RollbackFullyReversed = true;
        Assert.False(PatchService.RequiresManualRecoveryWarning(result));
    }
}
