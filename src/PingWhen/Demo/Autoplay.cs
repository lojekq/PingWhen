namespace PingWhen.Demo;

/// <summary>
/// --autoplay is the scene GIF (spec §10), recorded by the owner in a slot with the working instance closed:
/// Windows Terminal runs docs\fake-progress.ps1 → Ctrl+Alt+W → the page with "pwsh → fake-progress.ps1" and
/// "Process exited" → Enter → the card → (cut) 100 % → the toast. Nothing is scripted: the window starts hidden,
/// the hotkey is registered in this demo (Hotkeys.RegisterInDemo) and the engine watches the real script.
/// </summary>
public static class Autoplay
{
    public static void Start(MainWindow w) => w.Hide();
}
