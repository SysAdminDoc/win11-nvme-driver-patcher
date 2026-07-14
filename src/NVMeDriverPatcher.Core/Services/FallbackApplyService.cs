namespace NVMeDriverPatcher.Services;

public sealed class FallbackApplyResult
{
    public bool Success { get; init; }
    public string Method { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public IReadOnlyList<string> AppliedIds { get; init; } = Array.Empty<string>();
    public string IntegritySignal { get; init; } = "native";
    public string? MutationOperationId { get; set; }
}

/// <summary>
/// Applies the post-registry-block fallback without using ViVeTool unless the in-process
/// FeatureStore writer fails verification. This keeps the normal path offline and reserves
/// the third-party download for the explicit recovery branch.
/// </summary>
public static class FallbackApplyService
{
    public const string NativeMethod = "native-featurestore";
    public const string ViVeToolMethod = "vivetool";

    public static Task<FallbackApplyResult> ApplyAsync(
        string workingDir,
        Action<string>? log = null,
        CancellationToken cancellationToken = default,
        bool allowUnsupportedBuild = false) =>
        ApplyTransactionalAsync(workingDir, allowViVeToolFallback: true, allowUnsupportedBuild, log, cancellationToken);

    public static Task<FallbackApplyResult> ApplyNativeOnlyAsync(
        string workingDir,
        Action<string>? log = null,
        CancellationToken cancellationToken = default,
        bool allowUnsupportedBuild = false) =>
        ApplyTransactionalAsync(workingDir, allowViVeToolFallback: false, allowUnsupportedBuild, log, cancellationToken);

    private static async Task<FallbackApplyResult> ApplyTransactionalAsync(
        string workingDir,
        bool allowViVeToolFallback,
        bool allowUnsupportedBuild,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        var buildPolicy = BuildActionPolicyService.EvaluateCurrent(workingDir);
        if (!buildPolicy.MutationAllowed && !allowUnsupportedBuild)
        {
            return new FallbackApplyResult
            {
                Success = false,
                Message = "Fallback blocked by build policy: " + buildPolicy.Reason
            };
        }

        var criticalProbes = CriticalEnvironmentProbeService.EvaluateFeatureStoreFallback();
        if (!criticalProbes.AllPassed)
        {
            foreach (var probe in criticalProbes.Items.Where(item => item.BlocksMutation))
                log?.Invoke($"[ERROR] BLOCKED [{probe.Id}/{probe.Verdict}/{probe.ReasonCode}]: {probe.Detail}");
            return new FallbackApplyResult
            {
                Success = false,
                Message = criticalProbes.Summary
            };
        }

        var idSet = ViVeToolService.SelectFallbackSet();
        var prepared = MutationLedgerService.PrepareFeatureStoreFallback(workingDir, idSet.Ids, log);
        if (!prepared.Success || prepared.Ledger is null)
        {
            return new FallbackApplyResult
            {
                Success = false,
                Message = "Fallback blocked before mutation: " + prepared.Summary
            };
        }

        var bitLocker = BitLockerRecoveryService.PrepareForMutation(
            () => MutationLedgerService.MarkBitLockerSuspensionPlanned(
                workingDir,
                prepared.Ledger.OperationId,
                log),
            log);
        if (!bitLocker.Success)
        {
            if (bitLocker.SuspendedByThisCall)
                MutationLedgerService.RestoreOriginalState(workingDir, log);
            else
                MutationLedgerService.MarkPreparedWithoutMutation(workingDir, prepared.Ledger.OperationId, log);
            return new FallbackApplyResult
            {
                Success = false,
                Message = "Fallback blocked before FeatureStore mutation: " + bitLocker.Summary,
                MutationOperationId = prepared.Ledger.OperationId
            };
        }

        FallbackApplyResult result;
        try
        {
            result = await ApplyAsync(
                workingDir,
                log,
                cancellationToken,
                ids => FeatureStoreWriterService.WriteOverrides(ids),
                ViVeToolService.ApplyFallbackAsync,
                allowViVeToolFallback).ConfigureAwait(false);
        }
        catch
        {
            // Cancellation or an unexpected fallback exception must not leave an application-owned
            // BitLocker suspension behind. The ledger restores both FeatureStore and protector state.
            MutationLedgerService.RestoreOriginalState(workingDir, log);
            throw;
        }
        result.MutationOperationId = prepared.Ledger.OperationId;

        if (!result.Success)
        {
            var restored = MutationLedgerService.RestoreOriginalState(workingDir, log);
            if (!restored.Success)
            {
                return new FallbackApplyResult
                {
                    Success = false,
                    Message = result.Message + " Exact baseline restore was incomplete: " + string.Join("; ", restored.Failures),
                    IntegritySignal = result.IntegritySignal,
                    MutationOperationId = result.MutationOperationId
                };
            }
            return result;
        }

        if (!MutationLedgerService.MarkApplied(workingDir, result.MutationOperationId, log))
        {
            var restored = MutationLedgerService.RestoreOriginalState(workingDir, log);
            return new FallbackApplyResult
            {
                Success = false,
                Message = restored.Success
                    ? "Fallback write landed, but its Applied phase was not durable; exact original state was restored."
                    : "Fallback write landed, its Applied phase was not durable, and exact recovery was incomplete: " + string.Join("; ", restored.Failures),
                IntegritySignal = result.IntegritySignal,
                MutationOperationId = result.MutationOperationId
            };
        }

        return result;
    }

    internal static async Task<FallbackApplyResult> ApplyAsync(
        string workingDir,
        Action<string>? log,
        CancellationToken cancellationToken,
        Func<IEnumerable<int>, FeatureStoreWriteResult> nativeWriter,
        Func<string, Action<string>?, CancellationToken, Task<ViVeToolService.ViVeToolResult>> viveToolFallback,
        bool allowViVeToolFallback = true)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var idSet = ViVeToolService.SelectFallbackSet();
        log?.Invoke($"Using fallback ID set '{idSet.Name}' ({idSet.AppliesTo}; {idSet.Confidence}): {idSet.IdsDisplay}");
        log?.Invoke("Applying native FeatureStore fallback with in-process Rtl APIs (no network).");

        FeatureStoreWriteResult native;
        try
        {
            native = nativeWriter(idSet.Ids);
        }
        catch (Exception ex)
        {
            native = new FeatureStoreWriteResult
            {
                Success = false,
                Summary = $"Native FeatureStore fallback threw {ex.GetType().Name}: {ex.Message}"
            };
        }

        if (native.Success)
        {
            var applied = native.AppliedIds.Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            EventLogService.Write($"Native FeatureStore fallback applied ({string.Join(", ", applied)})");
            log?.Invoke(native.Summary);
            log?.Invoke($"Fallback method: {NativeMethod}; integrity signal: native");
            return new FallbackApplyResult
            {
                Success = true,
                Method = NativeMethod,
                Message = native.Summary,
                AppliedIds = applied,
                IntegritySignal = "native"
            };
        }

        log?.Invoke($"Native FeatureStore fallback failed: {native.Summary}");
        if (!allowViVeToolFallback)
        {
            return new FallbackApplyResult
            {
                Success = false,
                Message = "Native FeatureStore write failed verification: " + native.Summary,
                IntegritySignal = "native"
            };
        }
        log?.Invoke("Trying ViVeTool as the secondary fallback path; this may download the official GitHub release.");
        cancellationToken.ThrowIfCancellationRequested();

        var vive = await viveToolFallback(workingDir, log, cancellationToken).ConfigureAwait(false);
        if (vive.Success)
        {
            log?.Invoke($"Fallback method: {ViVeToolMethod}; integrity signal: {vive.IntegritySignal}");
            return new FallbackApplyResult
            {
                Success = true,
                Method = ViVeToolMethod,
                Message = vive.Message,
                AppliedIds = vive.AppliedIDs.ToArray(),
                IntegritySignal = vive.IntegritySignal
            };
        }

        return new FallbackApplyResult
        {
            Success = false,
            Method = string.Empty,
            Message = $"Native FeatureStore fallback failed: {native.Summary} ViVeTool fallback also failed: {vive.Message}",
            AppliedIds = Array.Empty<string>(),
            IntegritySignal = vive.IntegritySignal
        };
    }
}
