using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using PingWhen.Shell;

namespace PingWhen.Features;

/// <summary>
/// --mcp, --hook, --notify: no WPF Application, nothing on stdout except MCP traffic, log to the verb's own file.
/// If the tray isn't running it is started through explorer.exe (so it is not a child of Claude Code, which kills
/// the MCP server's process tree, and never inherits admin rights), and the pipe is awaited.
/// </summary>
public static class Cli
{
    /// <summary>Held while the CLI starts the tray: the tray then stays in the tray instead of opening its window.</summary>
    public static readonly string StartEvent = $@"Local\lojekq.{AppInfo.InstanceName}.cli-start";

    public static bool StartedByCli() => EventWaitHandle.TryOpenExisting(StartEvent, out var e) && e is not null && Dispose(e);
    static bool Dispose(IDisposable d) { d.Dispose(); return true; }

    public static int Mcp(string[] args)
    {
        using var pipe = Connect("mcp", TimeSpan.FromSeconds(10));
        if (pipe is null) return 1;
        var stdin = Console.OpenStandardInput();
        var stdout = Console.OpenStandardOutput();
        var up = stdin.CopyToAsync(pipe);   // EOF on stdin → close the pipe at once, the tray ends the session
        var down = pipe.CopyToAsync(stdout);
        try { Task.WaitAny(up, down); } catch { }
        Log.Info($"mcp relay ends: {(up.IsCompleted ? "stdin closed" : "pipe closed")}");
        return 0;
    }

    /// <summary>
    /// Always exit 0. Never stdout, except one JSON for a PermissionRequest answered from the phone (v1.0):
    /// for Stop, exit 2 or output would mean "keep working".
    /// </summary>
    public static int Hook(string[] args)
    {
        try
        {
            var json = ReadStdin(64 * 1024, TimeSpan.FromSeconds(2));
            if (json is null || Hooks.Parse(json) is null) return 0;
            using var pipe = Connect("hook", TimeSpan.FromSeconds(5));
            if (pipe is null) return 0;
            var compact = JsonSerializer.Serialize(JsonDocument.Parse(json).RootElement); // one line on the pipe
            var permission = Hooks.Parse(json)?.Event == "PermissionRequest";
            var answer = Exchange(pipe, compact, TimeSpan.FromSeconds(permission ? Permissions.HookTimeoutSeconds - 5 : 3));
            if (permission && answer is not null && JsonDocument.Parse(answer).RootElement.TryGetProperty("decision", out var d) && d.GetString() is "allow" or "deny")
            {
                using var stdout = Console.OpenStandardOutput();
                stdout.Write(System.Text.Encoding.UTF8.GetBytes(Permissions.Output(d.GetString()!) + "\n"));
            }
        }
        catch (Exception e) { Log.Warn("hook failed", e); }
        return 0;
    }

    /// <summary>--notify "Title" ["Body"]; more than two unquoted words are one title. Exit 0 = accepted by the tray.</summary>
    public static int Notify(string[] args)
    {
        if (args.Length == 0) { Console.Error.WriteLine("usage: PingWhen.exe --notify \"Title\" [\"Body\"]"); return 1; }
        var (title, body) = args.Length <= 2 ? (args[0], args.ElementAtOrDefault(1) ?? "") : (string.Join(" ", args), "");
        using var pipe = Connect("notify", TimeSpan.FromSeconds(10));
        if (pipe is null) return 1;
        return Exchange(pipe, JsonSerializer.Serialize(new { title, body }), TimeSpan.FromSeconds(3))?.Contains("\"ok\":true") == true ? 0 : 1;
    }

    /// <summary>Sends one line and returns the tray's answer line, or null on timeout.</summary>
    static string? Exchange(Stream pipe, string line, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            PipeServer.WriteLine(pipe, line, cts.Token).GetAwaiter().GetResult();
            return PipeServer.ReadLine(pipe, 4096, cts.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) { return null; }
    }

    /// <summary>Stdin up to <paramref name="max"/> bytes; null on timeout or overflow.</summary>
    static string? ReadStdin(int max, TimeSpan timeout)
    {
        var read = Task.Run(() =>
        {
            using var s = Console.OpenStandardInput();
            var buf = new MemoryStream();
            var chunk = new byte[8192];
            int n;
            while ((n = s.Read(chunk)) > 0)
            {
                buf.Write(chunk, 0, n);
                if (buf.Length > max) return null;
            }
            return System.Text.Encoding.UTF8.GetString(buf.ToArray());
        });
        return read.Wait(timeout) ? read.Result : null;
    }

    /// <summary>Connects, sends hello, waits for {"ok":true}. Starts the tray if it isn't running. Errors go to stderr.</summary>
    static NamedPipeClientStream? Connect(string mode, TimeSpan wait)
    {
        var pipe = TryConnect(300);
        if (pipe is null)
        {
            using var started = new EventWaitHandle(false, EventResetMode.ManualReset, StartEvent);
            if (!Mutex.TryOpenExisting($@"Local\lojekq.{AppInfo.InstanceName}", out _))
            {
                Log.Info("tray not running, starting it");
                try
                {
                    // A dev build (Debug, --data-dir) starts its own exe directly: explorer.exe would drop PINGWHEN_DATA_DIR
                    // and the tray would come up as the real instance. The real one goes through explorer, from the installed copy.
                    if (AppInfo.IsDev) Process.Start(new ProcessStartInfo(AppInfo.ExePath) { UseShellExecute = false });
                    else Process.Start(new ProcessStartInfo("explorer.exe", $"\"{(File.Exists(Install.Exe) ? Install.Exe : AppInfo.ExePath)}\"") { UseShellExecute = false });
                }
                catch (Exception e) { return Fail($"PingWhen: could not start the tray: {e.Message}"); }
            }
            pipe = TryConnect((int)wait.TotalMilliseconds);
            if (pipe is null) return Fail("PingWhen: the tray did not answer. Is PingWhen running?");
        }
        try
        {
            var ancestors = ProcessInfo.Tree().Ancestors(Environment.ProcessId).Select(p => p.Pid).ToArray();
            var hello = JsonSerializer.Serialize(new Hello(Hello.Version, mode, Environment.ProcessId, ancestors, Environment.CurrentDirectory), Json.Options);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            PipeServer.WriteLine(pipe, hello, cts.Token).GetAwaiter().GetResult();
            var answer = PipeServer.ReadLine(pipe, 4096, cts.Token).GetAwaiter().GetResult();
            if (answer?.Contains("\"ok\":true") == true) return pipe;
            pipe.Dispose();
            return Fail(answer?.Contains("version") == true
                ? "PingWhen: the running tray is another version. Restart PingWhen."
                : "PingWhen: the tray refused the connection.");
        }
        catch (Exception e)
        {
            pipe.Dispose();
            return Fail($"PingWhen: {e.Message}");
        }
    }

    static NamedPipeClientStream? TryConnect(int ms)
    {
        var p = new NamedPipeClientStream(".", PipeServer.Name, PipeDirection.InOut, PipeOptions.Asynchronous);
        try { p.Connect(ms); return p; }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException) { p.Dispose(); return null; }
    }

    static NamedPipeClientStream? Fail(string message)
    {
        Log.Warn(message);
        try { Console.Error.WriteLine(message); } catch { }
        return null;
    }
}
