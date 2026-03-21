using System.Diagnostics;
using NVMeDriverPatcher.Models;

namespace NVMeDriverPatcher.Services;

public static class EventLogService
{
    private static bool _initialized;
    private static bool _enabled = true;

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
            _enabled = false;
        }
    }

    public static void Write(string message, EventLogEntryType entryType = EventLogEntryType.Information, int eventId = 1000)
    {
        if (!_enabled || !_initialized) return;
        try
        {
            EventLog.WriteEntry(AppConfig.EventLogSourceName, message, entryType, eventId);
        }
        catch { /* Event log write best-effort */ }
    }
}
