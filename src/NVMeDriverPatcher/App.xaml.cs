using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace NVMeDriverPatcher;

public partial class App : Application
{
    private Mutex? _mutex;
    private bool _ownsMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += App_DispatcherUnhandledException;

        _mutex = new Mutex(true, @"Global\NVMeDriverPatcher_SingleInstance", out bool createdNew);
        _ownsMutex = createdNew;

        if (!createdNew)
        {
            MessageBox.Show("NVMe Driver Patcher is already running.", "Already Running",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        try { Services.DataService.Initialize(); } catch { /* Best-effort local data store */ }

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Services.ToastService.DisposeAll();
        if (_ownsMutex)
            _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }

    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NVMeDriverPatcher");
        Directory.CreateDirectory(logDir);
        var logPath = Path.Combine(logDir, "crash.log");

        try
        {
            var entry = $"[{DateTime.UtcNow:O}] {e.Exception.GetType().Name}: {e.Exception.Message}\n{e.Exception.StackTrace}\n\n";
            File.AppendAllText(logPath, entry);
        }
        catch { /* last-resort: can't write crash log */ }

        MessageBox.Show(
            $"An unexpected error occurred and has been logged.\n\n{e.Exception.Message}",
            "NVMe Driver Patcher - Error",
            MessageBoxButton.OK, MessageBoxImage.Error);

        e.Handled = true;
    }
}
