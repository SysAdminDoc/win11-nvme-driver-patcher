using Microsoft.Win32;
using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class PatchServiceTests
{
    [Theory]
    [InlineData(false, false, 0, PatchService.RestartInitiation.Failed)]   // shutdown.exe never started
    [InlineData(true, false, 0, PatchService.RestartInitiation.Unconfirmed)] // timed out — likely enqueued, unproven
    [InlineData(true, true, 0, PatchService.RestartInitiation.Scheduled)]   // clean exit 0
    [InlineData(true, true, 1, PatchService.RestartInitiation.Failed)]      // exited non-zero
    public void ClassifyRestart_MapsProcessOutcomeToHonestState(bool started, bool exited, int code, PatchService.RestartInitiation expected)
    {
        Assert.Equal(expected, PatchService.ClassifyRestart(started, exited, code));
    }

    [Fact]
    public void LiveHklmSmoke_ProbeAndOwnershipReadTheRealKeyWithoutThrowing()
    {
        // Live smoke only: proves the read path works on a real HKLM handle (owner lookup, the
        // writable-handle check) without admin and without writing anything. It asserts nothing
        // that depends on whether this machine is patched; the patched and unpatched branches
        // are pinned against HKCU fixtures in RegistryOverrideOwnershipTests.
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

        var report = PatchService.InspectRegistryOverrideOwnership(hklm);
        var residue = PatchService.ProbeRemovalResidue(hklm, workingDir: null, log: null);

        Assert.False(string.IsNullOrWhiteSpace(report.Summary));
        Assert.False(string.IsNullOrWhiteSpace(report.Owner));
        Assert.Equal(
            !report.Readable || (report.RemainingValueNames.Count > 0 && !report.CurrentUserCanWrite),
            report.HasBlockingResidue);
        Assert.All(residue, r => Assert.False(string.IsNullOrWhiteSpace(r)));
    }


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

        // Absolute path to Windows PowerShell 5.1, not a PATH lookup — PatchService runs elevated.
        Assert.Equal(SystemToolPathService.PowerShell, psi.FileName);
        Assert.True(Path.IsPathFullyQualified(psi.FileName));
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
    public void FullProfile_LeavesStandaloneFutureOffUnlessAskedFor()
    {
        // #19: 156965516 makes DISM /ScanHealth report store corruption, so Full alone doesn't write it.
        var ids = AppConfig.GetFeatureIDsForProfile(PatchProfile.Full);
        Assert.Equal(["735209102", "1853569164"], ids);

        var withOptIn = AppConfig.GetFeatureIDsForProfile(PatchProfile.Full, includeStandaloneFuture: true);
        Assert.Equal(AppConfig.FeatureIDs, withOptIn);
        Assert.Contains(AppConfig.StandaloneFutureFeatureID, withOptIn);
    }

    [Fact]
    public void SafeProfile_IgnoresTheStandaloneFutureOptIn()
    {
        Assert.Equal([AppConfig.PrimaryFeatureID], AppConfig.GetFeatureIDsForProfile(PatchProfile.Safe, includeStandaloneFuture: true));
    }

    [Theory]
    [InlineData(PatchProfile.Safe, false, false, 3)]  // 1 feature + 2 safeboot
    [InlineData(PatchProfile.Safe, true, false, 4)]   // 1 feature + server + 2 safeboot
    [InlineData(PatchProfile.Safe, false, true, 3)]   // Safe never writes 156965516
    [InlineData(PatchProfile.Full, false, false, 4)]  // 2 features + 2 safeboot
    [InlineData(PatchProfile.Full, true, false, 5)]   // 2 features + server + 2 safeboot
    [InlineData(PatchProfile.Full, false, true, 5)]   // 3 features + 2 safeboot
    [InlineData(PatchProfile.Full, true, true, 6)]    // 3 features + server + 2 safeboot
    public void GetTotalComponents_MatchesProfileServerKeyAndOptInCombination(
        PatchProfile profile, bool server, bool standaloneFuture, int expected)
    {
        Assert.Equal(expected, AppConfig.GetTotalComponents(profile, server, standaloneFuture));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BuildRequiredRegistryMutations_WritesStandaloneFutureOnlyWhenAskedFor(bool optIn)
    {
        var mutations = PatchService.BuildRequiredRegistryMutations(
            PatchProfile.Full, includeServer: false, mirrorControlSets: null, includeStandaloneFuture: optIn);

        Assert.Contains(mutations, m => m.ValueName == "1853569164");
        Assert.Equal(optIn, mutations.Any(m => m.ValueName == AppConfig.StandaloneFutureFeatureID));
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

    // Overrides keys an earlier Full apply wrote everything into, with a ledger baseline that had
    // them all absent (captured before that first apply).
    private static readonly IReadOnlyList<string> Overrides =
        MutationLedgerService.FeatureOverrideSubKeys(["ControlSet002"]);

    private static List<RegistryValueBaseline> AbsentBaseline() =>
        Overrides.SelectMany(subKey => AppConfig.OwnedOverrideValueNames.Select(id =>
            new RegistryValueBaseline { KeyPath = subKey, ValueName = id, Existed = false })).ToList();

    private static Func<string, string, bool> SetAfterFull(params string[] extraIds)
    {
        var set = new HashSet<string>(["735209102", "1853569164", "156965516", .. extraIds]);
        return (_, id) => set.Contains(id);
    }

    // The two extra flags set, the primary one not: nothing this tool does leaves a machine like that.
    private static bool ExtrasOnly(string _, string id) => id is "1853569164" or "156965516";

    [Theory]
    [InlineData(PatchProfile.Safe, false, new[] { "1853569164", "156965516" })]   // Safe after Full
    [InlineData(PatchProfile.Full, false, new[] { "156965516" })]                 // #19: Full without the opt-in
    [InlineData(PatchProfile.Full, true, new string[0])]
    public void FindUnplannedOverrides_ListsWhatThisApplyDoesNotWrite(PatchProfile profile, bool optIn, string[] expected)
    {
        var planned = PatchService.BuildRequiredRegistryMutations(profile, includeServer: false, ["ControlSet002"], optIn);

        var unplanned = PatchService.FindUnplannedOverrides(Overrides, planned, SetAfterFull(), AbsentBaseline());

        Assert.Equal(
            Overrides.SelectMany(subKey => expected.Select(id => (subKey, id))).OrderBy(x => x.subKey).ThenBy(x => x.id),
            unplanned.Select(u => (u.SubKey, u.ValueName)).OrderBy(x => x.SubKey).ThenBy(x => x.ValueName));
        Assert.All(unplanned, u => Assert.True(u.WrittenByThisTool));
    }

    [Fact]
    public void FindUnplannedOverrides_ServerKeyFromAnEarlierApply_IsUnplannedWhenTheBoxIsOff()
    {
        var planned = PatchService.BuildRequiredRegistryMutations(PatchProfile.Full, includeServer: false, ["ControlSet002"], true);

        var unplanned = PatchService.FindUnplannedOverrides(Overrides, planned, SetAfterFull(AppConfig.ServerFeatureID), AbsentBaseline());

        Assert.Equal(Overrides.Count, unplanned.Count(u => u.ValueName == AppConfig.ServerFeatureID && u.WrittenByThisTool));
    }

    [Fact]
    public void FindUnplannedOverrides_ValueThatPredatesTheFirstApply_BelongsToWhoeverSetIt()
    {
        // A community script wrote 156965516 before this tool's first apply.
        var baseline = AbsentBaseline();
        foreach (var entry in baseline.Where(b => b.ValueName == AppConfig.StandaloneFutureFeatureID))
        {
            entry.Existed = true;
            entry.Kind = 4;
            entry.IntegerData = 1;
        }
        var planned = PatchService.BuildRequiredRegistryMutations(PatchProfile.Full, false, ["ControlSet002"], false);

        var unplanned = PatchService.FindUnplannedOverrides(Overrides, planned, SetAfterFull(), baseline);

        // Full without the opt-in leaves 156965516 unplanned in both keys, and both stay.
        Assert.Equal(Overrides.Count, unplanned.Count);
        Assert.All(unplanned, u =>
        {
            Assert.Equal(AppConfig.StandaloneFutureFeatureID, u.ValueName);
            Assert.False(u.WrittenByThisTool);
            Assert.False(u.BaselineCapturedMidLife);
        });
        // No ledger to reuse and the primary flag isn't set: nobody but a script leaves a machine
        // like that, so the extras are whoever's and stay.
        var fresh = PatchService.FindUnplannedOverrides(Overrides, planned, ExtrasOnly, baseline: null);
        Assert.Equal(Overrides.Count, fresh.Count);
        Assert.All(fresh, u => Assert.False(u.WrittenByThisTool));
        // A ledger with no record of the value can't say whose it is.
        Assert.Empty(PatchService.FindUnplannedOverrides(Overrides, planned, SetAfterFull(), []));
    }

    [Fact]
    public void FindUnplannedOverrides_NoLedgerButThePrimaryFlagIsSet_DecidesLikeTheBaselineApplyWillCapture()
    {
        // A v5.0.0 machine on its first apply with this build: apply captures a baseline in which
        // the primary flag exists, and the mid-life rule then clears the extras. The preview runs
        // this same path with no ledger and must say the same thing.
        var planned = PatchService.BuildRequiredRegistryMutations(PatchProfile.Safe, false, ["ControlSet002"], false);

        var unplanned = PatchService.FindUnplannedOverrides(Overrides, planned, SetAfterFull(), baseline: null);

        Assert.Equal(Overrides.Count * 2, unplanned.Count);
        Assert.All(unplanned, u =>
        {
            Assert.True(u.WrittenByThisTool);
            Assert.True(u.BaselineCapturedMidLife);
        });
    }

    [Fact]
    public void FindUnplannedOverrides_BaselineCapturedOverAnOlderVersionsPatch_CallsItsFlagsThisTools()
    {
        // v5.0.0 Full wrote all three flags without a ledger; the v5.1.0 baseline then recorded
        // them as pre-existing. The primary flag in that baseline gives the older patch away.
        var baseline = AbsentBaseline();
        foreach (var entry in baseline.Where(b => AppConfig.FeatureIDs.Contains(b.ValueName)))
        {
            entry.Existed = true;
            entry.Kind = 4;
            entry.IntegerData = 1;
        }
        var planned = PatchService.BuildRequiredRegistryMutations(PatchProfile.Safe, false, ["ControlSet002"], false);

        var unplanned = PatchService.FindUnplannedOverrides(Overrides, planned, SetAfterFull(), baseline);

        Assert.Equal(Overrides.Count * 2, unplanned.Count);   // 1853569164 and 156965516 in each key
        Assert.All(unplanned, u =>
        {
            Assert.True(u.WrittenByThisTool);
            Assert.True(u.BaselineCapturedMidLife);
        });
    }

    [Fact]
    public void FindUnplannedOverrides_MidLifeRule_NeedsThePrimaryFlagInTheSameKey()
    {
        // Only the live control set's baseline holds the primary flag, so the mirror's extra
        // flag still reads as pre-existing there.
        var baseline = AbsentBaseline();
        var live = Overrides.Single(subKey => subKey.Contains("CurrentControlSet", StringComparison.OrdinalIgnoreCase));
        foreach (var entry in baseline.Where(b => b.ValueName == AppConfig.PrimaryFeatureID && b.KeyPath == live))
            entry.Existed = true;
        foreach (var entry in baseline.Where(b => b.ValueName == AppConfig.StandaloneFutureFeatureID))
            entry.Existed = true;
        var planned = PatchService.BuildRequiredRegistryMutations(PatchProfile.Full, false, ["ControlSet002"], false);

        var unplanned = PatchService.FindUnplannedOverrides(Overrides, planned, SetAfterFull(), baseline);

        Assert.Equal(Overrides.Count, unplanned.Count);
        Assert.True(unplanned.Single(u => u.SubKey == live).WrittenByThisTool);
        Assert.False(unplanned.Single(u => u.SubKey != live).WrittenByThisTool);
    }

    [Fact]
    public void ClearUnplannedOverrides_SaysWhenAnOlderVersionWroteTheValue()
    {
        var deleted = new List<string>();
        var log = new List<string>();

        int cleared = PatchService.ClearUnplannedOverrides(
            [new PatchService.UnplannedOverride(Overrides[0], "156965516", WrittenByThisTool: true, BaselineCapturedMidLife: true)],
            (_, id) => deleted.Add(id),
            log.Add);

        Assert.Equal(1, cleared);
        Assert.Equal(["156965516"], deleted);
        var line = Assert.Single(log);
        Assert.Contains("[CLEARED] 156965516", line, StringComparison.Ordinal);
        Assert.Contains("presumed to be an older version's", line, StringComparison.Ordinal);
        Assert.Contains("Remove clears it too", line, StringComparison.Ordinal);
    }

    [Fact]
    public void ClearLeftoverOverrides_RegistryReadFailure_IsAWarningNotARollback()
    {
        var log = new List<string>();
        var planned = PatchService.BuildRequiredRegistryMutations(PatchProfile.Safe, false, ["ControlSet002"], false);

        int cleared = PatchService.ClearLeftoverOverrides(
            Overrides, planned, AbsentBaseline(),
            (_, _) => throw new UnauthorizedAccessException("no handle"),
            (_, _) => throw new InvalidOperationException("must not be reached"),
            log.Add);

        Assert.Equal(0, cleared);
        var warning = Assert.Single(log);
        Assert.Contains("[WARNING] Couldn't check for leftover overrides", warning, StringComparison.Ordinal);
        Assert.Contains("no handle", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void ClearUnplannedOverrides_DeletesOnlyThisToolsValuesAndKeepsGoingPastAFailure()
    {
        var unplanned = new List<PatchService.UnplannedOverride>
        {
            new(Overrides[0], "156965516", WrittenByThisTool: true),
            new(Overrides[1], "156965516", WrittenByThisTool: true),
            new(Overrides[0], "1853569164", WrittenByThisTool: true),
            new(Overrides[1], "1853569164", WrittenByThisTool: false)
        };
        var deleted = new List<(string, string)>();
        var log = new List<string>();

        int cleared = PatchService.ClearUnplannedOverrides(
            unplanned,
            (subKey, id) =>
            {
                if (subKey == Overrides[1]) throw new UnauthorizedAccessException("Access denied");
                deleted.Add((subKey, id));
            },
            log.Add);

        Assert.Equal(2, cleared);
        Assert.Equal([(Overrides[0], "156965516"), (Overrides[0], "1853569164")], deleted);
        Assert.Equal(2, log.Count(line => line.Contains("[CLEARED]", StringComparison.Ordinal)));
        var warning = Assert.Single(log, line => line.Contains("[WARNING]", StringComparison.Ordinal));
        Assert.Contains(Overrides[1], warning, StringComparison.Ordinal);
        Assert.Contains("Remove the patch to clear it", warning, StringComparison.Ordinal);
        var kept = Assert.Single(log, line => line.Contains("[KEPT]", StringComparison.Ordinal));
        Assert.Contains("before this tool's first apply", kept, StringComparison.Ordinal);
    }

    [Fact]
    public void ClearUnplannedOverrides_NothingUnplanned_TouchesNothing()
    {
        var log = new List<string>();
        int cleared = PatchService.ClearUnplannedOverrides(
            [],
            (_, _) => throw new InvalidOperationException("delete must not run"),
            log.Add);

        Assert.Equal(0, cleared);
        Assert.Empty(log);
    }
}
