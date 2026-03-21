using System.Threading;
using System.Windows;

namespace NVMeDriverPatcher;

public partial class App : Application
{
    private Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        bool createdNew;
        _mutex = new Mutex(true, @"Global\NVMeDriverPatcher_SingleInstance", out createdNew);
        if (!createdNew)
        {
            MessageBox.Show("NVMe Driver Patcher is already running.", "Already Running",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
