using System.Diagnostics;
using NVMeDriverPatcher.Models;

namespace NVMeDriverPatcher.Services;

public static class EventLogService
{
    // Hard limit per Windows API: a single event log message must be <= 31839 chars.
    // We use a safety margin in case of internal expansion.
    private const int MaxMessageLength = 30000;

    private static volatile bool _enabled = true;

    public static void Initialize(bool enabled)
    {
        _enabled = enabled;
        if (!_enabled) return;

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
    }

    public static void Write(string message, EventLogEntryType entryType = EventLogEntryType.Information, int eventId = 1000)
    {
        if (!_enabled) return;
        if (string.IsNullOrEmpty(message)) return;

        // Guard the OS limit. Truncate-with-marker is more useful than throwing & swallowing.
        if (message.Length > MaxMessageLength)
            message = message.Substring(0, MaxMessageLength) + "... [truncated]";

        try
        {
            EventLog.WriteEntry(AppConfig.EventLogSourceName, message, entryType, eventId);
        }
        catch { /* Event log write best-effort */ }
    }
}
