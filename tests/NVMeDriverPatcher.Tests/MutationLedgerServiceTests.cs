using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class MutationLedgerServiceTests
{
    [Theory]
    [InlineData(MutationOperationPhase.Prepared, false, InterruptedMutationAction.RestoreOriginalState)]
    [InlineData(MutationOperationPhase.Prepared, true, InterruptedMutationAction.OperationStillRunning)]
    [InlineData(MutationOperationPhase.Applied, false, InterruptedMutationAction.RestoreOriginalState)]
    [InlineData(MutationOperationPhase.Applied, true, InterruptedMutationAction.OperationStillRunning)]
    [InlineData(MutationOperationPhase.RebootPending, false, InterruptedMutationAction.ResumePostRebootVerification)]
    [InlineData(MutationOperationPhase.Verified, false, InterruptedMutationAction.None)]
    [InlineData(MutationOperationPhase.Reverted, false, InterruptedMutationAction.None)]
    public void ClassifyInterruptedAction_CoversEveryDurablePhase(
        MutationOperationPhase phase,
        bool ownerActive,
        InterruptedMutationAction expected)
    {
        Assert.Equal(expected, MutationLedgerService.ClassifyInterruptedAction(phase, ownerActive));
    }

    [Theory]
    [InlineData(MutationOperationPhase.Prepared, true)]
    [InlineData(MutationOperationPhase.Applied, true)]
    [InlineData(MutationOperationPhase.RebootPending, true)]
    [InlineData(MutationOperationPhase.Verified, true)]
    [InlineData(MutationOperationPhase.Reverted, false)]
    public void ShouldReuseBaseline_PreservesFirstCleanStateUntilRevert(
        MutationOperationPhase phase,
        bool expected)
    {
        var ledger = CreateLedger("baseline", phase);
        Assert.Equal(expected, MutationLedgerService.ShouldReuseBaseline(ledger));
    }

    [Theory]
    [InlineData(MutationOperationPhase.Prepared, InterruptedMutationAction.RestoreOriginalState, 1)]
    [InlineData(MutationOperationPhase.Applied, InterruptedMutationAction.RestoreOriginalState, 1)]
    [InlineData(MutationOperationPhase.RebootPending, InterruptedMutationAction.ResumePostRebootVerification, 0)]
    [InlineData(MutationOperationPhase.Verified, InterruptedMutationAction.None, 0)]
    [InlineData(MutationOperationPhase.Reverted, InterruptedMutationAction.None, 0)]
    public void InjectedTermination_AtEveryPhase_IsRecoverableAndIdempotent(
        MutationOperationPhase phase,
        InterruptedMutationAction expectedAction,
        int expectedRestoreCalls)
    {
        var dir = TempDir();
        try
        {
            var ledger = CreateLedger("terminated", phase);
            Assert.True(MutationLedgerService.SaveForTest(dir, ledger, null, out var saveError), saveError);
            int restoreCalls = 0;
            long simulatedRegistryValue = 1;

            var first = MutationLedgerService.RecoverInterrupted(
                dir,
                durable =>
                {
                    restoreCalls++;
                    simulatedRegistryValue = durable.Baseline.RegistryValues.Single().IntegerData!.Value;
                    return MutationRestoreResult.Succeeded;
                },
                _ => false);

            Assert.True(first.Success);
            Assert.Equal(expectedAction, first.Action);
            Assert.Equal(expectedRestoreCalls, restoreCalls);
            Assert.Equal(expectedRestoreCalls == 1 ? 7 : 1, simulatedRegistryValue);

            var second = MutationLedgerService.RecoverInterrupted(
                dir,
                _ =>
                {
                    restoreCalls++;
                    return MutationRestoreResult.Succeeded;
                },
                _ => false);

            Assert.True(second.Success);
            Assert.Equal(expectedRestoreCalls, restoreCalls);
            if (phase is MutationOperationPhase.Prepared or MutationOperationPhase.Applied)
            {
                Assert.Equal(InterruptedMutationAction.None, second.Action);
                Assert.Equal(MutationOperationPhase.Reverted, MutationLedgerService.Load(dir)!.Phase);
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void AtomicPublish_FaultBeforeReplace_PreservesPreviousLedger()
    {
        var dir = TempDir();
        try
        {
            var original = CreateLedger("original", MutationOperationPhase.Prepared);
            Assert.True(MutationLedgerService.SaveForTest(dir, original, null, out var firstError), firstError);

            var replacement = CreateLedger("replacement", MutationOperationPhase.Applied);
            Assert.False(MutationLedgerService.SaveForTest(
                dir,
                replacement,
                _ => throw new IOException("injected termination before atomic replace"),
                out var error));
            Assert.Contains("injected termination", error, StringComparison.OrdinalIgnoreCase);

            var loaded = MutationLedgerService.Load(dir);
            Assert.NotNull(loaded);
            Assert.Equal("original", loaded!.OperationId);
            Assert.Equal(MutationOperationPhase.Prepared, loaded.Phase);
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void RegistryValueComparison_DetectsOverwrittenPreexistingValue()
    {
        var original = new RegistryValueBaseline
        {
            KeyPath = "k",
            ValueName = "v",
            Existed = true,
            Kind = (int)Microsoft.Win32.RegistryValueKind.DWord,
            IntegerData = 7
        };
        var overwritten = new RegistryValueBaseline
        {
            KeyPath = "k",
            ValueName = "v",
            Existed = true,
            Kind = (int)Microsoft.Win32.RegistryValueKind.DWord,
            IntegerData = 1
        };

        Assert.True(MutationLedgerService.RegistryValuesEqual(original, original));
        Assert.False(MutationLedgerService.RegistryValuesEqual(original, overwritten));
    }

    [Fact]
    public void PhaseTransition_RefusesToMoveBackward()
    {
        var dir = TempDir();
        try
        {
            var ledger = CreateLedger("phase", MutationOperationPhase.RebootPending);
            Assert.True(MutationLedgerService.SaveForTest(dir, ledger, null, out var error), error);

            Assert.False(MutationLedgerService.MarkApplied(dir, ledger.OperationId));
            Assert.Equal(MutationOperationPhase.RebootPending, MutationLedgerService.Load(dir)!.Phase);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void CorruptLedger_FailsClosedWithoutInvokingRestore()
    {
        var dir = TempDir();
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(MutationLedgerService.LedgerPath(dir), "{ truncated");
            int restoreCalls = 0;

            var result = MutationLedgerService.RecoverInterrupted(
                dir,
                _ =>
                {
                    restoreCalls++;
                    return MutationRestoreResult.Succeeded;
                },
                _ => false);

            Assert.False(result.Success);
            Assert.Equal(InterruptedMutationAction.RestoreOriginalState, result.Action);
            Assert.Equal(0, restoreCalls);
            Assert.Contains("unreadable", result.Summary, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("{ truncated", File.ReadAllText(MutationLedgerService.LedgerPath(dir)));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void CorruptPrimary_LoadsValidatedAtomicReplacementBackup()
    {
        var dir = TempDir();
        try
        {
            var original = CreateLedger("original", MutationOperationPhase.Applied);
            var replacement = CreateLedger("replacement", MutationOperationPhase.RebootPending);
            Assert.True(MutationLedgerService.SaveForTest(dir, original, null, out var firstError), firstError);
            Assert.True(MutationLedgerService.SaveForTest(dir, replacement, null, out var secondError), secondError);
            File.WriteAllText(MutationLedgerService.LedgerPath(dir), "{ corrupted primary");

            var recovered = MutationLedgerService.Load(dir);

            Assert.NotNull(recovered);
            Assert.Equal("original", recovered!.OperationId);
            Assert.Equal(MutationOperationPhase.Applied, recovered.Phase);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private static MutationOperationLedger CreateLedger(string operationId, MutationOperationPhase phase) => new()
    {
        OperationId = operationId,
        Kind = MutationOperationKind.RegistryPatch,
        Phase = phase,
        CreatedUtc = "2026-07-14T00:00:00.0000000Z",
        UpdatedUtc = "2026-07-14T00:00:00.0000000Z",
        Baseline = new MutationBaseline
        {
            SafeBoot = new SafeBootJournal { CapturedUtc = "2026-07-14T00:00:00.0000000Z" },
            RegistryValues =
            [
                new RegistryValueBaseline
                {
                    KeyPath = "test",
                    ValueName = "value",
                    Existed = true,
                    Kind = (int)Microsoft.Win32.RegistryValueKind.DWord,
                    IntegerData = 7
                }
            ]
        }
    };

    private static string TempDir() =>
        Path.Combine(Path.GetTempPath(), "NVMeDriverPatcher.Ledger." + Guid.NewGuid().ToString("N"));
}
