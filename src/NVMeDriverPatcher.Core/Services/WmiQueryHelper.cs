using System.Management;

namespace NVMeDriverPatcher.Services;

public static class WmiQueryHelper
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    public static ManagementObjectCollection ExecuteWithTimeout(
        ManagementObjectSearcher searcher,
        TimeSpan? timeout = null)
    {
        var options = new System.Management.EnumerationOptions
        {
            Timeout = timeout ?? DefaultTimeout,
            ReturnImmediately = true
        };
        searcher.Options = options;
        return searcher.Get();
    }

    public static ManagementObjectCollection ExecuteWithTimeout(
        string query,
        TimeSpan? timeout = null)
    {
        using var searcher = new ManagementObjectSearcher(query);
        return ExecuteWithTimeout(searcher, timeout);
    }

    public static ManagementObjectCollection ExecuteWithTimeout(
        string scope,
        string query,
        TimeSpan? timeout = null)
    {
        using var searcher = new ManagementObjectSearcher(scope, query);
        return ExecuteWithTimeout(searcher, timeout);
    }
}
