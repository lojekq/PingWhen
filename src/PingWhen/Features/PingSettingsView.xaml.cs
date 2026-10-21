using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using PingWhen.Shell;

namespace PingWhen.Features;

/// <summary>The app's settings section: Telegram (bot token → check → pair by code), routing, hotkey.</summary>
public partial class PingSettingsView
{
    const int HotkeyId = 1;

    /// <summary>Ctrl+Alt+W by default; at start and after a change. Only combinations from 00-shared §9.5.</summary>
    public static HotkeyStatus ApplyHotkey() =>
        Hotkey.Parse(Settings.Current.WatchHotkey) is { } h ? Hotkeys.Register(HotkeyId, h, NewWatcherPage.OpenForForeground) : HotkeyStatus.Ok;

    /// <summary>--demo: the card shows a paired demo bot, never a real token.</summary>
    public static TelegramSettings? DemoTelegram { get; set; }

    CancellationTokenSource? pairing;
    readonly DispatcherTimer countdown = new() { Interval = TimeSpan.FromSeconds(1) };
    DateTime pairUntil;
    string? botUsername, code;

    static TelegramSettings T => DemoTelegram ?? Settings.Current.Telegram;

    /// <summary>--page telegram: the settings page scrolled to this card (README screenshot).</summary>
    public static bool Reveal { get; set; }

    public PingSettingsView()
    {
        InitializeComponent();
        DemoTag.Visibility = DemoPushButton.Visibility = DemoTelegram is null ? Visibility.Collapsed : Visibility.Visible;
        CheckButton.Click += async (_, _) => await CheckToken();
        PairButton.Click += async (_, _) => await Pair();
        CancelPair.Click += (_, _) => pairing?.Cancel();
        OpenTelegram.Click += (_, _) => Process.Start(new ProcessStartInfo($"https://t.me/{botUsername}?start={code}") { UseShellExecute = true });
        ForgetTokenButton.Click += (_, _) => Unpair();
        UnpairButton.Click += (_, _) => Unpair();
        TestButton.Click += async (_, _) => await Test();
        DemoPushButton.Click += async (_, _) => await DemoPush();
        countdown.Tick += (_, _) => Countdown();
        Unloaded += (_, _) => pairing?.Cancel();

        AwayBox.Value = Settings.Current.AwayMinutes;
        AwayBox.ValueChanged += (_, _) => { Settings.Current.AwayMinutes = (int)Math.Clamp(AwayBox.Value ?? 3, 1, 120); Settings.Save(); };
        BusySwitch.IsChecked = Settings.Current.PhoneWhenBusy;
        BusySwitch.Click += (_, _) => { Settings.Current.PhoneWhenBusy = BusySwitch.IsChecked == true; Settings.Save(); };

        var box = new HotkeyBox { Hotkey = Settings.Current.WatchHotkey, Status = Args.Current.Demo ? HotkeyStatus.Ok : ApplyHotkey() };
        box.Changed += (_, _) =>
        {
            Settings.Current.WatchHotkey = box.Hotkey;
            Settings.Save();
            if (box.Hotkey is null) Hotkeys.Unregister(HotkeyId);
            box.Status = ApplyHotkey();
        };
        HotkeyHost.Content = box;
        Show();
        InitNtfy();
        if (Reveal) Loaded += (_, _) => Dispatcher.BeginInvoke(() => TelegramCard.BringIntoView(new Rect(0, -70, 1, TelegramCard.ActualHeight + 470)), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>All three steps stay visible; done steps show a check mark (spec §7: "three steps on one card").</summary>
    void Show(string? error = null)
    {
        var t = T;
        botUsername = t.BotUsername;
        var hasToken = t.Token is not null || DemoTelegram is not null;
        var paired = t.ChatId is not null;
        TokenRow.Visibility = hasToken ? Visibility.Collapsed : Visibility.Visible;
        TokenDone.Visibility = hasToken ? Visibility.Visible : Visibility.Collapsed;
        BotText.Text = "@" + t.BotUsername;
        PairRow.Visibility = !paired && pairing is null ? Visibility.Visible : Visibility.Collapsed;
        PairButton.IsEnabled = hasToken;
        CodePanel.Visibility = pairing is null ? Visibility.Collapsed : Visibility.Visible;
        PairedPanel.Visibility = paired ? Visibility.Visible : Visibility.Collapsed;
        PairedText.Text = string.Format(Loc.T("Tg.PairedTo"), t.FirstName, t.BotUsername);
        StatusText.Text = paired ? "" : hasToken ? "@" + t.BotUsername : Loc.T("Tg.NotSet");
        var err = error ?? (DemoTelegram is null ? Notifier.DeliveryError : null);
        ErrorText.Text = err ?? "";
        ErrorText.Visibility = err is null ? Visibility.Collapsed : Visibility.Visible;
        UnpairButton.Content = Loc.T(Notifier.DeliveryError is not null && DemoTelegram is null ? "Tg.Repair" : "Tg.Unpair");
    }

    async Task CheckToken()
    {
        var token = TokenBox.Password.Trim();
        if (!Telegram.LooksLikeToken(token)) { Show(Loc.T("Tg.Error.Token")); return; }
        CheckButton.IsEnabled = false;
        try
        {
            var name = await new Telegram(token).GetMe();
            if (DemoTelegram is not null) return;
            Settings.Current.Telegram = new TelegramSettings { Token = TelegramSettings.Protect(token), BotUsername = name };
            Settings.Save();
            TokenBox.Clear();
            Show();
        }
        catch (TgError e) { Show(Telegram.Describe(e)); }
        catch (Exception e) { Log.Warn("telegram", Telegram.Scrub(e)); Show(Telegram.Scrub(e.Message)); }
        finally { CheckButton.IsEnabled = true; }
    }

    async Task Pair()
    {
        if (DemoTelegram is not null || T.Unprotect() is not { } token) return;
        code = Pairing.NewCode();
        pairUntil = DateTime.UtcNow + Pairing.Lifetime;
        CodeText.Text = code;
        PairQr.Source = Qr($"https://t.me/{botUsername}?start={code}"); // for phones without Telegram on this PC
        using var cts = pairing = new CancellationTokenSource(Pairing.Lifetime);
        countdown.Start();
        Countdown();
        Show();
        string? error = null;
        try
        {
            var m = await Pairing.Wait(new Telegram(token), code, cts.Token);
            var t = Settings.Current.Telegram;
            (t.ChatId, t.UserId, t.FirstName, t.PairedAt) = (m.ChatId, m.UserId, m.FirstName, DateTime.UtcNow);
            Settings.Save();
            Notifier.Reconnect();
            Log.Info("telegram paired");
        }
        catch (OperationCanceledException) { error = DateTime.UtcNow >= pairUntil ? Loc.T("Tg.CodeExpired") : null; }
        catch (TgError e) { error = Telegram.Describe(e); }
        catch (Exception e) { Log.Warn("telegram pairing", Telegram.Scrub(e)); error = Telegram.Scrub(e.Message); }
        finally
        {
            countdown.Stop();
            pairing = null;
        }
        Show(error);
    }

    /// <summary>QR code as a frozen bitmap (QRCoder's PNG writer, no System.Drawing).</summary>
    static System.Windows.Media.ImageSource Qr(string text)
    {
        using var data = new QRCoder.QRCodeGenerator().CreateQrCode(text, QRCoder.QRCodeGenerator.ECCLevel.M);
        var png = new QRCoder.PngByteQRCode(data).GetGraphic(8);
        var img = new System.Windows.Media.Imaging.BitmapImage();
        img.BeginInit();
        img.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
        img.StreamSource = new System.IO.MemoryStream(png);
        img.EndInit();
        img.Freeze();
        return img;
    }

    void InitNtfy()
    {
        var n = Settings.Current.Ntfy;
        NtfyOn.IsChecked = n.Enabled;
        NtfyServer.Text = n.Server;
        NtfyTopic.Text = n.Topic ?? "";
        NtfyOn.Click += (_, _) =>
        {
            n.Enabled = NtfyOn.IsChecked == true;
            if (n.Enabled && string.IsNullOrWhiteSpace(n.Topic)) NtfyTopic.Text = n.Topic = NtfySettings.NewTopic();
            Settings.Save();
            Notifier.Reconnect();
            ShowNtfy();
        };
        NtfyNewTopic.Click += (_, _) => NtfyTopic.Text = NtfySettings.NewTopic();
        NtfySave.Click += (_, _) => SaveNtfy();
        NtfyTest.Click += async (_, _) =>
        {
            if (!SaveNtfy()) return;
            NtfyTest.IsEnabled = false;
            try
            {
                await new Ntfy(n).Send(Loc.T("Test.Title"), Loc.T("Test.Body"), Priority.Default);
                NtfyStatus.Text = Loc.T("Tg.TestSent");
            }
            catch (TgError e) { NtfyStatus.Text = Ntfy.Describe(e); }
            catch (Exception e) { NtfyStatus.Text = e.Message; }
            finally { NtfyTest.IsEnabled = true; }
        };
        ShowNtfy();
    }

    /// <summary>Server must be an http(s) URL; topic 1–64 of A-Z a-z 0-9 _ -; an empty token box keeps the saved token.</summary>
    bool SaveNtfy()
    {
        var n = Settings.Current.Ntfy;
        var server = NtfyServer.Text.Trim().TrimEnd('/');
        var topic = NtfyTopic.Text.Trim();
        if (!Uri.TryCreate(server, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")) { NtfyStatus.Text = Loc.T("Ntfy.Error.Server"); return false; }
        if (!System.Text.RegularExpressions.Regex.IsMatch(topic, "^[A-Za-z0-9_-]{1,64}$")) { NtfyStatus.Text = Loc.T("Ntfy.Error.Topic"); return false; }
        (n.Server, n.Topic) = (server, topic);
        if (NtfyToken.Password.Trim() is { Length: > 0 } token) n.Token = TelegramSettings.Protect(token);
        NtfyToken.Clear();
        Settings.Save();
        Notifier.Reconnect();
        NtfyStatus.Text = "";
        ShowNtfy();
        return true;
    }

    void ShowNtfy()
    {
        var n = Settings.Current.Ntfy;
        NtfyPanel.Visibility = n.Enabled ? Visibility.Visible : Visibility.Collapsed;
        NtfyQrPanel.Visibility = n.Ready ? Visibility.Visible : Visibility.Collapsed;
        if (n.Ready) NtfyQr.Source = Qr(n.SubscribeLink);
        NtfyToken.Tag = n.Token is null ? null : "set";
    }

    void Countdown()
    {
        var left = pairUntil - DateTime.UtcNow;
        WaitText.Text = string.Format(Loc.T("Tg.Waiting"), left < TimeSpan.Zero ? "0:00" : $"{(int)left.TotalMinutes}:{left.Seconds:00}");
    }

    void Unpair()
    {
        if (DemoTelegram is not null) return;
        pairing?.Cancel();
        Settings.Current.Telegram = new TelegramSettings();
        Settings.Save();
        Notifier.Reconnect();
        Show();
    }

    async Task Test()
    {
        var t = T;
        if (DemoTelegram is not null || t.Unprotect() is not { } token || t.ChatId is not { } chat) return;
        TestButton.IsEnabled = false;
        try
        {
            Toast.Show(new Note(Loc.T("Test.Title"), Loc.T("Test.Body")));
            await new Telegram(token).SendMessage(chat, $"{Loc.T("Test.Title")}\n{Loc.T("Test.Body")}", false);
            Show(null);
            StatusText.Text = Loc.T("Tg.TestSent");
        }
        catch (TgError e) { Show(Telegram.Describe(e)); }
        catch (Exception e) { Log.Warn("telegram", Telegram.Scrub(e)); Show(Telegram.Scrub(e.Message)); }
        finally { TestButton.IsEnabled = true; }
    }

    /// <summary>
    /// --demo only, for the phone screenshot: demo text through the really paired bot from the real settings file
    /// (the single network exception of the demo, spec §5.1).
    /// </summary>
    async Task DemoPush()
    {
        var real = Settings.Load(Settings.FilePath).Telegram;
        if (real.Unprotect() is not { } token || real.ChatId is not { } chat) { Show(Loc.T("Tg.DemoPush.NoBot")); return; }
        try
        {
            await new Telegram(token).SendMessage(chat, Loc.T("Demo.Push"), false);
            StatusText.Text = Loc.T("Tg.TestSent");
        }
        catch (TgError e) { Show(Telegram.Describe(e)); }
        catch (Exception e) { Log.Warn("telegram", Telegram.Scrub(e)); Show(Telegram.Scrub(e.Message)); }
    }
}
