using NVMeDriverPatcher.Models;

namespace NVMeDriverPatcher.Services;

public class AutoRevertOutcome
{
    public bool Executed { get; set; }
    public bool Success { get; set; }
    public string Summary { get; set; } = string.Empty;
    public WatchdogReport? TriggeringReport { get; set; }
}

// Runs once per startup. If the watchdog is Unstable AND the user opted into auto-revert,
// stages an uninstall + disarms the watchdog + writes an Event Log entry. Idempotent — if
// the patch is already gone, this is a no-op.
//
// Called from App.OnStartup (GUI) and Program.Main (CLI) so the watchdog verdict drives
// recovery even when the user never opens the app post-crash loop.
public static class AutoRevertService
{
    public static AutoRevertOutcome MaybeRun(AppConfig config, Action<string>? log = null)
    {
        var outcome = new AutoRevertOutcome();
        try
        {
            var report = EventLogWatchdogService.Evaluate(config);
            outcome.TriggeringReport = report;

            if (!EventLogWatchdogService.ShouldAutoRevert(config, report))
            {
                outcome.Summary = $"Auto-revert not eligible (verdict: {report.Verdict}).";
                return outcome;
            }

            // v4.6: honor the maintenance window. Eligible but outside the window? Defer until
            // the next run — prevents yanking the driver mid-workday when the user is on a
            // Teams call, at the cost of letting an unstable patch run until the window opens.
            var window = MaintenanceWindowService.Load(config);
            if (window.Enabled && !MaintenanceWindowService.IsInWindow(window))
            {
                outcome.Summary = $"Auto-revert deferred — outside maintenance window ({MaintenanceWindowService.Summarize(window)}).";
                return outcome;
            }

            log?.Invoke("[AUTO-REVERT] Watchdog verdict Unstable — initiating automatic patch removal.");
            EventLogService.Write(
                $"NVMe Driver Patcher auto-revert triggered — {report.TotalEvents} storage-stack events in watchdog window.",
                System.Diagnostics.EventLogEntryType.Warning, 3010);

            outcome.Executed = true;
            var nativeStatus = DriveService.TestNativeNVMeActive();
            var bypassStatus = DriveService.GetBypassIOStatus();
            var result = PatchService.Uninstall(config, nativeStatus, bypassStatus, log);
            outcome.Success = result.Success;
            outcome.Summary = result.Success
                ? $"Auto-revert completed ({result.AppliedCount} component(s) removed). Restart to finalize."
                : "Auto-revert failed to complete. Use the Recovery Kit from WinRE.";

            if (result.Success)
            {
                EventLogWatchdogService.Disarm(config);
                config.LastVerificationResult = "AutoReverted";
                ConfigService.Save(config);
            }
        }
        catch (Exception ex)
        {
            outcome.Summary = $"Auto-revert aborted: {ex.GetType().Name}: {ex.Message}";
        }
        return outcome;
    }
}
