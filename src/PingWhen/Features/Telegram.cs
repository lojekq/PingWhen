using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PingWhen.Shell;

namespace PingWhen.Shell
{
    public sealed partial class Settings
    {
        public Features.TelegramSettings Telegram { get; set; } = new();
    }
}

namespace PingWhen.Features
{
    /// <summary>Token is DPAPI (CurrentUser), base64. ChatId/UserId come from pairing.</summary>
    public sealed class TelegramSettings
    {
        public string? Token { get; set; }
        public string? BotUsername { get; set; }
        public long? ChatId { get; set; }
        public long? UserId { get; set; }
        public string? FirstName { get; set; }
        public DateTime? PairedAt { get; set; }

        public bool Paired => Token is not null && ChatId is not null;

        public static string Protect(string token) =>
            Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser));

        public string? Unprotect()
        {
            if (Token is null) return null;
            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(Token), null, DataProtectionScope.CurrentUser)); }
            catch (Exception e) { Log.Warn("telegram token not readable (another user or PC?)", Telegram.Scrub(e)); return null; }
        }
    }

    /// <summary>A failed Bot API call. Code 0 = network or timeout; RetryAfter from 429 parameters.</summary>
    public sealed class TgError(int code, string description, int retryAfter = 0) : Exception(description)
    {
        public int Code { get; } = code;
        public int RetryAfter { get; } = retryAfter;
        public bool Permanent => Code is 400 or 401 or 403 or 404;
    }

    /// <summary>
    /// The user's own bot, Bot API over HttpClient + System.Text.Json (no Telegram.Bot NuGet). MVP: getMe, getUpdates, sendMessage.
    /// The token is part of the URL: URLs are never logged and exception text goes through <see cref="Scrub(string)"/>.
    /// System proxy is used, so the user's VPN or proxy just works.
    /// </summary>
    public sealed partial class Telegram(HttpClient http, string token)
    {
        static readonly HttpClient Shared = new() { Timeout = Timeout.InfiniteTimeSpan };

        /// <summary>Debug builds take PINGWHEN_TG_API (http://127.0.0.1:9 = "server down") so the network never has to be switched off.</summary>
        public static string Api =>
#if DEBUG
            Environment.GetEnvironmentVariable("PINGWHEN_TG_API")?.TrimEnd('/') ??
#endif
            "https://api.telegram.org";

        public Telegram(string token) : this(Shared, token) { }

        [GeneratedRegex(@"\d+:[A-Za-z0-9_-]{30,}")] private static partial Regex TokenRx();
        public static string Scrub(string s) => TokenRx().Replace(s, "<token>");
        /// <summary>For Log.Warn: the exception as text without the token, never the exception object.</summary>
        public static Exception Scrub(Exception e) => new(Scrub(e.ToString()));

        public static bool LooksLikeToken(string s) => TokenRx().IsMatch(s.Trim()) && !s.Trim().Contains(' ');

        Task<JsonElement> Call(string method, object body, TimeSpan timeout, CancellationToken ct = default) =>
            Call(method, JsonContent.Create(body), timeout, ct);

        async Task<JsonElement> Call(string method, HttpContent content, TimeSpan timeout, CancellationToken ct = default)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            HttpResponseMessage r;
            try { r = await http.PostAsync($"{Api}/bot{token}/{method}", content, cts.Token); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException) { throw new TgError(0, Scrub(e.Message)); }
            using (r)
            {
                JsonElement json;
                try { json = (await r.Content.ReadFromJsonAsync<JsonElement>(cts.Token)).Clone(); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception) { throw new TgError(r.IsSuccessStatusCode ? 0 : (int)r.StatusCode, $"HTTP {(int)r.StatusCode}"); }
                if (json.TryGetProperty("ok", out var ok) && ok.GetBoolean()) return json.GetProperty("result");
                var code = json.TryGetProperty("error_code", out var c) ? c.GetInt32() : (int)r.StatusCode;
                var desc = json.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
                var retry = json.TryGetProperty("parameters", out var p) && p.TryGetProperty("retry_after", out var ra) ? ra.GetInt32() : 0;
                throw new TgError(code, desc, retry);
            }
        }

        /// <summary>The bot's @username; 401 → wrong token.</summary>
        public async Task<string> GetMe()
        {
            var me = await Call("getMe", new { }, TimeSpan.FromSeconds(15));
            return me.GetProperty("username").GetString() ?? "";
        }

        public async Task<JsonElement> GetUpdates(long offset, int timeout, CancellationToken ct) =>
            await Call("getUpdates", new { offset, timeout, allowed_updates = new[] { "message" } }, TimeSpan.FromSeconds(timeout + 10), ct);

        public const int MaxText = 4096;

        /// <summary>Plain text, no parse_mode: window titles with &lt;, _ or * go as they are.</summary>
        public Task SendMessage(long chatId, string text, bool silent, CancellationToken ct = default) =>
            Call("sendMessage", new { chat_id = chatId, text = Fmt.Cut(text, MaxText), disable_notification = silent }, TimeSpan.FromSeconds(30), ct);

        public const int MaxCaption = 1024;

        /// <summary>v1.0: JPEG (≤ 10 MB upload) with a caption of at most 1024 characters, multipart.</summary>
        public async Task SendPhoto(long chatId, byte[] jpeg, string caption, bool silent, CancellationToken ct = default)
        {
            using var form = new MultipartFormDataContent
            {
                { new StringContent(chatId.ToString(System.Globalization.CultureInfo.InvariantCulture)), "chat_id" },
                { new StringContent(Fmt.Cut(caption, MaxCaption)), "caption" },
                { new StringContent(silent ? "true" : "false"), "disable_notification" },
            };
            var file = new ByteArrayContent(jpeg);
            file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
            form.Add(file, "photo", "pingwhen.jpg");
            await Call("sendPhoto", form, TimeSpan.FromSeconds(90), ct);
        }

        /// <summary>Text for the settings card.</summary>
        public static string Describe(TgError e) => e.Code switch
        {
            0 => Loc.T("Tg.Error.Network"),
            401 or 404 => Loc.T("Tg.Error.Token"),
            403 => Loc.T("Tg.Error.Blocked"),
            409 when e.Message.Contains("webhook", StringComparison.OrdinalIgnoreCase) => Loc.T("Tg.Error.Webhook"),
            409 => Loc.T("Tg.Error.OtherPoller"),
            429 => string.Format(Loc.T("Tg.Error.TooMany"), e.RetryAfter),
            _ => $"{e.Code}: {e.Message}",
        };
    }

    /// <summary>One-time code pairing (spec §5.5): /start &lt;code&gt; or the bare code in a private chat, case and spaces ignored.</summary>
    public static class Pairing
    {
        const string Alphabet = "abcdefghijkmnopqrstuvwxyz23456789"; // [a-km-z2-9]: no l, 0, 1
        public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

        public static string NewCode() => string.Create(8, 0, (s, _) => { for (var i = 0; i < s.Length; i++) s[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]; });

        public sealed record Match(long ChatId, long UserId, string FirstName);

        public static Match? Check(JsonElement update, string code)
        {
            if (!update.TryGetProperty("message", out var m)) return null;
            if (!m.TryGetProperty("chat", out var chat) || chat.GetProperty("type").GetString() != "private") return null;
            if (!m.TryGetProperty("text", out var t) || t.GetString()?.Trim() is not { } text) return null;
            if (text.StartsWith("/start", StringComparison.OrdinalIgnoreCase)) text = text[6..].Trim();
            if (!text.Equals(code, StringComparison.OrdinalIgnoreCase)) return null;
            var from = m.GetProperty("from");
            return new(chat.GetProperty("id").GetInt64(), from.GetProperty("id").GetInt64(),
                from.TryGetProperty("first_name", out var f) ? f.GetString() ?? "" : "");
        }

        /// <summary>
        /// Long-polls getUpdates until the code arrives (or <paramref name="ct"/>: dialog closed, 10 min).
        /// Confirms offset = update_id + 1 and greets the chat. Never calls deleteWebhook: that would break someone else's integration.
        /// </summary>
        public static async Task<Match> Wait(Telegram tg, string code, CancellationToken ct)
        {
            long offset = 0;
            while (true)
            {
                var updates = await tg.GetUpdates(offset, 30, ct);
                foreach (var u in updates.EnumerateArray())
                {
                    offset = u.GetProperty("update_id").GetInt64() + 1;
                    if (Check(u, code) is not { } m) continue;
                    try { await tg.GetUpdates(offset, 0, CancellationToken.None); } catch (TgError) { } // confirm, so the code is not seen again
                    await tg.SendMessage(m.ChatId, Loc.T("Tg.Paired.Message"), false, CancellationToken.None);
                    return m;
                }
            }
        }
    }
}
