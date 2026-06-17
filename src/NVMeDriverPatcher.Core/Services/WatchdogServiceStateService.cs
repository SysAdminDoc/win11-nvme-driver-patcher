using System.ServiceProcess;

namespace NVMeDriverPatcher.Services;

public enum WatchdogServiceState
{
    NotInstalled,
    Running,
    Stopped,
    Pending,
    Unknown,
}

/// <summary>
/// Reports the install/run state of the optional real-time watchdog Windows Service
/// (NVMeDriverPatcher.Watchdog.exe). The service is opt-in — installed either by the
/// MSI WatchdogService feature or by running the exe with /install — so "NotInstalled"
/// is the normal state on portable installs.
/// </summary>
public static class WatchdogServiceStateService
{
    // Must match Program.ServiceName in src/NVMeDriverPatcher.Watchdog and the
    // ServiceInstall name in packaging/wix/NVMeDriverPatcher.wxs.
    public const string ServiceName = "NVMeDriverPatcherWatchdog";

    public static WatchdogServiceState Query()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            return sc.Status switch
            {
                ServiceControllerStatus.Running => WatchdogServiceState.Running,
                ServiceControllerStatus.Stopped => WatchdogServiceState.Stopped,
                ServiceControllerStatus.StartPending or ServiceControllerStatus.StopPending
                    or ServiceControllerStatus.ContinuePending or ServiceControllerStatus.PausePending
                    => WatchdogServiceState.Pending,
                _ => WatchdogServiceState.Unknown,
            };
        }
        catch (InvalidOperationException)
        {
            // ServiceController throws InvalidOperationException when no service with
            // that name exists on the machine.
            return WatchdogServiceState.NotInstalled;
        }
        catch
        {
            return WatchdogServiceState.Unknown;
        }
    }

    public static string Describe(WatchdogServiceState state) => state switch
    {
        WatchdogServiceState.Running => "real-time service running",
        WatchdogServiceState.Stopped => "real-time service installed, not running",
        WatchdogServiceState.Pending => "real-time service starting/stopping",
        WatchdogServiceState.NotInstalled => "real-time service not installed",
        _ => "real-time service state unknown",
    };
}
