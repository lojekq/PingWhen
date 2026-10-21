using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using PingWhen.Shell;

namespace PingWhen.Shell
{
    public sealed partial class Settings
    {
        public bool PhoneAnswers { get; set; }               // v1.0, Telegram only, off by default: approving = access to the Telegram account
        public int PhoneAnswerSeconds { get; set; } = 120;   // the hook's timeout in the JSON is 150
    }
}

namespace PingWhen.Features
{
    /// <summary>
    /// Answer Claude Code's permission prompt from the phone (v1.0, spec §3): PermissionRequest hook → Telegram message with
    /// "Allow once" / "Deny" buttons. callback_data is a one-time nonce; a press counts only from the paired user (from.id)
    /// and only while the request is alive. getUpdates runs only while a request waits. No "always allow", no text input.
    /// Until the terminal-dialog spike is done, the phone is asked only when the user is away; at the PC the hook exits at once.
    /// </summary>
    public static class Permissions
    {
        public const int HookTimeoutSeconds = 150;

        internal sealed record Pending(string Nonce, long MessageId, TaskCompletionSource<string?> Done, string Text);

        static readonly ConcurrentDictionary<string, Pending> pending = new();
        static int polling;
        static long offset;

        /// <summary>"mlink: Bash `npm test`" — the folder, the tool and its main argument.</summary>
        public static string Describe(Hooks.Input e)
        {
            var arg = e.ToolInput is { } t && t.ValueKind == JsonValueKind.Object
                ? Str(t, "command") ?? Str(t, "file_path") ?? Str(t, "url") ?? Str(t, "pattern") ?? Fmt.Cut(t.GetRawText(), 200)
                : null;
            return $"{e.Folder}: {e.ToolName ?? "?"}" + (arg is null ? "" : $" `{Fmt.Cut(arg.ReplaceLineEndings(" "), 300)}`");
            static string? Str(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }

        /// <summary>"allow", "deny" or null (no answer: Claude shows its usual dialog).</summary>
        public static async Task<string?> Ask(Hooks.Input e)
        {
            var s = Settings.Current;
            var t = s.Telegram;
            if (!s.PhoneAnswers || !t.Paired || t.UserId is null || t.Unprotect() is not { } token) return null;
            if (!Router.Away && !UserBusy.Now) return null; // at the PC: the terminal dialog is right there
            var bot = new Telegram(token);
            var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(12)).Replace('+', '-').Replace('/', '_'); // 16 chars
            var text = string.Format(Loc.T("Perm.Message"), Describe(e));
            long messageId;
            try { messageId = await bot.SendButtons(t.ChatId!.Value, text, [(Loc.T("Perm.Allow"), "a:" + nonce), (Loc.T("Perm.Deny"), "d:" + nonce)]); }
            catch (TgError ex) { Log.Warn($"permission message failed ({ex.Code})"); return null; }
            var p = new Pending(nonce, messageId, new(TaskCreationOptions.RunContinuationsAsynchronously), text);
            pending[nonce] = p;
            _ = Poll(bot, t.ChatId!.Value, t.UserId!.Value);
            try { return await p.Done.Task.WaitAsync(TimeSpan.FromSeconds(Math.Clamp(s.PhoneAnswerSeconds, 10, HookTimeoutSeconds - 10))); }
            catch (TimeoutException)
            {
                try { await bot.EditText(t.ChatId!.Value, messageId, text + "\n\n" + Loc.T("Perm.Expired")); } catch (TgError) { }
                return null;
            }
            finally { pending.TryRemove(nonce, out _); }
        }

        /// <summary>One loop while anything waits; button presses are checked against the nonce and the paired user.</summary>
        static async Task Poll(Telegram bot, long chatId, long userId)
        {
            if (Interlocked.Exchange(ref polling, 1) == 1) return;
            try
            {
                while (!pending.IsEmpty)
                {
                    JsonElement updates;
                    try { updates = await bot.GetUpdates(offset, 20, CancellationToken.None, "callback_query"); }
                    catch (TgError e) { Log.Warn($"permission polling failed ({e.Code})"); await Task.Delay(3000); continue; }
                    foreach (var u in updates.EnumerateArray())
                    {
                        offset = u.GetProperty("update_id").GetInt64() + 1;
                        if (Check(u, userId) is not { } press) continue;
                        try
                        {
                            if (press.Pending is not { } p) { await bot.AnswerCallback(press.QueryId, Loc.T("Perm.Gone")); continue; }
                            await bot.AnswerCallback(press.QueryId, "");
                            await bot.EditText(chatId, p.MessageId, p.Text + "\n\n" + Loc.T(press.Allow ? "Perm.Allowed" : "Perm.Denied"));
                            p.Done.TrySetResult(press.Allow ? "allow" : "deny");
                        }
                        catch (TgError e) { Log.Warn($"permission answer failed ({e.Code})"); }
                    }
                }
            }
            catch (Exception e) { Log.Error("permission polling", e); }
            finally { Volatile.Write(ref polling, 0); }
        }

        internal sealed record Press(string QueryId, bool Allow, Pending? Pending);

        /// <summary>A button press of the paired user on a live request; a stranger's press is ignored (null).</summary>
        internal static Press? Check(JsonElement update, long userId)
        {
            if (!update.TryGetProperty("callback_query", out var q)) return null;
            if (!q.TryGetProperty("from", out var from) || from.GetProperty("id").GetInt64() != userId) return null;
            var data = q.TryGetProperty("data", out var d) ? d.GetString() ?? "" : "";
            var id = q.GetProperty("id").GetString() ?? "";
            if (data.Length < 3 || data[1] != ':' || data[0] is not ('a' or 'd')) return new(id, false, null);
            return new(id, data[0] == 'a', pending.TryGetValue(data[2..], out var p) ? p : null);
        }

        /// <summary>Tests: a request that waits for a press.</summary>
        internal static TaskCompletionSource<string?> AddForTest(string nonce)
        {
            var p = new Pending(nonce, 1, new(), "t");
            pending[nonce] = p;
            return p.Done;
        }

        /// <summary>The single JSON line --hook prints for a decision; nothing at all otherwise.</summary>
        public static string Output(string decision) =>
            JsonSerializer.Serialize(new { hookSpecificOutput = new { hookEventName = "PermissionRequest", decision = new { behavior = decision } } });
    }
}
