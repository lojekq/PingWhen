using System.Diagnostics;
using System.IO;
using System.Threading.Channels;
using PingWhen.Shell;

namespace PingWhen.Features;

/// <summary>A watcher fired (or was lost / cancelled). Duration is since the process started or the watcher was made.</summary>
public sealed record Fire(Watcher W, Outcome Outcome, string Title, string Body, TimeSpan Duration);

/// <summary>What the UI shows for an active watcher: Live is "312 MB/s", "quiet 12/30 s" and the like.</summary>
public sealed record WatcherView(Watcher W, string Live, double Progress);

/// <summary>
/// The list of watchers, owned by one loop: commands come through a Channel, a 1 s tick samples windows, files and metrics.
/// No locks: everything that touches the list runs inside the loop. No watchers → no tick, 0 % CPU.
/// Process exits arrive from Process.Exited (RegisterWaitForSingleObject inside .NET) as commands.
/// </summary>
public sealed class Engine
{
    sealed class Live(Watcher w)
    {
        public readonly Watcher W = w;
        public Conditions.QuietState Q = new(w.AlreadyRunning, null, 0);
        public Conditions.GrowthState G;
        public readonly List<Process> Handles = [];
        public readonly Dictionary<int, int?> Exited = [];        // pid → exit code (null = unknown)
        public readonly Dictionary<int, DateTime?> Children = [];  // WaitChildren: descendants seen while the parents ran
        public string Text = "";
        public double Progress;
        public long LastSize = -1, LastSizeAt;
    }

    public static Engine Current { get; set; } = new();

    /// <summary>On the engine loop: hand it off quickly.</summary>
    public event Action<Fire>? Fired;
    /// <summary>Snapshot after every tick and command, on the engine loop.</summary>
    public event Action<IReadOnlyList<WatcherView>>? Changed;

    readonly Channel<Action> commands = Channel.CreateUnbounded<Action>(new() { SingleReader = true });
    readonly List<Live> live = [];
    readonly Metrics metrics = new();
    bool dirty;
    long lastSave;
    Task? loop;

    public IReadOnlyList<WatcherView> Snapshot { get; private set; } = [];

    public void Start() => loop ??= Task.Run(Run);

    public void Post(Action a) => commands.Writer.TryWrite(a);

    /// <summary>Runs <paramref name="f"/> on the loop and returns its result (or its exception).</summary>
    public Task<T> Invoke<T>(Func<T> f)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() => { try { tcs.SetResult(f()); } catch (Exception e) { tcs.SetException(e); } });
        return tcs.Task;
    }

    public Task<Watcher> Add(Watcher w) => Invoke(() => { AddNow(w); return w; });

    public Task<bool> Cancel(string id) => Invoke(() =>
    {
        if (live.Find(l => l.W.Id == id) is not { } l) return false;
        Finish(l, Outcome.Cancelled, string.Format(Loc.T("Fire.Cancelled"), l.W.Title), "");
        return true;
    });

    /// <summary>Edit: the old watcher goes away silently, the new one (same Id) starts fresh.</summary>
    public Task<Watcher> Replace(Watcher w) => Invoke(() =>
    {
        if (live.Find(l => l.W.Id == w.Id) is { } old)
        {
            live.Remove(old);
            foreach (var h in old.Handles) h.Dispose();
        }
        AddNow(w);
        return w;
    });

    public Task<List<Watcher>> List() => Invoke(() => live.Select(l => l.W).ToList());

    /// <summary>After resume from sleep: quiet and growth timers start over (no false "went quiet" right after wake).</summary>
    public void ResetTimers() => Post(() =>
    {
        foreach (var l in live)
        {
            l.Q = l.Q with { BelowSince = null, LastAt = 0 };
            l.G = l.G with { StableSince = null, LastAt = 0 };
        }
    });

    /// <summary>At start: pids are checked against their start time; what doesn't match is re-found by name/command line or lost.</summary>
    public void Restore(IEnumerable<Watcher> saved) => Post(() =>
    {
        foreach (var w in saved)
        {
            if (w.ExpiresAt < DateTime.UtcNow) { Report(w, Outcome.Cancelled, string.Format(Loc.T("Fire.Expired"), w.Title), "", TimeSpan.Zero); continue; }
            var ok = w.Kind switch
            {
                Kind.File => true,
                Kind.Quiet when w.Processes.Count == 0 => true,
                Kind.Window => Targets.Alive(w.Processes).Count == 1 && WindowProbe.Title(w.Hwnd, w.Processes[0].Pid, w.WindowClass) is not null,
                _ => Reattach(w),
            };
            if (ok) AddNow(w);
            else Report(w, Outcome.Lost, string.Format(Loc.T("Fire.Lost"), w.Title), Loc.T("Fire.Lost.Body"), DateTime.UtcNow - w.CreatedAt);
        }
        dirty = true;
    });

    static bool Reattach(Watcher w)
    {
        var alive = Targets.Alive(w.Processes);
        if (alive.Count == 0 && (w.ProcessName is not null || w.CommandLineContains is not null))
            alive = Targets.Match(null, w.ProcessName, w.CommandLineContains);
        w.Processes = alive;
        return alive.Count > 0;
    }

    /// <summary>Saves watchers.json now (exit).</summary>
    public Task Flush() => Invoke(() => { Save(); return 0; });

    void AddNow(Watcher w)
    {
        var l = new Live(w);
        if (w.Kind == Kind.File && w.Path is not null) l.G = new(Math.Max(0, FileProbe.Size(w.Path)), false, null, 0);
        if (w.Kind == Kind.Process)
            foreach (var p in w.Processes) Attach(l, p);
        if (w.Kind == Kind.Process && w.WaitChildren)
        {
            var tree = ProcessInfo.Tree();
            foreach (var p in w.Processes)
                foreach (var d in tree.Descendants(p.Pid)) l.Children.TryAdd(d.Pid, d.Started);
        }
        live.Add(l);
        dirty = true;
        if (w.Kind == Kind.Process) CheckExited(l);
    }

    void Attach(Live l, ProcRef r)
    {
        try
        {
            var p = Process.GetProcessById(r.Pid);
            p.EnableRaisingEvents = true; // opens SYNCHRONIZE | QUERY_LIMITED and waits on the pool
            p.Exited += (_, _) => Post(() => OnExit(l, p));
            l.Handles.Add(p);
            if (p.HasExited) Post(() => OnExit(l, p));
        }
        catch (ArgumentException) { l.Exited[r.Pid] = null; }      // already gone
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Log.Warn("process wait denied, polling", e);              // polled in Tick by pid + start time, exit code unknown
        }
    }

    void OnExit(Live l, Process p)
    {
        if (!live.Contains(l) || l.Exited.ContainsKey(p.Id)) return;
        int? code = null;
        try { code = p.ExitCode; } catch { }
        l.Exited[p.Id] = code;
        CheckExited(l);
    }

    void CheckExited(Live l)
    {
        if (l.W.Processes.Any(p => !l.Exited.ContainsKey(p.Pid))) return;
        if (l.W.WaitChildren && l.Children.Count > 0) return; // Tick fires once the children are gone too
        var codes = l.W.Processes.Select(p => l.Exited[p.Pid]).ToList();
        var code = codes.FirstOrDefault(c => c is not null and not 0) ?? codes.FirstOrDefault(c => c is not null);
        var dur = Duration(l.W);
        var title = l.W.Title;
        if (code is not null and not 0)
            Finish(l, Outcome.Error, string.Format(Loc.T("Fire.Error"), title, code), string.Format(Loc.T("Fire.Exited.Code"), code), dur);
        else
            Finish(l, Outcome.Done, string.Format(Loc.T("Fire.Done"), title, Fmt.Duration(dur)),
                code is null ? Loc.T("Fire.Exited") : string.Format(Loc.T("Fire.Exited.Code"), code), dur);
    }

    static TimeSpan Duration(Watcher w)
    {
        var start = w.Processes.Count > 0 && w.Kind == Kind.Process ? w.Processes.Min(p => p.Started) : w.CreatedAt;
        var d = DateTime.UtcNow - start;
        return d < TimeSpan.Zero ? TimeSpan.Zero : d;
    }

    void Finish(Live l, Outcome o, string title, string body, TimeSpan? duration = null)
    {
        live.Remove(l);
        foreach (var h in l.Handles) h.Dispose();
        dirty = true;
        Report(l.W, o, title, body, duration ?? Duration(l.W));
    }

    void Report(Watcher w, Outcome o, string title, string body, TimeSpan d)
    {
        try { Fired?.Invoke(new Fire(w, o, title, body, d)); }
        catch (Exception e) { Log.Error("fired handler", e); }
    }

    async Task Run()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        Task<bool>? tick = null, read = null;
        while (true)
        try
        {
            while (commands.Reader.TryRead(out var cmd))
                try { cmd(); } catch (Exception e) { Log.Error("engine command", e); }
            Publish();
            if (live.Count == 0)
            {
                if (dirty) Save();
                tick = null;
                await (read ??= commands.Reader.WaitToReadAsync().AsTask());
                read = null;
                continue;
            }
            tick ??= timer.WaitForNextTickAsync().AsTask();
            read ??= commands.Reader.WaitToReadAsync().AsTask();
            var done = await Task.WhenAny(tick, read);
            if (done == read) { read = null; continue; }
            tick = null;
            try { Tick(); } catch (Exception e) { Log.Error("engine tick", e); }
            if (dirty && Environment.TickCount64 - lastSave >= 1000) Save(); // debounce 1 s
        }
        catch (Exception e) { Log.Error("engine loop", e); await Task.Delay(1000); } // never let the loop die
    }

    void Save()
    {
        dirty = false;
        lastSave = Environment.TickCount64;
        Store.SaveWatchers(live.Select(l => l.W));
        KeepAwake.Set(live.Any(l => l.W.KeepAwake));
    }

    void Publish()
    {
        Snapshot = live.Select(l => new WatcherView(l.W, l.Text, l.Progress)).ToList();
        try { Changed?.Invoke(Snapshot); } catch (Exception e) { Log.Error("engine changed handler", e); }
    }

    void Tick()
    {
        var now = Environment.TickCount64;
        var needTree = live.Any(l => (l.W.Kind == Kind.Process && (l.W.WaitChildren || l.Handles.Count < l.W.Processes.Count))
                                     || (l.W.Kind == Kind.Quiet && l.W.Processes.Count > 0));
        var tree = needTree ? ProcessInfo.Tree() : null;
        metrics.Sample(live.Where(l => l.W.Kind == Kind.Quiet).Select(l => l.W.Metric));

        foreach (var l in live.ToList())
        {
            var w = l.W;
            if (w.ExpiresAt < DateTime.UtcNow) { Finish(l, Outcome.Cancelled, string.Format(Loc.T("Fire.Expired"), w.Title), ""); continue; }
            switch (w.Kind)
            {
                case Kind.Process: TickProcess(l, tree!); break;
                case Kind.Window: TickWindow(l); break;
                case Kind.File: TickFile(l, now); break;
                case Kind.Quiet: TickQuiet(l, tree, now); break;
            }
        }
        if (tree is not null) metrics.ForgetProcesses(tree.All.Select(p => p.Pid));
    }

    void TickProcess(Live l, ProcessTree? tree)
    {
        var w = l.W;
        var running = w.Processes.Where(p => !l.Exited.ContainsKey(p.Pid)).ToList();
        l.Text = string.Join(", ", running.Select(p => p.Name).Distinct()) + (running.Count > 1 ? $" · {running.Count}" : "");
        if (tree is null) return;
        bool Alive(int pid, DateTime? started) => tree[pid] is { } n && Targets.Same(n.Started, started);
        foreach (var p in w.Processes.Where(p => !l.Exited.ContainsKey(p.Pid) && l.Handles.All(h => h.Id != p.Pid)))
            if (!Alive(p.Pid, p.Started)) l.Exited[p.Pid] = null; // polled: wait was denied
        if (w.WaitChildren)
        {
            foreach (var root in w.Processes.Select(p => p.Pid).Concat(l.Children.Keys).ToList())
                if (tree[root] is not null)
                    foreach (var d in tree.Descendants(root)) l.Children.TryAdd(d.Pid, d.Started);
            foreach (var (pid, started) in l.Children.ToList())
                if (!Alive(pid, started)) l.Children.Remove(pid);
            if (l.Children.Count > 0 && l.W.Processes.All(p => l.Exited.ContainsKey(p.Pid)))
                l.Text = string.Format(Loc.T("Live.Children"), l.Children.Count);
        }
        if (live.Contains(l)) CheckExited(l);
    }

    void TickWindow(Live l)
    {
        var w = l.W;
        var title = WindowProbe.Title(w.Hwnd, w.Processes.FirstOrDefault()?.Pid ?? 0, w.WindowClass);
        l.Text = title ?? "";
        if (!Conditions.Window(w.Until, title, w.WindowTitle, w.Pattern, w.IsRegex)) return;
        if (title is null) Finish(l, Outcome.Done, string.Format(Loc.T("Fire.Done"), w.Title, Fmt.Duration(Duration(w))), Loc.T("Fire.WindowClosed"));
        else Finish(l, Outcome.Done, string.Format(Loc.T("Fire.Done"), w.Title, Fmt.Duration(Duration(w))),
            string.Format(Loc.T(w.Until == Until.TitleChanged ? "Fire.TitleChanged" : "Fire.TitleMatched"), Fmt.Cut(title, 200)));
    }

    void TickFile(Live l, long now)
    {
        var w = l.W;
        var size = FileProbe.Size(w.Path!);
        var done = string.Format(Loc.T("Fire.Done"), Path.GetFileName(w.Path), Fmt.Duration(Duration(w)));
        switch (w.Until)
        {
            case Until.Appeared:
                l.Text = Loc.T(size >= 0 ? "Live.Exists" : "Live.Missing");
                if (size >= 0) Finish(l, Outcome.Done, done, Loc.T("Fire.FileAppeared"));
                return;
            case Until.Disappeared:
                l.Text = size >= 0 ? Fmt.Bytes(size) : Loc.T("Live.Missing");
                if (size < 0) Finish(l, Outcome.Done, done, Loc.T("Fire.FileGone"));
                return;
        }
        if (size < 0) { Finish(l, Outcome.Done, done, Loc.T("Fire.FileGone")); return; }
        var rate = l.LastSize >= 0 && now > l.LastSizeAt ? Math.Max(0, size - l.LastSize) * 1000.0 / (now - l.LastSizeAt) : 0;
        (l.LastSize, l.LastSizeAt) = (size, now);
        (l.G, var fired) = Conditions.Growth(l.G, size, w.QuietSeconds, now);
        var stable = l.G.StableSince is { } s ? (now - s) / 1000 : 0;
        l.Progress = l.G.StableSince is null ? 0 : Math.Min(1, stable / (double)w.QuietSeconds);
        l.Text = !l.G.GrowthSeen ? $"{Fmt.Bytes(size)} · {Loc.T("Live.WaitingGrowth")}"
            : l.G.StableSince is null ? $"{Fmt.Bytes((long)rate)}/{Loc.T("Unit.S")} · {Fmt.Bytes(size)}"
            : $"{Fmt.Bytes(size)} · {string.Format(Loc.T("Live.Stable"), stable, w.QuietSeconds)}";
        if (fired) Finish(l, Outcome.Done, done, string.Format(Loc.T("Fire.Stopped"), Fmt.Bytes(size)));
    }

    void TickQuiet(Live l, ProcessTree? tree, long now)
    {
        var w = l.W;
        double? value;
        if (w.Processes.Count == 0) value = metrics.System(w.Metric);
        else
        {
            var roots = w.Processes.Where(p => tree?[p.Pid] is { } n && Targets.Same(n.Started, p.Started)).Select(p => p.Pid).ToList();
            if (roots.Count == 0)
            {
                Finish(l, Outcome.Done, string.Format(Loc.T("Fire.Done"), w.Title, Fmt.Duration(Duration(w))), Loc.T("Fire.Exited"));
                return;
            }
            var pids = roots.Concat(roots.SelectMany(r => tree!.Descendants(r).Select(d => d.Pid))).Distinct().ToList();
            value = metrics.Process(w.Metric, pids, now);
        }
        (l.Q, var fired) = Conditions.Quiet(l.Q, value, w.Below, w.QuietSeconds, now);
        var quiet = l.Q.BelowSince is { } s ? (now - s) / 1000 : 0;
        l.Progress = Math.Min(1, quiet / (double)w.QuietSeconds);
        l.Text = $"{Fmt.Metric(w.Metric, value)} · " + (l.Q.Armed
            ? string.Format(Loc.T("Live.Quiet"), quiet, w.QuietSeconds)
            : string.Format(Loc.T("Live.WaitingAbove"), Fmt.Metric(w.Metric, w.Below)));
        if (fired)
            Finish(l, Outcome.Quiet, string.Format(Loc.T("Fire.Quiet"), w.Title, Fmt.Duration(Duration(w))),
                string.Format(Loc.T("Fire.Quiet.Body"), Fmt.MetricName(w.Metric), Fmt.Metric(w.Metric, w.Below), w.QuietSeconds));
    }
}

/// <summary>Finding targets when a watcher is made (UI, MCP, CLI) and after a restart.</summary>
public static class Targets
{
    /// <summary>Start times from the toolhelp snapshot and from GetProcessTimes agree; allow rounding.</summary>
    public static bool Same(DateTime? a, DateTime? b) => a is not null && b is not null && Math.Abs((a.Value - b.Value).TotalSeconds) < 1;

    public static List<ProcRef> Alive(IEnumerable<ProcRef> refs) => refs.Where(r => Same(ProcessInfo.StartTime(r.Pid), r.Started)).ToList();

    public static ProcRef? ByPid(int pid) =>
        ProcessInfo.StartTime(pid) is { } t ? new ProcRef(pid, t, Path.GetFileName(ProcessInfo.ExePath(pid) ?? $"pid {pid}")) : null;

    /// <summary>All matching processes right now (not this app). Name with or without ".exe", command line substring ignoring case.</summary>
    public static List<ProcRef> Match(int? pid, string? name, string? commandLineContains)
    {
        if (pid is { } id) return ByPid(id) is { } r ? [r] : [];
        var tree = ProcessInfo.Tree();
        var self = Environment.ProcessId;
        var exe = name is null ? null : name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";
        return tree.All
            .Where(p => p.Pid != self && p.Pid > 4 && p.Started is not null && !p.Exe.Equals(AppInfo.Name + ".exe", StringComparison.OrdinalIgnoreCase))
            .Where(p => exe is null || p.Exe.Equals(exe, StringComparison.OrdinalIgnoreCase))
            .Where(p => commandLineContains is null || ProcessInfo.CommandLine(p.Pid)?.Contains(commandLineContains, StringComparison.OrdinalIgnoreCase) == true)
            .Select(p => new ProcRef(p.Pid, p.Started!.Value, p.Exe))
            .ToList();
    }
}

/// <summary>Numbers for people, in the UI language.</summary>
public static class Fmt
{
    public static string Duration(TimeSpan d) =>
        d.TotalMinutes < 1 ? string.Format(Loc.T("Dur.S"), (int)d.TotalSeconds)
        : d.TotalHours < 1 ? string.Format(Loc.T("Dur.M"), (int)d.TotalMinutes)
        : string.Format(Loc.T("Dur.HM"), (int)d.TotalHours, d.Minutes);

    static IFormatProvider Ui => System.Globalization.CultureInfo.CurrentUICulture;

    public static string Bytes(long b) =>
        b < 1024 * 1024 ? string.Format(Loc.T("Unit.KB"), (b / 1024.0).ToString("0", Ui))
        : b < 1024L * 1024 * 1024 ? string.Format(Loc.T("Unit.MB"), (b / 1048576.0).ToString("0", Ui))
        : string.Format(Loc.T("Unit.GB"), (b / 1073741824.0).ToString("0.0", Ui));

    public static string Metric(Metric m, double? v) => v is not { } x ? "—" : m switch
    {
        Features.Metric.Disk => string.Format(Loc.T("Unit.MBs"), x.ToString(x < 10 ? "0.0" : "0", Ui)),
        Features.Metric.Net => string.Format(Loc.T("Unit.KBs"), x.ToString("0", Ui)),
        _ => $"{x:0} %",
    };

    public static string MetricName(Metric m) => Loc.T("Metric." + m);

    public static string Cut(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
