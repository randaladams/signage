using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows;
using Forms = System.Windows.Forms;

namespace SignagePlayer;

/// <summary>
/// Entry point. Running the downloaded SignagePlayer.exe installs it for this Windows user and starts the installed
/// copy; after that it starts with Windows. Only one player runs at a time.
///
///   SignagePlayer.exe               install/update (if needed) and run
///   SignagePlayer.exe --uninstall   remove it (also from Settings → Apps)
///   SignagePlayer.exe --portable    run from where it is, without installing (testing)
/// </summary>
static class App
{
    private const string ShowEventName = @"Local\SignagePlayer.Show";
    private static Mutex? _running;
    private static PlayerWindow? _window;
    private static Forms.NotifyIcon? _tray;
    private static bool _trayTipShown;

    [STAThread]
    public static int Main(string[] args)
    {
        var has = (string a) => args.Any(x => string.Equals(x, a, StringComparison.OrdinalIgnoreCase));
        Log.Info($"{Config.Version} starting from {Environment.ProcessPath} {string.Join(' ', args)}");

        if (has("--uninstall"))
        {
            if (!has("--quiet") && MessageBox.Show("Remove Signage Player from this PC?\n\nThis screen will stop showing the slideshow. " +
                    "It will still be listed in the admin page until you remove it there.", Config.DisplayName,
                    MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return 0;
            Installer.Uninstall();
            if (!has("--quiet")) MessageBox.Show("Signage Player has been removed.", Config.DisplayName);
            Installer.RemoveProgramFolderAfterExit();
            return 0;
        }

        if (string.IsNullOrEmpty(Config.ServerUrl))
        {
            MessageBox.Show("This copy of Signage Player has no server address built in.\n\n" +
                "Download it from your signage server's /get/windows link.", Config.DisplayName, MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }

        // A downloaded copy installs (or updates) itself, then hands over to the installed copy.
        if (!Installer.IsInstalledCopy && !has("--portable"))
        {
            var firstTime = !System.IO.File.Exists(Config.InstalledExe);
            if (Installer.Install(out var err))
            {
                Process.Start(new ProcessStartInfo(Config.InstalledExe, firstTime ? "--welcome" : "") { UseShellExecute = false });
                return 0;
            }
            MessageBox.Show($"Couldn't install Signage Player ({err}).\n\nIt will run from here for now.", Config.DisplayName,
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // One player at a time. A second start just brings the slideshow back.
        _running = new Mutex(true, Installer.MutexName, out var createdNew);
        if (!createdNew)
        {
            var waitForOld = has("--restarted");   // the old one is closing
            if (!waitForOld || !_running.WaitOne(TimeSpan.FromSeconds(15)))
            {
                if (EventWaitHandle.TryOpenExisting(ShowEventName, out var show)) using (show) show.Set();
                return 0;
            }
        }

        try
        {
            return Run(has("--welcome"));
        }
        finally
        {
            try { _running.ReleaseMutex(); } catch { }
            _tray?.Dispose();
        }
    }

    private static int Run(bool welcome)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) =>
        {
            Log.Warn("Unexpected error", e.Exception);
            e.Handled = true;   // keep the slideshow going
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Warn("Fatal error", e.ExceptionObject as Exception);

        _window = new PlayerWindow();
        _window.HiddenToTray += () =>
        {
            if (_trayTipShown || _tray == null) return;
            _trayTipShown = true;
            _tray.ShowBalloonTip(5000, Config.DisplayName,
                "The slideshow is hidden and still updating. Double-click this icon to show it again.", Forms.ToolTipIcon.Info);
        };
        _window.QuitRequested += Quit;
        CreateTray();

        // Other copies (updates, uninstall) ask us to close; a second start asks us to show the slideshow.
        ListenFor(Installer.QuitEventName, () => app.Dispatcher.BeginInvoke(Quit));
        ListenFor(ShowEventName, () => app.Dispatcher.BeginInvoke(() => _window.ShowSlideshow()));

        app.Startup += (_, _) =>
        {
            _window.Start();
            if (welcome)
                _tray!.ShowBalloonTip(8000, "Signage Player is installed",
                    "It starts with Windows. Esc hides the slideshow; this tray icon brings it back or quits it.", Forms.ToolTipIcon.Info);
        };
        return app.Run();
    }

    private static void ListenFor(string name, Action onSignal)
    {
        var ev = new EventWaitHandle(false, EventResetMode.AutoReset, name);
        new Thread(() =>
        {
            while (true)
            {
                ev.WaitOne();
                onSignal();
            }
        }) { IsBackground = true, Name = name }.Start();
    }

    private static void CreateTray()
    {
        System.Drawing.Icon icon;
        try { icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Application; }
        catch { icon = System.Drawing.SystemIcons.Application; }

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Show slideshow", null, (_, _) => _window!.ShowSlideshow());
        menu.Items.Add("Diagnostics", null, (_, _) => _window!.ShowDiag());
        var nextMonitor = menu.Items.Add("Show on the next monitor", null, (_, _) => _window!.NextMonitor());
        menu.Items.Add(new Forms.ToolStripSeparator());
        var startup = new Forms.ToolStripMenuItem("Start with Windows") { CheckOnClick = true };
        startup.Click += (_, _) => Installer.SetStartWithWindows(startup.Checked);
        menu.Items.Add(startup);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Quit());
        menu.Opening += (_, _) =>
        {
            startup.Checked = Installer.StartsWithWindows;
            startup.Enabled = Installer.IsInstalledCopy;
            nextMonitor.Enabled = _window!.MonitorCount > 1;
        };

        _tray = new Forms.NotifyIcon
        {
            Icon = icon,
            Text = Config.DisplayName,
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => _window!.ShowSlideshow();
    }

    private static void Quit()
    {
        Log.Info("Quitting");
        if (_tray != null) _tray.Visible = false;
        _window?.Quit();
        Application.Current.Shutdown();
    }

    /// <summary>"Restart app" from the admin page: start a fresh copy, which waits for this one to close.</summary>
    public static void Restart()
    {
        Log.Info("Restarting");
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--restarted" + (Installer.IsInstalledCopy ? "" : " --portable"))
                { UseShellExecute = false });
        }
        catch (Exception e)
        {
            Log.Warn("Could not restart", e);
            return;
        }
        Quit();
    }
}
