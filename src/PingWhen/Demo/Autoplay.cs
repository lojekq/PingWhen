using System.Windows;
using System.Windows.Threading;

namespace PingWhen.Demo;

/// <summary>
/// --autoplay is the scene GIF (spec §10), recorded by the owner in a slot with the working instance closed.
/// The toast slides in at the bottom right of the primary monitor, so the scene is the bottom-right 1280×720
/// of its work area (owner: X 2160, Y 688): Windows Terminal there runs docs\fake-progress.ps1 → Ctrl+Alt+W →
/// the page with "pwsh → fake-progress.ps1" and "Process exited" → Enter → the card → the window hides →
/// 100 % → the toast. Nothing is scripted: the hotkey is registered in this demo and the engine watches the real script.
/// </summary>
public static class Autoplay
{
    public static Rect Scene
    {
        get
        {
            var work = SystemParameters.WorkArea;
            return new Rect(work.Right - 1280, work.Bottom - 720, 1280, 720);
        }
    }

    public static void Start(MainWindow w)
    {
        var s = Scene;
        (w.Left, w.Top) = (s.Left + 20, s.Top + 20);
        w.Hide();
    }

    /// <summary>After Watch: show the card briefly, then get out of the way of the terminal and the toast.</summary>
    public static void AfterWatch(Window w)
    {
        if (!Shell.Args.Current.Autoplay) return;
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        t.Tick += (_, _) => { t.Stop(); w.Hide(); };
        t.Start();
    }
}
