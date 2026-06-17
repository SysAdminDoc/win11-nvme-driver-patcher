using System.Diagnostics.Eventing.Reader;
using System.Text.RegularExpressions;
using NVMeDriverPatcher.Models;

namespace NVMeDriverPatcher.Services;

public static class CodeIntegrityEventService
{
    public const string LogName = "Microsoft-Windows-CodeIntegrity/Operational";

    private static readonly TimeSpan DefaultLookback = TimeSpan.FromDays(30);
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);
    private static readonly Regex RxWhitespace = new(@"\s+", RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex RxUserPath = new(@"\b([A-Za-z]:\\Users\\)([^\\\r\n]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexTimeout);

    private static readonly KnownBlockedDriver[] KnownBackupDrivers =
    [
        new(
            "psmounterex.sys",
            "Backup image-mount driver",
            ["Macrium Reflect", "Acronis", "UrBackup", "NinjaOne", "Paragon", "Veeam"]),
        new(
            "psmounter.sys",
            "Backup image-mount driver",
            ["Macrium Reflect", "Acronis", "UrBackup", "NinjaOne", "Paragon", "Veeam"])
    ];

    public static List<CodeIntegrityBlockedDriverEvent> RecentBackupDriverBlocks(
        TimeSpan? lookback = null,
        int maxRecords = 20)
    {
        var results = new List<CodeIntegrityBlockedDriverEvent>();
        var since = DateTime.UtcNow - (lookback ?? DefaultLookback);
        var sinceXml = since.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ");
        var xpath = $"*[System[(EventID=3076 or EventID=3077) and TimeCreated[@SystemTime >= '{sinceXml}']]]";

        try
        {
            var query = new EventLogQuery(LogName, PathType.LogName, xpath)
            {
                ReverseDirection = true
            };
            using var reader = new EventLogReader(query);
            EventRecord? record;
            while ((record = SafeRead(reader)) is not null && results.Count < maxRecords)
            {
                try
                {
                    var parsed = TryCreateBackupDriverEvent(
                        record.Id,
                        record.TimeCreated,
                        SafeEventText(record));
                    if (parsed is not null)
                        results.Add(parsed);
                }
                finally
                {
                    try { record.Dispose(); } catch { }
                }
            }
        }
        catch
        {
            // CodeIntegrity/Operational can be disabled or unreadable on hardened systems.
            // Preflight should degrade to "no evidence found", not fail the whole run.
        }

        return results
            .GroupBy(e => $"{e.EventId}|{e.DriverFile}|{e.TimestampUtc:o}|{e.Evidence}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderByDescending(e => e.TimestampUtc)
            .ToList();
    }

    internal static CodeIntegrityBlockedDriverEvent? TryCreateBackupDriverEvent(
        int eventId,
        DateTime? timeCreated,
        string? eventText)
    {
        if (eventId is not (3076 or 3077) || string.IsNullOrWhiteSpace(eventText))
            return null;

        var match = KnownBackupDrivers.FirstOrDefault(driver =>
            eventText.Contains(driver.FileName, StringComparison.OrdinalIgnoreCase));
        if (match is null)
            return null;

        return new CodeIntegrityBlockedDriverEvent
        {
            TimestampUtc = (timeCreated ?? DateTime.UtcNow).ToUniversalTime(),
            EventId = eventId,
            Mode = eventId == 3077 ? "Enforced block" : "Audit block",
            DriverFile = match.FileName,
            DriverDescription = match.Description,
            AffectedProducts = string.Join(", ", match.Products),
            Evidence = BuildEvidence(eventText, match.FileName)
        };
    }

    internal static string DescribeForPreflight(IReadOnlyCollection<CodeIntegrityBlockedDriverEvent> events)
    {
        if (events.Count == 0)
            return "No recent backup driver blocklist evidence";

        var drivers = events.Select(e => e.DriverFile).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var products = events.SelectMany(e => e.AffectedProducts.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToList();

        return $"{events.Count} recent CodeIntegrity backup-driver block event(s): " +
               $"{string.Join(", ", drivers)}; affected products include {string.Join(", ", products)}";
    }

    private static EventRecord? SafeRead(EventLogReader reader)
    {
        try { return reader.ReadEvent(); }
        catch { return null; }
    }

    private static string SafeEventText(EventRecord record)
    {
        try
        {
            var message = record.FormatDescription();
            if (!string.IsNullOrWhiteSpace(message))
                return message;
        }
        catch { }

        try { return record.ToXml(); }
        catch { return string.Empty; }
    }

    private static string BuildEvidence(string eventText, string driverFile)
    {
        var collapsed = CollapseAndRedact(eventText);
        var index = collapsed.IndexOf(driverFile, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
            return Truncate(collapsed, 260);

        var start = Math.Max(0, index - 90);
        var length = Math.Min(collapsed.Length - start, driverFile.Length + 190);
        return Truncate(collapsed.Substring(start, length).Trim(), 260);
    }

    private static string CollapseAndRedact(string text)
    {
        try { text = RxUserPath.Replace(text, "${1}[redacted]"); } catch { }
        try { text = RxWhitespace.Replace(text, " "); } catch { text = text.Replace("\r", " ").Replace("\n", " "); }
        return text.Trim();
    }

    private static string Truncate(string text, int maxLength)
    {
        if (text.Length <= maxLength)
            return text;

        return text[..Math.Max(0, maxLength - 3)] + "...";
    }

    private sealed record KnownBlockedDriver(
        string FileName,
        string Description,
        string[] Products);
}
