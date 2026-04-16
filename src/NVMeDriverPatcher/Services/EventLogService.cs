using System.Diagnostics;
using NVMeDriverPatcher.Models;

namespace NVMeDriverPatcher.Services;

public static class EventLogService
{
    private static volatile bool _initialized;
    private static volatile bool _enabled = true;

    public static void Initialize(bool enabled)
    {
        _enabled = enabled;
        if (!_enabled) return;

        try
        {
            if (!EventLog.SourceExists(AppConfig.EventLogSourceName))
                EventLog.CreateEventSource(AppConfig.EventLogSourceName, "Application");
            _initialized = true;
        }
        catch
        {
            // SourceExists/CreateEventSource require admin — don't permanently disable,
            // just mark not initialized. Write() will try WriteEntry directly which
            // succeeds if the source was previously registered.
            _initialized = false;
        }
    }

    public static void Write(string message, EventLogEntryType entryType = EventLogEntryType.Information, int eventId = 1000)
    {
        if (!_enabled) return;
        try
        {
            EventLog.WriteEntry(AppConfig.EventLogSourceName, message, entryType, eventId);
        }
        catch { /* Event log write best-effort */ }
    }
}
