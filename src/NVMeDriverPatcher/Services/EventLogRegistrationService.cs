using System.Diagnostics;

namespace NVMeDriverPatcher.Services;

// Registers the "NVMe Driver Patcher" event-log source once per install so EventLogService.Write
// doesn't silently fall back to the generic "Application Error" source. SourceExists + CreateEventSource
// both require admin privileges — admin is already a prerequisite of the app, so this is safe to call
// on every startup (idempotent).
public static class EventLogRegistrationService
{
    public const string SourceName = "NVMe Driver Patcher";
    public const string LogName = "Application";

    public static bool EnsureRegistered(Action<string>? log = null)
    {
        try
        {
            if (EventLog.SourceExists(SourceName)) return true;
            EventLog.CreateEventSource(new EventSourceCreationData(SourceName, LogName));
            log?.Invoke($"[OK] Registered Event Log source '{SourceName}' under '{LogName}'.");
            return true;
        }
        catch (System.Security.SecurityException)
        {
            // Not elevated — the creation would need admin. Silent no-op; EventLogService will
            // fall back to the default source and still write, just without our branded source.
            return false;
        }
        catch (Exception ex)
        {
            log?.Invoke($"[WARN] Could not register Event Log source: {ex.Message}");
            return false;
        }
    }
}
