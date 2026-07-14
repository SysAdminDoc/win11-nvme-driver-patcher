using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Watchdog;

// Real-time watchdog companion. Subscribes to the `System` event log for NVMe-stack distress
// signals via EventLogWatcher (push model) instead of the polling model used by the scheduled
// task. On every matching event, increments the in-memory counter; every N minutes, flushes
// state to watchdog.json so the GUI / CLI picks up the latest verdict.
//
// `/install` registers the service under LocalService with a restricted SID.
// `/grant-eventlog` grants that identity read access to the System channel.
// `/uninstall` removes it. Without arguments it runs as an interactive console.
internal static class Program
{
    private const string ServiceName = "NVMeDriverPatcherWatchdog";

    public static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && IsControlVerb(args[0]))
            return HandleServiceControl(args[0]);

        var builder = Host.CreateApplicationBuilder(args);
        builder.Services.AddWindowsService(o => o.ServiceName = ServiceName);
        builder.Services.AddHostedService<WatchdogWorker>();
        builder.Logging.AddEventLog(settings =>
        {
            settings.SourceName = EventLogRegistrationService.SourceName;
        });

        var host = builder.Build();
        try
        {
            await host.RunAsync();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Watchdog host aborted: {ex.Message}");
            return 1;
        }
    }

    private static bool IsControlVerb(string arg) =>
        arg.Equals("/install", StringComparison.OrdinalIgnoreCase) ||
        arg.Equals("/uninstall", StringComparison.OrdinalIgnoreCase) ||
        arg.Equals("/grant-eventlog", StringComparison.OrdinalIgnoreCase) ||
        arg.Equals("/grant-runtime-access", StringComparison.OrdinalIgnoreCase) ||
        arg.Equals("--install", StringComparison.OrdinalIgnoreCase) ||
        arg.Equals("--uninstall", StringComparison.OrdinalIgnoreCase) ||
        arg.Equals("--grant-eventlog", StringComparison.OrdinalIgnoreCase) ||
        arg.Equals("--grant-runtime-access", StringComparison.OrdinalIgnoreCase);

    private static int HandleServiceControl(string verb)
    {
        if (verb.Contains("grant-runtime-access", StringComparison.OrdinalIgnoreCase))
        {
            var stateAccess = GrantStateDirectoryAccess();
            return stateAccess == 0 ? GrantEventLogAccess(warnOnly: false) : stateAccess;
        }
        if (verb.Contains("grant-eventlog", StringComparison.OrdinalIgnoreCase))
            return GrantEventLogAccess(warnOnly: false);

        bool install = verb.EndsWith("install", StringComparison.OrdinalIgnoreCase) && !verb.Contains("uninstall");
        string exe = Environment.ProcessPath ?? "NVMeDriverPatcher.Watchdog.exe";
        if (install)
        {
            // Pass the exe path RAW: ProcessStartInfo.ArgumentList quotes each token itself, so
            // manually wrapping it in quotes would be double-escaped (\"C:\path\") and register a
            // broken ImagePath for any install dir with spaces (e.g. Program Files).
            int rc = RunSc("create", ServiceName, "binpath=", exe, "start=", "auto",
                "obj=", "NT AUTHORITY\\LocalService", "DisplayName=", "NVMe Driver Patcher Watchdog");
            if (rc != 0) return rc;

            // Both installer routes have one fail-closed service contract: restricted SID,
            // minimum token privileges, restart on first/second failure, never reboot the host.
            var configurationSteps = new Func<int>[]
            {
                () => RunSc("sidtype", ServiceName, "restricted"),
                () => RunSc("privs", ServiceName, "SeChangeNotifyPrivilege"),
                () => RunSc("failure", ServiceName, "reset=", "86400", "actions=",
                    "restart/10000/restart/10000/none/0"),
                () => RunSc("failureflag", ServiceName, "1"),
                GrantStateDirectoryAccess,
                () => GrantEventLogAccess(warnOnly: false)
            };
            foreach (var step in configurationSteps)
            {
                rc = step();
                if (rc == 0) continue;
                Console.Error.WriteLine("Watchdog service configuration failed; removing the partial service registration.");
                RunSc("delete", ServiceName);
                return rc;
            }
            return 0;
        }
        return RunSc("delete", ServiceName);
    }

    private static int GrantStateDirectoryAccess()
    {
        var workingDir = AppConfig.GetWorkingDir();
        var access = PrivilegedStateSecurityService.EnsureRuntimeTree();
        if (!access.Success)
        {
            Console.Error.WriteLine(access.Summary);
            return 1;
        }
        var watchdogAccess = PrivilegedStateSecurityService.EnsureForWatchdog(workingDir);
        if (!watchdogAccess.Success)
        {
            Console.Error.WriteLine(watchdogAccess.Summary);
            return 1;
        }
        Console.WriteLine($"Watchdog access is restricted to '{watchdogAccess.Directory}'.");
        return 0;
    }

    private static int GrantEventLogAccess(bool warnOnly)
    {
        var result = EventLogChannelAclService.EnsureSystemLogLocalServiceReadAccess();
        if (result.Success)
        {
            Console.WriteLine(result.Summary);
            return 0;
        }

        var message = $"{result.Summary} {result.Error}";
        if (warnOnly)
        {
            Console.Error.WriteLine($"Warning: {message}");
            return 0;
        }

        Console.Error.WriteLine(message);
        return 1;
    }

    private static int RunSc(params string[] args)
        => RunProcess("sc.exe", args);

    private static int RunProcess(string executable, params string[] args)
    {
        var psi = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi);
        if (proc is null) { Console.Error.WriteLine($"{executable} did not start."); return 1; }

        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        if (!proc.WaitForExit(30_000))
        {
            try { proc.Kill(true); } catch { }
            Console.Error.WriteLine($"{executable} timed out after 30s.");
            return 1;
        }

        string stdout = string.Empty, stderr = string.Empty;
        try { stdout = stdoutTask.GetAwaiter().GetResult(); } catch { }
        try { stderr = stderrTask.GetAwaiter().GetResult(); } catch { }
        if (!string.IsNullOrEmpty(stdout)) Console.WriteLine(stdout);
        if (proc.ExitCode != 0 && !string.IsNullOrEmpty(stderr)) Console.Error.WriteLine(stderr);
        return proc.ExitCode;
    }
}

internal sealed class WatchdogWorker : BackgroundService
{
    private readonly ILogger<WatchdogWorker> _logger;
    private EventLogWatcher? _watcher;
    private int _eventsSinceFlush;
    private readonly object _lock = new();
    private readonly TimeSpan _flushInterval = TimeSpan.FromMinutes(5);

    public WatchdogWorker(ILogger<WatchdogWorker> logger) { _logger = logger; }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var systemLogProbe = EventLogWatchdogService.ProbeSystemLogReadability();
        if (!systemLogProbe.Success)
        {
            _logger.LogCritical("System Event Log readiness probe failed: {code} {summary}",
                systemLogProbe.FailureCode, systemLogProbe.Summary);
            throw new InvalidOperationException(systemLogProbe.Summary);
        }
        _logger.LogInformation("System Event Log readiness proved: {summary}", systemLogProbe.Summary);

        try
        {
            var providerClause = "(Provider[@Name='nvmedisk'] or Provider[@Name='stornvme'] or " +
                                 "Provider[@Name='storport'] or Provider[@Name='storahci'] or " +
                                 "Provider[@Name='disk'] or Provider[@Name='BugCheck'] or " +
                                 "Provider[@Name='Microsoft-Windows-Kernel-Power'])";
            var query = new EventLogQuery("System", PathType.LogName,
                $"*[System[{providerClause}]]");
            _watcher = new EventLogWatcher(query) { Enabled = false };
            _watcher.EventRecordWritten += OnRecord;
            _watcher.Enabled = true;
            _logger.LogInformation("Watchdog subscribed to System event log (real-time).");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not subscribe to event log — falling back to poll-only operation.");
        }

        return RunFlushLoop(stoppingToken);
    }

    private void OnRecord(object? sender, EventRecordWrittenEventArgs e)
    {
        if (e.EventRecord is null) return;
        try
        {
            lock (_lock) _eventsSinceFlush++;
            _logger.LogDebug("Observed {provider}/{id}", e.EventRecord.ProviderName, e.EventRecord.Id);
        }
        finally
        {
            try { e.EventRecord.Dispose(); } catch { }
        }
    }

    private async Task RunFlushLoop(CancellationToken stoppingToken)
    {
        var consecutiveFailures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                FlushOnce();
                consecutiveFailures = 0;
            }
            catch (Exception ex)
            {
                consecutiveFailures++;
                _logger.LogError(ex, "Watchdog flush failed ({count}/3).", consecutiveFailures);
                if (consecutiveFailures >= 3)
                {
                    _logger.LogCritical("Watchdog evidence remained unavailable; terminating so SCM recovery can restart the service.");
                    throw;
                }
            }
            var delay = consecutiveFailures == 0 ? _flushInterval : TimeSpan.FromSeconds(30);
            try { await Task.Delay(delay, stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    private void FlushOnce()
    {
        // Re-run the polled Evaluate path so the JSON state matches what the rest of the app
        // sees. The in-memory counter is authoritative for "something happened since last
        // flush" — we log it and let Evaluate compute the full verdict from the event log.
        int observed;
        lock (_lock) { observed = _eventsSinceFlush; }
        var config = ConfigService.Load();
        var report = EventLogWatchdogService.Evaluate(config);
        if (!report.DataAvailable)
            throw new InvalidOperationException($"{report.FailureCode}: {report.Summary}");
        lock (_lock) { _eventsSinceFlush = Math.Max(0, _eventsSinceFlush - observed); }
        _logger.LogInformation("Watchdog flush: {observed} events observed since last flush; verdict={verdict} total={total}",
            observed, report.Verdict, report.TotalEvents);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        try { if (_watcher is not null) { _watcher.Enabled = false; _watcher.Dispose(); } } catch { }
        return base.StopAsync(cancellationToken);
    }
}
