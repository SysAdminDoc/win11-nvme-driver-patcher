using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

[Collection("Recovery safety gate")]
public sealed class RecoverySafetyGateServiceTests : IDisposable
{
    public RecoverySafetyGateServiceTests() => RecoverySafetyGateService.Reset();

    public void Dispose() => RecoverySafetyGateService.Reset();

    [Fact]
    public void ReportFailure_KeepsExactRecoveryGuidanceForProcessLifetime()
    {
        RecoverySafetyGateService.ReportFailure(
            "Interrupted mutation ledger",
            "SafeBoot Network could not be restored.");

        var state = RecoverySafetyGateService.Snapshot();

        Assert.False(state.MutationAllowed);
        Assert.Single(state.Failures);
        Assert.Contains("SafeBoot Network could not be restored", state.Summary, StringComparison.Ordinal);
        Assert.Contains("Remove the patch or export diagnostics/recovery material", state.Summary, StringComparison.Ordinal);

        RecoverySafetyGateService.ReportFailure(
            "Watchdog auto-revert",
            "Removal left registry residue.");
        Assert.Equal(2, RecoverySafetyGateService.Snapshot().Failures.Count);
    }

    [Fact]
    public async Task UnresolvedRecovery_BlocksEveryCoreMutationSurface()
    {
        RecoverySafetyGateService.ReportFailure("Test recovery", "exact recovery failed");

        var patchLog = new List<string>();
        var patch = PatchService.Install(
            new AppConfig { WorkingDir = Path.GetTempPath() },
            nativeStatus: null,
            bypassStatus: null,
            patchLog.Add);
        Assert.False(patch.Success);
        Assert.Contains(patchLog, line => line.Contains("unresolved startup recovery", StringComparison.OrdinalIgnoreCase));

        var fallback = await FallbackApplyService.ApplyAsync(Path.GetTempPath());
        Assert.False(fallback.Success);
        Assert.Contains("unresolved startup recovery", fallback.Message, StringComparison.OrdinalIgnoreCase);

        var safeBoot = SafeBootUpgradeService.UpgradeEntries();
        Assert.False(safeBoot.Success);
        Assert.Contains("unresolved startup recovery", safeBoot.Message, StringComparison.OrdinalIgnoreCase);

        var hotSwap = await HotSwapService.SwapAsync(drive: null);
        Assert.Equal(HotSwapOutcome.Blocked, hotSwap.Outcome);
        Assert.Contains("unresolved startup recovery", hotSwap.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Reset_ReopensGateOnlyForNewStartupBoundary()
    {
        RecoverySafetyGateService.ReportFailure("Recovery", "failed");
        Assert.False(RecoverySafetyGateService.Snapshot().MutationAllowed);

        RecoverySafetyGateService.Reset();

        Assert.True(RecoverySafetyGateService.Snapshot().MutationAllowed);
    }

    [Fact]
    public void StartupOutcomeTransitions_AreSharedByGuiAndCliCallers()
    {
        var cleanLedger = RecoverySafetyGateService.ObserveInterruptedRecovery(
            new InterruptedMutationRecoveryResult(
                InterruptedMutationAction.None,
                Success: true,
                "No interrupted mutation requires recovery."));
        Assert.True(cleanLedger.MutationAllowed);

        var failedLedger = RecoverySafetyGateService.ObserveInterruptedRecovery(
            new InterruptedMutationRecoveryResult(
                InterruptedMutationAction.RestoreOriginalState,
                Success: false,
                "SafeBoot rollback remained incomplete."));
        Assert.False(failedLedger.MutationAllowed);
        Assert.Contains("SafeBoot rollback remained incomplete", failedLedger.Summary, StringComparison.Ordinal);

        RecoverySafetyGateService.Reset();
        var cleanAutoRevert = RecoverySafetyGateService.ObserveAutoRevert(new AutoRevertOutcome
        {
            Executed = true,
            Success = true,
            Summary = "Auto-revert completed."
        });
        Assert.True(cleanAutoRevert.MutationAllowed);

        var failedAutoRevert = RecoverySafetyGateService.ObserveAutoRevert(new AutoRevertOutcome
        {
            Executed = true,
            Success = false,
            Failed = true,
            Summary = "Removal left registry residue."
        });
        Assert.False(failedAutoRevert.MutationAllowed);
        Assert.Contains("Removal left registry residue", failedAutoRevert.Summary, StringComparison.Ordinal);
    }
}

[CollectionDefinition("Recovery safety gate", DisableParallelization = true)]
public sealed class RecoverySafetyGateCollection;
