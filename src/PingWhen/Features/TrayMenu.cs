using System.Drawing;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using H.NotifyIcon;
using PingWhen.Shell;
using Image = System.Windows.Controls.Image;

namespace PingWhen.Features;

/// <summary>
/// Tray items (spec §7): "Tell me when…" ▸ top windows / process / file / system quiet, "Active (N)" ▸ cancel,
/// "Do not disturb 1 h", "Test notification". Icon: plain / number of watchers / delivery error; tooltip with the count.
/// </summary>
public static class TrayMenu
{
    static MenuItem? active, dnd;

    public static IEnumerable<Control> Items()
    {
        var when = new MenuItem { Header = Loc.T("Tray.When") };
        when.Items.Add(new MenuItem()); // placeholder: the arrow shows, the list is built on open
        when.SubmenuOpened += (_, e) => { if (e.OriginalSource == when) FillWhen(when); };
        yield return when;

        active = new MenuItem { Header = string.Format(Loc.T("Tray.Active"), 0), IsEnabled = false };
        active.Items.Add(new MenuItem());
        active.SubmenuOpened += (_, e) => { if (e.OriginalSource == active) FillActive(active); };
        yield return active;

        dnd = Tray.Item(Loc.T("Tray.Dnd"), ToggleDnd);
        yield return dnd;
        yield return Tray.Item(Loc.T("Tray.Test"), Notifier.Test);
    }

    static void FillWhen(MenuItem when)
    {
        when.Items.Clear();
        foreach (var w in (Demo.DemoData.FakeTargets ? Demo.DemoData.Windows : WindowProbe.Candidates()).Take(8))
        {
            var exe = ProcessInfo.ExePath(w.Pid);
            var item = Tray.Item(Fmt.Cut(w.Title, 60), () => NewWatcherPage.Open(new() { Window = w }));
            if (exe is not null && ExeIcon.Get(exe) is { } img) item.Icon = new Image { Source = img, Width = 16, Height = 16 };
            when.Items.Add(item);
        }
        when.Items.Add(new Separator());
        when.Items.Add(Tray.Item(Loc.T("Tray.When.Process"), () => NewWatcherPage.Open(new() { Kind = Kind.Process })));
        when.Items.Add(Tray.Item(Loc.T("Tray.When.File"), () => NewWatcherPage.Open(new() { Kind = Kind.File })));
        when.Items.Add(Tray.Item(Loc.T("Tray.When.Quiet"), () => NewWatcherPage.Open(new() { Kind = Kind.Quiet })));
    }

    static void FillActive(MenuItem menu)
    {
        menu.Items.Clear();
        foreach (var c in Cards.Items)
        {
            var item = new MenuItem { Header = $"{Fmt.Cut(c.Title, 50)} · {c.Elapsed}" };
            var card = c;
            item.Items.Add(Tray.Item(Loc.T("Watchers.Cancel"), () => { if (card.IsDemo) Cards.Items.Remove(card); else _ = Engine.Current.Cancel(card.Id); }));
            menu.Items.Add(item);
        }
    }

    static void ToggleDnd()
    {
        Notifier.DndUntil = Notifier.DndUntil > DateTime.UtcNow ? default : DateTime.UtcNow.AddHours(1);
        Update();
    }

    /// <summary>After any change of watchers or delivery state.</summary>
    public static void Update()
    {
        var n = Cards.Items.Count;
        if (active is not null)
        {
            active.Header = string.Format(Loc.T("Tray.Active"), n);
            active.IsEnabled = n > 0;
        }
        if (dnd is not null) dnd.Header = Loc.T(Notifier.DndUntil > DateTime.UtcNow ? "Tray.DndOff" : "Tray.Dnd");
        TrayIcon.Set(n, Notifier.DeliveryError is not null, Tooltip());
    }

    static string Tooltip()
    {
        if (Notifier.DeliveryError is { } e) return Fmt.Cut($"{AppInfo.DisplayName}: {e}", 120);
        if (Cards.Items.Count == 0) return AppInfo.DisplayName;
        var next = Cards.Items.OrderByDescending(c => c.Progress).ThenBy(c => c.Since).First();
        return Fmt.Cut(string.Format(Loc.T("Tray.Tooltip"), Cards.Items.Count, next.Title, next.Elapsed), 120);
    }
}

/// <summary>
/// The shell's tray icon with a badge. The shell keeps the TaskbarIcon private and the template is frozen,
/// so it is reached by reflection; if the field ever moves, the icon simply stays plain.
/// </summary>
// ponytail: reflection on Shell/Tray.icon; replace with a Tray API when the template gets one after v0.1 of all eight
static class TrayIcon
{
    static (int Count, bool Error) shown = (0, false);
    static nint hicon;
    static bool hooked;

    static TaskbarIcon? Tb => typeof(Tray).GetField("icon", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as TaskbarIcon;

    public static void Set(int count, bool error, string tooltip)
    {
        if (Tb is not { } icon) return;
        icon.ToolTipText = tooltip;
        if (!hooked)
        {
            hooked = true; // the shell resets the icon on a taskbar theme change: draw the badge again after it
            MessageWindow.WndProc += (nint _, int msg, nint _, nint _, ref bool _) =>
            {
                if (msg == MessageWindow.WM_SETTINGCHANGE) Application.Current?.Dispatcher.BeginInvoke(() => Draw(force: true));
                return 0;
            };
        }
        if (shown == (count, error)) return;
        shown = (count, error);
        Draw(force: false);
    }

    static void Draw(bool force)
    {
        if (Tb is not { } icon) return;
        var (count, error) = shown;
        var name = icon.Tag as string ?? "tray-dark";
        if (count == 0 && !error)
        {
            if (!force) icon.IconSource = new System.Windows.Media.Imaging.BitmapImage(new Uri($"pack://application:,,,/Assets/{name}.ico"));
            Free();
            return;
        }
        var size = Windows.Win32.PInvoke.GetSystemMetrics(Windows.Win32.UI.WindowsAndMessaging.SYSTEM_METRICS_INDEX.SM_CXSMICON);
        using var stream = Application.GetResourceStream(new Uri($"pack://application:,,,/Assets/{name}.ico"))!.Stream;
        using var baseIcon = new System.Drawing.Icon(stream, size, size);
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.DrawIcon(baseIcon, new Rectangle(0, 0, size, size));
            var d = (int)(size * 0.62);
            var r = new Rectangle(size - d, size - d, d, d);
            using var fill = new SolidBrush(error ? Color.FromArgb(0xC4, 0x2B, 0x1C) : Color.FromArgb(0x00, 0x67, 0xC0));
            g.FillEllipse(fill, r);
            using var font = new Font("Segoe UI", d * 0.62f, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
            var text = error ? "!" : count > 9 ? "9+" : count.ToString();
            g.DrawString(text, font, Brushes.White, r, new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
        }
        var h = bmp.GetHicon();
        icon.Icon = System.Drawing.Icon.FromHandle(h);
        Free();
        hicon = h;
    }

    static void Free()
    {
        if (hicon != 0) Windows.Win32.PInvoke.DestroyIcon(new Windows.Win32.UI.WindowsAndMessaging.HICON(hicon));
        hicon = 0;
    }
}
