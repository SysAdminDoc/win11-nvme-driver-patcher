using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tray;

// System tray agent — reads config + watchdog state every N seconds, renders a tray tooltip
// + colored icon reflecting patch state. Single-instance via named mutex. The agent exits
// cleanly on the Exit menu, and also exits at startup when another tray instance already
// owns the named mutex (keeps the tray idempotent across re-launches).
internal static class Program
{
    private const string MutexName = "Global\\NVMeDriverPatcher.Tray.Single";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private static NotifyIcon? _icon;
    private static ToolStripMenuItem? _statusItem;
    private static ToolStripMenuItem? _watchdogItem;
    private static System.Windows.Forms.Timer? _poll;
    private static AppConfig _config = new();

    [STAThread]
    private static int Main(string[] args)
    {
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (!createdNew) return 0;

        ApplicationConfiguration.Initialize();

        try
        {
            _config = ConfigService.Load();
        }
        catch
        {
            _config = new AppConfig { WorkingDir = AppConfig.GetWorkingDir() };
        }

        _icon = new NotifyIcon
        {
            Icon = TryLoadIcon() ?? SystemIcons.Information,
            Visible = true,
            Text = "NVMe Driver Patcher (loading…)"
        };

        var menu = new ContextMenuStrip();
        _statusItem = new ToolStripMenuItem("Status: (checking…)") { Enabled = false };
        _watchdogItem = new ToolStripMenuItem("Watchdog: (checking…)") { Enabled = false };
        menu.Items.Add(_statusItem);
        menu.Items.Add(_watchdogItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open Main App (elevated)", null, (_, _) => LaunchMainAppElevated());
        menu.Items.Add("Refresh Now", null, (_, _) => Refresh());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());
        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => LaunchMainAppElevated();

        _poll = new System.Windows.Forms.Timer { Interval = (int)PollInterval.TotalMilliseconds };
        _poll.Tick += (_, _) => Refresh();
        _poll.Start();

        Refresh();
        Application.Run();
        return 0;
    }

    private static Icon? TryLoadIcon()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "icon.ico");
            return File.Exists(path) ? new Icon(path) : null;
        }
        catch { return null; }
    }

    private static void Refresh()
    {
        try
        {
            // Reload config on every tick so changes the user makes in the main GUI (auto-revert
            // toggle, verification state after apply, renamed working directory) surface in the
            // tray tooltip within one PollInterval. Without this, the tray would show the config
            // state it saw at startup forever. Best-effort — if the file is mid-write we keep
            // the previous config and re-try next tick.
            try { _config = ConfigService.Load(); } catch { }

            var status = RegistryService.GetPatchStatus();
            var verification = PatchVerificationService.Evaluate(_config);
            var watchdog = EventLogWatchdogService.Evaluate(_config);

            string statusLine = $"Patch: {(status.Applied ? "Applied" : status.Partial ? "Partial" : "Not applied")} " +
                                $"({status.Count}/{status.Total}) — {verification.Outcome}";
            string watchdogLine = $"Watchdog: {watchdog.Verdict} ({watchdog.TotalEvents} events)";

            if (_statusItem is not null) _statusItem.Text = statusLine;
            if (_watchdogItem is not null) _watchdogItem.Text = watchdogLine;
            if (_icon is not null)
            {
                _icon.Text = Trim64($"{statusLine} | {watchdogLine}");
            }
        }
        catch (Exception ex)
        {
            if (_icon is not null) _icon.Text = Trim64("NVMe Driver Patcher — " + ex.Message);
        }
    }

    private static string Trim64(string s) => s.Length > 63 ? s[..63] : s;

    private static void LaunchMainAppElevated()
    {
        // The main app's manifest is requireAdministrator — ShellExecute with verb "runas"
        // triggers the UAC prompt. The tray agent itself stays non-admin.
        try
        {
            var exe = Path.Combine(AppContext.BaseDirectory, "NVMeDriverPatcher.exe");
            if (!File.Exists(exe))
            {
                // Running from a flat publish dir where tray + main share a folder, or a
                // split layout where main is one level up. Try both before giving up.
                var parent = Directory.GetParent(AppContext.BaseDirectory)?.FullName;
                if (parent is not null) exe = Path.Combine(parent, "NVMeDriverPatcher.exe");
            }
            if (!File.Exists(exe))
            {
                MessageBox.Show("Main app executable not found next to tray agent.", "NVMe Driver Patcher",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            var psi = new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" };
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "NVMe Driver Patcher", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static void ExitApp()
    {
        try { _poll?.Stop(); } catch { }
        try { if (_icon is not null) { _icon.Visible = false; _icon.Dispose(); } } catch { }
        Application.Exit();
    }
}
