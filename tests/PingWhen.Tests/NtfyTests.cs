using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using PingWhen.Features;

namespace PingWhen.Tests;

/// <summary>v1.0 stage 8: ntfy over a stub handler, no network.</summary>
[TestClass]
public class NtfyTests
{
    sealed class Stub(HttpStatusCode status, string body = "{}") : HttpMessageHandler
    {
        public readonly List<(HttpRequestMessage Req, string Body)> Seen = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Seen.Add((r, r.Content is null ? "" : await r.Content.ReadAsStringAsync(ct)));
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    [TestMethod]
    public async Task PublishesJsonToTheServerRootWithPriorityAndToken()
    {
        var stub = new Stub(HttpStatusCode.OK);
        await new Ntfy(new HttpClient(stub), "https://ntfy.example.com", "abc123topic", "tk_secret").Send("Done: ffmpeg", "code 0", Priority.High);
        var (req, body) = stub.Seen.Single();
        Assert.AreEqual(HttpMethod.Post, req.Method);
        Assert.AreEqual("https://ntfy.example.com/", req.RequestUri!.ToString());
        Assert.AreEqual("Bearer tk_secret", req.Headers.Authorization!.ToString());
        var json = JsonNode.Parse(body)!;
        Assert.AreEqual("abc123topic", (string)json["topic"]!);
        Assert.AreEqual("Done: ffmpeg", (string)json["title"]!);
        Assert.AreEqual(4, (int)json["priority"]!);
    }

    [TestMethod]
    public async Task LowIsTwoAndNoTokenMeansNoHeader()
    {
        var stub = new Stub(HttpStatusCode.OK);
        await new Ntfy(new HttpClient(stub), "https://ntfy.sh", "t", null).Send("a", "b", Priority.Low);
        Assert.IsNull(stub.Seen[0].Req.Headers.Authorization);
        Assert.AreEqual(2, (int)JsonNode.Parse(stub.Seen[0].Body)!["priority"]!);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.TooManyRequests, 429, true)]
    [DataRow(HttpStatusCode.Forbidden, 403, false)]
    [DataRow(HttpStatusCode.Unauthorized, 403, false)]
    [DataRow(HttpStatusCode.BadRequest, 400, false)]
    [DataRow(HttpStatusCode.BadGateway, 502, true)]
    public async Task ErrorsMapToOutboxRetries(HttpStatusCode status, int code, bool retried)
    {
        var stub = new Stub(status, """{"code":42901,"http":429,"error":"limit reached"}""");
        var e = await Assert.ThrowsExactlyAsync<TgError>(() => new Ntfy(new HttpClient(stub), "https://ntfy.sh", "t", null).Send("a", "b", Priority.Default));
        Assert.AreEqual(code, e.Code);
        Assert.AreEqual(retried, Outbox.RetryDelay(1, e) is not null);
    }

    [TestMethod]
    public void TopicsAreRandomAndTheSubscribeLinkHasTheHost()
    {
        var a = NtfySettings.NewTopic();
        Assert.AreEqual(24, a.Length);
        Assert.AreNotEqual(a, NtfySettings.NewTopic());
        var s = new NtfySettings { Enabled = true, Server = "https://ntfy.example.com:8443", Topic = a };
        Assert.IsTrue(s.Ready);
        Assert.AreEqual($"ntfy://ntfy.example.com:8443/{a}?display=PingWhen", s.SubscribeLink);
        Assert.IsFalse(new NtfySettings { Enabled = true, Topic = "" }.Ready);
    }
}
