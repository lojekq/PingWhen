using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using PingWhen.Features;
using PingWhen.Shell;

namespace PingWhen.Tests;

/// <summary>
/// Region watch on a real window of this test (capture + engine + OCR). Opens a window, so it runs only with
/// PINGWHEN_GUI_TESTS=1 and under C:\Demo\desktop.lock (00-shared §9.6); otherwise inconclusive.
/// </summary>
[TestClass]
public class RegionGuiTests
{
    static void RequireGui()
    {
        if (Environment.GetEnvironmentVariable("PINGWHEN_GUI_TESTS") != "1") Assert.Inconclusive("set PINGWHEN_GUI_TESTS=1 and hold C:\\Demo\\desktop.lock");
    }

    /// <summary>A window whose big text goes through <paramref name="texts"/>, one every 300 ms, then stays.</summary>
    static async Task<(nint Hwnd, Dispatcher Ui)> Open(params string[] texts)
    {
        var ready = new TaskCompletionSource<(nint, Dispatcher)>();
        var t = new Thread(() =>
        {
            var text = new TextBlock { FontSize = 64, Margin = new Thickness(24), Text = texts[0] };
            var w = new Window { Width = 640, Height = 280, Left = 40, Top = 40, Content = text, Title = "PingWhen region test", ShowActivated = false, Topmost = true };
            var i = 0;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            timer.Tick += (_, _) => { if (++i < texts.Length) text.Text = texts[i]; else timer.Stop(); };
            w.ContentRendered += (_, _) => { timer.Start(); ready.TrySetResult((new WindowInteropHelper(w).Handle, Dispatcher.CurrentDispatcher)); };
            w.Show();
            Dispatcher.Run();
        });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
        return await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    static Watcher RegionWatcher(nint hwnd, Until until) => new()
    {
        Kind = Kind.Region, Until = until, Title = "region", Hwnd = hwnd, QuietSeconds = 3,
        Processes = [Targets.ByPid(Environment.ProcessId)!], WindowClass = TopWindows.ClassOf(hwnd), WindowTitle = TopWindows.TitleOf(hwnd),
    };

    [TestMethod]
    public async Task UnchangedFiresAfterTheTextStops()
    {
        RequireGui();
        var (hwnd, ui) = await Open([.. Enumerable.Range(1, 20).Select(n => $"{n * 5} %")]); // 6 s of changes, then still
        try
        {
            var engine = new Engine();
            var fired = new BlockingCollection<Fire>();
            engine.Fired += fired.Add;
            engine.Start();
            var w = RegionWatcher(hwnd, Until.Unchanged);
            (w.RegionX, w.RegionY, w.RegionW, w.RegionH) = (0, 0.1, 0.35, 0.5); // tight around the number, as a user selects it
            await engine.Add(w);
            var states = new List<string>();
            Fire? f = null;
            for (var i = 0; i < 20 && f is null; i++)
            {
                if (fired.TryTake(out var got, TimeSpan.FromSeconds(1))) f = got;
                states.Add(engine.Snapshot.FirstOrDefault()?.Live ?? "-");
            }
            Assert.IsNotNull(f, "no fire: " + string.Join(" | ", states));
            Assert.AreEqual(string.Format(Loc.T("Fire.RegionUnchanged"), 3), f.Body);
        }
        finally { ui.InvokeShutdown(); }
    }

    [TestMethod]
    public async Task FramesKeepComingWhileTheWindowChanges()
    {
        RequireGui();
        var (hwnd, ui) = await Open([.. Enumerable.Range(1, 15).Select(n => $"{n * 7} %")]);
        try
        {
            var frames = new ConcurrentBag<(long At, double Diff)>();
            byte[]? last = null;
            var start = Environment.TickCount64;
            using (RegionCapture.Start(hwnd, (bgra, w, h) =>
            {
                var g = Frames.Gray64(bgra, w, h, new(0, 0, 0.45, 0.6)).Gray;
                frames.Add((Environment.TickCount64 - start, last is null ? -1 : Frames.Diff(last, g)));
                last = g;
            }))
                await Task.Delay(4000);
            var list = frames.OrderBy(f => f.At).Select(f => $"{f.At}ms:{f.Diff:0.0}%").ToList();
            Assert.IsGreaterThanOrEqualTo(3, list.Count, string.Join(" ", list));
            if (Environment.GetEnvironmentVariable("PINGWHEN_SHOW") == "1") Assert.Fail("frames: " + string.Join(" ", list));
        }
        finally { ui.InvokeShutdown(); }
    }

    [TestMethod]
    public async Task OcrFindsTheDoneText()
    {
        RequireGui();
        if (!RegionCapture.OcrAvailable) Assert.Inconclusive("no OCR language");
        var (hwnd, ui) = await Open("Render 37%", "Render 61%", "Render 88%", "Render 100% Done");
        try
        {
            var engine = new Engine();
            var fired = new BlockingCollection<Fire>();
            engine.Fired += fired.Add;
            engine.Start();
            var w = RegionWatcher(hwnd, Until.TextMatches);
            w.Pattern = "100%";
            await engine.Add(w);
            Assert.IsTrue(fired.TryTake(out var f, TimeSpan.FromSeconds(20)), "no fire");
            StringAssert.Contains(f.Body, "100%");
        }
        finally { ui.InvokeShutdown(); }
    }
}
