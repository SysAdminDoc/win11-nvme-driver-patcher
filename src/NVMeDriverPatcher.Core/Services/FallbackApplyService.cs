namespace NVMeDriverPatcher.Services;

public sealed class FallbackApplyResult
{
    public bool Success { get; init; }
    public string Method { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public IReadOnlyList<string> AppliedIds { get; init; } = Array.Empty<string>();
    public string IntegritySignal { get; init; } = "native";
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
        CancellationToken cancellationToken = default) =>
        ApplyAsync(
            workingDir,
            log,
            cancellationToken,
            ids => FeatureStoreWriterService.WriteOverrides(ids),
            ViVeToolService.ApplyFallbackAsync);

    internal static async Task<FallbackApplyResult> ApplyAsync(
        string workingDir,
        Action<string>? log,
        CancellationToken cancellationToken,
        Func<IEnumerable<int>, FeatureStoreWriteResult> nativeWriter,
        Func<string, Action<string>?, CancellationToken, Task<ViVeToolService.ViVeToolResult>> viveToolFallback)
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
