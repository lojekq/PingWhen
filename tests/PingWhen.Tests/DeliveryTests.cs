using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PingWhen.Features;
using PingWhen.Shell;

namespace PingWhen.Tests;

/// <summary>Bot API stub: no network, fake token. Answers in order; records every request.</summary>
sealed class StubTelegram : HttpMessageHandler
{
    public readonly List<(string Method, JsonNode? Body)> Requests = [];
    public readonly Queue<Func<string, JsonNode?, (int Status, string Json)>> Answers = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
    {
        var method = r.RequestUri!.Segments[^1];
        var body = r.Content is null ? null : JsonNode.Parse(await r.Content.ReadAsStringAsync(ct));
        Requests.Add((method, body));
        var (status, json) = Answers.Count > 0 ? Answers.Dequeue()(method, body) : (200, """{"ok":true,"result":[]}""");
        return new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    public static string Update(long id, string text, string chatType = "private", long chatId = 555, long fromId = 777) =>
        JsonSerializer.Serialize(new
        {
            update_id = id,
            message = new { message_id = 1, from = new { id = fromId, is_bot = false, first_name = "Alex" }, chat = new { id = chatId, type = chatType }, date = 1, text },
        });
}

[TestClass]
public class DeliveryTests
{
    const string FakeToken = "123456:AAFakeFakeFakeFakeFakeFakeFakeFake_x";

    static (Telegram, StubTelegram) Bot()
    {
        var stub = new StubTelegram();
        return (new Telegram(new HttpClient(stub), FakeToken), stub);
    }

    [TestMethod]
    public void RetryScheduleIs5s30s2mThenGiveUp()
    {
        var net = new TgError(0, "down");
        Assert.AreEqual(TimeSpan.FromSeconds(5), Outbox.RetryDelay(1, net));
        Assert.AreEqual(TimeSpan.FromSeconds(30), Outbox.RetryDelay(2, net));
        Assert.AreEqual(TimeSpan.FromMinutes(2), Outbox.RetryDelay(3, net));
        Assert.IsNull(Outbox.RetryDelay(4, net));
    }

    [TestMethod]
    public void TooManyRequestsWaitsRetryAfter()
    {
        Assert.AreEqual(TimeSpan.FromSeconds(60), Outbox.RetryDelay(1, new TgError(429, "Too Many Requests", 60)));
        Assert.AreEqual(TimeSpan.FromSeconds(30), Outbox.RetryDelay(2, new TgError(429, "Too Many Requests", 3))); // shorter: schedule wins
    }

    [TestMethod]
    public void BadRequestUnauthorizedForbiddenAreNotRetried()
    {
        foreach (var code in new[] { 400, 401, 403 }) Assert.IsNull(Outbox.RetryDelay(1, new TgError(code, "x")));
    }

    [TestMethod]
    public void EventsInTheWindowAreMergedIntoOneMessage()
    {
        var (text, silent) = Outbox.Merge([new Note("Done: ffmpeg", "code 0", Priority.Low), new Note("Done: npm", "code 0")]);
        Assert.AreEqual("Done: ffmpeg\ncode 0\n\nDone: npm\ncode 0", text);
        Assert.IsFalse(silent);
        Assert.IsTrue(Outbox.Merge([new Note("a", "", Priority.Low)]).Silent);
    }

    [TestMethod]
    public async Task OutboxMergesSendsOnceAndReportsFailure()
    {
        var sent = new List<string>();
        var box = new Outbox((text, _, _) => { sent.Add(text); throw new TgError(403, "Forbidden: bot was blocked by the user"); });
        var failed = new TaskCompletionSource<int>();
        box.Failed += (batch, e) => failed.TrySetResult(batch.Count * 1000 + e.Code);
        box.Enqueue(new Note("a", "1"));
        box.Enqueue(new Note("b", "2"));
        Assert.AreEqual(2403, await failed.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.HasCount(1, sent); // merged, 403 not retried
    }

    [TestMethod]
    public async Task PairingTakesStartCodeOrBareCodeInAnyCase()
    {
        foreach (var text in new[] { "/start k7m2p9qx", "K7M2P9QX", "  k7m2p9qx  ", "/START k7m2p9qx" })
        {
            var (tg, stub) = Bot();
            stub.Answers.Enqueue((_, _) => (200, $$"""{"ok":true,"result":[{{StubTelegram.Update(10, text)}}]}"""));
            var m = await Pairing.Wait(tg, "k7m2p9qx", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(555, m.ChatId);
            Assert.AreEqual(777, m.UserId);
            Assert.AreEqual("Alex", m.FirstName);
            // confirm offset = update_id + 1, then the greeting goes to the paired chat only
            Assert.AreEqual("getUpdates", stub.Requests[1].Method);
            Assert.AreEqual(11, (long)stub.Requests[1].Body!["offset"]!);
            Assert.AreEqual("sendMessage", stub.Requests[2].Method);
            Assert.AreEqual(555, (long)stub.Requests[2].Body!["chat_id"]!);
        }
    }

    [TestMethod]
    public async Task PairingIgnoresOtherCodesGroupsAndStopsWhenExpired()
    {
        var (tg, stub) = Bot();
        stub.Answers.Enqueue((_, _) => (200, $$"""{"ok":true,"result":[{{StubTelegram.Update(1, "/start aaaaaaaa")}},{{StubTelegram.Update(2, "k7m2p9qx", "group")}}]}"""));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500)); // the code's lifetime is the token
        var gotIt = false;
        try { await Pairing.Wait(tg, "k7m2p9qx", cts.Token); gotIt = true; }
        catch (OperationCanceledException) { }
        Assert.IsFalse(gotIt);
        Assert.IsFalse(stub.Requests.Any(r => r.Method == "sendMessage"));
        Assert.AreEqual(3, (long)stub.Requests[1].Body!["offset"]!); // moved past both
    }

    [TestMethod]
    public async Task WebhookConflictIsExplainedAndNeverDeleted()
    {
        var (tg, stub) = Bot();
        stub.Answers.Enqueue((_, _) => (409, """{"ok":false,"error_code":409,"description":"Conflict: can't use getUpdates method while webhook is active; use deleteWebhook to delete the webhook first"}"""));
        var e = await Assert.ThrowsExactlyAsync<TgError>(() => Pairing.Wait(tg, "k7m2p9qx", CancellationToken.None));
        Assert.AreEqual(Loc.T("Tg.Error.Webhook"), Telegram.Describe(e));
        Assert.IsFalse(stub.Requests.Any(r => r.Method.Contains("deleteWebhook", StringComparison.OrdinalIgnoreCase)));
        Assert.AreEqual(Loc.T("Tg.Error.OtherPoller"), Telegram.Describe(new TgError(409, "Conflict: terminated by other getUpdates request")));
    }

    [TestMethod]
    public async Task SendMessageCutsSilencesAndSendsTextAsIs()
    {
        var (tg, stub) = Bot();
        await tg.SendMessage(555, "<b>" + new string('x', 5000), silent: true);
        var body = stub.Requests[0].Body!;
        var text = (string)body["text"]!;
        Assert.AreEqual(Telegram.MaxText, text.Length);
        Assert.StartsWith("<b>", text);
        Assert.IsTrue((bool)body["disable_notification"]!);
        Assert.IsNull(body["parse_mode"]);
    }

    [TestMethod]
    public async Task UnauthorizedAndRetryAfterAreParsed()
    {
        var (tg, stub) = Bot();
        stub.Answers.Enqueue((_, _) => (401, """{"ok":false,"error_code":401,"description":"Unauthorized"}"""));
        Assert.AreEqual(401, (await Assert.ThrowsExactlyAsync<TgError>(tg.GetMe)).Code);
        stub.Answers.Enqueue((_, _) => (429, """{"ok":false,"error_code":429,"description":"Too Many Requests: retry after 60","parameters":{"retry_after":60}}"""));
        Assert.AreEqual(60, (await Assert.ThrowsExactlyAsync<TgError>(() => tg.SendMessage(1, "x", false))).RetryAfter);
    }

    [TestMethod]
    public void TokenIsScrubbedFromLogText()
    {
        var e = new HttpRequestException($"Connection refused (https://api.telegram.org/bot{FakeToken}/sendMessage)");
        var text = Telegram.Scrub(e).Message;
        Assert.DoesNotContain(FakeToken, text);
        Assert.DoesNotContain("AAFake", text);
        Assert.Contains("bot<token>/sendMessage", text);
    }

    [TestMethod]
    public void PairingCodesAreEightCharsWithoutLookalikes()
    {
        for (var i = 0; i < 200; i++)
        {
            var c = Pairing.NewCode();
            Assert.AreEqual(8, c.Length);
            Assert.IsTrue(c.All(ch => (ch is >= 'a' and <= 'z' && ch != 'l') || (ch is >= '2' and <= '9')), c);
        }
    }

    [TestMethod]
    public void TokenSurvivesDpapiRoundTrip()
    {
        var t = new TelegramSettings { Token = TelegramSettings.Protect(FakeToken) };
        Assert.AreEqual(FakeToken, t.Unprotect());
        Assert.DoesNotContain("AAFake", t.Token);
    }
}
