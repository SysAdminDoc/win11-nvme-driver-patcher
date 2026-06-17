using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class PatchServiceTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(1116, true)]
    [InlineData(5, false)]
    [InlineData(1, false)]
    public void IsCancelRestartSuccessExitCode_OnlyAcceptsCanceledOrNoShutdownInProgress(int exitCode, bool expected)
    {
        Assert.Equal(expected, PatchService.IsCancelRestartSuccessExitCode(exitCode));
    }

    [Theory]
    [InlineData("C:", "C:")]
    [InlineData("c:", "C:")]
    [InlineData("D:\\", "D:")]
    [InlineData(null, "C:")]
    [InlineData("", "C:")]
    [InlineData("not-a-drive", "C:")]
    public void NormalizeSystemDrive_ReturnsManageBdeCompatibleDriveName(string? raw, string expected)
    {
        Assert.Equal(expected, PatchService.NormalizeSystemDrive(raw));
    }

    [Fact]
    public void ReportProgress_SwallowsCallbackFailures()
    {
        PatchService.ReportProgress((_, _) => throw new InvalidOperationException("dispatcher closed"), 10, "working");
    }

    [Fact]
    public void SanitizeRestorePointDescription_EscapesPowerShellStringContent()
    {
        var sanitized = PatchService.SanitizeRestorePointDescription("pre'patch\r\nnext");

        Assert.Equal("pre''patch  next", sanitized);
    }

    [Fact]
    public void SanitizeRestorePointDescription_CapsLongDescriptions()
    {
        var sanitized = PatchService.SanitizeRestorePointDescription(new string('x', 250));

        Assert.Equal(200, sanitized.Length);
    }

    [Fact]
    public void CreateRestorePointStartInfo_UsesTokenizedPowerShellArguments()
    {
        var psi = PatchService.CreateRestorePointStartInfo("pre'patch");

        Assert.Equal("powershell.exe", psi.FileName);
        Assert.False(psi.UseShellExecute);
        Assert.Equal(string.Empty, psi.Arguments);
        Assert.Equal(
            new[]
            {
                "-NoProfile",
                "-NonInteractive",
                "-Command",
                "Checkpoint-Computer -Description 'pre''patch' -RestorePointType 'MODIFY_SETTINGS' -ErrorAction Stop"
            },
            psi.ArgumentList.ToArray());
    }

    // ========================================================================
    // ClassifyPreRegistryAbort — full truth table
    // ========================================================================

    [Fact]
    public void ClassifyPreRegistryAbort_VeraCrypt_AlwaysBlocks()
    {
        Assert.Equal(PatchPreRegistryAbortReason.VeraCryptSystemEncryption,
            PatchService.ClassifyPreRegistryAbort(veraCryptDetected: true, bitLockerEnabled: false, bitLockerSuspended: false));
    }

    [Fact]
    public void ClassifyPreRegistryAbort_VeraCrypt_TakesPrecedenceOverBitLocker()
    {
        Assert.Equal(PatchPreRegistryAbortReason.VeraCryptSystemEncryption,
            PatchService.ClassifyPreRegistryAbort(veraCryptDetected: true, bitLockerEnabled: true, bitLockerSuspended: false));
    }

    [Fact]
    public void ClassifyPreRegistryAbort_BitLockerNotSuspended_Aborts()
    {
        Assert.Equal(PatchPreRegistryAbortReason.BitLockerSuspensionFailed,
            PatchService.ClassifyPreRegistryAbort(veraCryptDetected: false, bitLockerEnabled: true, bitLockerSuspended: false));
    }

    [Fact]
    public void ClassifyPreRegistryAbort_BitLockerSuspended_NoAbort()
    {
        Assert.Equal(PatchPreRegistryAbortReason.None,
            PatchService.ClassifyPreRegistryAbort(veraCryptDetected: false, bitLockerEnabled: true, bitLockerSuspended: true));
    }

    [Fact]
    public void ClassifyPreRegistryAbort_NoBitLockerNoVeraCrypt_NoAbort()
    {
        Assert.Equal(PatchPreRegistryAbortReason.None,
            PatchService.ClassifyPreRegistryAbort(veraCryptDetected: false, bitLockerEnabled: false, bitLockerSuspended: false));
    }

    // ========================================================================
    // RequiresManualRecoveryWarning
    // ========================================================================

    [Fact]
    public void RequiresManualRecoveryWarning_TrueWhenRollbackIncomplete()
    {
        var result = new PatchOperationResult { WasRolledBack = true, RollbackFullyReversed = false };
        Assert.True(PatchService.RequiresManualRecoveryWarning(result));
    }

    [Fact]
    public void RequiresManualRecoveryWarning_FalseWhenRollbackSucceeded()
    {
        var result = new PatchOperationResult { WasRolledBack = true, RollbackFullyReversed = true };
        Assert.False(PatchService.RequiresManualRecoveryWarning(result));
    }

    [Fact]
    public void RequiresManualRecoveryWarning_FalseWhenNoRollback()
    {
        var result = new PatchOperationResult { WasRolledBack = false, RollbackFullyReversed = false };
        Assert.False(PatchService.RequiresManualRecoveryWarning(result));
    }

    // ========================================================================
    // Profile-driven key sets (Safe vs Full, with/without Server key)
    // ========================================================================

    [Fact]
    public void SafeProfile_OnlyIncludesPrimaryFeatureId()
    {
        var ids = AppConfig.GetFeatureIDsForProfile(PatchProfile.Safe);
        Assert.Single(ids);
        Assert.Equal(AppConfig.PrimaryFeatureID, ids[0]);
    }

    [Fact]
    public void FullProfile_IncludesAllFeatureIds()
    {
        var ids = AppConfig.GetFeatureIDsForProfile(PatchProfile.Full);
        Assert.Equal(AppConfig.FeatureIDs.Count, ids.Count);
        Assert.Contains("735209102", ids);
        Assert.Contains("1853569164", ids);
        Assert.Contains("156965516", ids);
    }

    [Theory]
    [InlineData(PatchProfile.Safe, false, 3)]  // 1 feature + 2 safeboot
    [InlineData(PatchProfile.Safe, true, 4)]   // 1 feature + server + 2 safeboot
    [InlineData(PatchProfile.Full, false, 5)]  // 3 features + 2 safeboot
    [InlineData(PatchProfile.Full, true, 6)]   // 3 features + server + 2 safeboot
    public void GetTotalComponents_MatchesProfileAndServerKeyCombination(PatchProfile profile, bool server, int expected)
    {
        Assert.Equal(expected, AppConfig.GetTotalComponents(profile, server));
    }

    // ========================================================================
    // PatchOperationResult defaults
    // ========================================================================

    [Fact]
    public void PatchOperationResult_DefaultsToNotSucceeded()
    {
        var result = new PatchOperationResult();
        Assert.False(result.Success);
        Assert.False(result.NeedsRestart);
        Assert.False(result.WasRolledBack);
        Assert.True(result.RollbackFullyReversed);
        Assert.Equal(0, result.AppliedCount);
        Assert.Null(result.FeatureStoreResetSummary);
    }
}
