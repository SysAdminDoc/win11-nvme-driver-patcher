using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using NVMeDriverPatcher.Views;

namespace NVMeDriverPatcher;

public partial class App : Application
{
    private Mutex? _mutex;
    private bool _ownsMutex;

    // Bound the crash log so a runaway exception loop can never fill the disk.
    private const long MaxCrashLogBytes = 1 * 1024 * 1024; // 1 MB

    protected override void OnStartup(StartupEventArgs e)
    {
        // Wire ALL exception sinks before doing anything else, so even early-startup faults
        // get logged and shown rather than terminating silently.
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += AppDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

        bool createdNew = true;
        try
        {
            _mutex = new Mutex(true, @"Global\NVMeDriverPatcher_SingleInstance", out createdNew);
            _ownsMutex = createdNew;
        }
        catch (Exception ex)
        {
            WriteCrashEntry("StartupMutexGlobal", ex);
            try
            {
                _mutex = new Mutex(true, @"Local\NVMeDriverPatcher_SingleInstance", out createdNew);
                _ownsMutex = createdNew;
            }
            catch (Exception localEx)
            {
                // If both mutex scopes fail on a hardened system, keep the app usable instead
                // of crashing on launch. We lose single-instance protection for that run only.
                WriteCrashEntry("StartupMutexLocal", localEx);
                _mutex = null;
                _ownsMutex = false;
                createdNew = true;
            }
        }

        if (!createdNew)
        {
            if (!TryShowThemedMessage(
                    "Already Open",
                    "NVMe Driver Patcher is already open.\n\nReturn to the existing window instead of launching a second elevated session.",
                    DialogIcon.Information))
            {
                MessageBox.Show("NVMe Driver Patcher is already running.", "Already Running",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            Shutdown();
            return;
        }

        try
        {
            Services.DataService.Initialize();
            // Lazy GC of stale rows so the SQLite file doesn't grow without bound for users
            // who keep the app installed for years. Both calls are best-effort.
            try { Services.DataService.PruneTelemetry(TimeSpan.FromDays(90)); } catch { }
            try { Services.DataService.PruneSnapshots(500); } catch { }
            try { Services.DataService.PruneBenchmarks(500); } catch { }
        }
        catch { /* Best-effort local data store */ }

        // If GetWorkingDir() had to fall back off LocalAppData, stamp a crash-log entry so
        // we have a breadcrumb if the app is writing to an unexpected directory.
        var fallbackReason = Models.AppConfig.WorkingDirFallbackReason;
        if (!string.IsNullOrEmpty(fallbackReason))
        {
            WriteCrashEntry("WorkingDirFallback",
                new InvalidOperationException(fallbackReason));
        }

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { Services.ToastService.DisposeAll(); } catch { }
        try
        {
            if (_ownsMutex)
                _mutex?.ReleaseMutex();
        }
        catch { /* Mutex may already be released or abandoned */ }
        try { _mutex?.Dispose(); } catch { }
        base.OnExit(e);
    }

    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrashEntry("Dispatcher", e.Exception);
        if (!TryShowThemedMessage(
                "Unexpected Error",
                $"Something interrupted NVMe Driver Patcher unexpectedly. The details were written to the local crash log.\n\n{e.Exception.Message}",
                DialogIcon.Error))
        {
            MessageBox.Show(
                $"An unexpected error occurred and has been logged.\n\n{e.Exception.Message}",
                "NVMe Driver Patcher - Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        e.Handled = true;
    }

    private void AppDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        // Background-thread exceptions surface here. We can't suppress these (CLR will terminate
        // the process if IsTerminating is true) but we can record them so the user has context.
        if (e.ExceptionObject is Exception ex)
            WriteCrashEntry(e.IsTerminating ? "AppDomain (terminating)" : "AppDomain", ex);
    }

    private void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        WriteCrashEntry("UnobservedTask", e.Exception);
        e.SetObserved();
    }

    private static void WriteCrashEntry(string source, Exception ex)
    {
        try
        {
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NVMeDriverPatcher");
            Directory.CreateDirectory(logDir);
            var logPath = Path.Combine(logDir, "crash.log");

            // Roll the file when it gets large, so a misbehaving driver can't fill the disk.
            try
            {
                if (File.Exists(logPath))
                {
                    var info = new FileInfo(logPath);
                    if (info.Length > MaxCrashLogBytes)
                    {
                        var rolled = Path.Combine(logDir, "crash.log.old");
                        try { if (File.Exists(rolled)) File.Delete(rolled); } catch { }
                        try { File.Move(logPath, rolled); } catch { try { File.Delete(logPath); } catch { } }
                    }
                }
            }
            catch { }

            var entry = $"[{DateTime.UtcNow:O}] [{source}] {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}\n\n";
            File.AppendAllText(logPath, entry);
        }
        catch { /* last-resort: can't write crash log */ }
    }

    private static bool TryShowThemedMessage(string title, string message, DialogIcon icon)
    {
        try
        {
            ThemedDialog.Show(message, title, DialogButtons.OK, icon);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
