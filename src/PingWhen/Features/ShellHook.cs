using PingWhen.Shell;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace PingWhen.Features;

/// <summary>
/// "Window flashed on the taskbar" (v1.0, spec §3): RegisterShellHookWindow on the shell's MessageWindow, HSHELL_FLASH.
/// Learn calls the shell hook "not intended for general use"; AutoHotkey has relied on it for years.
/// Installers and terminals (bell) flash when they want attention.
/// </summary>
public static class ShellHook
{
    const int HSHELL_FLASH = 0x8006;
    static uint message;

    /// <summary>On the UI thread; <paramref name="flashed"/> gets the flashing window's handle.</summary>
    public static void Start(Action<long> flashed)
    {
        if (message != 0) return;
        message = PInvoke.RegisterWindowMessage("SHELLHOOK");
        if (message == 0 || !PInvoke.RegisterShellHookWindow(new HWND(MessageWindow.Handle)))
        {
            Log.Warn("shell hook not registered: window flash watchers are off");
            return;
        }
        MessageWindow.WndProc += (nint _, int msg, nint wParam, nint lParam, ref bool _) =>
        {
            if (msg == message && (int)wParam == HSHELL_FLASH) flashed(lParam);
            return 0;
        };
        App.Exiting += () => PInvoke.DeregisterShellHookWindow(new HWND(MessageWindow.Handle));
    }
}
