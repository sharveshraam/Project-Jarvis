using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace Jarvis.Windows;

/// <summary>Windows startup integration via HKCU Run key (per-user, no admin).</summary>
public static class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "JARVIS";

    public static void SetEnabled(bool enabled, string exePath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, $"\"{exePath}\" --tray");
        else key.DeleteValue(ValueName, throwIfMissing: false);
    }

    public static bool IsEnabled() =>
        Registry.CurrentUser.OpenSubKey(RunKey)?.GetValue(ValueName) is not null;
}

/// <summary>Global hotkey via RegisterHotKey (user32). Used for push-to-talk
/// and show/hide. Conflicts are reported, never silently swallowed.</summary>
public sealed class GlobalHotKey : IDisposable
{
    private const int MOD_CONTROL = 0x0002;
    private const int MOD_ALT = 0x0001;
    private const int MOD_SHIFT = 0x0004;
    private const int WM_HOTKEY = 0x0312;

    private readonly IntPtr _windowHandle;
    private readonly int _id;
    private bool _registered;

    public GlobalHotKey(IntPtr windowHandle, int id) { _windowHandle = windowHandle; _id = id; }

    public bool Register(string shortcut)
    {
        var (modifiers, key) = Parse(shortcut);
        _registered = RegisterHotKey(_windowHandle, _id, modifiers, key);
        return _registered;
    }

    public static (uint Modifiers, uint Key) Parse(string shortcut)
    {
        var parts = shortcut.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        uint mods = 0;
        uint key = 0;
        foreach (var p in parts)
        {
            switch (p.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= MOD_CONTROL; break;
                case "alt": mods |= MOD_ALT; break;
                case "shift": mods |= MOD_SHIFT; break;
                case "space": key = 0x20; break;
                case "j": key = 0x4A; break;
                case "k": key = 0x4B; break;
                case "l": key = 0x4C; break;
                default:
                    if (p.Length == 1) key = char.ToUpperInvariant(p[0]);
                    break;
            }
        }
        return (mods, key);
    }

    public static bool IsHotKeyMessage(int msg) => msg == WM_HOTKEY;

    public void Dispose()
    {
        if (_registered) UnregisterHotKey(_windowHandle, _id);
    }

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
