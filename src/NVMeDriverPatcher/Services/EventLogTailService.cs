using System.Diagnostics.Eventing.Reader;

namespace NVMeDriverPatcher.Services;

public class EventLogTailRecord
{
    public DateTime TimestampUtc { get; set; }
    public string Provider { get; set; } = string.Empty;
    public int EventId { get; set; }
    public string Level { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

// Live tail of the `System` event channel filtered to NVMe-stack providers (nvmedisk,
// stornvme, storport, disk, partmgr, Kernel-Power). Complements the watchdog: watchdog
// counts summarize; the tail lets the user watch what's happening right now.
public static class EventLogTailService
{
    private static readonly string[] ProvidersOfInterest =
    {
        "nvmedisk", "stornvme", "storport", "storahci", "disk", "partmgr", "volmgr",
        "Microsoft-Windows-Kernel-Power"
    };

    public static List<EventLogTailRecord> Recent(int minutes = 60, int maxRecords = 100)
    {
        var results = new List<EventLogTailRecord>();
        var since = DateTime.UtcNow - TimeSpan.FromMinutes(Math.Max(5, minutes));
        var sinceXml = since.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ");

        var providerClauses = string.Join(" or ",
            ProvidersOfInterest.Select(p => $"Provider[@Name='{p}']"));
        string xpath = $"*[System[({providerClauses}) and TimeCreated[@SystemTime >= '{sinceXml}']]]";

        try
        {
            var query = new EventLogQuery("System", PathType.LogName, xpath);
            using var reader = new EventLogReader(query);
            EventRecord? record;
            while ((record = SafeRead(reader)) is not null && results.Count < maxRecords)
            {
                try
                {
                    results.Add(new EventLogTailRecord
                    {
                        TimestampUtc = record.TimeCreated?.ToUniversalTime() ?? DateTime.UtcNow,
                        Provider = record.ProviderName ?? string.Empty,
                        EventId = record.Id,
                        Level = record.LevelDisplayName ?? string.Empty,
                        Message = SafeMessage(record)
                    });
                }
                finally
                {
                    try { record.Dispose(); } catch { }
                }
            }
        }
        catch { /* channel denied — return what we have */ }

        return results.OrderByDescending(r => r.TimestampUtc).ToList();
    }

    private static EventRecord? SafeRead(EventLogReader reader)
    {
        try { return reader.ReadEvent(); }
        catch { return null; }
    }

    private static string SafeMessage(EventRecord record)
    {
        try
        {
            var msg = record.FormatDescription();
            if (string.IsNullOrWhiteSpace(msg)) return string.Empty;
            // Collapse multi-line messages onto a single line for tail display.
            var collapsed = msg.Replace("\r", " ").Replace("\n", " ").Trim();
            return collapsed.Length > 240 ? collapsed[..240] + "…" : collapsed;
        }
        catch { return string.Empty; }
    }
}
