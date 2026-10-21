using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using PingWhen.Features;
using PingWhen.Shell;

namespace PingWhen.Tests;

/// <summary>v1.0 stage 9: repeat, deadline, taskbar flash, Stop threshold.</summary>
[TestClass]
public class RepeatTests
{
    public TestContext TestContext { get; set; } = null!;
    string dir = "";

    [TestInitialize]
    public void Init()
    {
        dir = Path.Combine(Path.GetTempPath(), "PingWhen.Tests", TestContext.TestName + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        Store.Dir = dir;
        Store.ReadOnly = false;
    }

    [TestCleanup]
    public void Cleanup() => Watcher.RepeatPause = TimeSpan.FromSeconds(60);

    static (Engine, BlockingCollection<Fire>) Start()
    {
        var e = new Engine();
        var fired = new BlockingCollection<Fire>();
        e.Fired += fired.Add;
        e.Start();
        return (e, fired);
    }

    [TestMethod]
    public async Task RepeatingFileFiresEveryTimeItAppearsAndStays()
    {
        Watcher.RepeatPause = TimeSpan.Zero;
        var (engine, fired) = Start();
        var path = Path.Combine(dir, "drop.zip");
        var w = await engine.Add(new Watcher { Kind = Kind.File, Until = Until.Appeared, Title = "drop", Path = path, Repeat = true });
        for (var i = 0; i < 2; i++)
        {
            File.WriteAllText(path, "x");
            Assert.IsTrue(fired.TryTake(out var f, TimeSpan.FromSeconds(5)), $"appearance {i}");
            Assert.AreEqual(Outcome.Done, f.Outcome);
            File.Delete(path);
            await Task.Delay(1500); // re-armed by the disappearance
        }
        Assert.IsTrue((await engine.List()).Any(x => x.Id == w.Id), "a repeating watcher stays");
    }

    [TestMethod]
    public async Task RepeatPauseSuppressesReportsWithinAMinute()
    {
        var (engine, fired) = Start();
        var path = Path.Combine(dir, "flap.txt");
        await engine.Add(new Watcher { Kind = Kind.File, Until = Until.Appeared, Title = "flap", Path = path, Repeat = true });
        File.WriteAllText(path, "x");
        Assert.IsTrue(fired.TryTake(out _, TimeSpan.FromSeconds(5)));
        File.Delete(path);
        await Task.Delay(1500);
        File.WriteAllText(path, "x");
        Assert.IsFalse(fired.TryTake(out _, TimeSpan.FromSeconds(2.5)), "second report within 60 s");
    }

    [TestMethod]
    public async Task RepeatingProcessByCommandLineReportsEachExit()
    {
        Watcher.RepeatPause = TimeSpan.Zero;
        var (engine, fired) = Start();
        var marker = "pwrep" + Guid.NewGuid().ToString("N")[..8];
        Process Run(int code, int seconds) => Process.Start(new ProcessStartInfo("cmd.exe", $"/c title {marker} & ping -n {seconds + 1} 127.0.0.1 >nul & exit {code}") { CreateNoWindow = true, UseShellExecute = false })!;
        using var first = Run(3, 1);
        await Task.Delay(300);
        await engine.Add(new Watcher { Kind = Kind.Process, Title = "rep", CommandLineContains = marker, Processes = Targets.Match(null, null, marker), Repeat = true });
        Assert.IsTrue(fired.TryTake(out var f1, TimeSpan.FromSeconds(10)), "first exit");
        Assert.AreEqual(Outcome.Error, f1.Outcome);
        using var second = Run(0, 7);                     // started later, lives longer than the 5 s command-line re-match
        Assert.IsTrue(fired.TryTake(out var f2, TimeSpan.FromSeconds(15)), "second exit");
        Assert.AreEqual(Outcome.Done, f2.Outcome);
        Assert.HasCount(1, await engine.List());
    }

    [TestMethod]
    public async Task DeadlineReportsOnceAndKeepsWatching()
    {
        var (engine, fired) = Start();
        var w = await engine.Add(new Watcher
        {
            Kind = Kind.File, Until = Until.Appeared, Title = "late", Path = Path.Combine(dir, "never.bin"),
            DeadlineMinutes = 1, CreatedAt = DateTime.UtcNow.AddMinutes(-2),
        });
        Assert.IsTrue(fired.TryTake(out var f, TimeSpan.FromSeconds(5)));
        Assert.AreEqual(Outcome.Overdue, f.Outcome);
        Assert.AreEqual(string.Format(Loc.T("Fire.Overdue"), "late", 1), f.Title);
        Assert.IsFalse(fired.TryTake(out _, TimeSpan.FromSeconds(2.5)), "once");
        Assert.IsTrue((await engine.List()).Single().DeadlineSent);
    }

    [TestMethod]
    public async Task FlashFiresForTheWatchedWindowOnly()
    {
        var win = TopWindows.List().FirstOrDefault(x => Targets.ByPid(x.Pid) is not null && x.Title.Length > 0);
        if (win is null) Assert.Inconclusive("no accessible top window");
        var (engine, fired) = Start();
        await engine.Add(new Watcher
        {
            Kind = Kind.Window, Until = Until.Flashed, Title = "flash", Hwnd = win!.Hwnd,
            Processes = [Targets.ByPid(win.Pid)!], WindowClass = win.Class, WindowTitle = win.Title,
        });
        engine.Flashed(win.Hwnd + 4);
        Assert.IsFalse(fired.TryTake(out _, TimeSpan.FromSeconds(1)), "another window");
        engine.Flashed(win.Hwnd);
        Assert.IsTrue(fired.TryTake(out var f, TimeSpan.FromSeconds(3)));
        Assert.AreEqual(Loc.T("Fire.Flashed"), f.Body);
    }

    [TestMethod]
    public void StopThresholdSkipsShortTurns()
    {
        const string stop = """{"session_id":"s1","cwd":"C:\\Users\\demo\\src\\mlink","hook_event_name":"Stop","stop_hook_active":false}""";
        var now = new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);
        var e = Hooks.Parse(stop)!;
        Assert.IsNull(Hooks.Decide(e, new(now, null, false, false, now.AddSeconds(-20), 60)));
        Assert.IsNotNull(Hooks.Decide(e, new(now, null, false, false, now.AddSeconds(-90), 60)));
        Assert.IsNotNull(Hooks.Decide(e, new(now, null, false, false, null, 60)), "no UserPromptSubmit seen: ping");
        Assert.IsNotNull(Hooks.Decide(e, new(now, null, false, false, now.AddSeconds(-1), 0)), "0 = every turn");
    }
}
