using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

/// <summary>
/// Issue #15, part 2. This guard re-arms a boot-critical driver with no human present, so the
/// decision table is the safety argument and every gate here is asserted in the refusing
/// direction as well as the permitting one. A test that only proves it re-applies would be
/// covering the cheap half.
/// </summary>
public class PersistenceGuardServiceTests
{
    private const int Max = PersistenceGuardService.DefaultMaxConsecutiveReapplies;

    private static PersistenceGuardDecision Decide(
        bool enabled = true,
        VerificationOutcome outcome = VerificationOutcome.Reverted,
        MutationOperationPhase? phase = MutationOperationPhase.Applied,
        int used = 0,
        int max = Max,
        WatchdogVerdict watchdog = WatchdogVerdict.Healthy,
        bool buildPolicy = true,
        bool recoveryGate = true) =>
        PersistenceGuardService.Decide(enabled, outcome, phase, used, max, watchdog, buildPolicy, recoveryGate);

    [Fact]
    public void TheOneEligibleState_Reapplies()
    {
        // Keys gone (Reverted) + ledger says an apply completed = something other than the user
        // removed the patch. This is the only combination that may mutate.
        Assert.Equal(PersistenceGuardDecision.Reapply, Decide());
    }

    [Fact]
    public void DisabledByDefault_IsTheShippedPosture()
    {
        Assert.False(new AppConfig().PersistenceGuardEnabled);
        Assert.Equal(PersistenceGuardDecision.Disabled, Decide(enabled: false));
    }

    [Theory]
    [InlineData(VerificationOutcome.None)]
    [InlineData(VerificationOutcome.Confirmed)]
    [InlineData(VerificationOutcome.AwaitingRestart)]
    [InlineData(VerificationOutcome.OverrideBlocked)]
    [InlineData(VerificationOutcome.FlagsEnabledNotBound)]
    public void OnlyTheRevertedOutcomeIsEligible(VerificationOutcome outcome)
    {
        // OverrideBlocked in particular must NOT re-apply: the keys are present and Windows is
        // refusing to bind. Re-applying would rewrite keys that are already there, forever.
        Assert.Equal(PersistenceGuardDecision.NothingToDo, Decide(outcome: outcome));
    }

    [Fact]
    public void ADeliberateUninstallIsNeverUndone()
    {
        // Phase.Reverted is the signature of a user-driven removal.
        Assert.Equal(PersistenceGuardDecision.NothingToDo, Decide(phase: MutationOperationPhase.Reverted));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(MutationOperationPhase.Prepared)]
    public void APatchThatNeverCompletedAnApplyIsNotRestored(MutationOperationPhase? phase)
    {
        Assert.Equal(PersistenceGuardDecision.NothingToDo, Decide(phase: phase));
    }

    [Theory]
    [InlineData(MutationOperationPhase.Applied)]
    [InlineData(MutationOperationPhase.RebootPending)]
    [InlineData(MutationOperationPhase.Verified)]
    public void EveryTerminalAppliedPhaseIsEligible(MutationOperationPhase phase)
    {
        Assert.Equal(PersistenceGuardDecision.Reapply, Decide(phase: phase));
    }

    // --- Anti-boot-loop budget ---

    [Fact]
    public void BudgetIsSpentAfterTheConfiguredNumberOfConsecutiveReapplies()
    {
        Assert.Equal(PersistenceGuardDecision.Reapply, Decide(used: Max - 1));
        Assert.Equal(PersistenceGuardDecision.BudgetExhausted, Decide(used: Max));
        Assert.Equal(PersistenceGuardDecision.BudgetExhausted, Decide(used: Max + 5));
    }

    [Fact]
    public void AZeroBudgetRefusesEveryReapply()
    {
        // The knob must be able to express "detect but never act".
        Assert.Equal(PersistenceGuardDecision.BudgetExhausted, Decide(used: 0, max: 0));
    }

    [Fact]
    public void AnExhaustedBudgetOutranksAHealthyLookingMachine()
    {
        // Budget is checked before the health gates on purpose: a flapping machine that looks
        // healthy on this particular boot must not re-enter the loop it just escaped.
        Assert.Equal(
            PersistenceGuardDecision.BudgetExhausted,
            Decide(used: Max, watchdog: WatchdogVerdict.Healthy, buildPolicy: true, recoveryGate: true));
    }

    // --- Health gates, each asserted in the refusing direction ---

    [Fact]
    public void AnUnstableMachineDefersRatherThanReapplying()
    {
        Assert.Equal(PersistenceGuardDecision.DeferredUnstable, Decide(watchdog: WatchdogVerdict.Unstable));
    }

    [Fact]
    public void AnUnprovableWatchdogDefers_AbsenceOfEvidenceIsNotHealth()
    {
        Assert.Equal(PersistenceGuardDecision.DeferredUnstable, Decide(watchdog: WatchdogVerdict.Unavailable));
    }

    [Theory]
    [InlineData(WatchdogVerdict.Healthy)]
    [InlineData(WatchdogVerdict.Idle)]
    [InlineData(WatchdogVerdict.Completed)]
    [InlineData(WatchdogVerdict.Warning)]
    public void NonFatalWatchdogVerdictsStillAllowRestoration(WatchdogVerdict verdict)
    {
        // Warning is deliberately permitted — it is the "surface a notice, don't revert" tier,
        // and refusing here would make the guard useless on any machine with routine disk noise.
        Assert.Equal(PersistenceGuardDecision.Reapply, Decide(watchdog: verdict));
    }

    [Fact]
    public void BuildPolicyVerifyOnlyModeBlocksTheGuard()
    {
        Assert.Equal(PersistenceGuardDecision.DeferredBuildPolicy, Decide(buildPolicy: false));
    }

    [Fact]
    public void TheFailClosedStartupRecoveryLatchBlocksTheGuard()
    {
        Assert.Equal(PersistenceGuardDecision.DeferredRecoveryLatch, Decide(recoveryGate: false));
    }

    [Fact]
    public void GatePrecedence_UnstableOutranksBuildPolicyAndLatch()
    {
        // Reported reason must be the most safety-relevant one, not whichever check ran first.
        Assert.Equal(
            PersistenceGuardDecision.DeferredUnstable,
            Decide(watchdog: WatchdogVerdict.Unstable, buildPolicy: false, recoveryGate: false));
    }

    // --- Operator-facing text ---

    [Fact]
    public void ExhaustedBudgetMessageNamesTheCountAndTheManualEscape()
    {
        var message = PersistenceGuardService.Describe(PersistenceGuardDecision.BudgetExhausted, 2, 2);
        Assert.Contains("2 consecutive", message);
        Assert.Contains("apply it manually", message);
    }

    [Fact]
    public void EveryDecisionHasNonPlaceholderText()
    {
        foreach (PersistenceGuardDecision decision in Enum.GetValues<PersistenceGuardDecision>())
        {
            var message = PersistenceGuardService.Describe(decision, 1, 2);
            Assert.False(string.IsNullOrWhiteSpace(message));
            Assert.DoesNotContain("unknown decision", message, StringComparison.OrdinalIgnoreCase);
        }
    }

    // --- Config surface ---

    [Fact]
    public void MaxReappliesIsClampedSoItCannotBecomeABootLoopGenerator()
    {
        var config = new AppConfig { PersistenceGuardMaxReapplies = 9999 };
        Assert.Equal(10, config.PersistenceGuardMaxReapplies);
        config.PersistenceGuardMaxReapplies = -5;
        Assert.Equal(0, config.PersistenceGuardMaxReapplies);
    }

    // --- Boot-task ordering: the safety argument, not a style preference ---

    [Fact]
    public void GuardRunsStrictlyAfterBothRevertOnlyConsumersOnTheBootTask()
    {
        // AutoRevert and the fallback reset can both REMOVE the patch. If the guard ran before
        // either of them it would re-apply a patch that is about to be reverted, or resurrect one
        // that just was. Source-level because the ordering lives in a command body that needs an
        // elevated process and a real event log to execute.
        var source = ReadCliProgram();
        int start = source.IndexOf("static int WatchdogAutoRevertCommand", StringComparison.Ordinal);
        Assert.True(start >= 0, "WatchdogAutoRevertCommand not found — the boot task moved.");
        int end = source.IndexOf("\n    static int ", start + 10, StringComparison.Ordinal);
        var body = end > start ? source[start..end] : source[start..];

        int autoRevert = body.IndexOf("AutoRevertService.MaybeRun", StringComparison.Ordinal);
        int fallback = body.IndexOf("FallbackRecoveryCoordinator.RunOnce", StringComparison.Ordinal);
        int guard = body.IndexOf("PersistenceGuardService.RunOnce", StringComparison.Ordinal);

        Assert.True(autoRevert >= 0, "boot task no longer runs the watchdog auto-revert");
        Assert.True(fallback >= 0, "boot task no longer runs the fallback recovery coordinator");
        Assert.True(guard >= 0, "boot task no longer runs the persistence guard");
        Assert.True(autoRevert < guard, "persistence guard must run AFTER the watchdog auto-revert");
        Assert.True(fallback < guard, "persistence guard must run AFTER the fallback recovery reset");
    }

    [Fact]
    public void GuardIsNotWiredIntoTheGuiStartupPath()
    {
        // Deliberate: a GUI launch has a human present who can click Apply. Silent re-arming of a
        // boot-critical driver belongs to the unattended boot task only.
        var appStartup = ReadRepoFile("src", "NVMeDriverPatcher", "App.xaml.cs");
        Assert.DoesNotContain("PersistenceGuardService.RunOnce", appStartup, StringComparison.Ordinal);
    }

    private static string ReadCliProgram() => ReadRepoFile("src", "NVMeDriverPatcher.Cli", "Program.cs");

    private static string ReadRepoFile(params string[] relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NVMeDriverPatcher.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var path = Path.Combine(new[] { dir!.FullName }.Concat(relative).ToArray());
        Assert.True(File.Exists(path), $"expected repo file missing: {path}");
        return File.ReadAllText(path);
    }

    // --- CLI argument parsing ---

    [Fact]
    public void NoArgs_IsAReadOnlyQuery()
    {
        var change = PersistenceGuardService.ParseSettingsArgs(["persistence-guard"]);
        Assert.Null(change.Error);
        Assert.False(change.Mutates);
        Assert.Null(change.Enable);
    }

    [Fact]
    public void OnAndOffTogetherIsRejectedRatherThanSilentlyPickingOne()
    {
        var change = PersistenceGuardService.ParseSettingsArgs(["--on", "--off"]);
        Assert.NotNull(change.Error);
        Assert.False(change.Mutates);
    }

    [Theory]
    [InlineData("--on", true)]
    [InlineData("--off", false)]
    [InlineData("--ON", true)]
    public void OnOffSetsTheEnableFlag(string arg, bool expected)
    {
        var change = PersistenceGuardService.ParseSettingsArgs([arg]);
        Assert.Null(change.Error);
        Assert.Equal(expected, change.Enable);
        Assert.True(change.Mutates);
    }

    [Theory]
    [InlineData("--max=11")]
    [InlineData("--max=-1")]
    [InlineData("--max=abc")]
    [InlineData("--max=")]
    public void OutOfRangeOrUnparsableMaxIsRejected(string arg)
    {
        // An unbounded budget is the one setting that could turn this feature into a boot loop,
        // so the CLI must refuse rather than clamp silently.
        var change = PersistenceGuardService.ParseSettingsArgs([arg]);
        Assert.NotNull(change.Error);
        Assert.False(change.Mutates);
    }

    [Theory]
    [InlineData("--max=0", 0)]
    [InlineData("--max=10", 10)]
    public void MaxAcceptsTheInclusiveBounds(string arg, int expected)
    {
        var change = PersistenceGuardService.ParseSettingsArgs([arg]);
        Assert.Null(change.Error);
        Assert.Equal(expected, change.MaxReapplies);
    }

    [Fact]
    public void ResetIsRecognisedAndCombinesWithOtherFlags()
    {
        var change = PersistenceGuardService.ParseSettingsArgs(["--on", "--max=3", "--reset"]);
        Assert.Null(change.Error);
        Assert.True(change.Enable);
        Assert.Equal(3, change.MaxReapplies);
        Assert.True(change.ResetBudget);
    }

    [Fact]
    public void ResetBudgetIsANoOpWhenAlreadyZero()
    {
        // Guards against a config write (and its failure paths) on every ordinary apply.
        var config = new AppConfig { PersistenceGuardConsecutiveReapplies = 0, WorkingDir = string.Empty };
        PersistenceGuardService.ResetBudget(config);
        Assert.Equal(0, config.PersistenceGuardConsecutiveReapplies);
    }
}
