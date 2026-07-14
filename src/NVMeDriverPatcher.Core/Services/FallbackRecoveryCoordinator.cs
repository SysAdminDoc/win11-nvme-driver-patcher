using NVMeDriverPatcher.Models;

namespace NVMeDriverPatcher.Services;

public sealed record FallbackRecoveryResult(bool Attempted, bool Success, string Summary)
{
    public static FallbackRecoveryResult NotNeeded { get; } =
        new(false, false, "No fallback recovery needed.");
}

/// <summary>
/// One-shot post-reboot recovery for the FeatureStore fallback. This is the SINGLE owner of the
/// fallback reset that used to live (as a side effect) inside <see cref="PatchVerificationService.Evaluate"/>.
///
/// Semantics:
///  - Runs only when a fallback was applied (<see cref="AppConfig.PendingFallbackApplied"/>) AND the
///    post-reboot outcome / watchdog verdict warrants a reset.
///  - On a successful reset it clears the checkpoint and persists it, so a second invocation is a
///    no-op (idempotent — cannot be re-triggered).
///  - On a failed reset it RETAINS the checkpoint so the next startup retries.
///  - If the reset succeeds but the config save fails, the reset is reported as not durable so the
///    caller does not treat the machine as clean.
///
/// Call sites are limited to genuine once-per-startup surfaces (GUI startup, CLI boot task). Polling
/// callers — the tray tick, dashboard/telemetry render — must never call this; they use the pure
/// <see cref="PatchVerificationService.Evaluate"/> read instead.
/// </summary>
public static class FallbackRecoveryCoordinator
{
    public static FallbackRecoveryResult RunOnce(AppConfig config, Action<string>? log = null)
        => RunOnce(config, PatchVerificationService.Evaluate(config), log);

    public static FallbackRecoveryResult RunOnce(AppConfig config, VerificationReport report, Action<string>? log = null)
        => RunOnce(config, report, FeatureStoreWriterService.ResetAppliedFallback, log);

    // Internal overload with an injectable reset so the once/success/retry semantics are unit-testable
    // without invoking the machine-global Rtl FeatureStore API.
    internal static FallbackRecoveryResult RunOnce(
        AppConfig config,
        VerificationReport report,
        Func<FeatureStoreWriteResult> resetFn,
        Action<string>? log = null)
    {
        if (!config.PendingFallbackApplied ||
            !PatchVerificationService.ShouldAutoResetFallback(report.Outcome, config))
            return FallbackRecoveryResult.NotNeeded;

        try
        {
            var reset = resetFn();
            if (!reset.Success)
            {
                log?.Invoke("[FALLBACK-RECOVERY] Reset failed — checkpoint retained for retry. " + reset.Summary);
                return new FallbackRecoveryResult(true, false,
                    "Fallback reset failed; checkpoint retained for retry. " + reset.Summary);
            }

            // Reset landed — clear the one-shot checkpoint and make the clear durable.
            config.PendingFallbackApplied = false;
            var saved = ConfigService.Save(config);
            if (!saved)
            {
                log?.Invoke("[FALLBACK-RECOVERY] FeatureStore IDs were reset but the checkpoint clear failed to persist — will re-verify next launch.");
                return new FallbackRecoveryResult(true, false,
                    "Fallback IDs reset but the checkpoint could not be persisted; it will be re-evaluated next launch. " + reset.Summary);
            }

            log?.Invoke("[FALLBACK-RECOVERY] FeatureStore fallback IDs reset and checkpoint cleared. " + reset.Summary);
            return new FallbackRecoveryResult(true, true,
                "FeatureStore fallback IDs reset and checkpoint cleared. " + reset.Summary);
        }
        catch (Exception ex)
        {
            log?.Invoke("[FALLBACK-RECOVERY] Reset threw — checkpoint retained: " + ex.Message);
            return new FallbackRecoveryResult(true, false, "Fallback recovery threw: " + ex.Message);
        }
    }
}
