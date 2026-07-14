using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using NVMeDriverPatcher.Models;

namespace NVMeDriverPatcher.Services;

public enum MutationOperationKind
{
    RegistryPatch,
    FeatureStoreFallback
}

public enum MutationOperationPhase
{
    Prepared = 10,
    Applied = 20,
    RebootPending = 30,
    Verified = 40,
    Reverted = 50
}

public enum InterruptedMutationAction
{
    None,
    OperationStillRunning,
    RestoreOriginalState,
    ResumePostRebootVerification
}

public sealed class RegistryValueBaseline
{
    public string KeyPath { get; set; } = string.Empty;
    public string ValueName { get; set; } = string.Empty;
    public bool Existed { get; set; }
    public int Kind { get; set; }
    public string? StringData { get; set; }
    public long? IntegerData { get; set; }
    public List<string>? StringArrayData { get; set; }
    public string? BinaryBase64 { get; set; }
}

public sealed class FeatureStoreConfigurationBaseline
{
    public int FeatureId { get; set; }
    public bool BootStore { get; set; }
    public bool Found { get; set; }
    public uint CompactState { get; set; }
    public uint VariantPayload { get; set; }
}

public sealed class MutationBaseline
{
    public List<RegistryValueBaseline> RegistryValues { get; set; } = new();
    public SafeBootJournal SafeBoot { get; set; } = new();
    public List<FeatureStoreConfigurationBaseline> FeatureStore { get; set; } = new();
    public bool FeatureStoreCaptureComplete { get; set; }
}

public sealed class MutationOperationLedger
{
    public int SchemaVersion { get; set; } = 1;
    public string OperationId { get; set; } = string.Empty;
    public MutationOperationKind Kind { get; set; }
    public MutationOperationPhase Phase { get; set; } = MutationOperationPhase.Prepared;
    public string CreatedUtc { get; set; } = string.Empty;
    public string UpdatedUtc { get; set; } = string.Empty;
    public int OwnerProcessId { get; set; }
    public string OwnerProcessStartedUtc { get; set; } = string.Empty;
    public string PatchProfile { get; set; } = string.Empty;
    public bool IncludeServerKey { get; set; }
    public bool FeatureStoreTouched { get; set; }
    public List<string> IntendedFeatureIds { get; set; } = new();
    public MutationBaseline Baseline { get; set; } = new();
}

public sealed record MutationPreparationResult(
    bool Success,
    string Summary,
    MutationOperationLedger? Ledger = null,
    bool ReusedBaseline = false);

public sealed record MutationRestoreResult(bool Success, IReadOnlyList<string> Failures)
{
    public static MutationRestoreResult Succeeded { get; } = new(true, Array.Empty<string>());
}

public sealed record InterruptedMutationRecoveryResult(
    InterruptedMutationAction Action,
    bool Success,
    string Summary);

/// <summary>
/// Durable write-ahead record for every boot-critical mutation. The ledger owns the first clean
/// baseline across reapply attempts, is published with write-through + atomic replacement, and is
/// the single recovery source for interrupted apply, uninstall, and FeatureStore fallback work.
/// </summary>
public static class MutationLedgerService
{
    public const string LedgerFileName = "mutation_ledger.json";
    private const string LedgerMutexName = @"Global\NVMeDriverPatcher.MutationLedger";
    private static readonly TimeSpan MutexTimeout = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public static string LedgerPath(string workingDir) => Path.Combine(workingDir, LedgerFileName);

    public static MutationPreparationResult PrepareRegistryPatch(
        string workingDir,
        PatchProfile profile,
        bool includeServerKey,
        IEnumerable<string> intendedFeatureIds,
        Action<string>? log = null) =>
        Prepare(
            workingDir,
            MutationOperationKind.RegistryPatch,
            profile,
            includeServerKey,
            intendedFeatureIds,
            requireFeatureStoreBaseline: false,
            log);

    public static MutationPreparationResult PrepareFeatureStoreFallback(
        string workingDir,
        IEnumerable<int> intendedFeatureIds,
        Action<string>? log = null) =>
        Prepare(
            workingDir,
            MutationOperationKind.FeatureStoreFallback,
            PatchProfile.Safe,
            includeServerKey: false,
            intendedFeatureIds.Select(id => id.ToString(CultureInfo.InvariantCulture)),
            requireFeatureStoreBaseline: true,
            log);

    private static MutationPreparationResult Prepare(
        string workingDir,
        MutationOperationKind kind,
        PatchProfile profile,
        bool includeServerKey,
        IEnumerable<string> intendedFeatureIds,
        bool requireFeatureStoreBaseline,
        Action<string>? log)
    {
        if (string.IsNullOrWhiteSpace(workingDir))
            return new(false, "Working directory is empty; the mutation ledger cannot be persisted.");

        using var lease = AcquireMutex();
        if (!lease.Held)
            return new(false, "Mutation ledger is busy in another process; no system state was changed.");

        try
        {
            Directory.CreateDirectory(workingDir);
            var prior = LoadUnsafe(workingDir);
            if (prior is null && File.Exists(LedgerPath(workingDir)))
                return new(false, "Existing mutation ledger is unreadable; refusing to replace possible recovery evidence.");
            bool reuseBaseline = ShouldReuseBaseline(prior);

            if (prior is not null &&
                prior.Phase is MutationOperationPhase.Prepared or MutationOperationPhase.Applied &&
                IsOwnerActive(prior))
            {
                return new(false,
                    $"Mutation operation {prior.OperationId} is still running in process {prior.OwnerProcessId}; retry after it completes.");
            }

            if (prior is not null &&
                prior.Phase is MutationOperationPhase.Prepared or MutationOperationPhase.Applied &&
                !IsOwnerActive(prior))
            {
                return new(false,
                    $"Interrupted mutation {prior.OperationId} must be recovered before another apply can begin.");
            }

            var baseline = reuseBaseline ? prior!.Baseline : CaptureBaseline();
            if (baseline.SafeBoot.Entries.Any(entry => entry.AccessDenied))
                return new(false, "SafeBoot pre-state could not be read exactly; refusing to mutate boot-critical state.");

            if (requireFeatureStoreBaseline && !baseline.FeatureStoreCaptureComplete)
            {
                var featureStore = CaptureFeatureStoreBaseline();
                if (!featureStore.Complete)
                    return new(false, "FeatureStore pre-state could not be read exactly; refusing to apply fallback overrides.");
                baseline.FeatureStore = featureStore.Entries;
                baseline.FeatureStoreCaptureComplete = true;
            }

            var now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            var ownerStart = TryGetCurrentProcessStartUtc();
            var ledger = new MutationOperationLedger
            {
                OperationId = Guid.NewGuid().ToString("N"),
                Kind = kind,
                Phase = MutationOperationPhase.Prepared,
                CreatedUtc = now,
                UpdatedUtc = now,
                OwnerProcessId = Environment.ProcessId,
                OwnerProcessStartedUtc = ownerStart,
                PatchProfile = profile.ToString(),
                IncludeServerKey = includeServerKey,
                FeatureStoreTouched = kind == MutationOperationKind.FeatureStoreFallback,
                IntendedFeatureIds = intendedFeatureIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                Baseline = baseline
            };

            if (!SaveUnsafe(workingDir, ledger, out var saveError))
                return new(false, $"Could not durably publish mutation ledger: {saveError}");

            log?.Invoke($"[LEDGER] Prepared operation {ledger.OperationId}; original state is durable before mutation.");
            return new(true,
                reuseBaseline ? "Prepared using the first clean baseline." : "Captured and persisted a new clean baseline.",
                ledger,
                reuseBaseline);
        }
        catch (Exception ex)
        {
            return new(false, $"Mutation ledger preparation failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public static bool MarkApplied(string workingDir, string? operationId, Action<string>? log = null) =>
        MarkPhase(workingDir, operationId, MutationOperationPhase.Applied, log);

    public static bool MarkRebootPending(string workingDir, string? operationId, Action<string>? log = null) =>
        MarkPhase(workingDir, operationId, MutationOperationPhase.RebootPending, log);

    public static bool MarkLatestVerified(string workingDir, Action<string>? log = null) =>
        MarkPhase(workingDir, operationId: null, MutationOperationPhase.Verified, log);

    public static bool MarkLatestReverted(string workingDir, Action<string>? log = null) =>
        MarkPhase(workingDir, operationId: null, MutationOperationPhase.Reverted, log);

    private static bool MarkPhase(
        string workingDir,
        string? operationId,
        MutationOperationPhase phase,
        Action<string>? log)
    {
        using var lease = AcquireMutex();
        if (!lease.Held)
        {
            log?.Invoke("[LEDGER] Phase update refused: mutation ledger is busy.");
            return false;
        }

        var ledger = LoadUnsafe(workingDir);
        if (ledger is null ||
            (!string.IsNullOrWhiteSpace(operationId) &&
             !string.Equals(ledger.OperationId, operationId, StringComparison.OrdinalIgnoreCase)))
        {
            log?.Invoke("[LEDGER] Phase update refused: operation identity does not match the durable ledger.");
            return false;
        }

        if (phase < ledger.Phase)
        {
            log?.Invoke($"[LEDGER] Refused non-monotonic phase transition {ledger.Phase} -> {phase}.");
            return false;
        }

        ledger.Phase = phase;
        ledger.UpdatedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        if (phase >= MutationOperationPhase.RebootPending)
        {
            ledger.OwnerProcessId = 0;
            ledger.OwnerProcessStartedUtc = string.Empty;
        }

        if (!SaveUnsafe(workingDir, ledger, out var error))
        {
            log?.Invoke($"[LEDGER] Could not persist phase {phase}: {error}");
            return false;
        }

        log?.Invoke($"[LEDGER] Operation {ledger.OperationId} is durably {phase}.");
        return true;
    }

    public static MutationOperationLedger? Load(string workingDir)
    {
        using var lease = AcquireMutex();
        return lease.Held ? LoadUnsafe(workingDir) : null;
    }

    public static InterruptedMutationRecoveryResult RecoverInterrupted(
        string workingDir,
        Action<string>? log = null) =>
        RecoverInterrupted(workingDir, RestoreOriginalStateCore, IsOwnerActive, log);

    internal static InterruptedMutationRecoveryResult RecoverInterrupted(
        string workingDir,
        Func<MutationOperationLedger, MutationRestoreResult> restore,
        Func<MutationOperationLedger, bool> ownerActive,
        Action<string>? log = null)
    {
        using var lease = AcquireMutex();
        if (!lease.Held)
            return new(InterruptedMutationAction.OperationStillRunning, false,
                "Mutation ledger is busy in another process; startup recovery did not run.");

        var ledger = LoadUnsafe(workingDir);
        if (ledger is null && File.Exists(LedgerPath(workingDir)))
            return new(InterruptedMutationAction.RestoreOriginalState, false,
                "Mutation ledger exists but is unreadable; preserve it and use the recovery kit before retrying.");
        if (ledger is null)
            return new(InterruptedMutationAction.None, true, "No mutation ledger is present.");

        var action = ClassifyInterruptedAction(ledger.Phase, ownerActive(ledger));
        if (action == InterruptedMutationAction.OperationStillRunning)
            return new(action, true, $"Mutation {ledger.OperationId} is still owned by a live process.");
        if (action == InterruptedMutationAction.ResumePostRebootVerification)
            return new(action, true, $"Mutation {ledger.OperationId} is reboot-pending; original state remains available for rollback.");
        if (action == InterruptedMutationAction.None)
            return new(action, true, $"Mutation {ledger.OperationId} is already terminal ({ledger.Phase}).");

        log?.Invoke($"[LEDGER] Interrupted operation {ledger.OperationId} detected at phase {ledger.Phase}; restoring exact original state.");
        var restored = restore(ledger);
        if (!restored.Success)
        {
            var detail = string.Join("; ", restored.Failures);
            log?.Invoke("[LEDGER] Automatic recovery incomplete: " + detail);
            return new(action, false, "Interrupted mutation recovery was incomplete: " + detail);
        }

        ledger.Phase = MutationOperationPhase.Reverted;
        ledger.UpdatedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        ledger.OwnerProcessId = 0;
        ledger.OwnerProcessStartedUtc = string.Empty;
        if (!SaveUnsafe(workingDir, ledger, out var error))
            return new(action, false, "Original state was restored, but the reverted phase was not durable: " + error);

        log?.Invoke("[LEDGER] Interrupted mutation restored exactly and marked Reverted.");
        return new(action, true, "Interrupted mutation restored to its exact original state.");
    }

    internal static InterruptedMutationAction ClassifyInterruptedAction(
        MutationOperationPhase phase,
        bool ownerActive)
    {
        if (phase is MutationOperationPhase.Prepared or MutationOperationPhase.Applied)
            return ownerActive
                ? InterruptedMutationAction.OperationStillRunning
                : InterruptedMutationAction.RestoreOriginalState;
        if (phase == MutationOperationPhase.RebootPending)
            return InterruptedMutationAction.ResumePostRebootVerification;
        return InterruptedMutationAction.None;
    }

    internal static bool ShouldReuseBaseline(MutationOperationLedger? prior) =>
        prior is not null &&
        prior.SchemaVersion == 1 &&
        prior.Phase != MutationOperationPhase.Reverted;

    public static MutationRestoreResult RestoreOriginalState(string workingDir, Action<string>? log = null)
    {
        using var lease = AcquireMutex();
        if (!lease.Held)
            return new(false, new[] { "Mutation ledger is busy." });

        var ledger = LoadUnsafe(workingDir);
        if (ledger is null)
            return new(false, new[] { "No mutation ledger is available." });

        var restored = RestoreOriginalStateCore(ledger, log);
        if (!restored.Success)
            return restored;

        ledger.Phase = MutationOperationPhase.Reverted;
        ledger.UpdatedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        ledger.OwnerProcessId = 0;
        ledger.OwnerProcessStartedUtc = string.Empty;
        return SaveUnsafe(workingDir, ledger, out var error)
            ? restored
            : new(false, new[] { "Original state restored but ledger finalization failed: " + error });
    }

    public static MutationRestoreResult RestoreFeatureStoreBaseline(string workingDir, Action<string>? log = null)
    {
        using var lease = AcquireMutex();
        if (!lease.Held)
            return new(false, new[] { "Mutation ledger is busy." });
        var ledger = LoadUnsafe(workingDir);
        if (ledger is null || !ledger.FeatureStoreTouched || !ledger.Baseline.FeatureStoreCaptureComplete)
            return new(false, new[] { "No complete FeatureStore baseline is available." });

        var failures = FeatureStoreWriterService.RestoreConfigurations(ledger.Baseline.FeatureStore, log);
        return failures.Count == 0
            ? MutationRestoreResult.Succeeded
            : new(false, failures);
    }

    private static MutationRestoreResult RestoreOriginalStateCore(MutationOperationLedger ledger) =>
        RestoreOriginalStateCore(ledger, log: null);

    private static MutationRestoreResult RestoreOriginalStateCore(
        MutationOperationLedger ledger,
        Action<string>? log)
    {
        var failures = new List<string>();
        RestoreRegistryValues(ledger.Baseline.RegistryValues, failures, log);

        var safeBootFailures = SafeBootStateService.RestoreFromJournal(
            new RealSafeBootRegistry(), ledger.Baseline.SafeBoot, log);
        failures.AddRange(safeBootFailures.Select(path => "SafeBoot " + path));

        if (ledger.FeatureStoreTouched)
        {
            if (!ledger.Baseline.FeatureStoreCaptureComplete)
                failures.Add("FeatureStore baseline is incomplete");
            else
                failures.AddRange(FeatureStoreWriterService.RestoreConfigurations(ledger.Baseline.FeatureStore, log));
        }

        failures.AddRange(ProbeBaselineDifferences(ledger));
        return failures.Count == 0
            ? MutationRestoreResult.Succeeded
            : new(false, failures.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    internal static IReadOnlyList<string> ProbeBaselineDifferences(MutationOperationLedger ledger)
    {
        var differences = new List<string>();
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            foreach (var expected in ledger.Baseline.RegistryValues)
            {
                var actual = CaptureRegistryValue(hklm, expected.KeyPath, expected.ValueName);
                if (!RegistryValuesEqual(expected, actual))
                    differences.Add($"Registry value {expected.KeyPath}\\{expected.ValueName} differs from baseline");
            }
        }
        catch (Exception ex)
        {
            differences.Add("Registry baseline verification failed: " + ex.GetType().Name);
        }

        var safeBoot = new RealSafeBootRegistry();
        foreach (var expected in ledger.Baseline.SafeBoot.Entries)
        {
            try
            {
                var actual = safeBoot.Read(expected.Path);
                if (!SafeBootSnapshotsEqual(expected.ToSnapshot(), actual))
                    differences.Add("SafeBoot key differs from baseline: " + expected.Path);
            }
            catch (Exception ex)
            {
                differences.Add($"SafeBoot key unverifiable: {expected.Path} ({ex.GetType().Name})");
            }
        }

        if (ledger.FeatureStoreTouched && ledger.Baseline.FeatureStoreCaptureComplete)
            differences.AddRange(FeatureStoreWriterService.ProbeConfigurationDifferences(ledger.Baseline.FeatureStore));
        return differences;
    }

    private static MutationBaseline CaptureBaseline()
    {
        var baseline = new MutationBaseline
        {
            SafeBoot = SafeBootStateService.CaptureJournal(
                new RealSafeBootRegistry(), DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture))
        };

        using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
        {
            foreach (var id in AppConfig.FeatureIDs.Append(AppConfig.ServerFeatureID).Distinct())
                baseline.RegistryValues.Add(CaptureRegistryValue(hklm, AppConfig.RegistrySubKey, id));
        }

        var featureStore = CaptureFeatureStoreBaseline();
        baseline.FeatureStore = featureStore.Entries;
        baseline.FeatureStoreCaptureComplete = featureStore.Complete;
        return baseline;
    }

    private static (List<FeatureStoreConfigurationBaseline> Entries, bool Complete) CaptureFeatureStoreBaseline()
    {
        var entries = new List<FeatureStoreConfigurationBaseline>();
        bool complete = true;
        foreach (var id in FeatureStoreWriterService.PostBlockFeatureIds)
        {
            foreach (bool bootStore in new[] { false, true })
            {
                var state = FeatureStoreWriterService.QueryConfigurationExact(id, bootStore);
                if (!state.QuerySucceeded)
                {
                    complete = false;
                    continue;
                }
                entries.Add(new FeatureStoreConfigurationBaseline
                {
                    FeatureId = id,
                    BootStore = bootStore,
                    Found = state.Found,
                    CompactState = state.CompactState,
                    VariantPayload = state.VariantPayload
                });
            }
        }
        return (entries, complete && entries.Count == FeatureStoreWriterService.PostBlockFeatureIds.Length * 2);
    }

    private static void RestoreRegistryValues(
        IEnumerable<RegistryValueBaseline> values,
        List<string> failures,
        Action<string>? log)
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            foreach (var value in values)
            {
                try
                {
                    if (!value.Existed)
                    {
                        using var key = hklm.OpenSubKey(value.KeyPath, writable: true);
                        key?.DeleteValue(value.ValueName, throwOnMissingValue: false);
                        key?.Flush();
                    }
                    else
                    {
                        using var key = hklm.CreateSubKey(value.KeyPath, writable: true)
                            ?? throw new IOException("Registry key could not be opened for restore.");
                        key.SetValue(value.ValueName, DecodeRegistryValue(value), (RegistryValueKind)value.Kind);
                        key.Flush();
                    }
                    log?.Invoke($"  [LEDGER] Restored registry value {value.ValueName}.");
                }
                catch (Exception ex)
                {
                    failures.Add($"Registry {value.ValueName} ({ex.GetType().Name})");
                }
            }
        }
        catch (Exception ex)
        {
            failures.Add("Registry baseline open failed: " + ex.GetType().Name);
        }
    }

    private static RegistryValueBaseline CaptureRegistryValue(
        RegistryKey hklm,
        string keyPath,
        string valueName)
    {
        using var key = hklm.OpenSubKey(keyPath, writable: false);
        if (key is null || !key.GetValueNames().Contains(valueName, StringComparer.OrdinalIgnoreCase))
            return new RegistryValueBaseline { KeyPath = keyPath, ValueName = valueName, Existed = false };

        var kind = key.GetValueKind(valueName);
        var raw = key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        var snapshot = new RegistryValueBaseline
        {
            KeyPath = keyPath,
            ValueName = valueName,
            Existed = true,
            Kind = (int)kind
        };
        switch (kind)
        {
            case RegistryValueKind.DWord:
            case RegistryValueKind.QWord:
                snapshot.IntegerData = Convert.ToInt64(raw, CultureInfo.InvariantCulture);
                break;
            case RegistryValueKind.MultiString:
                snapshot.StringArrayData = (raw as string[] ?? Array.Empty<string>()).ToList();
                break;
            case RegistryValueKind.Binary:
            case RegistryValueKind.None:
                snapshot.BinaryBase64 = Convert.ToBase64String(raw as byte[] ?? Array.Empty<byte>());
                break;
            default:
                snapshot.StringData = raw?.ToString();
                break;
        }
        return snapshot;
    }

    private static object DecodeRegistryValue(RegistryValueBaseline value) =>
        (RegistryValueKind)value.Kind switch
        {
            RegistryValueKind.DWord => checked((int)(value.IntegerData ?? 0)),
            RegistryValueKind.QWord => value.IntegerData ?? 0L,
            RegistryValueKind.MultiString => value.StringArrayData?.ToArray() ?? Array.Empty<string>(),
            RegistryValueKind.Binary or RegistryValueKind.None =>
                string.IsNullOrWhiteSpace(value.BinaryBase64)
                    ? Array.Empty<byte>()
                    : Convert.FromBase64String(value.BinaryBase64),
            _ => value.StringData ?? string.Empty
        };

    internal static bool RegistryValuesEqual(RegistryValueBaseline left, RegistryValueBaseline right) =>
        left.Existed == right.Existed &&
        (!left.Existed ||
         (left.Kind == right.Kind &&
          string.Equals(left.StringData, right.StringData, StringComparison.Ordinal) &&
          left.IntegerData == right.IntegerData &&
          (left.StringArrayData ?? new()).SequenceEqual(right.StringArrayData ?? new(), StringComparer.Ordinal) &&
          string.Equals(left.BinaryBase64, right.BinaryBase64, StringComparison.Ordinal)));

    private static bool SafeBootSnapshotsEqual(SafeBootKeySnapshot left, SafeBootKeySnapshot right)
    {
        if (left.Existed != right.Existed || left.AccessDenied != right.AccessDenied)
            return false;
        var l = left.Values.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        var r = right.Values.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        return l.SequenceEqual(r);
    }

    private static MutationOperationLedger? LoadUnsafe(string workingDir)
    {
        var path = LedgerPath(workingDir);
        return TryLoadLedgerFile(path) ?? TryLoadLedgerFile(path + ".bak");
    }

    private static MutationOperationLedger? TryLoadLedgerFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path, Encoding.UTF8);
            var ledger = JsonSerializer.Deserialize<MutationOperationLedger>(json, JsonOptions);
            return ledger is { SchemaVersion: 1 } &&
                   !string.IsNullOrWhiteSpace(ledger.OperationId) &&
                   ledger.Baseline is not null
                ? ledger
                : null;
        }
        catch { return null; }
    }

    private static bool SaveUnsafe(
        string workingDir,
        MutationOperationLedger ledger,
        out string error,
        Action<string>? beforePublish = null)
    {
        var target = LedgerPath(workingDir);
        var temp = target + "." + Environment.ProcessId + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(workingDir);
            var json = JsonSerializer.Serialize(ledger, JsonOptions);
            using (var fs = new FileStream(
                       temp,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            using (var writer = new StreamWriter(fs, new UTF8Encoding(false)))
            {
                writer.Write(json);
                writer.Flush();
                fs.Flush(flushToDisk: true);
            }

            // Validate the exact staged bytes before publication. A serialization or truncated-write
            // fault therefore leaves the previous ledger authoritative.
            var staged = JsonSerializer.Deserialize<MutationOperationLedger>(File.ReadAllText(temp, Encoding.UTF8), JsonOptions);
            if (staged is null || staged.OperationId != ledger.OperationId || staged.Phase != ledger.Phase)
                throw new InvalidDataException("Staged ledger validation failed.");

            beforePublish?.Invoke(temp);
            if (File.Exists(target))
            {
                var backup = target + ".bak";
                File.Replace(temp, target, backup, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, target);
            }
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + ": " + ex.Message;
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            return false;
        }
    }

    internal static bool SaveForTest(
        string workingDir,
        MutationOperationLedger ledger,
        Action<string>? beforePublish,
        out string error)
    {
        using var lease = AcquireMutex();
        if (!lease.Held)
        {
            error = "busy";
            return false;
        }
        return SaveUnsafe(workingDir, ledger, out error, beforePublish);
    }

    private static bool IsOwnerActive(MutationOperationLedger ledger)
    {
        if (ledger.OwnerProcessId <= 0 || string.IsNullOrWhiteSpace(ledger.OwnerProcessStartedUtc))
            return false;
        try
        {
            using var process = Process.GetProcessById(ledger.OwnerProcessId);
            var actual = process.StartTime.ToUniversalTime();
            return DateTime.TryParse(
                       ledger.OwnerProcessStartedUtc,
                       CultureInfo.InvariantCulture,
                       DateTimeStyles.RoundtripKind,
                       out var expected) &&
                   Math.Abs((actual - expected.ToUniversalTime()).TotalSeconds) < 1;
        }
        catch
        {
            return false;
        }
    }

    private static string TryGetCurrentProcessStartUtc()
    {
        try { return Process.GetCurrentProcess().StartTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture); }
        catch { return DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture); }
    }

    private static MutexLease AcquireMutex()
    {
        Mutex? mutex = null;
        bool held = false;
        try
        {
            mutex = new Mutex(initiallyOwned: false, LedgerMutexName);
            try { held = mutex.WaitOne(MutexTimeout); }
            catch (AbandonedMutexException) { held = true; }
            return new MutexLease(mutex, held);
        }
        catch
        {
            mutex?.Dispose();
            return new MutexLease(null, false);
        }
    }

    private sealed class MutexLease(Mutex? mutex, bool held) : IDisposable
    {
        public bool Held { get; } = held;

        public void Dispose()
        {
            if (Held) { try { mutex?.ReleaseMutex(); } catch { } }
            mutex?.Dispose();
        }
    }
}
