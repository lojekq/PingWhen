using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PingWhen.Features;

public enum Kind { Process, Window, File, Quiet, Region }
public enum Until { Exited, Closed, TitleChanged, TitleMatches, StoppedGrowing, Appeared, Disappeared, Quiet, Flashed, Unchanged, Changed, TextMatches }
public enum Metric { Cpu, Gpu, Disk, Net }
public enum Route { Auto, Pc, Phone, Both }
public enum Priority { Low, Default, High }
public enum Outcome { Done, Error, Quiet, Lost, Cancelled, Notify, Overdue }

/// <summary>A process as matched when the watcher was made. Started (UTC) guards against pid reuse.</summary>
public sealed record ProcRef(int Pid, DateTime Started, string Name);

/// <summary>What watchers.json keeps (spec §5.4). Runtime state lives in Engine.</summary>
public sealed class Watcher
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public Kind Kind { get; set; }
    public Until Until { get; set; }

    // Process, Quiet of a process, owner of a Window
    public List<ProcRef> Processes { get; set; } = [];
    public string? ProcessName { get; set; }            // selector by name: re-matched after a restart
    public string? CommandLineContains { get; set; }    // selector by command line: re-matched after a restart
    public bool WaitChildren { get; set; }

    // Window; Hwnd is also the "Open window" target of the toast for any kind
    public long Hwnd { get; set; }
    public string? WindowClass { get; set; }
    public string? WindowTitle { get; set; }            // title when the watcher was made
    public string? Pattern { get; set; }
    public bool IsRegex { get; set; }

    // File
    public string? Path { get; set; }
    public int QuietSeconds { get; set; } = 30;         // File: stopped growing for N s; Quiet: below the threshold for N s

    // Quiet: whole system when Processes is empty
    public Metric Metric { get; set; }
    public double Below { get; set; }                   // % for cpu/gpu, MB/s for disk, KB/s for net
    public bool AlreadyRunning { get; set; }            // armed from the start

    public string Title { get; set; } = "";
    public string? ExePath { get; set; }                // for the card icon
    public Route Route { get; set; }
    public bool KeepAwake { get; set; }
    public string CreatedBy { get; set; } = "ui";       // ui | cli | mcp:<client>/<folder>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresAt { get; set; }

    /// <summary>v1.0: keeps watching after it fires ("every time ffmpeg.exe exits"), at most once a minute.</summary>
    public bool Repeat { get; set; }
    /// <summary>v1.0: "not done within X min" once, then keeps watching (catches stuck jobs).</summary>
    public int? DeadlineMinutes { get; set; }
    public bool DeadlineSent { get; set; }

    /// <summary>v1.0 Region: fractions of the window frame; Pattern is the OCR text for TextMatches.</summary>
    public double RegionX { get; set; }
    public double RegionY { get; set; }
    public double RegionW { get; set; } = 1;
    public double RegionH { get; set; } = 1;
    [JsonIgnore] public Frames.Rect Region => new(RegionX, RegionY, RegionW, RegionH);
    /// <summary>v1.0: a JPEG of the window (or region) goes with the notification. Off by default: it leaves the PC.</summary>
    public bool Screenshot { get; set; }

    public static TimeSpan RepeatPause { get; internal set; } = TimeSpan.FromSeconds(60); // tests shorten it

    public static double DefaultBelow(Metric m) => m switch { Metric.Disk => 1, Metric.Net => 100, _ => 5 };
    public static int DefaultSeconds(Metric m) => m == Metric.Net ? 60 : 30;
}

/// <summary>One line of history.jsonl. A later line with the same Id replaces the earlier one (delivery result arrives later).</summary>
public sealed record HistoryEntry(string Id, DateTime Ts, string Watcher, string Title, string Body, Outcome Outcome, string Channels, double Seconds = 0);

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>%APPDATA%\PingWhen\watchers.json and history.jsonl (spec §5.7). Nothing is written under --demo.</summary>
public static class Store
{
    sealed record WatchersFile(int Version, List<Watcher> Watchers);

    public static string Dir { get; set; } = Shell.AppInfo.DataDir;
    static string WatchersPath => System.IO.Path.Combine(Dir, "watchers.json");
    static string HistoryPath => System.IO.Path.Combine(Dir, "history.jsonl");
    public static bool ReadOnly { get; set; }
    public const int HistoryLimit = 500;

    static readonly Lock HistoryGate = new();

    public static List<Watcher> LoadWatchers()
    {
        try { return Shell.AtomicFile.Read(WatchersPath, s => JsonSerializer.Deserialize<WatchersFile>(s, Json.Options))?.Watchers ?? []; }
        catch (Exception e) { Shell.Log.Error("watchers.json not read", e); return []; }
    }

    public static void SaveWatchers(IEnumerable<Watcher> list)
    {
        if (ReadOnly) return;
        try { Shell.AtomicFile.WriteAllText(WatchersPath, JsonSerializer.Serialize(new WatchersFile(1, list.ToList()), Json.Options)); }
        catch (Exception e) { Shell.Log.Error("watchers.json not saved", e); }
    }

    public static void Append(HistoryEntry e)
    {
        if (ReadOnly) return;
        try
        {
            lock (HistoryGate)
            {
                Directory.CreateDirectory(Dir);
                File.AppendAllText(HistoryPath, JsonSerializer.Serialize(e, Json.Options) + "\n");
            }
        }
        catch (Exception ex) { Shell.Log.Error("history not written", ex); }
    }

    /// <summary>Newest first, the last 500; rewrites the file trimmed (at start).</summary>
    public static List<HistoryEntry> LoadHistory()
    {
        lock (HistoryGate)
        {
            if (!File.Exists(HistoryPath)) return [];
            var byId = new Dictionary<string, HistoryEntry>();
            var order = new List<string>();
            var lines = 0;
            foreach (var line in File.ReadLines(HistoryPath))
            {
                lines++;
                HistoryEntry? e = null;
                try { e = JsonSerializer.Deserialize<HistoryEntry>(line, Json.Options); } catch (JsonException) { } // a torn last line after a crash
                if (e is null) continue;
                if (!byId.ContainsKey(e.Id)) order.Add(e.Id);
                byId[e.Id] = e;
            }
            var keep = order.TakeLast(HistoryLimit).Select(id => byId[id]).ToList();
            if (!ReadOnly && keep.Count != lines) // trimmed, merged delivery results or torn lines
                Shell.AtomicFile.WriteAllText(HistoryPath, string.Concat(keep.Select(e => JsonSerializer.Serialize(e, Json.Options) + "\n")));
            keep.Reverse();
            return keep;
        }
    }

    public static void ClearHistory()
    {
        if (ReadOnly) return;
        lock (HistoryGate) try { File.Delete(HistoryPath); } catch (Exception e) { Shell.Log.Error("history not cleared", e); }
    }
}

/// <summary>Watcher target and condition in words, for cards, the tray menu and list_watchers.</summary>
public static class Describe
{
    public static string Target(Watcher w) => w.Kind switch
    {
        Kind.Process => w.CommandLineContains ?? w.ProcessName ?? string.Join(", ", w.Processes.Select(p => $"{p.Name} ({p.Pid})")),
        Kind.Window => w.WindowTitle ?? "",
        Kind.File => w.Path ?? "",
        Kind.Region => w.WindowTitle ?? "",
        _ => w.Processes.Count == 0 ? Shell.Loc.T("Target.System") : w.ProcessName ?? w.Processes[0].Name,
    };

    public static string Condition(Watcher w)
    {
        string T(string k) => Shell.Loc.T(k);
        var extra = (w.Repeat ? " · " + T("Cond.Repeat") : "") + (w.DeadlineMinutes is { } m ? " · " + string.Format(T("Cond.Deadline"), m) : "");
        return Base() + extra;

        string Base() => w.Kind switch
        {
            Kind.Process => T(w.WaitChildren ? "Cond.ExitedChildren" : "Cond.Exited"),
            Kind.Window => w.Until switch
            {
                Until.TitleChanged => T("Cond.TitleChanged"),
                Until.TitleMatches => string.Format(T(w.IsRegex ? "Cond.TitleRegex" : "Cond.TitleContains"), w.Pattern),
                Until.Flashed => T("Cond.Flashed"),
                _ => T("Cond.Closed"),
            },
            Kind.Region => w.Until switch
            {
                Until.Changed => T("Cond.RegionChanged"),
                Until.TextMatches => string.Format(T("Cond.RegionText"), w.Pattern),
                _ => string.Format(T("Cond.RegionUnchanged"), w.QuietSeconds),
            },
            Kind.File => w.Until switch
            {
                Until.Appeared => string.Format(T("Cond.Appeared"), System.IO.Path.GetFileName(w.Path)),
                Until.Disappeared => string.Format(T("Cond.Disappeared"), System.IO.Path.GetFileName(w.Path)),
                _ => string.Format(T("Cond.StoppedGrowing"), System.IO.Path.GetFileName(w.Path), w.QuietSeconds),
            },
            _ => string.Format(T("Cond.Quiet"), Fmt.MetricName(w.Metric), Fmt.Metric(w.Metric, w.Below), w.QuietSeconds),
        };
    }
}
