using System.Diagnostics;
using NVMeDriverPatcher.Models;

namespace NVMeDriverPatcher.Services;

public static class EventLogService
{
    // Hard limit per Windows API: a single event log message must be <= 31839 chars.
    // We use a safety margin in case of internal expansion.
    private const int MaxMessageLength = 30000;

    /// <summary>
    /// Formats a UTC timestamp for an event-log XPath <c>TimeCreated[@SystemTime &gt;= '...']</c>
    /// predicate. Must be invariant: in a custom .NET format string ':' and '.' are the CULTURE's
    /// time and decimal separators, not literals, so a hand-rolled "yyyy-MM-ddTHH:mm:ss.fffffffZ"
    /// emits e.g. "2026-08-11T14.30.00,0000000Z" on a Finnish or German system. That is not a
    /// valid SystemTime, so the query either throws — leaving the watchdog permanently Unavailable
    /// and auto-revert dead — or matches nothing and reports a falsely healthy patch.
    /// </summary>
    internal static string FormatXPathTimestamp(DateTime utc) =>
        utc.ToString("o", System.Globalization.CultureInfo.InvariantCulture);

    private static volatile bool _enabled = true;
    private static volatile bool _sourceRegistrationAttempted;

    public static void Initialize(bool enabled)
    {
        _enabled = enabled;
        if (!_enabled || _sourceRegistrationAttempted) return;

        try
        {
            // SourceExists/CreateEventSource require admin. If they fail, Write() will still
            // try WriteEntry directly — which succeeds when the source was previously registered
            // by a past run, even if the current process can't re-register it.
            if (!EventLog.SourceExists(AppConfig.EventLogSourceName))
                EventLog.CreateEventSource(AppConfig.EventLogSourceName, "Application");
        }
        catch
        {
            // Best-effort — Write() is also wrapped in try/catch.
        }
        finally
        {
            _sourceRegistrationAttempted = true;
        }
    }

    public static void Write(string message, EventLogEntryType entryType = EventLogEntryType.Information, int eventId = 1000)
    {
        if (!_enabled) return;
        if (string.IsNullOrEmpty(message)) return;

        // Guard the OS limit. Truncate-with-marker is more useful than throwing & swallowing.
        if (message.Length > MaxMessageLength)
            message = TruncatePreservingSurrogates(message, MaxMessageLength) + "... [truncated]";

        try
        {
            EventLog.WriteEntry(AppConfig.EventLogSourceName, message, entryType, eventId);
        }
        catch { /* Event log write best-effort */ }
    }

    // A raw Substring(0, N) can slice a UTF-16 surrogate pair in half — if index N-1 is a
    // high surrogate and index N would have been the low surrogate, the result is an
    // invalid UTF-16 code unit sequence that Event Log may reject or mangle. This helper
    // drops the stray high surrogate so the truncated string remains well-formed even
    // when the underlying message contains emoji, CJK extension-plane characters, or
    // other supplementary-plane codepoints. Internal so EventLogServiceTests can exercise
    // it directly without pulling in the actual Windows Event Log.
    internal static string TruncatePreservingSurrogates(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength) return value;
        int cutoff = maxLength;
        if (char.IsHighSurrogate(value[cutoff - 1])) cutoff--;
        return value.Substring(0, cutoff);
    }
}
