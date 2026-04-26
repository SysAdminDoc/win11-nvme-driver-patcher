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
// `/install` registers the service under SYSTEM. `/uninstall` removes it. Without arguments
// it runs as an interactive console — useful for debugging before promoting to a service.
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
        arg.Equals("--install", StringComparison.OrdinalIgnoreCase) ||
        arg.Equals("--uninstall", StringComparison.OrdinalIgnoreCase);

    private static int HandleServiceControl(string verb)
    {
        bool install = verb.EndsWith("install", StringComparison.OrdinalIgnoreCase) && !verb.Contains("uninstall");
        string exe = Environment.ProcessPath ?? "NVMeDriverPatcher.Watchdog.exe";
        var args = install
            ? new[] { "create", ServiceName, "binpath=", $"\"{exe}\"", "start=", "auto", "obj=", "LocalSystem", "DisplayName=", "NVMe Driver Patcher Watchdog" }
            : new[] { "delete", ServiceName };
        var psi = new ProcessStartInfo("sc.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi);
        if (proc is null) { Console.Error.WriteLine("sc.exe did not start."); return 1; }

        // Start the pipe reads BEFORE waiting for exit — a chatty sc.exe error (localized
        // failure detail, for example) can fill the stdout/stderr pipe buffers and deadlock
        // the child while we sit in WaitForExit. Draining async keeps both sides moving.
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        if (!proc.WaitForExit(30_000))
        {
            try { proc.Kill(true); } catch { }
            Console.Error.WriteLine("sc.exe timed out after 30s.");
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
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                FlushOnce();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Watchdog flush failed — will retry next interval.");
            }
            try { await Task.Delay(_flushInterval, stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    private void FlushOnce()
    {
        // Re-run the polled Evaluate path so the JSON state matches what the rest of the app
        // sees. The in-memory counter is authoritative for "something happened since last
        // flush" — we log it and let Evaluate compute the full verdict from the event log.
        int observed;
        lock (_lock) { observed = _eventsSinceFlush; _eventsSinceFlush = 0; }
        var config = ConfigService.Load();
        var report = EventLogWatchdogService.Evaluate(config);
        _logger.LogInformation("Watchdog flush: {observed} events observed since last flush; verdict={verdict} total={total}",
            observed, report.Verdict, report.TotalEvents);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        try { if (_watcher is not null) { _watcher.Enabled = false; _watcher.Dispose(); } } catch { }
        return base.StopAsync(cancellationToken);
    }
}
