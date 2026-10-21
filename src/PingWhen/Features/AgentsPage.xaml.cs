using System.Text.Json;
using System.Windows;
using PingWhen.Shell;

namespace PingWhen.Features;

/// <summary>Connect Claude Code (and other MCP clients): exact command and hooks JSON with the real exe path (spec §7, page 4).</summary>
public partial class AgentsPage
{
    /// <summary>The installed exe (%LOCALAPPDATA%\Programs\PingWhen\PingWhen.exe), whatever copy runs now; --demo shows a neutral path.</summary>
    public static string ExeForCommands => Args.Current.Demo ? @"C:\Users\demo\AppData\Local\Programs\PingWhen\PingWhen.exe" : Install.Exe;

    public static string McpText(string exe) =>
        $"claude mcp add --scope user --transport stdio pingwhen -- \"{exe}\" --mcp\r\nclaude mcp list";

    public static string HooksText(string exe, bool permission = false)
    {
        string Hook(int timeout) => $$"""[{ "hooks": [{ "type": "command", "command": {{JsonSerializer.Serialize(exe)}}, "args": ["--hook"], "timeout": {{timeout}} }] }]""";
        var hook = Hook(10);
        if (permission)
            return $"{{ \"hooks\": {{\r\n  \"Stop\":              {hook},\r\n  \"Notification\":      {hook},\r\n  \"UserPromptSubmit\":  {hook},\r\n  \"PermissionRequest\": {Hook(Permissions.HookTimeoutSeconds)}\r\n}}}}";
        return $"{{ \"hooks\": {{\r\n  \"Stop\":             {hook},\r\n  \"Notification\":     {hook},\r\n  \"UserPromptSubmit\": {hook}\r\n}}}}";
    }

    sealed record SessionRow(string Client, string Folder, string Details);
    sealed record RecentRow(string Time, string Title);

    /// <summary>--demo: fictitious sessions for the screenshot.</summary>
    public static List<(string Client, string Folder, int Watchers, int Minutes)> DemoSessions { get; } = [];

    public AgentsPage()
    {
        InitializeComponent();
        var exe = ExeForCommands;
        MoveBar.IsOpen = !Args.Current.Demo && !Install.IsInstalled;
        McpCommand.Text = McpText(exe);
        HooksJson.Text = HooksText(exe, Settings.Current.PhoneAnswers);
        CopyMcp.Click += (_, _) => Copy(McpCommand.Text);
        CopyHooks.Click += (_, _) => Copy(HooksJson.Text);
        ClaudeText.IsChecked = Settings.Current.ClaudeTextToPhone;
        ClaudeText.Click += (_, _) => { Settings.Current.ClaudeTextToPhone = ClaudeText.IsChecked == true; Settings.Save(); };
        StopMin.Value = Settings.Current.StopMinSeconds;
        StopMin.ValueChanged += (_, _) => { Settings.Current.StopMinSeconds = (int)Math.Clamp(StopMin.Value ?? 0, 0, 86400); Settings.Save(); };

        void Refresh()
        {
            var watchers = Engine.Current.Snapshot;
            var rows = PipeServer.Sessions.Select(s => new SessionRow(s.Client, s.Folder,
                    string.Format(Loc.T("Agents.SessionDetails"), watchers.Count(v => v.W.CreatedBy == s.Key), Fmt.Duration(DateTime.Now - s.Since))))
                .Concat(DemoSessions.Select(d => new SessionRow(d.Client, d.Folder, string.Format(Loc.T("Agents.SessionDetails"), d.Watchers, Fmt.Duration(TimeSpan.FromMinutes(d.Minutes))))))
                .ToList();
            SessionList.ItemsSource = rows;
            PipeStatus.Text = Args.Current.Demo || PipeServer.Running
                ? string.Format(Loc.T("Agents.PipeOn"), rows.Count)
                : Loc.T("Agents.PipeOff");
            var recent = Notifier.History.Where(h => h.Watcher.StartsWith("mcp:") || h.Watcher.StartsWith("hook:") || h.Watcher.Contains(" · ")).Take(8)
                .Select(h => new RecentRow(new HistoryPage.Row(h).Time, h.Title)).ToList();
            RecentList.ItemsSource = recent;
            RecentEmpty.Visibility = recent.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        PipeServer.Sessions.CollectionChanged += (_, _) => Refresh();
        Notifier.History.CollectionChanged += (_, _) => Refresh();
        Refresh();
    }

    static void Copy(string text)
    {
        try { Clipboard.SetText(text); }
        catch (Exception e) { Log.Warn("clipboard busy", e); }
    }
}
