using System.Collections.ObjectModel;
using System.IO;
using System.Security;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using PingWhen.Shell;
using Windows.UI.Notifications;
using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace PingWhen.Shell
{
    public sealed partial class Settings
    {
        public string? WatchHotkey { get; set; } = "Ctrl+Alt+W";
        public int AwayMinutes { get; set; } = 3;
        public bool PhoneWhenBusy { get; set; } = true;
        public bool ClaudeTextToPhone { get; set; }
        public int StopMinSeconds { get; set; }   // v1.0: "Claude finished" only after turns this long (0 = every turn)
    }
}

namespace PingWhen.Features
{
    /// <summary>One notification on its way: from a watcher, an agent (notify, hook) or --notify.</summary>
    public sealed record Note(string Title, string Body, Priority Priority = Priority.Default, Route Route = Route.Auto, long Hwnd = 0, string? HistoryId = null);

    /// <summary>Which channels a note goes to (spec §5.5). Pure.</summary>
    public static class Router
    {
        /// <summary>Away: no input for N min. Busy: fullscreen game, presentation (and the phone-when-busy setting is on). Dnd: tray "Do not disturb 1 h".</summary>
        public record struct Env(bool Away, bool Busy, bool Dnd, bool PhoneReady);

        public static (bool Pc, bool Phone) Decide(Route route, Env e)
        {
            var phone = route switch { Route.Pc => false, Route.Phone or Route.Both => true, _ => e.Away || e.Busy } && e.PhoneReady && !e.Dnd;
            var pc = route != Route.Phone || !e.PhoneReady; // phone-only without a bot still shows a toast
            return (pc, phone);
        }

        public static unsafe TimeSpan Idle
        {
            get
            {
                var li = new LASTINPUTINFO { cbSize = (uint)sizeof(LASTINPUTINFO) };
                return PInvoke.GetLastInputInfo(ref li) ? TimeSpan.FromMilliseconds((uint)Environment.TickCount - li.dwTime) : TimeSpan.Zero;
            }
        }

        public static bool Away => Idle >= TimeSpan.FromMinutes(Math.Max(1, Settings.Current.AwayMinutes));

        public static Env Now() => new(Away, Settings.Current.PhoneWhenBusy && UserBusy.Now, Notifier.DndUntil > DateTime.UtcNow, Notifier.PhoneReady);
    }

    /// <summary>
    /// Phone messages: events within 3 s are merged into one; 3 retries (5 s, 30 s, 2 min; 429 waits retry_after if longer);
    /// 400/401/403 are not retried. Failures go to history and the tray icon.
    /// </summary>
    public sealed class Outbox(Func<IReadOnlyList<Note>, CancellationToken, Task> send)
    {
        public static readonly TimeSpan MergeWindow = TimeSpan.FromSeconds(3);
        static readonly TimeSpan[] Schedule = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2)];

        /// <summary>Delay before retry number <paramref name="failures"/> (1 = after the first failure); null = give up.</summary>
        public static TimeSpan? RetryDelay(int failures, TgError e)
        {
            if (e.Permanent || failures > Schedule.Length) return null;
            var d = Schedule[failures - 1];
            return e.Code == 429 && e.RetryAfter > d.TotalSeconds ? TimeSpan.FromSeconds(e.RetryAfter) : d;
        }

        public static (string Text, bool Silent) Merge(IReadOnlyList<Note> batch)
        {
            var silent = batch.All(n => n.Priority == Priority.Low);
            if (batch.Count == 1) return ($"{batch[0].Title}\n{batch[0].Body}".TrimEnd(), silent);
            return (string.Join("\n\n", batch.Select(n => $"{n.Title}\n{n.Body}".TrimEnd())), silent);
        }

        public event Action<IReadOnlyList<Note>>? Delivered;
        public event Action<IReadOnlyList<Note>, TgError>? Failed;

        readonly Lock gate = new();
        List<Note>? pending;
        readonly CancellationTokenSource stop = new();
        Task current = Task.CompletedTask;

        public void Enqueue(Note n)
        {
            lock (gate)
            {
                if (pending is not null) { pending.Add(n); return; }
                pending = [n];
                current = Task.Run(Pump);
            }
        }

        async Task Pump()
        {
            try { await Task.Delay(MergeWindow, stop.Token); } catch (OperationCanceledException) { } // exit flushes at once
            List<Note> batch;
            lock (gate) { batch = pending!; pending = null; }
            for (var failures = 0; ; )
            {
                try
                {
                    await send(batch, CancellationToken.None);
                    Delivered?.Invoke(batch);
                    return;
                }
                catch (Exception ex)
                {
                    var e = ex as TgError ?? new TgError(0, Telegram.Scrub(ex.Message));
                    failures++;
                    Log.Warn($"telegram send failed ({e.Code}), attempt {failures}");
                    if (RetryDelay(failures, e) is not { } d || stop.IsCancellationRequested) { Failed?.Invoke(batch, e); return; }
                    try { await Task.Delay(d, stop.Token); } catch (OperationCanceledException) { Failed?.Invoke(batch, e); return; }
                }
            }
        }

        /// <summary>Exit: send what waits in the merge window now, at most <paramref name="timeout"/>.</summary>
        public void Flush(TimeSpan timeout)
        {
            stop.Cancel();
            current.Wait(timeout);
        }
    }

    /// <summary>Toasts, phone and history in one place. Router decides, Toast shows, Outbox sends, history records.</summary>
    public static class Notifier
    {
        public static DateTime DndUntil { get; set; }
        public static ObservableCollection<HistoryEntry> History { get; } = [];

        /// <summary>Last delivery error for the tray icon and settings; null after a success.</summary>
        public static string? DeliveryError { get; private set; }
        public static event Action? StateChanged;

        static (string Name, Outbox Box)[] phones = []; // "phone" = Telegram, "ntfy"; swapped whole, read from any thread
        static bool phoneOff; // --demo: the phone gets nothing but the demo push

        public static bool PhoneReady => phones.Length > 0;

        public static void Start(bool phone)
        {
            phoneOff = !phone;
            foreach (var e in Store.LoadHistory()) History.Add(e);
            Reconnect();
        }

        /// <summary>After pairing, unpairing, a new token or ntfy settings. Pending sends of the old channels still finish.</summary>
        public static void Reconnect()
        {
            var list = new List<(string, Outbox)>();
            if (!phoneOff)
            {
                var t = Settings.Current.Telegram;
                if (t.Paired && t.Unprotect() is { } token)
                {
                    var bot = new Telegram(token);
                    var chat = t.ChatId!.Value;
                    list.Add(Channel("phone", (batch, ct) => { var (text, silent) = Outbox.Merge(batch); return bot.SendMessage(chat, text, silent, ct); }, Telegram.Describe));
                }
                var n = Settings.Current.Ntfy;
                if (n.Ready)
                {
                    var ntfy = new Ntfy(n);
                    list.Add(Channel("ntfy", (batch, ct) => batch.Count == 1
                        ? ntfy.Send(batch[0].Title, batch[0].Body, batch[0].Priority, ct)
                        : ntfy.Send($"{AppInfo.DisplayName} · {batch.Count}", Outbox.Merge(batch).Text, batch.Max(b => b.Priority), ct), Ntfy.Describe));
                }
            }
            phones = [.. list];
        }

        static (string, Outbox) Channel(string name, Func<IReadOnlyList<Note>, CancellationToken, Task> send, Func<TgError, string> describe)
        {
            var box = new Outbox(send);
            box.Delivered += batch =>
            {
                SetError(null);
                foreach (var n in batch) Mark(n.HistoryId, name);
            };
            box.Failed += (batch, e) =>
            {
                SetError(describe(e));
                foreach (var n in batch) Mark(n.HistoryId, name + ":failed");
            };
            return (name, box);
        }

        static void SetError(string? e)
        {
            if (DeliveryError == e) return;
            DeliveryError = e;
            StateChanged?.Invoke();
        }

        /// <summary>Routes and sends; returns what happened per channel ("pc", "phone:queued").</summary>
        /// <summary>Tests: replaces toast and phone.</summary>
        internal static Func<Note, (bool Pc, bool Phone)>? Sink;

        public static (bool Pc, bool Phone) Deliver(Note n)
        {
            if (Sink is not null) return Sink(n);
            var (pc, phone) = Router.Decide(n.Route, Router.Now());
            if (pc) Toast.Show(n);
            if (phone) foreach (var p in phones) p.Box.Enqueue(n);
            return (pc, phone);
        }

        /// <summary>A watcher fired: history + toast/phone. Lost and cancelled only go to history.</summary>
        public static void OnFired(Fire f)
        {
            var id = Guid.NewGuid().ToString("N")[..12];
            var channels = "";
            if (f.Outcome is not (Outcome.Lost or Outcome.Cancelled))
            {
                var (pc, phone) = Deliver(new Note(f.Title, f.Body, Priority.Default, f.W.Route, f.W.Hwnd, id));
                channels = Channels(pc, phone);
            }
            Record(new HistoryEntry(id, DateTime.UtcNow, f.W.Title, f.Title, f.Body, f.Outcome, channels, f.Duration.TotalSeconds));
        }

        /// <summary>notify from an agent, a hook or --notify.</summary>
        public static (bool Pc, bool Phone) Notify(string title, string body, Priority priority, Route route, string source)
        {
            var id = Guid.NewGuid().ToString("N")[..12];
            var r = Deliver(new Note(title, body, priority, route, 0, id));
            Record(new HistoryEntry(id, DateTime.UtcNow, source, title, body, Outcome.Notify, Channels(r.Pc, r.Phone)));
            return r;
        }

        static string Channels(bool pc, bool phone) =>
            string.Join(",", (pc ? ["pc"] : Array.Empty<string>()).Concat(phone ? phones.Select(p => p.Name + ":queued") : []));

        static void Record(HistoryEntry e)
        {
            Store.Append(e);
            Ui(() =>
            {
                History.Insert(0, e);
                while (History.Count > Store.HistoryLimit) History.RemoveAt(History.Count - 1);
            });
        }

        /// <summary>Delivery result of one channel: "phone", "ntfy:failed" replaces that channel's "…:queued".</summary>
        static void Mark(string? id, string result)
        {
            if (id is null) return;
            var name = result.Split(':')[0];
            Ui(() =>
            {
                var i = History.ToList().FindIndex(h => h.Id == id);
                if (i < 0) return;
                var e = History[i] with { Channels = string.Join(",", History[i].Channels.Split(',').Where(c => c.Length > 0 && c.Split(':')[0] != name).Append(result)) };
                History[i] = e;
                Store.Append(e);
            });
        }

        public static void ClearHistory()
        {
            Store.ClearHistory();
            History.Clear();
        }

        static void Ui(Action a)
        {
            var d = Application.Current?.Dispatcher;
            if (d is null || d.CheckAccess()) a(); else d.BeginInvoke(a);
        }

        /// <summary>Settings → Test, tray → Test notification: both channels, ignoring the route.</summary>
        public static void Test() => Notify(Loc.T("Test.Title"), Loc.T("Test.Body"), Priority.Default, PhoneReady ? Route.Both : Route.Pc, "test");

        public static void Flush()
        {
            foreach (var p in phones) p.Box.Flush(TimeSpan.FromSeconds(3));
        }
    }

    /// <summary>
    /// WinRT toasts for an unpackaged exe: identity from HKCU\Software\Classes\AppUserModelId\PingWhen (DisplayName, IconUri).
    /// "Open window" activates the watched window; Activated only works while this process runs (no COM activator).
    /// </summary>
    public static class Toast
    {
        public const string Aumid = "PingWhen";
        static readonly List<ToastNotification> Alive = []; // keep the objects: Activated is lost with them
        static ToastNotifier? notifier;

        /// <summary>At start, not under --demo: the registry key and a PNG icon for it.</summary>
        public static void Register()
        {
            try
            {
                var icon = Path.Combine(AppInfo.LocalDir, "toast.png");
                if (!File.Exists(icon))
                {
                    Directory.CreateDirectory(AppInfo.LocalDir);
                    var ico = BitmapDecoder.Create(new Uri("pack://application:,,,/Assets/app.ico"), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                    var png = new PngBitmapEncoder();
                    png.Frames.Add(ico.Frames.OrderByDescending(f => f.PixelWidth).First());
                    using var f = File.Create(icon);
                    png.Save(f);
                }
                using var k = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{Aumid}");
                k.SetValue("DisplayName", AppInfo.DisplayName);
                k.SetValue("IconUri", icon);
            }
            catch (Exception e) { Log.Error("toast identity", e); }
        }

        public static void Unregister() => Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\AppUserModelId\{Aumid}", false);

        public static void Show(Note n)
        {
            try
            {
                notifier ??= ToastNotificationManager.CreateToastNotifier(Aumid);
                var open = n.Hwnd != 0 ? $"<actions><action content=\"{SecurityElement.Escape(Loc.T("Toast.OpenWindow"))}\" arguments=\"hwnd={n.Hwnd}\" activationType=\"foreground\"/></actions>" : "";
                var xml = new Windows.Data.Xml.Dom.XmlDocument();
                xml.LoadXml($"<toast launch=\"{(n.Hwnd != 0 ? $"hwnd={n.Hwnd}" : "history")}\"><visual><binding template=\"ToastGeneric\">" +
                            $"<text>{SecurityElement.Escape(Fmt.Cut(n.Title, 200))}</text><text>{SecurityElement.Escape(Fmt.Cut(n.Body, 400))}</text>" +
                            $"</binding></visual>{open}</toast>");
                var t = new ToastNotification(xml);
                t.Activated += (_, a) => Application.Current?.Dispatcher.BeginInvoke(() => OnActivated((a as ToastActivatedEventArgs)?.Arguments));
                lock (Alive)
                {
                    Alive.Add(t);
                    if (Alive.Count > 20) Alive.RemoveAt(0);
                }
                notifier.Show(t);
            }
            catch (Exception e)
            {
                Log.Warn("toast failed, balloon instead", e);
                Application.Current?.Dispatcher.BeginInvoke(() => Tray.Notify(n.Title, n.Body, "history"));
            }
        }

        static void OnActivated(string? args)
        {
            if (args?.StartsWith("hwnd=") == true && long.TryParse(args[5..], out var h)) WindowProbe.Activate(h);
            else App.ShowMain("history");
        }
    }
}
