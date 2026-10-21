using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using PingWhen.Shell;

namespace PingWhen.Features;

/// <summary>First line of every pipe connection: {"v":1,"mode":"mcp|hook|notify","pid":…,"ancestors":[…],"cwd":"…"}.</summary>
public sealed record Hello(int V, string Mode, int Pid, int[] Ancestors, string? Cwd)
{
    public const int Version = 1;
    public string Folder => Path.GetFileName((Cwd ?? "").TrimEnd('\\', '/')) is { Length: > 0 } f ? f : "?";
}

/// <summary>An MCP client connected through --mcp: shown on the Agents page.</summary>
public sealed class AgentSession(Hello hello) : INotifyPropertyChanged
{
    public Hello Hello { get; } = hello;
    public string Folder => Hello.Folder;
    public DateTime Since { get; } = DateTime.Now;
    string client = "mcp";
    public string Client { get => client; set { client = value; PropertyChanged?.Invoke(this, new(nameof(Client))); PropertyChanged?.Invoke(this, new(nameof(Key))); } }
    /// <summary>Watcher.CreatedBy for this session: stable across reconnects of the same client in the same folder.</summary>
    public string Key => $"mcp:{Client}/{Folder}";
    public readonly Queue<DateTime> Notifies = new();
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// \\.\pipe\PingWhen-&lt;user SID&gt;: ACL for the current user's SID only (not CurrentUserOnly, which also compares elevation
/// and would lock out Claude Code started from an admin terminal). Up to 16 connections, one task each.
/// </summary>
public static class PipeServer
{
    public static string Name { get; } = $"{AppInfo.Name}-{WindowsIdentity.GetCurrent().User!.Value}";
    public static ObservableCollection<AgentSession> Sessions { get; } = [];
    public static bool Running { get; private set; }

    public static async Task Run(string name, CancellationToken ct)
    {
        Running = true;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                NamedPipeServerStream pipe;
                try { pipe = Create(name); }
                catch (IOException) { await Task.Delay(500, ct); continue; } // all 16 instances busy
                try { await pipe.WaitForConnectionAsync(ct); }
                catch { pipe.Dispose(); throw; }
                _ = Task.Run(() => Handle(pipe, ct), CancellationToken.None);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Log.Error("pipe server stopped", e); } // e.g. the name is taken by another user's pipe: agents are off, the app lives
        finally { Running = false; }
    }

    static NamedPipeServerStream Create(string name)
    {
        var sec = new PipeSecurity();
        sec.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
        sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny)); // not over SMB
        return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 16, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, sec);
    }

    static async Task Handle(NamedPipeServerStream pipe, CancellationToken ct)
    {
        await using var _ = pipe;
        try
        {
            var line = await ReadLine(pipe, 4096, ct);
            var hello = line is null ? null : JsonSerializer.Deserialize<Hello>(line, Json.Options);
            if (hello is null || hello.V != Hello.Version)
            {
                await WriteLine(pipe, """{"ok":false,"error":"version"}""", ct);
                return;
            }
            await WriteLine(pipe, """{"ok":true}""", ct);
            switch (hello.Mode)
            {
                case "mcp": await Mcp.Serve(pipe, hello, ct); break;
                case "hook":
                    if (await ReadLine(pipe, 128 * 1024, ct) is { } hook) Hooks.Handle(hook, hello);
                    await WriteLine(pipe, """{"ok":true}""", ct);
                    break;
                case "notify":
                    var n = await ReadLine(pipe, 16 * 1024, ct) is { } s ? JsonNode.Parse(s) : null;
                    var title = Fmt.Cut((string?)n?["title"] ?? AppInfo.DisplayName, 120);
                    Notifier.Notify(title, Fmt.Cut((string?)n?["body"] ?? "", 1000), Priority.Default, Route.Auto, "cli");
                    await WriteLine(pipe, """{"ok":true}""", ct);
                    break;
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or JsonException or ObjectDisposedException) { }
        catch (Exception e) { Log.Error("pipe connection", e); }
    }

    /// <summary>Byte by byte up to \n: a buffered reader would swallow the start of the MCP stream.</summary>
    public static async Task<string?> ReadLine(Stream s, int max, CancellationToken ct)
    {
        var buf = new List<byte>();
        var one = new byte[1];
        while (buf.Count < max)
        {
            if (await s.ReadAsync(one, ct) == 0) return null;
            if (one[0] == '\n') return Encoding.UTF8.GetString(buf.ToArray());
            buf.Add(one[0]);
        }
        return null;
    }

    public static async Task WriteLine(Stream s, string line, CancellationToken ct)
    {
        await s.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), ct);
        await s.FlushAsync(ct);
    }
}

/// <summary>MCP tools (spec §5.6), served by the SDK in the tray process over the pipe.</summary>
public static class Mcp
{
    public const int NotifyPerMinute = 6, NotifyPerHour = 60, MaxWatchers = 50, MaxPerSession = 20;
    public static readonly TimeSpan AgentWatcherLife = TimeSpan.FromHours(24);
    static readonly Queue<DateTime> HourNotifies = new();

    public const string Instructions =
        "PingWhen sends the user a Windows toast and, when they are away, a Telegram message. " +
        "\"Done\" and \"needs your answer\" are already sent by the Claude Code hooks; use notify only for special cases. " +
        "For a long background command use watch_process with command_line_contains: $! in Git Bash is not a Windows PID. " +
        "Watchers made by agents expire after 24 h.";

    public static async Task Serve(Stream pipe, Hello hello, CancellationToken ct)
    {
        var session = new AgentSession(hello);
        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "pingwhen", Version = AppInfo.Version },
            ServerInstructions = Instructions,
            ToolCollection = [.. Tools(session)],
        };
        await using var server = McpServer.Create(new StreamServerTransport(pipe, pipe, "pingwhen"), options);
        Ui(() => PipeServer.Sessions.Add(session));
        try { await server.RunAsync(ct); }
        finally { Ui(() => PipeServer.Sessions.Remove(session)); }
    }

    static void Ui(Action a)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) a(); else d.BeginInvoke(a);
    }

    static void Identify(AgentSession s, McpServer server)
    {
        if (server.ClientInfo?.Name is { Length: > 0 } n && n != s.Client) s.Client = n; // now: the same request may create a watcher with s.Key
    }

    public static IEnumerable<McpServerTool> Tools(AgentSession s)
    {
        yield return McpServerTool.Create(
            (McpServer server,
             [Description("Short title, at most 120 characters.")] string title,
             [Description("Body, at most 1000 characters.")] string? message = null,
             [Description("low | default | high. low = silent on the phone.")] string? priority = null,
             [Description("auto (toast; phone when the user is away or in a fullscreen game) | pc | phone | both.")] string? route = null) =>
            { Identify(s, server); return Notify(s, title, message, priority, route); },
            new() { Name = "notify", Description = "Send the user a notification now: a toast on the PC and/or a Telegram message.", Destructive = false, OpenWorld = true });

        yield return McpServerTool.Create(
            (McpServer server,
             [Description("Windows process id.")] int? pid = null,
             [Description("Process name, e.g. ffmpeg or ffmpeg.exe. All matching processes are watched.")] string? name = null,
             [Description("Substring of the command line, e.g. train.py. Use this for background commands.")] string? command_line_contains = null,
             [Description("Title for the notification.")] string? title = null,
             [Description("auto | pc | phone | both.")] string? route = null,
             [Description("Keep the PC awake until it ends.")] bool? keep_awake = null) =>
            { Identify(s, server); return WatchProcess(s, pid, name, command_line_contains, title, route, keep_awake ?? false); },
            new() { Name = "watch_process", Description = "Notify the user when a process (or every process matching) exits, with duration and exit code.", Destructive = false, OpenWorld = false });

        yield return McpServerTool.Create(
            (McpServer server,
             [Description("Absolute path.")] string path,
             [Description("stopped_growing | appeared | disappeared.")] string until,
             [Description("stopped_growing: seconds without growth, default 30, minimum 5.")] int? quiet_seconds = null,
             [Description("Title for the notification.")] string? title = null) =>
            { Identify(s, server); return WatchFile(s, path, until, quiet_seconds, title); },
            new() { Name = "watch_file", Description = "Notify the user when a file stops growing (after it was seen growing), appears or disappears.", Destructive = false, OpenWorld = false });

        yield return McpServerTool.Create(
            (McpServer server,
             [Description("Part of the window title (case-insensitive).")] string? title_contains = null,
             [Description("Window handle as a number.")] long? hwnd = null,
             [Description("closed | title_changed | title_matches.")] string until = "closed",
             [Description("title_matches: text to look for in the title.")] string? pattern = null,
             [Description("title_matches: pattern is a .NET regex (100 ms timeout).")] bool? regex = null) =>
            { Identify(s, server); return WatchWindow(s, title_contains, hwnd, until, pattern, regex ?? false); },
            new() { Name = "watch_window", Description = "Notify the user when a window closes, changes its title or its title matches.", Destructive = false, OpenWorld = false });

        yield return McpServerTool.Create(
            (McpServer server,
             [Description("cpu | gpu | disk | net.")] string metric,
             [Description("Process name: the process with its children; omit for the whole system. net is system only.")] string? process = null,
             [Description("Threshold: % for cpu/gpu (default 5), MB/s for disk (default 1), KB/s for net (default 100).")] double? below = null,
             [Description("Seconds below the threshold, default 30 (net 60).")] int? seconds = null,
             [Description("Count as busy already (do not wait until the metric goes above the threshold first).")] bool? already_running = null) =>
            { Identify(s, server); return WatchQuiet(s, metric, process, below, seconds, already_running ?? false); },
            new() { Name = "watch_quiet", Description = "Notify the user when CPU, GPU, disk or network stays below a threshold. Arms only after the metric was above it.", Destructive = false, OpenWorld = false });

        yield return McpServerTool.Create(
            (McpServer server) => { Identify(s, server); return ListWatchers(); },
            new() { Name = "list_watchers", Description = "Active watchers: id, kind, target, state, elapsed, author.", ReadOnly = true, OpenWorld = false });

        yield return McpServerTool.Create(
            (McpServer server, [Description("Watcher id from watch_* or list_watchers.")] string id) =>
            { Identify(s, server); return Cancel(id); },
            new() { Name = "cancel_watcher", Description = "Stop a watcher without notifying.", Destructive = false, Idempotent = true, OpenWorld = false });
    }

    static T Parse<T>(string? value, T fallback, string arg) where T : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        return Enum.TryParse<T>(value.Replace("_", ""), true, out var v) && Enum.IsDefined(v) ? v
            : throw new McpException($"{arg}: unknown value '{value}'");
    }

    static void Check(bool ok, string error) { if (!ok) throw new McpException(error); }

    public static object Notify(AgentSession s, string title, string? message, string? priority, string? route)
    {
        Check(!string.IsNullOrWhiteSpace(title), "title is required");
        Check(title.Length <= 120, "title: at most 120 characters");
        Check((message?.Length ?? 0) <= 1000, "message: at most 1000 characters");
        var p = Parse(priority, Priority.Default, "priority");
        var r = Parse(route, Route.Auto, "route");
        var now = DateTime.UtcNow;
        lock (HourNotifies)
        {
            while (s.Notifies.TryPeek(out var t) && now - t > TimeSpan.FromMinutes(1)) s.Notifies.Dequeue();
            while (HourNotifies.TryPeek(out var t) && now - t > TimeSpan.FromHours(1)) HourNotifies.Dequeue();
            Check(s.Notifies.Count < NotifyPerMinute, $"rate limit: at most {NotifyPerMinute} notifications per minute per session");
            Check(HourNotifies.Count < NotifyPerHour, $"rate limit: at most {NotifyPerHour} notifications per hour");
            s.Notifies.Enqueue(now);
            HourNotifies.Enqueue(now);
        }
        var (pc, phone) = Notifier.Notify($"{s.Folder}: {title}", message ?? "", p, r, s.Key);
        return new { pc = pc ? "sent" : "off", phone = phone ? "queued" : Notifier.PhoneReady ? "off" : "not configured" };
    }

    static Watcher New(AgentSession s, Kind kind, Until until, string title, string? route, bool keepAwake)
    {
        var all = Engine.Current.Snapshot;
        Check(all.Count < MaxWatchers, $"limit: at most {MaxWatchers} watchers");
        Check(all.Count(v => v.W.CreatedBy == s.Key) < MaxPerSession, $"limit: at most {MaxPerSession} watchers per session");
        return new Watcher
        {
            Kind = kind, Until = until, Title = $"{s.Folder}: {Fmt.Cut(title, 100)}", Route = Parse(route, Route.Auto, "route"),
            KeepAwake = keepAwake, CreatedBy = s.Key, ExpiresAt = DateTime.UtcNow + AgentWatcherLife,
        };
    }

    public static async Task<object> WatchProcess(AgentSession s, int? pid, string? name, string? cmd, string? title, string? route, bool keepAwake)
    {
        Check(new object?[] { pid, name, cmd }.Count(x => x is not null) == 1, "give exactly one of pid, name, command_line_contains");
        Check(cmd is null || cmd.Trim().Length >= 3, "command_line_contains: at least 3 characters");
        var found = Targets.Match(pid, name?.Trim(), cmd?.Trim());
        Check(found.Count > 0, "no running process matches");
        Check(found.Count <= 64, $"{found.Count} processes match: narrow it down");
        var w = New(s, Kind.Process, Until.Exited, title ?? cmd ?? Path.GetFileNameWithoutExtension(found[0].Name), route, keepAwake);
        w.Processes = found;
        w.ProcessName = name;
        w.CommandLineContains = cmd;
        w.ExePath = ProcessInfo.ExePath(found[0].Pid);
        await Engine.Current.Add(w);
        return new { id = w.Id, processes = found.Select(p => new { pid = p.Pid, name = p.Name }) };
    }

    public static async Task<object> WatchFile(AgentSession s, string path, string until, int? quietSeconds, string? title)
    {
        Check(!string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path), "path must be absolute");
        var u = Parse(until, Until.StoppedGrowing, "until");
        Check(u is Until.StoppedGrowing or Until.Appeared or Until.Disappeared, "until: stopped_growing | appeared | disappeared");
        var size = FileProbe.Size(path);
        Check(u == Until.Appeared || size >= 0, "file not found");
        var w = New(s, Kind.File, u, title ?? Path.GetFileName(path), null, false);
        w.Path = Path.GetFullPath(path);
        w.QuietSeconds = Math.Max(5, quietSeconds ?? 30);
        await Engine.Current.Add(w);
        return new { id = w.Id, size = size >= 0 ? size : (long?)null };
    }

    public static async Task<object> WatchWindow(AgentSession s, string? titleContains, long? hwnd, string until, string? pattern, bool regex)
    {
        Check((titleContains is null) != (hwnd is null), "give exactly one of title_contains, hwnd");
        var u = Parse(until, Until.Closed, "until");
        Check(u is Until.Closed or Until.TitleChanged or Until.TitleMatches, "until: closed | title_changed | title_matches");
        Check(u != Until.TitleMatches || !string.IsNullOrEmpty(pattern), "title_matches needs pattern");
        if (regex && pattern is not null && Conditions.RegexError(pattern) is { } err) throw new McpException("pattern: " + err);
        var win = hwnd is { } h
            ? TopWindows.List().FirstOrDefault(x => x.Hwnd == (nint)h)
            : WindowProbe.Candidates().FirstOrDefault(x => x.Title.Contains(titleContains!, StringComparison.OrdinalIgnoreCase));
        Check(win is not null, "no such window");
        Check(Targets.ByPid(win!.Pid) is not null, "the window's process is not accessible");
        var w = New(s, Kind.Window, u, Fmt.Cut(win.Title, 80), null, false);
        w.Hwnd = win.Hwnd;
        w.Processes = [Targets.ByPid(win.Pid)!];
        w.WindowClass = win.Class;
        w.WindowTitle = win.Title;
        w.Pattern = pattern;
        w.IsRegex = regex;
        w.ExePath = ProcessInfo.ExePath(win.Pid);
        await Engine.Current.Add(w);
        return new { id = w.Id, window = new { hwnd = (long)win.Hwnd, title = win.Title } };
    }

    public static async Task<object> WatchQuiet(AgentSession s, string metric, string? process, double? below, int? seconds, bool alreadyRunning)
    {
        var m = Parse<Metric>(metric, Metric.Cpu, "metric");
        Check(string.IsNullOrWhiteSpace(metric) == false, "metric is required");
        Check(m != Metric.Net || process is null, "net is system-wide only");
        Check(below is null or > 0, "below must be positive");
        var w = New(s, Kind.Quiet, Until.Quiet, process ?? Loc.T("Target.System"), null, false);
        if (process is not null)
        {
            var found = Targets.Match(null, process.Trim(), null);
            Check(found.Count > 0, "no running process matches");
            w.Processes = found;
            w.ProcessName = process.Trim();
            w.ExePath = ProcessInfo.ExePath(found[0].Pid);
        }
        w.Metric = m;
        w.Below = below ?? Watcher.DefaultBelow(m);
        w.QuietSeconds = Math.Max(5, seconds ?? Watcher.DefaultSeconds(m));
        w.AlreadyRunning = alreadyRunning;
        await Engine.Current.Add(w);
        return new { id = w.Id };
    }

    public static object ListWatchers() => Engine.Current.Snapshot.Select(v => new
    {
        id = v.W.Id,
        kind = v.W.Kind.ToString().ToLowerInvariant(),
        target = Describe.Target(v.W),
        state = v.Live,
        elapsed_seconds = (int)(DateTime.UtcNow - v.W.CreatedAt).TotalSeconds,
        author = v.W.CreatedBy,
    }).ToList();

    public static async Task<object> Cancel(string id)
    {
        Check(await Engine.Current.Cancel(id), "no watcher with this id");
        return new { ok = true };
    }
}

/// <summary>Claude Code hooks, tray side (spec §5.6): the --hook process forwards the hook JSON here.</summary>
public static class Hooks
{
    public sealed record Input(string Event, string? SessionId, string? Cwd, string? NotificationType, string? Message, bool StopHookActive, string? LastAssistantMessage)
    {
        public string Folder => Path.GetFileName((Cwd ?? "").TrimEnd('\\', '/')) is { Length: > 0 } f ? f : "Claude";
    }

    /// <summary>Null for anything unexpected: garbage never throws.</summary>
    public static Input? Parse(string json)
    {
        try
        {
            using var d = JsonDocument.Parse(json);
            var r = d.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;
            string? S(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            return S("hook_event_name") is { } e
                ? new Input(e, S("session_id"), S("cwd"), S("notification_type"), S("message"),
                    r.TryGetProperty("stop_hook_active", out var a) && a.ValueKind == JsonValueKind.True, S("last_assistant_message"))
                : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Seen: the session's own terminal/editor is the foreground window and the user is at the PC.</summary>
    public sealed record Context(DateTime Now, DateTime? LastStop, bool Seen, bool ClaudeText);

    public static readonly TimeSpan IdleAfterStop = TimeSpan.FromMinutes(5);

    public static Note? Decide(Input e, Context c)
    {
        Note? n = e.Event switch
        {
            "Stop" when !e.StopHookActive => new Note(string.Format(Loc.T("Hook.Done"), e.Folder),
                c.ClaudeText && e.LastAssistantMessage is { Length: > 0 } m ? Fmt.Cut(m.ReplaceLineEndings(" ").Trim(), 150) : ""),
            "Notification" => e.NotificationType switch
            {
                "permission_prompt" or "elicitation_dialog" or "agent_needs_input" =>
                    new Note(string.Format(Loc.T("Hook.Needs"), e.Folder), Fmt.Cut(e.Message ?? "", 300), Priority.High),
                "idle_prompt" when c.LastStop is not { } t || c.Now - t > IdleAfterStop =>
                    new Note(string.Format(Loc.T("Hook.Needs"), e.Folder), Fmt.Cut(e.Message ?? "", 300)),
                _ => null,
            },
            _ => null,
        };
        return c.Seen ? null : n;
    }

    static readonly Dictionary<string, DateTime> LastStop = [];

    public static void Handle(string json, Hello hello)
    {
        if (Parse(json) is not { } e) return;
        var key = e.SessionId ?? hello.Cwd ?? "";
        DateTime? last;
        lock (LastStop)
        {
            last = LastStop.TryGetValue(key, out var t) ? t : null;
            if (e.Event == "Stop") LastStop[key] = DateTime.UtcNow;
        }
        var seen = hello.Ancestors.Contains(Foreground.Pid) && !Router.Away;
        if (Decide(e, new(DateTime.UtcNow, last, seen, Settings.Current.ClaudeTextToPhone)) is not { } n) return;
        Notifier.Notify(n.Title, n.Body, n.Priority, Route.Auto, $"hook:{e.Folder}");
    }
}

public static class Foreground
{
    public static int Pid => Windows.Win32.PInvoke.GetForegroundWindow() is var h && h != default ? TopWindows.Pid(h) : 0;
}
