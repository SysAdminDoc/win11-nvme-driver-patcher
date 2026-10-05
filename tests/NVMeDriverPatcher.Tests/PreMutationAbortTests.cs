using NVMeDriverPatcher.Services;
using static NVMeDriverPatcher.Services.PatchService;

namespace NVMeDriverPatcher.Tests;

/// <summary>
/// An apply that stops after BitLocker was suspended but before any registry write landed. On a
/// re-apply the ledger reuses the first clean (pre-patch) baseline, so restoring it would silently
/// revert the patch the machine is already running.
/// </summary>
public sealed class PreMutationAbortTests
{
    [Theory]
    [InlineData(false, false, nameof(PreMutationAbortAction.CloseLedger))]
    [InlineData(false, true, nameof(PreMutationAbortAction.CloseLedger))]
    [InlineData(true, false, nameof(PreMutationAbortAction.RestoreBaseline))]
    [InlineData(true, true, nameof(PreMutationAbortAction.ResumeBitLockerAndCloseLedger))]
    public void ClassifyPreMutationAbort_NeverRestoresAReusedBaseline(
        bool bitLockerStateMayHaveChanged,
        bool reusedBaseline,
        string expected)
    {
        Assert.Equal(
            Enum.Parse<PreMutationAbortAction>(expected),
            ClassifyPreMutationAbort(bitLockerStateMayHaveChanged, reusedBaseline));
    }

    [Fact]
    public void ReapplyAbortAfterBitLockerSuspend_ResumesProtectionAndKeepsTheLivePatch()
    {
        bool restored = false, resumed = false, closed = false;
        var log = new List<string>();

        var outcome = CloseApplyBeforeMutation(
            ClassifyPreMutationAbort(bitLockerStateMayHaveChanged: true, reusedBaseline: true),
            () => { restored = true; return MutationRestoreResult.Succeeded; },
            () => { resumed = true; return new BitLockerNativeResult(true, "resumed"); },
            () => { closed = true; return true; },
            log.Add);

        Assert.False(restored);
        Assert.True(resumed);
        Assert.True(closed);
        Assert.True(outcome.Success);
        Assert.Contains(log, line => line.Contains("stays as it is", StringComparison.Ordinal));
    }

    [Fact]
    public void ReapplyAbort_ResumeFailureIsReportedButTheLedgerStillCloses()
    {
        bool restored = false, closed = false;
        var log = new List<string>();

        var outcome = CloseApplyBeforeMutation(
            PreMutationAbortAction.ResumeBitLockerAndCloseLedger,
            () => { restored = true; return MutationRestoreResult.Succeeded; },
            () => new BitLockerNativeResult(false, "WMI unavailable"),
            () => { closed = true; return true; },
            log.Add);

        Assert.False(restored);
        Assert.True(closed);
        Assert.False(outcome.Success);
        Assert.Contains(outcome.Failures, f => f.Contains("WMI unavailable", StringComparison.Ordinal));
        Assert.Contains(log, line => line.Contains("manage-bde -protectors -enable", StringComparison.Ordinal));
    }

    [Fact]
    public void FirstApplyAbortAfterBitLockerSuspend_StillRestoresTheFreshBaseline()
    {
        bool restored = false, resumed = false, closed = false;

        var outcome = CloseApplyBeforeMutation(
            ClassifyPreMutationAbort(bitLockerStateMayHaveChanged: true, reusedBaseline: false),
            () => { restored = true; return MutationRestoreResult.Succeeded; },
            () => { resumed = true; return new BitLockerNativeResult(true, "resumed"); },
            () => { closed = true; return true; },
            log: null);

        Assert.True(restored);
        Assert.False(resumed);
        Assert.False(closed);
        Assert.True(outcome.Success);
    }

    [Fact]
    public void AbortWithoutBitLockerChange_OnlyClosesTheLedger()
    {
        bool restored = false, resumed = false;

        var outcome = CloseApplyBeforeMutation(
            PreMutationAbortAction.CloseLedger,
            () => { restored = true; return MutationRestoreResult.Succeeded; },
            () => { resumed = true; return new BitLockerNativeResult(true, "resumed"); },
            () => false,
            log: null);

        Assert.False(restored);
        Assert.False(resumed);
        Assert.False(outcome.Success);
    }
}
