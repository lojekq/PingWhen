using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using PingWhen.Features;
using PingWhen.Shell;

namespace PingWhen.Tests;

[TestClass]
public class HookTests
{
    // Shapes from the Claude Code hooks reference (2026-10-21)
    const string Stop = """{"session_id":"abc123","transcript_path":"C:\\Users\\demo\\.claude\\projects\\x.jsonl","cwd":"C:\\Users\\demo\\src\\mlink","permission_mode":"default","hook_event_name":"Stop","stop_hook_active":false,"last_assistant_message":"All 42 tests pass.\nNext I would refactor the parser."}""";
    const string Permission = """{"session_id":"abc123","transcript_path":"x","cwd":"C:\\Users\\demo\\src\\mlink","hook_event_name":"Notification","message":"Claude needs your permission to use Bash","title":"Permission needed","notification_type":"permission_prompt"}""";
    const string Idle = """{"session_id":"abc123","cwd":"C:\\Users\\demo\\src\\mlink","hook_event_name":"Notification","message":"Claude is waiting for your input","notification_type":"idle_prompt"}""";
    const string Auth = """{"session_id":"abc123","cwd":"C:\\Users\\demo\\src\\mlink","hook_event_name":"Notification","message":"ok","notification_type":"auth_success"}""";

    static readonly DateTime Now = new(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);
    static Hooks.Context Ctx(DateTime? lastStop = null, bool seen = false, bool text = false) => new(Now, lastStop, seen, text);

    [TestMethod]
    public void StopSaysDoneWithTheFolderAndNoTextByDefault()
    {
        var n = Hooks.Decide(Hooks.Parse(Stop)!, Ctx())!;
        Assert.AreEqual(string.Format(Loc.T("Hook.Done"), "mlink"), n.Title);
        Assert.AreEqual("", n.Body);
    }

    [TestMethod]
    public void StopTextIsOptInAndCutTo150()
    {
        var long_ = Stop.Replace("All 42 tests pass.", new string('y', 300));
        var n = Hooks.Decide(Hooks.Parse(long_)!, Ctx(text: true))!;
        Assert.AreEqual(150, n.Body.Length);
        Assert.DoesNotContain("\n", Hooks.Decide(Hooks.Parse(Stop)!, Ctx(text: true))!.Body);
    }

    [TestMethod]
    public void StopHookActiveIsSkipped() =>
        Assert.IsNull(Hooks.Decide(Hooks.Parse(Stop.Replace("\"stop_hook_active\":false", "\"stop_hook_active\":true"))!, Ctx()));

    [TestMethod]
    public void PermissionPromptNeedsAnAnswerWithHighPriority()
    {
        var n = Hooks.Decide(Hooks.Parse(Permission)!, Ctx())!;
        Assert.AreEqual(string.Format(Loc.T("Hook.Needs"), "mlink"), n.Title);
        Assert.AreEqual(Priority.High, n.Priority);
    }

    [TestMethod]
    public void IdlePromptOnlyWithoutARecentStop()
    {
        Assert.IsNull(Hooks.Decide(Hooks.Parse(Idle)!, Ctx(lastStop: Now.AddMinutes(-2))));
        Assert.IsNotNull(Hooks.Decide(Hooks.Parse(Idle)!, Ctx(lastStop: Now.AddMinutes(-6))));
        Assert.IsNotNull(Hooks.Decide(Hooks.Parse(Idle)!, Ctx()));
    }

    [TestMethod]
    public void OtherTypesAndSeenSessionsAreQuiet()
    {
        Assert.IsNull(Hooks.Decide(Hooks.Parse(Auth)!, Ctx()));
        Assert.IsNull(Hooks.Decide(Hooks.Parse(Permission)!, Ctx(seen: true)));
        Assert.IsNull(Hooks.Decide(Hooks.Parse(Stop)!, Ctx(seen: true)));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("not json")]
    [DataRow("[1,2,3]")]
    [DataRow("{\"hook_event_name\":42}")]
    [DataRow("{\"x\":")]
    public void GarbageParsesToNull(string input) => Assert.IsNull(Hooks.Parse(input));

    [TestMethod]
    [DataRow("garbage {{{")]
    [DataRow("")]
    public void HookProcessExitsZeroWithEmptyStdout(string stdin)
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "PingWhen.exe");
        Assert.IsTrue(File.Exists(exe), exe);
        using var p = Process.Start(new ProcessStartInfo(exe, "--hook")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        })!;
        p.StandardInput.Write(stdin);
        p.StandardInput.Close();
        var stdout = p.StandardOutput.ReadToEnd();
        Assert.IsTrue(p.WaitForExit(10_000));
        Assert.AreEqual(0, p.ExitCode);
        Assert.AreEqual("", stdout);
    }
}

[TestClass]
public class StoreTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void Init()
    {
        Store.Dir = Path.Combine(Path.GetTempPath(), "PingWhen.Tests", TestContext.TestName + "-" + Guid.NewGuid().ToString("N")[..8]);
        Store.ReadOnly = false;
    }

    [TestMethod]
    public void WatchersRoundTrip()
    {
        var w = new Watcher { Kind = Kind.Quiet, Until = Until.Quiet, Metric = Metric.Net, Below = 100, QuietSeconds = 60, Title = "Steam", Route = Route.Phone, Processes = [new(42, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), "steam.exe")] };
        Store.SaveWatchers([w]);
        var back = Store.LoadWatchers().Single();
        Assert.AreEqual(w.Id, back.Id);
        Assert.AreEqual(Metric.Net, back.Metric);
        Assert.AreEqual(Route.Phone, back.Route);
        Assert.AreEqual(w.Processes[0], back.Processes[0]);
        StringAssert.Contains(File.ReadAllText(Path.Combine(Store.Dir, "watchers.json")), "\"version\":1");
    }

    [TestMethod]
    public void HistoryKeepsTheLatestLinePerIdAndTrimsTo500()
    {
        for (var i = 0; i < 510; i++) Store.Append(new HistoryEntry($"id{i}", DateTime.UtcNow, "w", $"t{i}", "", Outcome.Done, "pc"));
        Store.Append(new HistoryEntry("id509", DateTime.UtcNow, "w", "t509", "", Outcome.Done, "pc,phone:failed"));
        var h = Store.LoadHistory();
        Assert.HasCount(500, h);
        Assert.AreEqual("id509", h[0].Id);
        Assert.AreEqual("pc,phone:failed", h[0].Channels);
        Assert.AreEqual("id10", h[^1].Id);
        Assert.HasCount(500, File.ReadAllLines(Path.Combine(Store.Dir, "history.jsonl")));
    }

    [TestMethod]
    public async Task RestoreKeepsMatchingPidsAndLosesReusedOnes()
    {
        using var child = Process.Start(new ProcessStartInfo("ping", "-n 30 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false })!;
        try
        {
            var started = ProcessInfo.StartTime(child.Id)!.Value;
            var ok = new Watcher { Kind = Kind.Process, Title = "ok", Processes = [new(child.Id, started, "PING.EXE")] };
            var reused = new Watcher { Kind = Kind.Process, Title = "reused", Processes = [new(child.Id, started.AddMinutes(-5), "PING.EXE")] };
            var byName = new Watcher { Kind = Kind.Process, Title = "byName", ProcessName = "PING", Processes = [new(child.Id, started.AddMinutes(-5), "PING.EXE")] };
            var file = new Watcher { Kind = Kind.File, Until = Until.Appeared, Title = "file", Path = Path.Combine(Store.Dir, "never.part") };
            var engine = new Engine();
            var lost = new List<string>();
            engine.Fired += f => { if (f.Outcome == Outcome.Lost) lost.Add(f.W.Title); };
            engine.Start();
            engine.Restore([ok, reused, byName, file]);
            var alive = await engine.List();
            CollectionAssert.AreEquivalent(new[] { "ok", "byName", "file" }, alive.Select(w => w.Title).ToList());
            CollectionAssert.AreEqual(new[] { "reused" }, lost);
            Assert.AreEqual(started, alive.Single(w => w.Title == "byName").Processes.Single(p => p.Pid == child.Id).Started);
        }
        finally { child.Kill(); }
    }

    [TestMethod]
    public async Task ProcessExitFiresWithTheExitCode()
    {
        using var child = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 2 127.0.0.1 >nul & exit 3") { CreateNoWindow = true, UseShellExecute = false })!;
        var engine = new Engine();
        var fired = new TaskCompletionSource<Fire>();
        engine.Fired += f => fired.TrySetResult(f);
        engine.Start();
        await engine.Add(new Watcher { Kind = Kind.Process, Title = "cmd", Processes = Targets.Match(child.Id, null, null) });
        var f = await fired.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.AreEqual(Outcome.Error, f.Outcome);
        Assert.AreEqual(string.Format(Loc.T("Fire.Error"), "cmd", 3), f.Title);
    }
}

[TestClass]
public class McpTests
{
    static AgentSession Session(string folder = "mlink") => new(new Hello(1, "mcp", 1, [], $@"C:\Users\demo\src\{folder}"));

    [TestInitialize]
    public void Init() => Store.ReadOnly = true;

    [TestMethod]
    public async Task ArgumentsAreValidated()
    {
        var s = Session();
        await Assert.ThrowsExactlyAsync<McpException>(() => Mcp.WatchFile(s, @"relative\file.bin", "stopped_growing", null, null));
        await Assert.ThrowsExactlyAsync<McpException>(() => Mcp.WatchFile(s, @"C:\nope\missing.bin", "stopped_growing", null, null));
        await Assert.ThrowsExactlyAsync<McpException>(() => Mcp.WatchFile(s, @"C:\x.bin", "exploded", null, null));
        await Assert.ThrowsExactlyAsync<McpException>(() => Mcp.WatchProcess(s, 1, "x", null, null, null, false));
        await Assert.ThrowsExactlyAsync<McpException>(() => Mcp.WatchProcess(s, null, null, "ab", null, null, false));
        await Assert.ThrowsExactlyAsync<McpException>(() => Mcp.WatchProcess(s, null, "surely-no-such-process-xyz", null, null, null, false));
        await Assert.ThrowsExactlyAsync<McpException>(() => Mcp.WatchQuiet(s, "net", "steam", null, null, false));
        await Assert.ThrowsExactlyAsync<McpException>(() => Mcp.WatchWindow(s, "x", null, "title_matches", "(bad", true));
        Assert.ThrowsExactly<McpException>(() => Mcp.Notify(s, new string('t', 121), null, null, null));
        Assert.ThrowsExactly<McpException>(() => Mcp.Notify(s, "ok", new string('m', 1001), null, null));
        Assert.ThrowsExactly<McpException>(() => Mcp.Notify(s, "ok", null, "urgent", null));
    }

    [TestMethod]
    public void NotifyIsLimitedPerSessionPerMinute()
    {
        Notifier.Sink = _ => (true, false);
        try
        {
            var s = Session("limits");
            for (var i = 0; i < Mcp.NotifyPerMinute; i++) Mcp.Notify(s, $"n{i}", null, null, null);
            var e = Assert.ThrowsExactly<McpException>(() => Mcp.Notify(s, "one too many", null, null, null));
            StringAssert.Contains(e.Message, "rate limit");
        }
        finally { Notifier.Sink = null; }
    }

    [TestMethod]
    public async Task EndToEndNotifyThroughThePipe()
    {
        var notes = new List<Note>();
        Notifier.Sink = n => { lock (notes) notes.Add(n); return (true, false); };
        var name = "PingWhen-test-" + Guid.NewGuid().ToString("N");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var server = PipeServer.Run(name, cts.Token);
        try
        {
            await using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(5000, cts.Token);
            var hello = JsonSerializer.Serialize(new Hello(1, "mcp", Environment.ProcessId, [], @"C:\Users\demo\src\mlink"), Json.Options);
            await PipeServer.WriteLine(pipe, hello, cts.Token);
            Assert.AreEqual("""{"ok":true}""", await PipeServer.ReadLine(pipe, 4096, cts.Token));

            await using var client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe), new McpClientOptions { ClientInfo = new() { Name = "claude-code", Version = "2" } }, cancellationToken: cts.Token);
            var tools = (await client.ListToolsAsync(cancellationToken: cts.Token)).Select(t => t.Name).ToList();
            CollectionAssert.AreEquivalent(new[] { "notify", "watch_process", "watch_file", "watch_window", "watch_quiet", "list_watchers", "cancel_watcher" }, tools);
            Assert.IsTrue((await client.ListToolsAsync(cancellationToken: cts.Token)).Single(t => t.Name == "list_watchers").ProtocolTool.Annotations?.ReadOnlyHint);

            var r = await client.CallToolAsync("notify", new Dictionary<string, object?> { ["title"] = "tests pass", ["priority"] = "low" }, cancellationToken: cts.Token);
            Assert.IsFalse(r.IsError ?? false);
            lock (notes)
            {
                Assert.HasCount(1, notes);
                Assert.AreEqual("mlink: tests pass", notes[0].Title);
                Assert.AreEqual(Priority.Low, notes[0].Priority);
            }

            var bad = await client.CallToolAsync("watch_file", new Dictionary<string, object?> { ["path"] = "rel.txt", ["until"] = "appeared" }, cancellationToken: cts.Token);
            Assert.IsTrue(bad.IsError);
            StringAssert.Contains(((TextContentBlock)bad.Content[0]).Text, "absolute");
        }
        finally
        {
            Notifier.Sink = null;
            cts.Cancel();
            await server;
        }
    }

    /// <summary>The real exe as Claude Code starts it: stdio ↔ --mcp relay ↔ pipe ↔ MCP server (here in the test process).</summary>
    [TestMethod]
    public async Task StdioRelayOfTheRealExe()
    {
        if (Mutex.TryOpenExisting($@"Local\lojekq.{AppInfo.Name}", out var m)) { m.Dispose(); Assert.Inconclusive("a PingWhen tray is running"); }
        var notes = new List<Note>();
        Notifier.Sink = n => { lock (notes) notes.Add(n); return (true, false); };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var server = PipeServer.Run(PipeServer.Name, cts.Token);
        try
        {
            var cwd = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "PingWhen.Tests", "mlink")).FullName;
            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Command = Path.Combine(AppContext.BaseDirectory, "PingWhen.exe"), Arguments = ["--mcp"], Name = "pingwhen", WorkingDirectory = cwd,
            });
            await using var client = await McpClient.CreateAsync(transport, cancellationToken: cts.Token);
            Assert.AreEqual("pingwhen", client.ServerInfo.Name);
            StringAssert.Contains(client.ServerInstructions, "watch_process");
            var r = await client.CallToolAsync("notify", new Dictionary<string, object?> { ["title"] = "via stdio" }, cancellationToken: cts.Token);
            Assert.IsFalse(r.IsError ?? false);
            lock (notes) Assert.AreEqual("mlink: via stdio", notes.Single().Title); // folder from the relay's cwd
            var list = await client.CallToolAsync("list_watchers", cancellationToken: cts.Token);
            Assert.IsFalse(list.IsError ?? false);
        }
        finally
        {
            Notifier.Sink = null;
            cts.Cancel();
            await server;
        }
    }

    [TestMethod]
    public async Task WrongHelloVersionIsRefused()
    {
        var name = "PingWhen-test-" + Guid.NewGuid().ToString("N");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var server = PipeServer.Run(name, cts.Token);
        await using (var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await pipe.ConnectAsync(5000, cts.Token);
            await PipeServer.WriteLine(pipe, """{"v":2,"mode":"mcp","pid":1,"ancestors":[],"cwd":"x"}""", cts.Token);
            StringAssert.Contains(await PipeServer.ReadLine(pipe, 4096, cts.Token), "version");
        }
        cts.Cancel();
        await server;
    }
}
