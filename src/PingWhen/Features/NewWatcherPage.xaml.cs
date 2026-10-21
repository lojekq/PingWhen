using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PingWhen.Shell;
using Wpf.Ui.Controls;

namespace PingWhen.Features;

/// <summary>What the page opens with: a window (hotkey, tray), a kind (tray items) or a watcher to edit.</summary>
public sealed class NewRequest
{
    public TopWindow? Window { get; init; }
    public Kind? Kind { get; init; }
    public Watcher? Edit { get; init; }
}

/// <summary>Target → condition → parameters → notification → Watch (spec §7, page 2).</summary>
public partial class NewWatcherPage
{
    enum Cond { Closed, TitleChanged, TitleMatches, Flashed, RegionUnchanged, RegionChanged, RegionText, Exited, Quiet, StoppedGrowing, Appeared, Disappeared }
    sealed record Choice<T>(T Value, string Text);
    sealed record Item(object Value, string Text, ImageSource? Icon);
    sealed record CmdItem(Terminals.Command? Cmd, string Text, SymbolRegular Symbol);

    static NewRequest? pending;
    static NewWatcherPage? shown;

    Watcher? editing;
    Frames.Rect region = new(0, 0, 1, 1);
    List<Item> allProcs = [];

    /// <summary>Shows the main window on this page; if it is already open, reloads it with the new request.</summary>
    public static void Open(NewRequest? r)
    {
        pending = r ?? new();
        if (shown is { IsLoaded: true } p) { p.Load(pending); pending = null; App.ShowMain(null); }
        else App.ShowMain("new");
    }

    /// <summary>Ctrl+Alt+W: the active window as the target.</summary>
    public static void OpenForForeground()
    {
        var h = (nint)Windows.Win32.PInvoke.GetForegroundWindow();
        var win = h != 0 && TopWindows.Passes(h) ? new TopWindow(h, TopWindows.Pid(h), TopWindows.ClassOf(h), TopWindows.TitleOf(h), false) : null;
        Open(new() { Window = win });
    }

    public NewWatcherPage()
    {
        InitializeComponent();
        RouteBox.ItemsSource = Enum.GetValues<Route>().Select(r => new Choice<Route>(r, Loc.T("Route." + r))).ToList();
        RouteBox.SelectedIndex = 0;
        foreach (var rb in new[] { KindWindow, KindProcess, KindFile, KindSystem }) rb.Checked += (_, _) => KindChanged();
        WindowBox.SelectionChanged += (_, _) => WindowChanged();
        CommandsBox.SelectionChanged += (_, _) => RefreshConditions();
        ProcFilter.TextChanged += (_, _) => FilterProcs();
        ProcBox.SelectionChanged += (_, _) => RefreshTitle();
        CondBox.SelectionChanged += (_, _) => RefreshParams();
        MetricBox.SelectionChanged += (_, _) => MetricChanged();
        FilePath.TextChanged += (_, _) => RefreshTitle();
        Browse.Click += (_, _) => BrowseFile();
        PickRegion.Click += async (_, _) =>
        {
            var hwnd = editing?.Hwnd ?? SelectedWindow?.Hwnd ?? 0;
            if (hwnd == 0 || Window.GetWindow(this) is not { } owner) return;
            if (await RegionPicker.Pick(owner, hwnd, region) is { } r) { region = r; ShowRegion(); }
        };
        WatchButton.Click += async (_, _) =>
        {
            try { await Watch(); }
            catch (Exception e) { Log.Error("watch", e); ErrorBar.Title = e.Message; ErrorBar.IsOpen = true; }
        };
        CancelButton.Click += (_, _) => App.ShowMain("main");
        Loaded += (_, _) =>
        {
            shown = this;
            Load(pending ?? new());
            pending = null;
        };
        Unloaded += (_, _) => { if (shown == this) shown = null; };
    }

    void Load(NewRequest r)
    {
        ErrorBar.IsOpen = false;
        editing = r.Edit;
        var edit = editing is not null;
        Heading.Text = Loc.T(edit ? "Page.Edit" : "Page.New");
        KindPanel.Visibility = edit ? Visibility.Collapsed : Visibility.Visible;
        EditTarget.Visibility = edit ? Visibility.Visible : Visibility.Collapsed;
        TitleBox.Text = "";
        if (editing is { } w)
        {
            EditTarget.Text = Describe.Target(w);
            foreach (var p in new FrameworkElement[] { WindowPanel, ProcessPanel, FilePanel, SystemText }) p.Visibility = Visibility.Collapsed;
            TitleBox.Text = w.Title;
            RouteBox.SelectedIndex = (int)w.Route;
            KeepAwakeBox.IsChecked = w.KeepAwake;
            WaitChildrenBox.IsChecked = w.WaitChildren;
            RepeatBox.IsChecked = w.Repeat;
            ScreenshotBox.IsChecked = w.Screenshot;
            region = w.Region;
            DeadlineBox.IsChecked = w.DeadlineMinutes is not null;
            DeadlineMinutes.Value = w.DeadlineMinutes ?? 60;
            Pattern.Text = w.Pattern ?? "";
            IsRegex.IsChecked = w.IsRegex;
            AlreadyRunning.IsChecked = w.AlreadyRunning;
            RefreshConditions(w.Kind switch
            {
                Kind.Window => w.Until switch { Until.TitleChanged => Cond.TitleChanged, Until.TitleMatches => Cond.TitleMatches, Until.Flashed => Cond.Flashed, _ => Cond.Closed },
                Kind.File => w.Until switch { Until.Appeared => Cond.Appeared, Until.Disappeared => Cond.Disappeared, _ => Cond.StoppedGrowing },
                Kind.Region => w.Until switch { Until.Changed => Cond.RegionChanged, Until.TextMatches => Cond.RegionText, _ => Cond.RegionUnchanged },
                Kind.Quiet => Cond.Quiet,
                _ => Cond.Exited,
            });
            if (w.Kind == Kind.Quiet) MetricBox.SelectedItem = ((IEnumerable<Choice<Metric>>)MetricBox.ItemsSource).First(m => m.Value == w.Metric);
            BelowBox.Value = w.Below;
            SecondsBox.Value = w.QuietSeconds;
            return;
        }

        var windows = Demo.DemoData.FakeTargets ? Demo.DemoData.Windows.ToList() : WindowProbe.Candidates();
        if (r.Window is { } target && windows.All(x => x.Hwnd != target.Hwnd)) windows.Insert(0, target);
        WindowBox.ItemsSource = windows.Select(x => new Item(x, Fmt.Cut(x.Title, 100), Icon(x.Pid))).ToList();
        WindowBox.SelectedIndex = r.Window is { } t2 ? windows.FindIndex(x => x.Hwnd == t2.Hwnd) : windows.Count > 0 ? 0 : -1;
        KeepAwakeBox.IsChecked = false;
        WaitChildrenBox.IsChecked = false;
        RepeatBox.IsChecked = false;
        DeadlineBox.IsChecked = false;
        ScreenshotBox.IsChecked = false;
        region = new(0, 0, 1, 1);
        RouteBox.SelectedIndex = 0;
        var kind = r.Kind ?? Kind.Window;
        (kind switch { Kind.Process => KindProcess, Kind.File => KindFile, Kind.Quiet => KindSystem, _ => KindWindow }).IsChecked = true;
        KindChanged();
        (CommandsPanel.IsVisible ? CommandsBox : (FrameworkElement)WindowBox).Focus();
        Scroller.ScrollToTop();
    }

    static ImageSource? Icon(int pid) => ProcessInfo.ExePath(pid) is { } p ? ExeIcon.Get(p) : null;

    Kind SelectedKind => KindProcess.IsChecked == true ? Kind.Process : KindFile.IsChecked == true ? Kind.File : KindSystem.IsChecked == true ? Kind.Quiet : Kind.Window;
    TopWindow? SelectedWindow => (WindowBox.SelectedItem as Item)?.Value as TopWindow;
    Terminals.Command? SelectedCommand => (CommandsBox.SelectedItem as CmdItem)?.Cmd;
    ProcessNode? SelectedProc => (ProcBox.SelectedItem as Item)?.Value as ProcessNode;
    Cond? SelectedCond => (CondBox.SelectedItem as Choice<Cond>)?.Value;
    Metric SelectedMetric => (MetricBox.SelectedItem as Choice<Metric>)?.Value ?? Metric.Cpu;

    void KindChanged()
    {
        if (editing is not null) return;
        var k = SelectedKind;
        WindowPanel.Visibility = k == Kind.Window ? Visibility.Visible : Visibility.Collapsed;
        ProcessPanel.Visibility = k == Kind.Process ? Visibility.Visible : Visibility.Collapsed;
        FilePanel.Visibility = k == Kind.File ? Visibility.Visible : Visibility.Collapsed;
        SystemText.Visibility = k == Kind.Quiet ? Visibility.Visible : Visibility.Collapsed;
        if (k == Kind.Process && allProcs.Count == 0) LoadProcs();
        if (k == Kind.Window) WindowChanged(); else RefreshConditions();
    }

    /// <summary>A terminal window lists the commands running in its shells; the newest one is preselected.</summary>
    void WindowChanged()
    {
        var w = SelectedWindow;
        var commands = w is null ? []
            : Demo.DemoData.FakeTargets ? Demo.DemoData.Commands(w.Pid)
            : Terminals.IsTerminal(ProcessInfo.ExePath(w.Pid)) ? Terminals.Commands(w.Pid, ProcessInfo.Tree()) : [];
        CommandsPanel.Visibility = commands.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var items = commands.Select(c => new CmdItem(c, $"{c.Shell} → {Fmt.Cut(Terminals.Short(c.CommandLine), 110)}", SymbolRegular.WindowConsole20)).ToList();
        items.Add(new CmdItem(null, Loc.T("New.WindowItself"), SymbolRegular.Window24));
        CommandsBox.ItemsSource = items;
        CommandsBox.SelectedIndex = 0;
        RefreshConditions();
    }

    void LoadProcs()
    {
        if (Demo.DemoData.FakeTargets)
        {
            allProcs = Demo.DemoData.Processes.Select(p => new Item(p, $"{p.Exe} · {p.Pid}", null)).ToList();
            FilterProcs();
            return;
        }
        var self = Environment.ProcessId;
        Task.Run(() => ProcessInfo.Tree().All
                .Where(p => p.Pid > 4 && p.Pid != self && p.Started is not null)
                .Select(p => (p, cmd: ProcessInfo.CommandLine(p.Pid), path: ProcessInfo.ExePath(p.Pid)))
                .Where(x => x.path is not null)
                .Select(x => new Item(x.p, $"{x.p.Exe} · {x.p.Pid}{(x.cmd is null ? "" : " · " + Fmt.Cut(Terminals.Short(x.cmd), 90))}", ExeIcon.Get(x.path!)))
                .OrderBy(i => ((ProcessNode)i.Value).Exe, StringComparer.OrdinalIgnoreCase).ToList())
            .ContinueWith(t =>
            {
                if (t.IsFaulted) { Log.Error("process list", t.Exception); return; }
                allProcs = t.Result;
                FilterProcs();
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    void FilterProcs()
    {
        var f = ProcFilter.Text.Trim();
        ProcBox.ItemsSource = f.Length == 0 ? allProcs : allProcs.Where(i => i.Text.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    void RefreshConditions(Cond? prefer = null)
    {
        Cond[] list = editing is { } w
            ? w.Kind switch { Kind.Window => [Cond.Closed, Cond.TitleChanged, Cond.TitleMatches, Cond.Flashed], Kind.Region => RegionConds(), Kind.File => [Cond.StoppedGrowing, Cond.Appeared, Cond.Disappeared], Kind.Quiet => [Cond.Quiet], _ => [Cond.Exited] }
            : SelectedKind switch
            {
                Kind.Window when SelectedCommand is not null => [Cond.Exited, Cond.Quiet],
                Kind.Window => [Cond.Closed, Cond.TitleChanged, Cond.TitleMatches, Cond.Flashed, .. RegionConds(), Cond.Exited, Cond.Quiet],
                Kind.Process => [Cond.Exited, Cond.Quiet],
                Kind.File => [Cond.StoppedGrowing, Cond.Appeared, Cond.Disappeared],
                _ => [Cond.Quiet],
            };
        var keep = prefer ?? SelectedCond;
        CondBox.ItemsSource = list.Select(c => new Choice<Cond>(c, Loc.T("New.Cond." + c))).ToList();
        CondBox.SelectedIndex = keep is { } k && Array.IndexOf(list, k) is >= 0 and var i ? i : 0;
        RefreshParams();
    }

    /// <summary>"Text matched" needs a Windows OCR language; without one only the pixel conditions are offered.</summary>
    static Cond[] RegionConds() => RegionCapture.OcrAvailable ? [Cond.RegionUnchanged, Cond.RegionChanged, Cond.RegionText] : [Cond.RegionUnchanged, Cond.RegionChanged];

    static bool IsRegion(Cond? c) => c is Cond.RegionUnchanged or Cond.RegionChanged or Cond.RegionText;

    void ShowRegion() => RegionText.Text = region == new Frames.Rect(0, 0, 1, 1)
        ? Loc.T("New.RegionWhole")
        : string.Format(Loc.T("New.RegionPart"), (int)(region.X * 100), (int)(region.Y * 100), (int)(region.W * 100), (int)(region.H * 100));

    bool SystemTarget => editing is { } w ? w.Processes.Count == 0 : SelectedKind == Kind.Quiet;

    void RefreshParams()
    {
        var c = SelectedCond;
        PatternPanel.Visibility = c is Cond.TitleMatches or Cond.RegionText ? Visibility.Visible : Visibility.Collapsed;
        RegionPanel.Visibility = IsRegion(c) ? Visibility.Visible : Visibility.Collapsed;
        ShowRegion();
        var hasWindow = editing is { } ew ? ew.Hwnd != 0 : SelectedKind == Kind.Window;
        ScreenshotBox.Visibility = ScreenshotNote.Visibility = hasWindow ? Visibility.Visible : Visibility.Collapsed;
        MetricPanel.Visibility = AlreadyRunning.Visibility = c == Cond.Quiet ? Visibility.Visible : Visibility.Collapsed;
        SecondsPanel.Visibility = c is Cond.Quiet or Cond.StoppedGrowing or Cond.RegionUnchanged ? Visibility.Visible : Visibility.Collapsed;
        WaitChildrenBox.Visibility = c == Cond.Exited ? Visibility.Visible : Visibility.Collapsed;
        RepeatBox.Visibility = c == Cond.Closed ? Visibility.Collapsed : Visibility.Visible; // a closed window can't close again
        if (c == Cond.Quiet)
        {
            var metrics = SystemTarget ? Enum.GetValues<Metric>() : [Metric.Cpu, Metric.Gpu, Metric.Disk]; // network per process needs admin
            var keep = SelectedMetric;
            MetricBox.ItemsSource = metrics.Select(m => new Choice<Metric>(m, Fmt.MetricName(m))).ToList();
            MetricBox.SelectedIndex = Math.Max(0, Array.IndexOf(metrics, keep));
        }
        else if (c is Cond.StoppedGrowing or Cond.RegionUnchanged && editing is null) SecondsBox.Value = 30;
        RefreshTitle();
    }

    void MetricChanged()
    {
        var m = SelectedMetric;
        BelowUnit.Text = m switch { Metric.Disk => Loc.T("Unit.MBsShort"), Metric.Net => Loc.T("Unit.KBsShort"), _ => "%" };
        if (editing is not null) return;
        BelowBox.Value = Watcher.DefaultBelow(m);
        SecondsBox.Value = Watcher.DefaultSeconds(m);
    }

    void RefreshTitle() => TitleBox.PlaceholderText = editing?.Title ?? DefaultTitle() ?? Loc.T("New.TitlePlaceholder");

    string? DefaultTitle() => SelectedKind switch
    {
        Kind.Window when SelectedCommand is { } c => Fmt.Cut(Terminals.Short(c.CommandLine), 60),
        Kind.Window => SelectedWindow is { } w ? Fmt.Cut(w.Title, 60) : null,
        Kind.Process => SelectedProc is { } p ? Path.GetFileNameWithoutExtension(p.Exe) : null,
        Kind.File => FilePath.Text.Trim() is { Length: > 0 } f ? Path.GetFileName(f) : null,
        _ => Loc.T("Target.System"),
    };

    void BrowseFile()
    {
        var d = new Microsoft.Win32.OpenFileDialog { CheckFileExists = false, Title = Loc.T("New.Browse") };
        if (d.ShowDialog(Window.GetWindow(this)) == true) FilePath.Text = d.FileName;
    }

    async Task Watch()
    {
        string? error = null;
        Watcher? w = null;
        if (Demo.DemoData.FakeTargets && editing is null)
        {
            var d = new Watcher { Kind = SelectedKind == Kind.Quiet ? Kind.Quiet : Kind.Process };
            if (ApplyCommon(d, out _)) Demo.DemoData.AddCard(d);
            App.ShowMain("main");
            return;
        }
        try { w = editing is null ? Build(out error) : Edited(out error); }
        catch (Exception e) { Log.Error("new watcher", e); error = e.Message; }
        if (w is null)
        {
            ErrorBar.Title = error ?? "";
            ErrorBar.IsOpen = true;
            return;
        }
        if (editing is not null) await Engine.Current.Replace(w);
        else await Engine.Current.Add(w);
        var win = Window.GetWindow(this); // before navigating away from this page
        App.ShowMain("main");
        if (win is not null) Demo.Autoplay.AfterWatch(win);
    }

    /// <summary>The notification and condition fields onto <paramref name="w"/>.</summary>
    bool ApplyCommon(Watcher w, out string? error)
    {
        error = null;
        var c = SelectedCond ?? Cond.Exited;
        w.Until = c switch
        {
            Cond.Closed => Until.Closed, Cond.TitleChanged => Until.TitleChanged, Cond.TitleMatches => Until.TitleMatches, Cond.Flashed => Until.Flashed,
            Cond.RegionUnchanged => Until.Unchanged, Cond.RegionChanged => Until.Changed, Cond.RegionText => Until.TextMatches,
            Cond.StoppedGrowing => Until.StoppedGrowing, Cond.Appeared => Until.Appeared, Cond.Disappeared => Until.Disappeared,
            Cond.Quiet => Until.Quiet, _ => Until.Exited,
        };
        if (c is Cond.TitleMatches or Cond.RegionText)
        {
            w.Pattern = Pattern.Text;
            w.IsRegex = IsRegex.IsChecked == true;
            if (w.Pattern.Length == 0) { error = Loc.T("New.Error.Pattern"); return false; }
            if (w.IsRegex && Conditions.RegexError(w.Pattern) is { } re) { error = re; return false; }
        }
        if (c is Cond.Quiet or Cond.StoppedGrowing or Cond.RegionUnchanged) w.QuietSeconds = (int)Math.Max(5, SecondsBox.Value ?? 30);
        if (IsRegion(c)) (w.RegionX, w.RegionY, w.RegionW, w.RegionH) = (region.X, region.Y, region.W, region.H);
        w.Screenshot = w.Hwnd != 0 && ScreenshotBox.IsChecked == true;
        if (c == Cond.Quiet)
        {
            w.Metric = SelectedMetric;
            w.Below = BelowBox.Value is { } b && b > 0 ? b : Watcher.DefaultBelow(w.Metric);
            w.AlreadyRunning = AlreadyRunning.IsChecked == true;
        }
        w.Title = TitleBox.Text.Trim() is { Length: > 0 } t ? t : DefaultTitle() ?? w.Title;
        w.Route = (RouteBox.SelectedItem as Choice<Route>)?.Value ?? Route.Auto;
        w.KeepAwake = KeepAwakeBox.IsChecked == true;
        w.WaitChildren = c == Cond.Exited && WaitChildrenBox.IsChecked == true;
        w.Repeat = c != Cond.Closed && RepeatBox.IsChecked == true;
        if (w.Repeat && w.Kind == Kind.Process && w.ProcessName is null && w.CommandLineContains is null)
            w.ProcessName = w.Processes.FirstOrDefault()?.Name; // "every time it exits" needs a name: a pid exits once
        w.DeadlineMinutes = DeadlineBox.IsChecked == true ? (int)Math.Max(1, DeadlineMinutes.Value ?? 60) : null;
        return true;
    }

    Watcher? Edited(out string? error)
    {
        var w = JsonSerializer.Deserialize<Watcher>(JsonSerializer.Serialize(editing, Json.Options), Json.Options)!;
        return ApplyCommon(w, out error) ? w : null;
    }

    Watcher? Build(out string? error)
    {
        var w = new Watcher();
        var c = SelectedCond;
        switch (SelectedKind)
        {
            case Kind.Window:
                if (SelectedWindow is not { } win) { error = Loc.T("New.Error.Window"); return null; }
                w.Hwnd = win.Hwnd;
                if (SelectedCommand is { } cmd)
                {
                    w.Processes = [new ProcRef(cmd.Pid, cmd.Started, cmd.Exe)];
                    w.ExePath = ProcessInfo.ExePath(cmd.Pid);
                }
                else
                {
                    if (Targets.ByPid(win.Pid) is not { } owner) { error = Loc.T("New.Error.Gone"); return null; }
                    w.Processes = [owner];
                    w.ExePath = ProcessInfo.ExePath(win.Pid);
                    w.WindowClass = win.Class;
                    w.WindowTitle = TopWindows.TitleOf(win.Hwnd);
                }
                w.Kind = c switch { Cond.Exited => Kind.Process, Cond.Quiet => Kind.Quiet, _ when IsRegion(c) => Kind.Region, _ => Kind.Window };
                if (w.Kind == Kind.Region && SelectedCommand is not null) { error = Loc.T("New.Error.Window"); return null; }
                break;
            case Kind.Process:
                if (SelectedProc is not { } p) { error = Loc.T("New.Error.Process"); return null; }
                w.Processes = ProcAll.IsChecked == true ? Targets.Match(null, p.Exe, null) : Targets.Match(p.Pid, null, null);
                if (ProcAll.IsChecked == true) w.ProcessName = p.Exe;
                if (w.Processes.Count == 0) { error = Loc.T("New.Error.Gone"); return null; }
                w.ExePath = ProcessInfo.ExePath(p.Pid);
                w.Kind = c == Cond.Quiet ? Kind.Quiet : Kind.Process;
                break;
            case Kind.File:
                var path = FilePath.Text.Trim().Trim('"');
                if (path.Length == 0 || !Path.IsPathFullyQualified(path)) { error = Loc.T("New.Error.Path"); return null; }
                if (c != Cond.Appeared && FileProbe.Size(path) < 0) { error = Loc.T("New.Error.NoFile"); return null; }
                w.Kind = Kind.File;
                w.Path = Path.GetFullPath(path);
                break;
            default:
                w.Kind = Kind.Quiet;
                break;
        }
        return ApplyCommon(w, out error) ? w : null;
    }
}
