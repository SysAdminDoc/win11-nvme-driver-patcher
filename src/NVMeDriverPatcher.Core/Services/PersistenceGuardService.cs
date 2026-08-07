using NVMeDriverPatcher.Models;

namespace NVMeDriverPatcher.Services;

/// <summary>Why the guard did or did not re-apply. Ordered so a caller can log the reason verbatim.</summary>
public enum PersistenceGuardDecision
{
    /// <summary>The feature is off. Nothing was evaluated.</summary>
    Disabled,
    /// <summary>The patch is present, or was never applied, or the user removed it deliberately.</summary>
    NothingToDo,
    /// <summary>Eligible, but the consecutive re-apply budget is spent. Refuses until a manual apply resets it.</summary>
    BudgetExhausted,
    /// <summary>Eligible, but the watchdog says this machine is unstable. Stability outranks persistence.</summary>
    DeferredUnstable,
    /// <summary>Eligible, but build policy currently forbids mutation on this Windows build.</summary>
    DeferredBuildPolicy,
    /// <summary>Eligible, but unresolved startup recovery has latched mutation off for this process.</summary>
    DeferredRecoveryLatch,
    /// <summary>Re-apply the patch.</summary>
    Reapply
}

public sealed record PersistenceGuardOutcome(
    PersistenceGuardDecision Decision,
    string Summary)
{
    public bool Executed { get; init; }
    public bool Success { get; init; }
}

/// <summary>
/// Restores a patch that Windows removed without the user asking (GitHub issue #15).
///
/// <para>
/// Control-set mirroring covers the common case — a boot-recovery promotion of a spare set. It
/// cannot cover Startup Repair's driver-rollback diagnostics, which delete the keys outright. This
/// guard is the backstop: when post-reboot verification says the patch is gone but the mutation
/// ledger says the user never removed it, something other than the user took it away.
/// </para>
///
/// <para>
/// This re-arms a <em>boot-critical</em> driver on a machine that may have just failed to boot, so
/// every gate is fail-closed and the budget is the last line of defence: a machine that keeps
/// losing the patch stops being re-patched rather than being pushed into a boot loop. Ordering
/// matters — this must run strictly after the revert-only consumers, so a watchdog revert or a
/// fallback reset always wins over re-apply.
/// </para>
/// </summary>
public static class PersistenceGuardService
{
    /// <summary>Consecutive automatic re-applies allowed before the guard stands down.</summary>
    public const int DefaultMaxConsecutiveReapplies = 2;

    /// <summary>
    /// Pure decision. Every input is a fact the caller has already established, so the whole
    /// truth table — including the anti-boot-loop budget — is testable without a registry,
    /// an event log, or a reboot.
    /// </summary>
    public static PersistenceGuardDecision Decide(
        bool enabled,
        VerificationOutcome outcome,
        MutationOperationPhase? ledgerPhase,
        int consecutiveReapplies,
        int maxConsecutiveReapplies,
        WatchdogVerdict watchdogVerdict,
        bool buildPolicyAllowsMutation,
        bool recoveryGateAllowsMutation)
    {
        if (!enabled) return PersistenceGuardDecision.Disabled;

        // Only one state means "something else took the patch away": verification proved the keys
        // are gone, while the ledger's terminal phase says this machine has an applied patch.
        if (outcome != VerificationOutcome.Reverted) return PersistenceGuardDecision.NothingToDo;
        if (ledgerPhase is not (MutationOperationPhase.Applied
            or MutationOperationPhase.RebootPending
            or MutationOperationPhase.Verified))
        {
            // Phase.Reverted is a deliberate uninstall; Prepared/absent never completed an apply.
            return PersistenceGuardDecision.NothingToDo;
        }

        // Budget before every other gate: an exhausted budget must stay exhausted even if the
        // machine later looks healthy, or a flapping system re-enters the loop it just escaped.
        if (consecutiveReapplies >= maxConsecutiveReapplies) return PersistenceGuardDecision.BudgetExhausted;

        // Stability outranks persistence. Unavailable is not proof of health, so it defers too.
        if (watchdogVerdict is WatchdogVerdict.Unstable or WatchdogVerdict.Unavailable)
            return PersistenceGuardDecision.DeferredUnstable;

        if (!buildPolicyAllowsMutation) return PersistenceGuardDecision.DeferredBuildPolicy;
        if (!recoveryGateAllowsMutation) return PersistenceGuardDecision.DeferredRecoveryLatch;

        return PersistenceGuardDecision.Reapply;
    }

    /// <summary>The result of parsing persistence-guard command arguments.</summary>
    public sealed record GuardSettingsChange(
        bool? Enable,
        int? MaxReapplies,
        bool ResetBudget,
        string? Error)
    {
        public bool Mutates => Enable is not null || MaxReapplies is not null || ResetBudget;
    }

    /// <summary>
    /// Pure: parses the persistence-guard argument set. Kept out of the CLI so the validation
    /// rules are testable without an elevated process or a config file on disk.
    /// </summary>
    public static GuardSettingsChange ParseSettingsArgs(IReadOnlyList<string>? args)
    {
        args ??= [];
        bool on = args.Any(a => string.Equals(a, "--on", StringComparison.OrdinalIgnoreCase));
        bool off = args.Any(a => string.Equals(a, "--off", StringComparison.OrdinalIgnoreCase));
        bool reset = args.Any(a => string.Equals(a, "--reset", StringComparison.OrdinalIgnoreCase));

        if (on && off)
            return new(null, null, false, "Specify only one of --on or --off.");

        int? max = null;
        var maxArg = args.FirstOrDefault(a =>
            a is not null && a.StartsWith("--max=", StringComparison.OrdinalIgnoreCase));
        if (maxArg is not null)
        {
            var raw = maxArg["--max=".Length..];
            if (!int.TryParse(raw, out int parsed) || parsed < 0 || parsed > 10)
                return new(null, null, false, "--max must be an integer between 0 and 10.");
            max = parsed;
        }

        return new(on || off ? on : null, max, reset, null);
    }

    /// <summary>Pure: the operator-facing sentence for a decision.</summary>
    public static string Describe(PersistenceGuardDecision decision, int consecutiveReapplies, int max) =>
        decision switch
        {
            PersistenceGuardDecision.Disabled =>
                "Persistence guard is disabled.",
            PersistenceGuardDecision.NothingToDo =>
                "Persistence guard: nothing to restore.",
            PersistenceGuardDecision.BudgetExhausted =>
                $"Persistence guard stood down after {consecutiveReapplies} consecutive automatic re-applies (limit {max}). " +
                "Windows keeps removing this patch — apply it manually to investigate and reset the budget.",
            PersistenceGuardDecision.DeferredUnstable =>
                "Persistence guard deferred — the stability watchdog cannot vouch for this machine.",
            PersistenceGuardDecision.DeferredBuildPolicy =>
                "Persistence guard deferred — build policy currently allows verify/rollback only.",
            PersistenceGuardDecision.DeferredRecoveryLatch =>
                "Persistence guard deferred — unresolved startup recovery has disabled mutation.",
            PersistenceGuardDecision.Reapply =>
                "Persistence guard: the patch was removed without an uninstall — re-applying.",
            _ => "Persistence guard: unknown decision."
        };

    /// <summary>
    /// Runs the guard once. Call only from the boot task / startup path, strictly AFTER
    /// <see cref="AutoRevertService.MaybeRun"/> and <see cref="FallbackRecoveryCoordinator.RunOnce"/>.
    /// </summary>
    public static PersistenceGuardOutcome RunOnce(AppConfig config, Action<string>? log = null)
    {
        try
        {
            if (!config.PersistenceGuardEnabled)
                return new(PersistenceGuardDecision.Disabled,
                    Describe(PersistenceGuardDecision.Disabled, 0, config.PersistenceGuardMaxReapplies));

            string workingDir = string.IsNullOrWhiteSpace(config.WorkingDir)
                ? AppConfig.GetWorkingDir()
                : config.WorkingDir;

            var verification = PatchVerificationService.Evaluate(config);
            var ledger = MutationLedgerService.Load(workingDir);
            var watchdog = EventLogWatchdogService.Evaluate(config);
            var buildPolicy = BuildActionPolicyService.EvaluateCurrent(workingDir);
            var recoveryGate = RecoverySafetyGateService.Snapshot();

            var decision = Decide(
                config.PersistenceGuardEnabled,
                verification.Outcome,
                ledger?.Phase,
                config.PersistenceGuardConsecutiveReapplies,
                config.PersistenceGuardMaxReapplies,
                watchdog.Verdict,
                buildPolicy.MutationAllowed,
                recoveryGate.MutationAllowed);

            var summary = Describe(decision, config.PersistenceGuardConsecutiveReapplies, config.PersistenceGuardMaxReapplies);
            if (decision != PersistenceGuardDecision.Reapply)
            {
                if (decision != PersistenceGuardDecision.Disabled && decision != PersistenceGuardDecision.NothingToDo)
                    log?.Invoke("[GUARD] " + summary);
                return new(decision, summary);
            }

            log?.Invoke("[GUARD] " + summary);
            EventLogService.Write(
                "NVMe Driver Patcher persistence guard re-applying a patch removed without an uninstall " +
                "(likely Windows boot recovery).",
                System.Diagnostics.EventLogEntryType.Warning, 3012);

            // Spend the budget BEFORE mutating. A re-apply that bluescreens the machine must not
            // come back with an untouched counter and try again forever.
            config.PersistenceGuardConsecutiveReapplies++;
            if (!ConfigService.Save(config))
            {
                var failure = "Persistence guard aborted — the re-apply budget could not be persisted, " +
                              "so a retry loop could not be bounded.";
                log?.Invoke("[GUARD] " + failure);
                EventLogService.Write(failure, System.Diagnostics.EventLogEntryType.Error, 3013);
                return new(decision, failure) { Executed = false, Success = false };
            }

            var nativeStatus = DriveService.TestNativeNVMeActive();
            var bypassStatus = DriveService.GetBypassIOStatus();
            var result = PatchService.Install(config, nativeStatus, bypassStatus, log);

            var resultSummary = result.Success
                ? $"Persistence guard re-applied the patch ({result.AppliedCount}/{result.TotalExpected} components). Restart to finalize."
                : "Persistence guard could not re-apply the patch. The machine is running the legacy stack; apply manually.";
            log?.Invoke("[GUARD] " + resultSummary);
            if (!result.Success)
            {
                EventLogService.Write(resultSummary, System.Diagnostics.EventLogEntryType.Error, 3013);
            }
            return new(decision, resultSummary) { Executed = true, Success = result.Success };
        }
        catch (Exception ex) when (!IsFatal(ex))
        {
            var summary = $"Persistence guard aborted: {ex.GetType().Name}: {ex.Message}";
            log?.Invoke("[GUARD] " + summary);
            return new(PersistenceGuardDecision.NothingToDo, summary);
        }
    }

    /// <summary>
    /// Clears the re-apply budget. Called after a deliberate user apply or removal — both prove a
    /// human is in the loop, which is exactly the condition the budget exists to wait for.
    /// </summary>
    public static void ResetBudget(AppConfig config)
    {
        if (config.PersistenceGuardConsecutiveReapplies == 0) return;
        config.PersistenceGuardConsecutiveReapplies = 0;
        ConfigService.Save(config);
    }

    // Mirrors AutoRevertService: never swallow a fault that means the process state is untrustworthy
    // while a boot-critical mutation may be half-written.
    private static bool IsFatal(Exception ex) =>
        ex is OutOfMemoryException or StackOverflowException or AccessViolationException
            or AppDomainUnloadedException or BadImageFormatException or InvalidProgramException;
}
