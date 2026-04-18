using System.Drawing;
using System.Windows.Threading;

namespace NVMeDriverPatcher.Services;

public enum ToastType { Info, Success, Warning, Error }

public static class ToastService
{
    private const int MaxActiveToasts = 8;
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
            // Cap the active toast list. Without this, a flood of notifications (e.g. a service
            // emitting hundreds of warnings during preflight) can keep accumulating tray icons
            // until the system runs out of HICONs.
            lock (ActiveToasts)
            {
                while (ActiveToasts.Count >= MaxActiveToasts)
                {
                    var oldest = ActiveToasts[0];
                    ActiveToasts.RemoveAt(0);
                    try { oldest.Visible = false; oldest.Dispose(); } catch { }
                }
            }

            var icon = new System.Windows.Forms.NotifyIcon
            {
                Icon = type switch
                {
                    ToastType.Success => SystemIcons.Information,
                    ToastType.Warning => SystemIcons.Warning,
                    ToastType.Error => SystemIcons.Error,
                    _ => SystemIcons.Information
                },
                BalloonTipTitle = title ?? string.Empty,
                BalloonTipText = message ?? string.Empty,
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
                try { icon.Visible = false; } catch { }
                try { icon.Dispose(); } catch { }
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
