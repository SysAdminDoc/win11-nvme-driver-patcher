namespace NVMeDriverPatcher.Services;

/// <summary>
/// Process-wide, fail-closed record of startup recovery failures. Startup resets the gate once,
/// before any recovery work runs; every failed recovery path then contributes a durable reason
/// for the lifetime of that process. Recovery, removal, and diagnostics remain available, but
/// no new boot-storage mutation may begin until a clean application restart proves recovery.
/// </summary>
public static class RecoverySafetyGateService
{
    private static readonly object Sync = new();
    private static readonly List<RecoverySafetyFailure> Failures = [];

    public static void Reset()
    {
        lock (Sync)
            Failures.Clear();
    }

    public static void ReportFailure(string source, string summary)
    {
        source = string.IsNullOrWhiteSpace(source) ? "Startup recovery" : source.Trim();
        summary = string.IsNullOrWhiteSpace(summary)
            ? "Recovery did not provide a complete success result."
            : summary.Trim();

        lock (Sync)
        {
            if (Failures.Any(failure =>
                    failure.Source.Equals(source, StringComparison.OrdinalIgnoreCase) &&
                    failure.Summary.Equals(summary, StringComparison.Ordinal)))
                return;

            Failures.Add(new RecoverySafetyFailure(source, summary, DateTimeOffset.UtcNow));
        }
    }

    public static RecoverySafetyState ObserveInterruptedRecovery(InterruptedMutationRecoveryResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Success)
            ReportFailure("Interrupted mutation ledger", result.Summary);
        return Snapshot();
    }

    public static RecoverySafetyState ObserveAutoRevert(AutoRevertOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (outcome.Failed || (outcome.Executed && !outcome.Success))
            ReportFailure("Watchdog auto-revert", outcome.Summary);
        return Snapshot();
    }

    public static RecoverySafetyState Snapshot()
    {
        lock (Sync)
        {
            var failures = Failures.ToArray();
            if (failures.Length == 0)
            {
                return new RecoverySafetyState(
                    MutationAllowed: true,
                    Summary: "Startup recovery is resolved; mutation actions may proceed.",
                    Failures: failures);
            }

            var exactFailures = string.Join("; ", failures.Select(f => $"{f.Source}: {f.Summary}"));
            return new RecoverySafetyState(
                MutationAllowed: false,
                Summary: "Startup recovery is unresolved. " + exactFailures +
                         " Apply, reinstall, fallback, SafeBoot upgrade, and hot-swap actions " +
                         "remain disabled. Remove the patch or export diagnostics/recovery material, " +
                         "then restart the app after recovery succeeds.",
                Failures: failures);
        }
    }
}

public sealed record RecoverySafetyFailure(
    string Source,
    string Summary,
    DateTimeOffset DetectedAtUtc);

public sealed record RecoverySafetyState(
    bool MutationAllowed,
    string Summary,
    IReadOnlyList<RecoverySafetyFailure> Failures);
