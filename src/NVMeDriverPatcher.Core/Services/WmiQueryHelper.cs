using System.Management;

namespace NVMeDriverPatcher.Services;

public static class WmiQueryHelper
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    public static ManagementObjectCollection ExecuteWithTimeout(
        ManagementObjectSearcher searcher,
        TimeSpan? timeout = null)
    {
        searcher.Options = CreateEnumerationOptions(timeout);
        return searcher.Get();
    }

    internal static System.Management.EnumerationOptions CreateEnumerationOptions(TimeSpan? timeout = null) => new()
    {
        Timeout = timeout ?? DefaultTimeout,
        ReturnImmediately = true
    };

    // NOTE: string-query overloads were removed — they disposed the searcher (`using`) before the
    // caller enumerated the lazily-evaluated ManagementObjectCollection (use-after-dispose). All
    // callers pass a live ManagementObjectSearcher they own and dispose; keep it that way.
}
