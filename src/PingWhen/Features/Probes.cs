using System.Diagnostics;
using System.IO;
using PingWhen.Shell;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Threading;

namespace PingWhen.Features;

/// <summary>
/// Performance counters read a whole category at once (ReadCategory: 1–2 ms after the first call, which loads perflib for ~5 s).
/// English names, so RU Windows works. Only categories needed by active watchers are read; per-instance deltas need two reads.
/// </summary>
public sealed class Metrics
{
    sealed class Category(string name, string counter)
    {
        Dictionary<string, CounterSample> prev = [];

        public Dictionary<string, float> Read()
        {
            var now = new Dictionary<string, CounterSample>();
            var values = new Dictionary<string, float>();
            var data = new PerformanceCounterCategory(name).ReadCategory()[counter];
            if (data is null) return values;
            foreach (InstanceData d in data.Values)
            {
                now[d.InstanceName] = d.Sample;
                if (prev.TryGetValue(d.InstanceName, out var p)) values[d.InstanceName] = CounterSample.Calculate(p, d.Sample);
            }
            prev = now;
            return values;
        }
    }

    readonly Category cpu = new("Processor", "% Processor Time");
    readonly Category gpu = new("GPU Engine", "Utilization Percentage");
    readonly Category disk = new("PhysicalDisk", "Disk Bytes/sec");
    readonly Category net = new("Network Interface", "Bytes Total/sec");
    Dictionary<Metric, Dictionary<string, float>> last = [];
    readonly Dictionary<int, (long Cpu100ns, long Io, long At)> procPrev = [];

    /// <summary>Once per tick, for the metrics active watchers need. A failed category reads as "no sample".</summary>
    public void Sample(IEnumerable<Metric> needed)
    {
        var next = new Dictionary<Metric, Dictionary<string, float>>();
        foreach (var m in needed.Distinct())
            try { next[m] = (m switch { Metric.Cpu => cpu, Metric.Gpu => gpu, Metric.Disk => disk, _ => net }).Read(); }
            catch (Exception e) { Log.Warn($"counter {m} failed", e); }
        last = next;
    }

    /// <summary>% for cpu/gpu, MB/s for disk, KB/s for net; null until two reads exist.</summary>
    public double? System(Metric m)
    {
        if (!last.TryGetValue(m, out var v) || v.Count == 0) return null;
        return m switch
        {
            Metric.Cpu => v.TryGetValue("_Total", out var t) ? t : null,
            Metric.Gpu => GpuPercent(v, _ => true),
            Metric.Disk => v.TryGetValue("_Total", out var d) ? d / 1048576.0 : null,
            _ => v.Values.Sum() / 1024.0,
        };
    }

    /// <summary>A process with its descendants. Network per process needs ETW (admin), so it has none.</summary>
    public double? Process(Metric m, IReadOnlyCollection<int> pids, long now)
    {
        switch (m)
        {
            case Metric.Gpu:
                if (!last.TryGetValue(m, out var v) || v.Count == 0) return null;
                var prefixes = pids.Select(p => $"pid_{p}_").ToArray();
                return GpuPercent(v, name => prefixes.Any(name.StartsWith));
            case Metric.Cpu or Metric.Disk:
                double? sum = null;
                foreach (var pid in pids)
                {
                    if (ProcStats(pid) is not { } s) continue;
                    if (procPrev.TryGetValue(pid, out var p) && now > p.At)
                    {
                        var sec = (now - p.At) / 1000.0;
                        sum = (sum ?? 0) + (m == Metric.Cpu
                            ? (s.Cpu100ns - p.Cpu100ns) / 1e7 / sec / Environment.ProcessorCount * 100
                            : (s.Io - p.Io) / sec / 1048576.0);
                    }
                    else sum ??= 0; // the first read of a new child: counts as 0, not as a gap
                    procPrev[pid] = (s.Cpu100ns, s.Io, now);
                }
                return sum;
            default: return null;
        }
    }

    public void ForgetProcesses(IEnumerable<int> alive)
    {
        var keep = alive.ToHashSet();
        foreach (var pid in procPrev.Keys.Where(p => !keep.Contains(p)).ToList()) procPrev.Remove(pid);
    }

    /// <summary>Busiest engine type (3D, copy, video…): the sum of its instances, as Task Manager shows it.</summary>
    static double? GpuPercent(Dictionary<string, float> v, Func<string, bool> filter)
    {
        var byType = v.Where(kv => filter(kv.Key))
            .GroupBy(kv => kv.Key[(kv.Key.LastIndexOf("_engtype_", StringComparison.Ordinal) + 1)..])
            .Select(g => g.Sum(kv => kv.Value)).ToList();
        return byType.Count == 0 ? 0 : Math.Min(100, byType.Max());
    }

    /// <summary>Kernel+user time and read+write bytes; null if the process is gone or denied (elevated: "no data").</summary>
    static unsafe (long Cpu100ns, long Io)? ProcStats(int pid)
    {
        using var h = PInvoke.OpenProcess_SafeHandle(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (h.IsInvalid) return null;
        if (!PInvoke.GetProcessTimes(h, out _, out _, out var k, out var u)) return null;
        static long L(global::System.Runtime.InteropServices.ComTypes.FILETIME f) => ((long)f.dwHighDateTime << 32) | (uint)f.dwLowDateTime;
        PInvoke.GetProcessIoCounters(h, out var io);
        return (L(k) + L(u), (long)(io.ReadTransferCount + io.WriteTransferCount));
    }
}

public static class WindowProbe
{
    /// <summary>The window's title, or null when it is gone or the handle now belongs to another process or class (reuse).</summary>
    public static string? Title(long hwnd, int pid, string? cls)
    {
        var h = (nint)hwnd;
        if (!PInvoke.IsWindow((HWND)h) || TopWindows.Pid(h) != pid) return null;
        if (cls is not null && TopWindows.ClassOf(h) != cls) return null;
        return TopWindows.TitleOf(h);
    }

    public static bool IsMinimized(long hwnd) => PInvoke.IsIconic((HWND)(nint)hwnd);

    /// <summary>Restore if minimized and bring to front ("Open window" on the toast).</summary>
    public static void Activate(long hwnd)
    {
        var h = (HWND)(nint)hwnd;
        if (!PInvoke.IsWindow(h)) return;
        if (PInvoke.IsIconic(h)) PInvoke.ShowWindow(h, Windows.Win32.UI.WindowsAndMessaging.SHOW_WINDOW_CMD.SW_RESTORE);
        PInvoke.SetForegroundWindow(h);
    }

    /// <summary>Top windows worth offering: not cloaked, with a title.</summary>
    public static List<TopWindow> Candidates() => TopWindows.List().Where(w => !w.Cloaked && w.Title.Length > 0).ToList();
}

public static class FileProbe
{
    /// <summary>The file's own attributes (Refresh), not the directory cache; -1 when it does not exist.</summary>
    public static long Size(string path)
    {
        var f = new FileInfo(path);
        f.Refresh();
        return f.Exists ? f.Length : -1;
    }
}

/// <summary>Commands running in a terminal's shells (spec §2 "Terminals").</summary>
public static class Terminals
{
    static readonly HashSet<string> Hosts = new(StringComparer.OrdinalIgnoreCase)
        { "WindowsTerminal.exe", "conhost.exe", "OpenConsole.exe", "Code.exe", "Code - Insiders.exe", "cmd.exe", "pwsh.exe", "powershell.exe", "wezterm-gui.exe", "alacritty.exe" };
    static readonly HashSet<string> Shells = new(StringComparer.OrdinalIgnoreCase)
        { "cmd.exe", "pwsh.exe", "powershell.exe", "bash.exe", "wsl.exe", "sh.exe", "zsh.exe", "nu.exe", "fish.exe" };
    static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase)
        { "conhost.exe", "OpenConsole.exe", "wslhost.exe", "wslrelay.exe" };

    public static bool IsTerminal(string? exe) => exe is not null && Hosts.Contains(Path.GetFileName(exe));

    public sealed record Command(int Pid, DateTime Started, string Shell, string Exe, string CommandLine);

    /// <summary>Children of every shell under (or equal to) the window's process, newest first. Nested shells count as shells.</summary>
    public static List<Command> Commands(int windowPid, ProcessTree tree)
    {
        var scope = new[] { tree[windowPid] }.Concat(tree.Descendants(windowPid)).OfType<ProcessNode>().ToList();
        var list = new List<Command>();
        foreach (var shell in scope.Where(p => Shells.Contains(p.Exe)))
            foreach (var c in tree.All.Where(c => c.ParentPid == shell.Pid && tree.Parent(c.Pid) == shell))
            {
                if (Skip.Contains(c.Exe) || c.Started is null) continue;
                if (Shells.Contains(c.Exe) && tree.Descendants(c.Pid).Any(d => !Skip.Contains(d.Exe))) continue; // a nested shell: its children are listed instead
                var line = ProcessInfo.CommandLine(c.Pid) ?? c.Exe;
                list.Add(new(c.Pid, c.Started.Value, Path.GetFileNameWithoutExtension(shell.Exe), c.Exe, line));
            }
        return list.OrderByDescending(c => c.Started).ToList();
    }

    /// <summary>"python train.py --epochs 20": exe name without path and .exe, then the arguments as typed.</summary>
    public static string Short(string commandLine)
    {
        var (exe, args) = CmdLine.SplitExe(commandLine);
        var name = Path.GetFileNameWithoutExtension(exe.Trim('"'));
        return (name + " " + args.Trim()).Trim();
    }
}

/// <summary>"Don't let the PC sleep while waiting": PowerSetRequest(SystemRequired), visible in powercfg /requests.</summary>
public static class KeepAwake
{
    static System.Runtime.InteropServices.SafeHandle? request;

    public static unsafe void Set(bool on)
    {
        if (on == request is not null) return;
        if (!on)
        {
            PInvoke.PowerClearRequest(request!, Windows.Win32.System.Power.POWER_REQUEST_TYPE.PowerRequestSystemRequired);
            request!.Dispose();
            request = null;
            return;
        }
        fixed (char* reason = "PingWhen: waiting for a watcher")
        {
            var ctx = new Windows.Win32.System.Threading.REASON_CONTEXT
            {
                Version = PInvoke.POWER_REQUEST_CONTEXT_VERSION,
                Flags = POWER_REQUEST_CONTEXT_FLAGS.POWER_REQUEST_CONTEXT_SIMPLE_STRING,
            };
            ctx.Reason.SimpleReasonString = reason;
            var h = PInvoke.PowerCreateRequest(ctx);
            if (h.IsInvalid || !PInvoke.PowerSetRequest(h, Windows.Win32.System.Power.POWER_REQUEST_TYPE.PowerRequestSystemRequired))
            {
                Log.Warn("power request failed");
                h.Dispose();
                return;
            }
            request = h;
        }
    }
}
