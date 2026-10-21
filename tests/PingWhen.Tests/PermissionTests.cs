using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PingWhen.Features;

namespace PingWhen.Tests;

/// <summary>v1.0 stage 10: answering Claude Code's permission prompt from the phone.</summary>
[TestClass]
public class PermissionTests
{
    const string Request = """{"session_id":"s1","cwd":"C:\\Users\\demo\\src\\mlink","hook_event_name":"PermissionRequest","tool_name":"Bash","tool_input":{"command":"npm test","description":"Run tests"}}""";

    static JsonElement Press(long fromId, string data) => JsonDocument.Parse(JsonSerializer.Serialize(new
    {
        update_id = 5,
        callback_query = new { id = "q1", from = new { id = fromId, is_bot = false, first_name = "Alex" }, data, chat_instance = "x" },
    })).RootElement;

    [TestMethod]
    public void RequestIsDescribedWithFolderToolAndCommand()
    {
        var e = Hooks.Parse(Request)!;
        Assert.AreEqual("Bash", e.ToolName);
        Assert.AreEqual("mlink: Bash `npm test`", Permissions.Describe(e));
        var edit = Hooks.Parse(Request.Replace("\"Bash\"", "\"Edit\"").Replace("\"command\":\"npm test\"", "\"file_path\":\"C:\\\\x\\\\a.cs\""))!;
        Assert.AreEqual(@"mlink: Edit `C:\x\a.cs`", Permissions.Describe(edit));
    }

    [TestMethod]
    public void OnlyThePairedUserOnALiveRequestCounts()
    {
        var done = Permissions.AddForTest("nonce01");
        var mine = Permissions.Check(Press(777, "a:nonce01"), 777)!;
        Assert.IsTrue(mine.Allow);
        Assert.IsNotNull(mine.Pending);
        Assert.IsNull(Permissions.Check(Press(666, "a:nonce01"), 777), "a stranger's press is ignored");
        Assert.IsNull(Permissions.Check(Press(777, "d:expired"), 777)!.Pending, "an expired request is not answered");
        Assert.IsNull(Permissions.Check(Press(777, "zz"), 777)!.Pending);
        Assert.IsFalse(Permissions.Check(Press(777, "d:nonce01"), 777)!.Allow);
        Assert.IsFalse(done.Task.IsCompleted);
    }

    [TestMethod]
    public void OutputIsExactlyTheHookJson()
    {
        Assert.AreEqual("""{"hookSpecificOutput":{"hookEventName":"PermissionRequest","decision":{"behavior":"allow"}}}""", Permissions.Output("allow"));
        Assert.AreEqual("""{"hookSpecificOutput":{"hookEventName":"PermissionRequest","decision":{"behavior":"deny"}}}""", Permissions.Output("deny"));
    }

    [TestMethod]
    public async Task OffByDefaultMeansNoMessageAndNoDecision() =>
        Assert.IsNull(await Permissions.Ask(Hooks.Parse(Request)!)); // test settings: phone answers off, no bot

    sealed class Stub : HttpMessageHandler
    {
        public readonly List<(string Method, JsonNode Body)> Seen = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Seen.Add((r.RequestUri!.Segments[^1], JsonNode.Parse(await r.Content!.ReadAsStringAsync(ct))!));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"ok":true,"result":{"message_id":42}}""", Encoding.UTF8, "application/json") };
        }
    }

    [TestMethod]
    public async Task ButtonsAnswerAndEditGoToTheBotApi()
    {
        var stub = new Stub();
        var bot = new Telegram(new HttpClient(stub), "123456:AAFakeFakeFakeFakeFakeFakeFakeFake_x");
        Assert.AreEqual(42, await bot.SendButtons(555, "mlink: Bash `npm test`", [("Allow once", "a:abcdefghijklmnop"), ("Deny", "d:abcdefghijklmnop")]));
        await bot.AnswerCallback("q1", "");
        await bot.EditText(555, 42, "done");
        var keyboard = stub.Seen[0].Body["reply_markup"]!["inline_keyboard"]![0]!.AsArray();
        Assert.HasCount(2, keyboard);
        Assert.AreEqual("a:abcdefghijklmnop", (string)keyboard[0]!["callback_data"]!);
        Assert.IsLessThanOrEqualTo(64, Encoding.UTF8.GetByteCount((string)keyboard[1]!["callback_data"]!));
        CollectionAssert.AreEqual(new[] { "sendMessage", "answerCallbackQuery", "editMessageText" }, stub.Seen.Select(s => s.Method).ToArray());
        Assert.IsNull(stub.Seen[2].Body["reply_markup"], "the edit drops the buttons");
    }
}
