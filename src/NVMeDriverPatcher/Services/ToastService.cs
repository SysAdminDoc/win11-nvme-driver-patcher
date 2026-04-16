using System.Drawing;
using System.Windows.Threading;

namespace NVMeDriverPatcher.Services;

public enum ToastType { Info, Success, Warning, Error }

public static class ToastService
{
    private static readonly List<System.Windows.Forms.NotifyIcon> ActiveToasts = [];

    public static void Show(string title, string message, ToastType type = ToastType.Info, bool enabled = true)
    {
        if (!enabled) return;

        // NotifyIcon and DispatcherTimer must run on the UI thread
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => Show(title, message, type, enabled));
            return;
        }

        try
        {
            var icon = new System.Windows.Forms.NotifyIcon
            {
                Icon = SystemIcons.Information,
                BalloonTipTitle = title,
                BalloonTipText = message,
                BalloonTipIcon = type switch
                {
                    ToastType.Success => System.Windows.Forms.ToolTipIcon.Info,
                    ToastType.Warning => System.Windows.Forms.ToolTipIcon.Warning,
                    ToastType.Error => System.Windows.Forms.ToolTipIcon.Error,
                    _ => System.Windows.Forms.ToolTipIcon.Info
                },
                Visible = true
            };
            icon.ShowBalloonTip(5000);
            lock (ActiveToasts) { ActiveToasts.Add(icon); }

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(6000) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                icon.Visible = false;
                icon.Dispose();
                lock (ActiveToasts) { ActiveToasts.Remove(icon); }
            };
            timer.Start();
        }
        catch { /* Toast best-effort */ }
    }

    public static void DisposeAll()
    {
        lock (ActiveToasts)
        {
            foreach (var icon in ActiveToasts)
            {
                try { icon.Visible = false; icon.Dispose(); } catch { }
            }
            ActiveToasts.Clear();
        }
    }
}
