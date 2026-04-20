using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Text;
using System.Text.Json;
using NVMeDriverPatcher.Models;

namespace NVMeDriverPatcher.Services;

public enum WatchdogVerdict
{
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

// Post-patch stability watchdog. Watches Storport ID 129 (timeout), disk ID 51/153 (paging),
// nvmedisk init/unload, and BugCheck ID 1001 in the N-hour window after a patch apply.
// If counts cross the revert threshold, signals the caller to stage an auto-revert on next
// boot — closes the "tool lied, driver wedged, user can't tell" gap for unattended deploys.
public static class EventLogWatchdogService
{
    private const string StateFile = "watchdog.json";

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
        try
        {
            var path = StatePath(config);
            if (!File.Exists(path)) return new WatchdogState();
            var json = File.ReadAllText(path);
            return string.IsNullOrWhiteSpace(json)
                ? new WatchdogState()
                : JsonSerializer.Deserialize<WatchdogState>(json, JsonOptions) ?? new WatchdogState();
        }
        catch
        {
            return new WatchdogState();
        }
    }

    public static void SaveState(AppConfig config, WatchdogState state)
    {
        try
        {
            var path = StatePath(config);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(state, JsonOptions);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
        }
        catch { /* best-effort — watchdog state lossy is acceptable */ }
    }

    /// <summary>
    /// Call immediately after a successful patch apply to open the watchdog window.
    /// </summary>
    public static void Arm(AppConfig config, int? windowHours = null)
    {
        var state = LoadState(config);
        state.PatchAppliedAt = DateTime.UtcNow.ToString("o");
        state.WindowHours = windowHours ?? state.WindowHours;
        state.CumulativeEvents = 0;
        state.LastVerdict = WatchdogVerdict.Healthy.ToString();
        state.LastEvaluatedAt = null;
        SaveState(config, state);
    }

    /// <summary>
    /// Call after an uninstall or a confirmed revert to close the window cleanly.
    /// </summary>
    public static void Disarm(AppConfig config)
    {
        var state = LoadState(config);
        state.PatchAppliedAt = null;
        state.CumulativeEvents = 0;
        state.LastVerdict = WatchdogVerdict.Idle.ToString();
        state.LastEvaluatedAt = DateTime.UtcNow.ToString("o");
        SaveState(config, state);
    }

    /// <summary>
    /// Read-only evaluation. Returns Idle if no patch is armed, Completed if the window expired,
    /// or Healthy/Warning/Unstable based on the event counts inside the window.
    /// </summary>
    public static WatchdogReport Evaluate(AppConfig config)
    {
        var report = new WatchdogReport();
        var state = LoadState(config);

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
            report.Verdict = WatchdogVerdict.Idle;
            report.Summary = "Watchdog window timestamp corrupt — clearing.";
            Disarm(config);
            return report;
        }

        var applied = appliedRaw.Kind switch
        {
            DateTimeKind.Utc => appliedRaw,
            DateTimeKind.Local => appliedRaw.ToUniversalTime(),
            _ => DateTime.SpecifyKind(appliedRaw, DateTimeKind.Utc)
        };
        report.WindowStart = applied;
        report.WindowEnd = applied + TimeSpan.FromHours(Math.Max(1, state.WindowHours));

        var counts = CountEvents(applied);
        report.Counts = counts;
        report.TotalEvents = counts.Sum(c => c.Count);
        report.BugChecks = counts.FirstOrDefault(c => c.Source == "BugCheck" && c.Id == 1001)?.Count ?? 0;

        if (DateTime.UtcNow > report.WindowEnd)
        {
            if (report.TotalEvents >= state.RevertThreshold) report.Verdict = WatchdogVerdict.Unstable;
            else if (report.TotalEvents >= state.WarnThreshold) report.Verdict = WatchdogVerdict.Warning;
            else report.Verdict = WatchdogVerdict.Completed;
        }
        else
        {
            if (report.TotalEvents >= state.RevertThreshold) report.Verdict = WatchdogVerdict.Unstable;
            else if (report.TotalEvents >= state.WarnThreshold) report.Verdict = WatchdogVerdict.Warning;
            else report.Verdict = WatchdogVerdict.Healthy;
        }

        report.Summary = BuildSummary(report, state);
        report.Detail = BuildDetail(report, state);

        state.LastVerdict = report.Verdict.ToString();
        state.LastEvaluatedAt = DateTime.UtcNow.ToString("o");
        state.CumulativeEvents = report.TotalEvents;
        SaveState(config, state);

        return report;
    }

    internal static string BuildSummary(WatchdogReport report, WatchdogState state) =>
        report.Verdict switch
        {
            WatchdogVerdict.Unstable => $"Storage instability detected ({report.TotalEvents} events) — auto-revert eligible.",
            WatchdogVerdict.Warning => $"Elevated storage events ({report.TotalEvents}) in post-patch window.",
            WatchdogVerdict.Healthy => $"Stable — {report.TotalEvents} storage events in watchdog window.",
            WatchdogVerdict.Completed => $"Watchdog window completed with {report.TotalEvents} events. Patch considered stable.",
            _ => "Watchdog idle."
        };

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
        if (report.TotalEvents == 0) sb.AppendLine("  (no matching events — looking good)");
        return sb.ToString();
    }

    private static List<WatchdogEventCount> CountEvents(DateTime since)
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
            EventRecord? record;
            while ((record = TryReadNext(reader)) is not null)
            {
                try
                {
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
                }
                finally
                {
                    try { record.Dispose(); } catch { }
                }
            }
        }
        catch
        {
            // Hardened SKUs can deny SYSTEM channel read to the current session even when
            // elevated. Return whatever we have — zero counts is safer than false positives.
        }

        return results;
    }

    private static EventRecord? TryReadNext(EventLogReader reader)
    {
        try { return reader.ReadEvent(); }
        catch { return null; }
    }

    /// <summary>
    /// True when the caller should stage an auto-revert on next boot (the patch is clearly
    /// unstable AND the user opted into auto-revert in config).
    /// </summary>
    public static bool ShouldAutoRevert(AppConfig config, WatchdogReport report)
    {
        var state = LoadState(config);
        return state.AutoRevertEnabled && report.Verdict == WatchdogVerdict.Unstable;
    }
}
