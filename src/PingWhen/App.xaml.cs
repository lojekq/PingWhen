using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;
using PingWhen.Demo;
using PingWhen.Features;
using PingWhen.Shell;

namespace PingWhen;

public partial class App : Application
{
    /// <summary>Synchronous hook before exit: tray Exit, Windows session end, crash handler (best effort, before the error box).</summary>
    public static event Action? Exiting;

    /// <summary>Asked on a user exit (tray, restart); return false to stay. May ask the user.</summary>
    public static Func<bool>? ConfirmExit;

    /// <summary>"Remove data &amp; autostart" and uninstall: undo what the app registered and return null, or return why not (shown to the user).
    /// Set it before Installer.HandleAsync: --uninstall calls it there.</summary>
    public static Func<string?>? RemoveData;

    /// <summary>Installer turns autostart on for a fresh install: a watcher tray is useful from the first logon.</summary>
    public static bool AutostartOnInstall = true;

    static MainWindow? window;
    static bool quitting;
    static readonly CancellationTokenSource pipeStop = new();

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var a = Args.Current;
        var fromCli = Cli.StartedByCli(); // first thing: the CLI closes the event as soon as the pipe answers
        Crash.Install(this);
        Settings.Load();
        Loc.Apply(Settings.Current.Language);
        RemoveData = RemovePingWhen; // before the installer: --uninstall calls it there
        // install, update, switch to the installed copy or uninstall: this process is done.
        // Not when an agent started the tray (--mcp, --hook, --notify): a dialog then would pop up in the middle of its work.
        if ((!fromCli || a.Uninstall) && await Installer.HandleAsync())
        {
            Shutdown();
            return;
        }
        if (!SingleInstance.Acquire(() => Dispatcher.BeginInvoke(() => ShowMain(null)), () => Dispatcher.BeginInvoke(() => Quit(confirm: false))))
        {
            Shutdown();
            return;
        }
        Log.Info($"start {AppInfo.Version}{(a.Unknown.Count > 0 ? $", {a.Unknown.Count} unknown argument(s)" : "")}");

        if (!a.Demo && Install.IsInstalled) Installer.InstalledStart(AutostartOnInstall); // Debug builds and exes elsewhere write nothing by themselves

        // PingWhen (spec §5.8): AUMID → watchers.json with pid checks → Engine → PipeServer → hotkey → tray
        Store.ReadOnly = a.Demo;
        if (!a.Demo) Toast.Register();
        Engine.Current.Fired += Notifier.OnFired;
        Notifier.Start(phone: !a.Demo);
        Cards.Start();
        if (a.Demo && !a.Autoplay) DemoData.Fill();
        if (!a.Demo) Engine.Current.Restore(Store.LoadWatchers());
        Engine.Current.Start();
        if (!a.Demo) _ = PipeServer.Run(PipeServer.Name, pipeStop.Token);
        if (!a.Demo || a.Autoplay) ShellHook.Start(Engine.Current.Flashed); // v1.0: "window flashed on the taskbar"
        SystemEvents.PowerModeChanged += (_, pe) => { if (pe.Mode == PowerModes.Resume) Engine.Current.ResetTimers(); };
        SettingsPage.AppSection = () => new PingSettingsView();
        Hotkeys.RegisterInDemo = a.Autoplay; // the scene GIF presses Ctrl+Alt+W; only with the working instance closed
        PingSettingsView.ApplyHotkey();
        ConfirmExit = () => Engine.Current.Snapshot.Count == 0 || a.Demo ||
            MessageBox.Show(string.Format(Loc.T("Exit.Confirm"), Engine.Current.Snapshot.Count), AppInfo.DisplayName,
                MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;
        Exiting += () =>
        {
            pipeStop.Cancel();          // agents get EOF
            Notifier.Flush();           // ≤ 3 s
            Engine.Current.Flush().Wait(TimeSpan.FromSeconds(2));
            KeepAwake.Set(false);
        };

        window = new MainWindow();
        if (a.Width > 0) (window.Width, window.Height) = (a.Width, a.Height);
        Theme.Apply(window, Settings.Current.Theme);
        window.Closing += (_, ce) =>
        {
            if (quitting) return;
            ce.Cancel = true; // the cross hides to the tray
            window.Hide();
            if (Settings.Current.TrayHintShown) return;
            Settings.Current.TrayHintShown = true;
            Settings.Save();
            Tray.Notify(AppInfo.DisplayName, Loc.T("Tray.Hidden"));
        };
        Capture.Attach(window, () => { if (a.Autoplay) Autoplay.Start(window); });

        if (a.Screenshot is null)
        {
            Tray.Create(ShowMain, () => Quit(), TrayMenu.Items());
            Cards.Changed += TrayMenu.Update;
            Cards.Items.CollectionChanged += (_, _) => TrayMenu.Update();
            Notifier.StateChanged += () => Dispatcher.BeginInvoke(TrayMenu.Update);
            TrayMenu.Update();
        }
        SessionEnding += (_, _) => RaiseExiting();
        Updates.Found += (version, url) =>
        {
            window.ShowUpdate(version, url);
            if (Settings.Current.NotifiedVersion == version) return;
            Settings.Current.NotifiedVersion = version;
            Settings.Save();
            Tray.Notify(AppInfo.DisplayName, string.Format(Loc.T("Update.Message"), version), "about");
        };
        Updates.Start();

        if (!a.Background && !fromCli) ShowMain(a.Page); // started by --mcp/--hook/--notify: stay in the tray
    }

    public static void ShowMain(string? page)
    {
        if (window is null) return;
        window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
        if (page is not null) window.Navigate(page);
    }

    /// <summary>
    /// Uninstall and "Remove data": the toast identity goes away here. PingWhen never edits Claude Code's files (spec §4),
    /// so the user is told how to remove the MCP server and the hooks; a running --mcp relay keeps the exe busy until its agent ends.
    /// </summary>
    static string? RemovePingWhen()
    {
        try { Toast.Unregister(); } catch (Exception e) { Log.Warn("toast identity not removed", e); }
        MessageBox.Show(Loc.T("Uninstall.Claude"), AppInfo.DisplayName, MessageBoxButton.OK, MessageBoxImage.Information);
        return null;
    }

    public static void RaiseExiting()
    {
        var hook = Exiting;
        Exiting = null; // once, whoever comes first
        foreach (var h in hook?.GetInvocationList() ?? [])
            try { ((Action)h)(); }
            catch (Exception e) { Log.Error("Exiting hook failed", e); } // the other hooks still run
    }

    public static void Quit(bool confirm = true)
    {
        if (confirm && ConfirmExit?.Invoke() == false) return;
        quitting = true;
        RaiseExiting();
        Tray.Dispose();
        Log.Info("exit");
        Current.Shutdown();
    }

    /// <summary>After a language change. The mutex is released first, so the new process does not just wake this one.</summary>
    public static void Restart()
    {
        if (ConfirmExit?.Invoke() == false) return;
        SingleInstance.Release();
        Process.Start(AppInfo.ExePath);
        Quit(confirm: false);
    }
}
