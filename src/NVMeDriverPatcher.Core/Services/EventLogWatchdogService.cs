using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Text;
using System.Text.Json;
using NVMeDriverPatcher.Models;

namespace NVMeDriverPatcher.Services;

public enum WatchdogVerdict
{
    /// <summary>State persistence or System Event Log evidence could not be proved.</summary>
    Unavailable,
    /// <summary>No patch active or no pending watchdog window. Nothing to report.</summary>
    Idle,
    /// <summary>Watching — post-patch window still open, event counts below threshold.</summary>
    Healthy,
    /// <summary>Event counts crossed the warning threshold — surface a UI notice but don't revert.</summary>
    Warning,
    /// <summary>Event counts crossed the revert threshold — caller should stage an auto-revert.</summary>
    Unstable,
    /// <summary>Watchdog window expired cleanly — Healthy outcome locked in.</summary>
    Completed
}

public class WatchdogEventCount
{
    public string Source { get; set; } = string.Empty;
    public int Id { get; set; }
    public string Description { get; set; } = string.Empty;
    public int Count { get; set; }
    public DateTime? LatestOccurrence { get; set; }
}

public class WatchdogReport
{
    public WatchdogVerdict Verdict { get; set; }
    public WatchdogVerdict? ObservedVerdict { get; set; }
    public bool DataAvailable => Verdict != WatchdogVerdict.Unavailable;
    public bool AutoRevertEnabled { get; set; }
    public string? FailureCode { get; set; }
    public DateTime? WindowStart { get; set; }
    public DateTime? WindowEnd { get; set; }
    public int TotalEvents { get; set; }
    public int BugChecks { get; set; }
    public List<WatchdogEventCount> Counts { get; set; } = new();
    public string Summary { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
}

public class WatchdogState
{
    public string? PatchAppliedAt { get; set; }
    public int WindowHours { get; set; } = 48;
    public int WarnThreshold { get; set; } = 3;
    public int RevertThreshold { get; set; } = 6;
    public bool AutoRevertEnabled { get; set; } = true;
    public string? LastVerdict { get; set; }
    public string? LastEvaluatedAt { get; set; }
    public int CumulativeEvents { get; set; }
}

public enum WatchdogStateAccessStatus
{
    Loaded,
    Missing,
    Saved,
    Busy,
    Unavailable
}

public sealed record WatchdogStateLoadResult(
    WatchdogState State,
    WatchdogStateAccessStatus Status,
    string Summary)
{
    public bool Success => Status is WatchdogStateAccessStatus.Loaded or WatchdogStateAccessStatus.Missing;
}

public sealed record WatchdogStateSaveResult(WatchdogStateAccessStatus Status, string Summary)
{
    public bool Success => Status == WatchdogStateAccessStatus.Saved;
}

public sealed record WatchdogEventLogProbeResult(bool Success, string Summary, string? FailureCode = null);

internal sealed record WatchdogEventQueryResult(
    bool Success,
    List<WatchdogEventCount> Counts,
    string Summary,
    string? FailureCode = null);

// Post-patch stability watchdog. Watches Storport ID 129 (timeout), disk ID 51/153 (paging),
// nvmedisk init/unload, and BugCheck ID 1001 in the N-hour window after a patch apply.
// If counts cross the revert threshold, signals the caller to stage an auto-revert on next
// boot — closes the "tool lied, driver wedged, user can't tell" gap for unattended deploys.
public static class EventLogWatchdogService
{
    private const string StateFile = "watchdog.json";
    internal const string StateMutexName = @"Global\NVMeDriverPatcher.WatchdogState";
    private static readonly TimeSpan StateMutexTimeout = TimeSpan.FromSeconds(10);

    // Events we treat as storage-stack distress signals. Each is either a direct NVMe failure
    // or a class of failure that correlates with the patch on community BSOD threads.
    // Keep this list tight — false positives would cost user trust on perfectly stable systems.
    private static readonly (string Source, int Id, string Description)[] WatchEvents =
    {
        ("storahci", 129, "Storport timeout (device reset)"),
        ("storport", 129, "Storport timeout (device reset)"),
        ("stornvme", 129, "NVMe timeout (device reset)"),
        ("nvmedisk", 129, "Native NVMe timeout (device reset)"),
        ("disk", 51, "Paging I/O error on disk"),
        ("disk", 153, "I/O completed with reset"),
        ("Microsoft-Windows-Kernel-Power", 41, "Unexpected shutdown (kernel power)"),
        ("BugCheck", 1001, "Bug check (BSOD) — correlation only")
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static string StatePath(AppConfig config) =>
        Path.Combine(string.IsNullOrWhiteSpace(config.WorkingDir) ? AppConfig.GetWorkingDir() : config.WorkingDir, StateFile);

    public static WatchdogState LoadState(AppConfig config)
    {
        var result = LoadStateWithStatus(config);
        if (!result.Success)
            throw new IOException(result.Summary);
        return result.State;
    }

    public static WatchdogStateLoadResult LoadStateWithStatus(AppConfig config) =>
        LoadStateWithStatus(config, StateMutexTimeout, StateMutexName);

    internal static WatchdogStateLoadResult LoadStateWithStatus(
        AppConfig config,
        TimeSpan timeout,
        string mutexName)
    {
        using var lease = AcquireStateMutex(timeout, mutexName);
        return lease.Held
            ? LoadStateCore(config)
            : new WatchdogStateLoadResult(
                new WatchdogState(),
                lease.Error is null ? WatchdogStateAccessStatus.Busy : WatchdogStateAccessStatus.Unavailable,
                lease.Error ?? "Watchdog state is busy in another process; no protected state was read.");
    }

    public static WatchdogStateSaveResult SaveState(AppConfig config, WatchdogState state) =>
        SaveState(config, state, StateMutexTimeout, StateMutexName);

    internal static WatchdogStateSaveResult SaveState(
        AppConfig config,
        WatchdogState state,
        TimeSpan timeout,
        string mutexName)
    {
        using var lease = AcquireStateMutex(timeout, mutexName);
        return lease.Held
            ? SaveStateCore(config, state)
            : new WatchdogStateSaveResult(
                lease.Error is null ? WatchdogStateAccessStatus.Busy : WatchdogStateAccessStatus.Unavailable,
                lease.Error ?? "Watchdog state is busy in another process; no protected state was written.");
    }

    public static WatchdogStateSaveResult UpdateState(
        AppConfig config,
        Action<WatchdogState> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        using var lease = AcquireStateMutex(StateMutexTimeout, StateMutexName);
        if (!lease.Held)
        {
            return new WatchdogStateSaveResult(
                lease.Error is null ? WatchdogStateAccessStatus.Busy : WatchdogStateAccessStatus.Unavailable,
                lease.Error ?? "Watchdog state is busy in another process; the update did not run.");
        }

        var loaded = LoadStateCore(config);
        if (!loaded.Success)
            return new WatchdogStateSaveResult(WatchdogStateAccessStatus.Unavailable, loaded.Summary);
        update(loaded.State);
        return SaveStateCore(config, loaded.State);
    }

    /// <summary>
    /// Call immediately after a successful patch apply to open the watchdog window.
    /// </summary>
    public static WatchdogStateSaveResult Arm(AppConfig config, int? windowHours = null) =>
        UpdateState(config, state =>
        {
            state.PatchAppliedAt = DateTime.UtcNow.ToString("o");
            state.WindowHours = Math.Clamp(windowHours ?? state.WindowHours, 1, 168);
            state.CumulativeEvents = 0;
            state.LastVerdict = WatchdogVerdict.Healthy.ToString();
            state.LastEvaluatedAt = null;
        });

    /// <summary>
    /// Call after an uninstall or a confirmed revert to close the window cleanly.
    /// </summary>
    public static WatchdogStateSaveResult Disarm(AppConfig config) =>
        UpdateState(config, state =>
        {
            state.PatchAppliedAt = null;
            state.CumulativeEvents = 0;
            state.LastVerdict = WatchdogVerdict.Idle.ToString();
            state.LastEvaluatedAt = DateTime.UtcNow.ToString("o");
        });

    /// <summary>
    /// Returns Idle if no patch is armed, Completed if the window expired, Healthy/Warning/Unstable
    /// from proved Event Log evidence, or Unavailable when state/query/persistence cannot be proved.
    /// </summary>
    public static WatchdogReport Evaluate(AppConfig config) =>
        Evaluate(config, CountEvents, DateTime.UtcNow, StateMutexTimeout, StateMutexName, SaveStateCore);

    internal static WatchdogReport Evaluate(
        AppConfig config,
        Func<DateTime, WatchdogEventQueryResult> queryEvents,
        DateTime utcNow,
        TimeSpan mutexTimeout,
        string mutexName,
        Func<AppConfig, WatchdogState, WatchdogStateSaveResult> persistState)
    {
        using var lease = AcquireStateMutex(mutexTimeout, mutexName);
        if (!lease.Held)
        {
            return UnavailableReport(
                lease.Error is null ? "StateBusy" : "StateLockUnavailable",
                lease.Error ?? "Watchdog state is busy in another process; evaluation did not read protected state.");
        }

        var loaded = LoadStateCore(config);
        if (!loaded.Success)
            return UnavailableReport("StateUnavailable", loaded.Summary);

        var state = loaded.State;
        var report = new WatchdogReport { AutoRevertEnabled = state.AutoRevertEnabled };

        if (string.IsNullOrWhiteSpace(state.PatchAppliedAt))
        {
            report.Verdict = WatchdogVerdict.Idle;
            report.Summary = "Watchdog idle — no patch window active.";
            return report;
        }

        if (!DateTime.TryParse(
                state.PatchAppliedAt,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var appliedRaw))
        {
            return UnavailableReport(
                "InvalidWindowTimestamp",
                "Watchdog state contains an invalid patch-window timestamp; it was preserved for recovery instead of being cleared.",
                state.AutoRevertEnabled);
        }

        var applied = appliedRaw.Kind switch
        {
            DateTimeKind.Utc => appliedRaw,
            DateTimeKind.Local => appliedRaw.ToUniversalTime(),
            _ => DateTime.SpecifyKind(appliedRaw, DateTimeKind.Utc)
        };
        report.WindowStart = applied;
        report.WindowEnd = applied + TimeSpan.FromHours(Math.Max(1, state.WindowHours));

        var query = queryEvents(applied);
        if (!query.Success)
        {
            state.LastVerdict = WatchdogVerdict.Unavailable.ToString();
            state.LastEvaluatedAt = utcNow.ToString("o");
            var persisted = persistState(config, state);
            var persistenceDetail = persisted.Success ? string.Empty : " State persistence also failed: " + persisted.Summary;
            return UnavailableReport(
                query.FailureCode ?? "EventLogQueryFailed",
                query.Summary + persistenceDetail,
                state.AutoRevertEnabled);
        }

        report.Counts = query.Counts;
        report.TotalEvents = query.Counts.Sum(c => c.Count);
        report.BugChecks = query.Counts.FirstOrDefault(c => c.Source == "BugCheck" && c.Id == 1001)?.Count ?? 0;

        report.Verdict = DeriveVerdict(
            report.TotalEvents, state.WarnThreshold, state.RevertThreshold,
            windowExpired: utcNow > report.WindowEnd);
        report.ObservedVerdict = report.Verdict;

        report.Summary = BuildSummary(report, state);
        report.Detail = BuildDetail(report, state);

        state.LastVerdict = report.Verdict.ToString();
        state.LastEvaluatedAt = utcNow.ToString("o");
        state.CumulativeEvents = report.TotalEvents;
        var saved = persistState(config, state);
        if (!saved.Success)
        {
            report.Verdict = WatchdogVerdict.Unavailable;
            report.FailureCode = "StatePersistenceFailed";
            report.Summary = $"Watchdog observed {report.ObservedVerdict} from System-log evidence, but the result is unavailable as a durable checkpoint: {saved.Summary}";
            report.Detail = report.Summary + Environment.NewLine + report.Detail;
        }

        return report;
    }

    private static WatchdogReport UnavailableReport(
        string failureCode,
        string summary,
        bool autoRevertEnabled = false) =>
        new()
        {
            Verdict = WatchdogVerdict.Unavailable,
            FailureCode = failureCode,
            Summary = summary,
            Detail = summary,
            AutoRevertEnabled = autoRevertEnabled
        };

    /// <summary>
    /// Pure verdict derivation — the watchdog's core decision, extracted so the full
    /// (window-state × event-count) table is unit-testable. Threshold crossings always
    /// win; under-threshold reads Healthy while the window is open and Completed
    /// ("patch deemed stable") once it expires.
    /// </summary>
    internal static WatchdogVerdict DeriveVerdict(int totalEvents, int warnThreshold, int revertThreshold, bool windowExpired)
    {
        if (totalEvents >= revertThreshold) return WatchdogVerdict.Unstable;
        if (totalEvents >= warnThreshold) return WatchdogVerdict.Warning;
        return windowExpired ? WatchdogVerdict.Completed : WatchdogVerdict.Healthy;
    }

    private const string CommandTimeoutGuidance =
        "command timeout (Storport 129) detected; this usually means the controller stopped responding under load. Consider reverting immediately if it repeats or appears with disk 51/153 events.";

    internal static string BuildSummary(WatchdogReport report, WatchdogState state)
    {
        var summary = report.Verdict switch
        {
            WatchdogVerdict.Unavailable => "Watchdog evidence is unavailable — do not interpret missing counts as healthy.",
            WatchdogVerdict.Unstable => $"Storage instability detected ({report.TotalEvents} events) — auto-revert eligible.",
            WatchdogVerdict.Warning => $"Elevated storage events ({report.TotalEvents}) in post-patch window.",
            WatchdogVerdict.Healthy => $"Stable — {report.TotalEvents} storage events in watchdog window.",
            WatchdogVerdict.Completed => $"Watchdog window completed with {report.TotalEvents} events. Patch considered stable.",
            _ => "Watchdog idle."
        };

        return HasStorportCommandTimeout(report)
            ? $"{summary} {CommandTimeoutGuidance}"
            : summary;
    }

    internal static string BuildDetail(WatchdogReport report, WatchdogState state)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Window: {report.WindowStart:u} → {report.WindowEnd:u}");
        sb.AppendLine($"Warn threshold: {state.WarnThreshold} | Revert threshold: {state.RevertThreshold}");
        sb.AppendLine($"Auto-revert enabled: {state.AutoRevertEnabled}");
        sb.AppendLine();
        foreach (var c in report.Counts.Where(c => c.Count > 0))
        {
            sb.AppendLine($"  [{c.Count}] {c.Source}/{c.Id} — {c.Description} (last: {c.LatestOccurrence:u})");
        }
        if (HasStorportCommandTimeout(report))
        {
            sb.AppendLine();
            sb.AppendLine($"Guidance: {CommandTimeoutGuidance}");
        }
        if (report.TotalEvents == 0) sb.AppendLine("  (no matching events — looking good)");
        return sb.ToString();
    }

    internal static bool HasStorportCommandTimeout(WatchdogReport report) =>
        report.Counts.Any(c => c.Id == 129 && c.Count > 0);

    private static WatchdogStateLoadResult LoadStateCore(AppConfig config)
    {
        var path = StatePath(config);
        var backupPath = path + ".bak";
        if (File.Exists(path) && TryReadState(path, out var primary, out _))
        {
            return new WatchdogStateLoadResult(
                primary!,
                WatchdogStateAccessStatus.Loaded,
                "Loaded validated watchdog state.");
        }

        string? primaryFailure = null;
        if (File.Exists(path))
        {
            TryReadState(path, out _, out var error);
            var evidence = PreserveCorruptCopy(path);
            primaryFailure = $"Primary watchdog state failed validation ({error})." +
                (evidence is null ? " Corrupt evidence could not be copied." : $" Preserved as {Path.GetFileName(evidence)}.");
        }

        if (File.Exists(backupPath) && TryReadState(backupPath, out var backup, out _))
        {
            return new WatchdogStateLoadResult(
                backup!,
                WatchdogStateAccessStatus.Loaded,
                string.Join(" ", new[] { primaryFailure, "Loaded validated watchdog.json.bak." }
                    .Where(value => !string.IsNullOrWhiteSpace(value))));
        }

        if (!File.Exists(path) && !File.Exists(backupPath))
        {
            return new WatchdogStateLoadResult(
                new WatchdogState(),
                WatchdogStateAccessStatus.Missing,
                "No watchdog state exists; the watchdog is not armed.");
        }

        string? backupFailure = null;
        if (File.Exists(backupPath))
        {
            TryReadState(backupPath, out _, out var error);
            var evidence = PreserveCorruptCopy(backupPath);
            backupFailure = $"Watchdog backup failed validation ({error})." +
                (evidence is null ? " Corrupt evidence could not be copied." : $" Preserved as {Path.GetFileName(evidence)}.");
        }

        return new WatchdogStateLoadResult(
            new WatchdogState(),
            WatchdogStateAccessStatus.Unavailable,
            string.Join(" ", new[] { primaryFailure, backupFailure, "No validated watchdog state is available." }
                .Where(value => !string.IsNullOrWhiteSpace(value))));
    }

    private static WatchdogStateSaveResult SaveStateCore(AppConfig config, WatchdogState state)
    {
        var path = StatePath(config);
        var backupPath = path + ".bak";
        var tempPath = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory))
                return new(WatchdogStateAccessStatus.Unavailable, "Watchdog state path has no parent directory.");
            Directory.CreateDirectory(directory);

            var json = JsonSerializer.Serialize(state, JsonOptions);
            WriteDurableText(tempPath, json);
            if (!TryReadState(tempPath, out _, out var stagingError))
            {
                return new(WatchdogStateAccessStatus.Unavailable,
                    "Flushed watchdog staging file failed validation: " + stagingError);
            }

            if (File.Exists(path))
            {
                if (TryReadState(path, out _, out _))
                    File.Replace(tempPath, path, backupPath, ignoreMetadataErrors: true);
                else
                    File.Replace(tempPath, path, UniqueEvidencePath(path), ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, path, overwrite: false);
            }

            if (!EnsureValidBackup(path, backupPath, out var backupError))
                return new(WatchdogStateAccessStatus.Unavailable, backupError);
            if (!TryReadState(path, out _, out var primaryError))
                return new(WatchdogStateAccessStatus.Unavailable, "Published watchdog state failed validation: " + primaryError);

            return new(WatchdogStateAccessStatus.Saved,
                "Watchdog state was validated, flushed, and published atomically with a validated backup.");
        }
        catch (Exception ex)
        {
            return new(WatchdogStateAccessStatus.Unavailable,
                $"Watchdog state persistence failed ({ex.GetType().Name}): {ex.Message}");
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    private static bool EnsureValidBackup(string primaryPath, string backupPath, out string error)
    {
        error = string.Empty;
        if (File.Exists(backupPath) && TryReadState(backupPath, out _, out _))
            return true;

        var tempPath = $"{backupPath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.Copy(primaryPath, tempPath, overwrite: false);
            FlushExistingFile(tempPath);
            if (!TryReadState(tempPath, out _, out error))
            {
                error = "Watchdog backup staging failed validation: " + error;
                return false;
            }

            if (File.Exists(backupPath))
                File.Replace(tempPath, backupPath, UniqueEvidencePath(backupPath), ignoreMetadataErrors: true);
            else
                File.Move(tempPath, backupPath, overwrite: false);
            return true;
        }
        catch (Exception ex)
        {
            error = $"Could not publish a validated watchdog backup ({ex.GetType().Name}): {ex.Message}";
            return false;
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    internal static bool TryReadState(string path, out WatchdogState? state, out string error)
    {
        state = null;
        error = string.Empty;
        try
        {
            var json = File.ReadAllText(path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(json))
            {
                error = "file is empty";
                return false;
            }

            state = JsonSerializer.Deserialize<WatchdogState>(json, JsonOptions);
            if (state is null)
            {
                error = "JSON deserialized to null";
                return false;
            }
            if (state.WindowHours is < 1 or > 168 ||
                state.WarnThreshold < 1 ||
                state.RevertThreshold < state.WarnThreshold ||
                state.CumulativeEvents < 0)
            {
                error = "state values violate watchdog bounds";
                state = null;
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    private static string? PreserveCorruptCopy(string path)
    {
        try
        {
            var evidence = UniqueEvidencePath(path);
            File.Copy(path, evidence, overwrite: false);
            FlushExistingFile(evidence);
            return evidence;
        }
        catch
        {
            return null;
        }
    }

    private static string UniqueEvidencePath(string path)
    {
        var basePath = path + ".corrupt";
        if (!File.Exists(basePath)) return basePath;
        return basePath + "." + DateTime.UtcNow.ToString(
            "yyyyMMdd'T'HHmmssfffffff'Z'",
            System.Globalization.CultureInfo.InvariantCulture) + "." + Guid.NewGuid().ToString("N");
    }

    private static void WriteDurableText(string path, string content)
    {
        using var stream = new FileStream(
            path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16 * 1024, FileOptions.WriteThrough);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
        writer.Write(content);
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    private static void FlushExistingFile(string path)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 16 * 1024, FileOptions.WriteThrough);
        stream.Flush(flushToDisk: true);
    }

    private static StateMutexLease AcquireStateMutex(TimeSpan timeout, string mutexName)
    {
        Mutex? mutex = null;
        try
        {
            mutex = new Mutex(initiallyOwned: false, mutexName);
            var held = false;
            try { held = mutex.WaitOne(timeout); }
            catch (AbandonedMutexException) { held = true; }
            return new StateMutexLease(mutex, held, null);
        }
        catch (Exception ex)
        {
            mutex?.Dispose();
            return new StateMutexLease(null, false,
                $"Watchdog state lock is unavailable ({ex.GetType().Name}): {ex.Message}");
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private sealed class StateMutexLease(Mutex? mutex, bool held, string? error) : IDisposable
    {
        public bool Held { get; } = held;
        public string? Error { get; } = error;

        public void Dispose()
        {
            if (Held) { try { mutex?.ReleaseMutex(); } catch { } }
            mutex?.Dispose();
        }
    }

    // Safety cap on the per-evaluation event loop. Above this many matching records we stop
    // reading — the verdict would already be locked in as Unstable (revert threshold is
    // typically 6, not thousands), and walking the remaining tail only costs memory and IO.
    // Sized so a burst of genuine distress signals still gets full counts in the summary, but
    // an unrelated flood (e.g. a driver re-install spamming disk 153 events) can't wedge us.
    private const int MaxEventsScanned = 10_000;

    private static WatchdogEventQueryResult CountEvents(DateTime since)
    {
        var results = WatchEvents
            .Select(w => new WatchdogEventCount { Source = w.Source, Id = w.Id, Description = w.Description })
            .ToList();

        // Build a single System-channel query covering every (source, id) pair we track.
        // Using one query is dramatically cheaper than eight separate sessions — on a chatty
        // system the event log has hundreds of thousands of entries.
        var sinceXml = since.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ");
        var sourceClauses = string.Join(" or ",
            WatchEvents.Select(w => $"(Provider[@Name='{w.Source}'] and EventID={w.Id})"));
        string xpath = $"*[System[({sourceClauses}) and TimeCreated[@SystemTime >= '{sinceXml}']]]";

        try
        {
            var query = new EventLogQuery("System", PathType.LogName, xpath);
            using var reader = new EventLogReader(query);
            int scanned = 0;
            while (true)
            {
                using var record = reader.ReadEvent();
                if (record is null) break;
                var src = record.ProviderName ?? string.Empty;
                var id = record.Id;
                var ts = record.TimeCreated?.ToUniversalTime();
                foreach (var c in results)
                {
                    if (c.Id == id && string.Equals(c.Source, src, StringComparison.OrdinalIgnoreCase))
                    {
                        c.Count++;
                        if (ts is not null && (c.LatestOccurrence is null || ts > c.LatestOccurrence))
                            c.LatestOccurrence = ts;
                        break;
                    }
                }

                if (++scanned >= MaxEventsScanned) break;
            }

            return new WatchdogEventQueryResult(
                true,
                results,
                $"System Event Log query succeeded ({scanned} matching record(s) scanned).");
        }
        catch (Exception ex)
        {
            return new WatchdogEventQueryResult(
                false,
                results,
                $"System Event Log query failed ({ex.GetType().Name}): {ex.Message}",
                ex is UnauthorizedAccessException ? "EventLogAccessDenied" : "EventLogQueryFailed");
        }
    }

    public static WatchdogEventLogProbeResult ProbeSystemLogReadability()
    {
        try
        {
            var query = new EventLogQuery("System", PathType.LogName, "*[System]")
            {
                ReverseDirection = true
            };
            using var reader = new EventLogReader(query);
            using var record = reader.ReadEvent();
            return new(true, record is null
                ? "System Event Log opened successfully; the channel currently has no records."
                : $"System Event Log live read succeeded ({record.ProviderName ?? "unknown"}/{record.Id}).");
        }
        catch (Exception ex)
        {
            return new(false,
                $"System Event Log live read failed ({ex.GetType().Name}): {ex.Message}",
                ex is UnauthorizedAccessException ? "EventLogAccessDenied" : "EventLogProbeFailed");
        }
    }

    /// <summary>
    /// True when the caller should stage an auto-revert on next boot (the patch is clearly
    /// unstable AND the user opted into auto-revert in config).
    /// </summary>
    public static bool ShouldAutoRevert(AppConfig config, WatchdogReport report)
    {
        _ = config;
        var actionable = report.ObservedVerdict ?? report.Verdict;
        return report.AutoRevertEnabled && actionable == WatchdogVerdict.Unstable;
    }
}
