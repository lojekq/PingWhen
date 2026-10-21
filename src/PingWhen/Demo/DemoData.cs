using System.Windows.Threading;
using PingWhen.Features;
using PingWhen.Shell;

namespace PingWhen.Demo;

/// <summary>
/// --demo: believable data for screenshots and GIFs. Names like Alex, example.com, C:\Users\demo\…; never real data.
/// Under --demo no probes of real data are shown, nothing is written, no hotkeys, no update check (00-shared §2.3);
/// the phone gets nothing except the "Send demo push" button (spec §5.1).
/// </summary>
public static class DemoData
{
    static readonly Random Rng = new(7);

    public static void Fill()
    {
        bool ru = Loc.T("Demo.Lang") == "ru";
        var now = DateTime.UtcNow;
        var ffmpeg = Add(new Watcher { Kind = Kind.Process, Title = ru ? "ffmpeg — рендер 4K" : "ffmpeg — 4K render", ProcessName = "ffmpeg.exe", Route = Route.Auto, KeepAwake = true }, now.AddMinutes(-37));
        var gguf = Add(new Watcher { Kind = Kind.File, Until = Until.StoppedGrowing, Title = "qwen3-32b.gguf", Path = @"C:\Models\qwen3-32b-q4_k_m.gguf", QuietSeconds = 30 }, now.AddMinutes(-12));
        var steam = Add(new Watcher { Kind = Kind.Quiet, Metric = Metric.Net, Below = 100, QuietSeconds = 60, Title = ru ? "Steam — обновление Dota 2" : "Steam — Dota 2 update", ProcessName = "steam.exe", Route = Route.Phone }, now.AddMinutes(-21));
        var npm = Add(new Watcher { Kind = Kind.Process, Title = "npm run build", CommandLineContains = "npm run build", Route = Route.Auto }, now.AddMinutes(-3));
        var agent = Add(new Watcher { Kind = Kind.Process, Title = "mlink: pytest -x", CommandLineContains = "pytest -x", CreatedBy = "mcp:claude-code/mlink", Route = Route.Auto }, now.AddMinutes(-8));

        ffmpeg.Live = ru ? "кадр 48 210 / 86 400 · 1.9x" : "frame 48,210 / 86,400 · 1.9x";
        npm.Live = ru ? "Windows Terminal · pwsh" : "Windows Terminal · pwsh";
        agent.Live = "python.exe · 2";
        var size = 14.2;
        var quiet = 18;
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        void Tick()
        {
            var rate = 280 + Rng.Next(0, 60);
            size += rate / 1024.0;
            gguf.Live = $"{string.Format(Loc.T("Unit.MB"), rate)}/{Loc.T("Unit.S")} · {string.Format(Loc.T("Unit.GB"), size.ToString("0.0"))}";
            quiet = quiet >= 59 ? 18 : quiet + 1;
            steam.Live = $"{string.Format(Loc.T("Unit.KBs"), Rng.Next(0, 9))} · {string.Format(Loc.T("Live.Quiet"), quiet, 60)}";
            steam.Progress = quiet / 60.0;
        }
        t.Tick += (_, _) => Tick();
        Tick();
        t.Start();

        // 20 history entries: done, error, quiet, lost, cancelled, agent notifications
        (int Min, Outcome O, string Title, string Body, double Sec, string Ch)[] h = ru
            ?
            [
                (4, Outcome.Done, "Готово: cargo build · 6 мин", "Процесс завершился, код 0", 372, "pc"),
                (11, Outcome.Notify, "mlink: Claude закончил", "", 0, "pc,phone"),
                (19, Outcome.Error, "Ошибка: npm test · код 1", "Процесс завершился с кодом 1", 95, "pc,phone"),
                (26, Outcome.Done, "Готово: setup-tools.msi · 2 мин", "Окно закрылось", 140, "pc"),
                (41, Outcome.Quiet, "Затихло: Система · 9 мин", "Диск ниже 1 МБ/с 30 с", 540, "pc,phone"),
                (58, Outcome.Done, "Готово: dataset.zip · 18 мин", "Файл исчез", 1080, "phone"),
                (75, Outcome.Notify, "PingWhen: нужен ответ", "Claude needs your permission to use Bash", 0, "phone"),
                (92, Outcome.Done, "Готово: python train.py · 1 ч 12 мин", "Процесс завершился, код 0", 4320, "pc,phone"),
                (130, Outcome.Cancelled, "Отменён: blender", "", 0, ""),
                (161, Outcome.Lost, "Потерян: render.exe", "Процесс не найден после перезапуска", 0, ""),
                (190, Outcome.Done, "Готово: ollama pull · 7 мин", "Сеть ниже 100 КБ/с 60 с", 420, "pc"),
                (240, Outcome.Error, "Ошибка: gradle · код 137", "Процесс завершился с кодом 137", 860, "pc,phone:failed"),
            ]
            :
            [
                (4, Outcome.Done, "Done: cargo build · 6 min", "Process exited, code 0", 372, "pc"),
                (11, Outcome.Notify, "mlink: Claude finished", "", 0, "pc,phone"),
                (19, Outcome.Error, "Error: npm test · code 1", "Process exited with code 1", 95, "pc,phone"),
                (26, Outcome.Done, "Done: setup-tools.msi · 2 min", "Window closed", 140, "pc"),
                (41, Outcome.Quiet, "Quiet: System · 9 min", "Disk below 1 MB/s for 30 s", 540, "pc,phone"),
                (58, Outcome.Done, "Done: dataset.zip · 18 min", "File is gone", 1080, "phone"),
                (75, Outcome.Notify, "PingWhen: needs your answer", "Claude needs your permission to use Bash", 0, "phone"),
                (92, Outcome.Done, "Done: python train.py · 1 h 12 min", "Process exited, code 0", 4320, "pc,phone"),
                (130, Outcome.Cancelled, "Cancelled: blender", "", 0, ""),
                (161, Outcome.Lost, "Lost: render.exe", "Process not found after a restart", 0, ""),
                (190, Outcome.Done, "Done: ollama pull · 7 min", "Network below 100 KB/s for 60 s", 420, "pc"),
                (240, Outcome.Error, "Error: gradle · code 137", "Process exited with code 137", 860, "pc,phone:failed"),
            ];
        for (var i = 0; i < 20; i++)
        {
            var e = h[i % h.Length];
            var minutes = e.Min + i / h.Length * 300;
            var src = e.O == Outcome.Notify ? (e.Title.StartsWith("mlink") ? "hook:mlink" : "mcp:claude-code/PingWhen") : "ui";
            Notifier.History.Add(new HistoryEntry($"demo{i}", now.AddMinutes(-minutes), src, e.Title, e.Body, e.O, e.Ch, e.Sec));
        }

        AgentsPage.DemoSessions.Add(("claude-code", "PingWhen", 0, 26));
        AgentsPage.DemoSessions.Add(("claude-code", "mlink", 1, 8));
        PingSettingsView.DemoTelegram = new TelegramSettings { BotUsername = "my_pingwhen_bot", ChatId = 1, FirstName = "Alex", PairedAt = now };
    }

    /// <summary>Under --demo the New page and the tray menu offer these instead of the real windows and processes.</summary>
    public static bool FakeTargets => Args.Current.Demo && !Args.Current.Autoplay;

    public static List<TopWindow> Windows { get; } =
    [
        new(1, 9001, "CASCADIA_HOSTING_WINDOW_CLASS", "Windows Terminal", false),
        new(2, 9002, "SDL_app", "Steam", false),
        new(3, 9003, "HwndWrapper", "trailer_4k.mp4 — HandBrake", false),
        new(4, 9004, "GHOST_WindowClass", "render.blend — Blender", false),
        new(5, 9005, "#32770", "Tools 2.4 Setup", false),
        new(6, 9006, "Chrome_WidgetWin_1", "mlink — Visual Studio Code", false),
        new(7, 9007, "CabinetWClass", "Downloads — File Explorer", false),
        new(8, 9008, "Chrome_WidgetWin_1", "example.com — Microsoft Edge", false),
    ];

    public static List<Terminals.Command> Commands(int windowPid) => windowPid != 9001 ? [] :
    [
        new(9101, DateTime.UtcNow.AddMinutes(-14), "pwsh", "python.exe", "python train.py --epochs 20"),
        new(9102, DateTime.UtcNow.AddMinutes(-52), "pwsh", "node.exe", @"node C:\Users\demo\src\shop\node_modules\.bin\vite"),
    ];

    public static List<ProcessNode> Processes { get; } =
        new[] { "blender.exe", "Code.exe", "ffmpeg.exe", "HandBrake.exe", "node.exe", "ollama.exe", "python.exe", "steam.exe", "WindowsTerminal.exe" }
            .Select((e, i) => new ProcessNode(9200 + i * 4, 1, e, DateTime.UtcNow.AddMinutes(-i))).ToList();

    public static void AddCard(Watcher w) => Add(w, DateTime.UtcNow);

    static Card Add(Watcher w, DateTime since)
    {
        var c = new Card(w) { IsDemo = true, Since = since };
        Cards.Items.Add(c);
        return c;
    }
}
