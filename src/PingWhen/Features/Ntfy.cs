using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using PingWhen.Shell;

namespace PingWhen.Shell
{
    public sealed partial class Settings
    {
        public Features.NtfySettings Ntfy { get; set; } = new();
    }
}

namespace PingWhen.Features
{
    /// <summary>ntfy (v1.0, spec §3): ntfy.sh or your own server; a random 24-character topic; optional access token (DPAPI).</summary>
    public sealed class NtfySettings
    {
        public bool Enabled { get; set; }
        public string Server { get; set; } = "https://ntfy.sh";
        public string? Topic { get; set; }
        public string? Token { get; set; }   // DPAPI, base64, like the Telegram token

        [System.Text.Json.Serialization.JsonIgnore] public bool Ready => Enabled && !string.IsNullOrWhiteSpace(Topic) && Uri.TryCreate(Server, UriKind.Absolute, out _);

        public string? UnprotectToken()
        {
            if (Token is null) return null;
            try { return System.Text.Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(Token), null, DataProtectionScope.CurrentUser)); }
            catch (Exception e) { Log.Warn("ntfy token not readable", Telegram.Scrub(e)); return null; }
        }

        /// <summary>Hard to guess: whoever knows the topic reads the messages.</summary>
        public static string NewTopic() =>
            string.Create(24, 0, (s, _) => { for (var i = 0; i < s.Length; i++) s[i] = "abcdefghijkmnopqrstuvwxyz23456789"[RandomNumberGenerator.GetInt32(33)]; });

        /// <summary>The Android/iOS app link for the QR code: ntfy://&lt;host&gt;/&lt;topic&gt;?display=PingWhen.</summary>
        [System.Text.Json.Serialization.JsonIgnore] public string SubscribeLink => $"ntfy://{new Uri(Server).Authority}/{Topic}?display={AppInfo.DisplayName}";
    }

    /// <summary>
    /// JSON publish to the server root ({"topic","title","message","priority"}), an attachment by PUT /&lt;topic&gt;.
    /// Errors map to <see cref="TgError"/> codes, so Outbox retries them the same way (429, 5xx, network; 4xx is final).
    /// </summary>
    public sealed class Ntfy(HttpClient http, string server, string topic, string? token)
    {
        static readonly HttpClient Shared = new() { Timeout = TimeSpan.FromSeconds(30) };
        public Ntfy(NtfySettings s) : this(Shared, s.Server.TrimEnd('/'), s.Topic!, s.UnprotectToken()) { }

        public const int MaxAttachment = 2 * 1024 * 1024; // ntfy.sh limit

        public static string Describe(TgError e) => e.Code switch
        {
            0 => Loc.T("Ntfy.Error.Network"),
            403 => Loc.T("Ntfy.Error.Denied"),
            429 => Loc.T("Ntfy.Error.TooMany"),
            _ => $"ntfy {e.Code}: {e.Message}",
        };

        /// <summary>ntfy priorities: 2 low, 3 default, 4 high.</summary>
        public async Task Send(string title, string message, Priority priority, CancellationToken ct = default)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, server + "/")
            {
                Content = JsonContent.Create(new { topic, title = Fmt.Cut(title, 250), message = Fmt.Cut(message, 4000), priority = priority switch { Priority.Low => 2, Priority.High => 4, _ => 3 } }),
            };
            await Send(req, ct);
        }

        public async Task SendFile(byte[] jpeg, string fileName, string title, string message, CancellationToken ct = default)
        {
            using var req = new HttpRequestMessage(HttpMethod.Put, $"{server}/{topic}") { Content = new ByteArrayContent(jpeg) };
            req.Headers.Add("Filename", fileName);
            req.Headers.Add("Title", Uri.EscapeDataString(Fmt.Cut(title, 250)));     // headers are ASCII: ntfy decodes RFC 2047 or %-escapes
            req.Headers.Add("Message", Uri.EscapeDataString(Fmt.Cut(message, 1000)));
            await Send(req, ct);
        }

        async Task Send(HttpRequestMessage req, CancellationToken ct)
        {
            if (token is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            HttpResponseMessage r;
            try { r = await http.SendAsync(req, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException) { throw new TgError(0, e.Message); }
            using (r)
            {
                if (r.IsSuccessStatusCode) return;
                var body = await r.Content.ReadAsStringAsync(ct);
                string? error = null;
                try { error = JsonDocument.Parse(body).RootElement.GetProperty("error").GetString(); } catch { }
                var code = (int)r.StatusCode;
                throw new TgError(code is >= 500 or 429 ? code : code is 401 or 403 ? 403 : 400, error ?? $"HTTP {code}");
            }
        }
    }
}
