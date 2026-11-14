using System.Diagnostics;
using Jarvis.Core.Abstractions;
using Jarvis.Core.Models;
using Jarvis.Core.Shell;
using Microsoft.Win32;

namespace Jarvis.Windows;

/// <summary>Per-user "start with Windows" entry (HKCU Run key). No administrator rights, no services, no scheduled tasks.</summary>
public sealed class StartupManager : IStartupManager
{
    public const string TrayArgument = "--tray";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "JARVIS";

    private static string? Command()
    {
        var exe = Environment.ProcessPath;
        return string.IsNullOrWhiteSpace(exe) ? null : $"\"{exe}\" {TrayArgument}";
    }

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        var value = key?.GetValue(ValueName) as string;
        var expected = Command();
        return expected is not null && string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
    }

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled)
        {
            var command = Command();
            if (command is null) throw new InvalidOperationException("The application path could not be determined.");
            key.SetValue(ValueName, command, RegistryValueKind.String);
        }
        else if (key.GetValue(ValueName) is not null)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}

/// <summary>
/// System-wide shortcuts via RegisterHotKey on a message-only window. Must be created on the UI thread.
/// A shortcut registered with a release callback behaves as hold-to-talk: the key state is polled only while it is held.
/// </summary>
public sealed class GlobalHotkeys : System.Windows.Forms.NativeWindow, IDisposable
{
    private sealed class Registration
    {
        public Registration(HotkeyGesture gesture, Action pressed, Action? released)
        {
            Gesture = gesture;
            Pressed = pressed;
            Released = released;
        }

        public HotkeyGesture Gesture { get; }
        public Action Pressed { get; }
        public Action? Released { get; }
    }

    private static readonly IntPtr MessageOnlyParent = new IntPtr(-3);
    private readonly Dictionary<int, Registration> _registrations = new Dictionary<int, Registration>();
    private readonly System.Windows.Forms.Timer _releaseTimer;
    private Registration? _held;
    private bool _disposed;

    public GlobalHotkeys()
    {
        CreateHandle(new System.Windows.Forms.CreateParams { Parent = MessageOnlyParent });
        _releaseTimer = new System.Windows.Forms.Timer { Interval = 40 };
        _releaseTimer.Tick += (sender, e) => CheckReleased();
    }

    /// <summary>Registers (or replaces) a shortcut. Returns false with a reason when Windows refuses, e.g. another app owns it.</summary>
    public bool Register(int id, HotkeyGesture gesture, Action pressed, Action? released, out string error)
    {
        error = "";
        Unregister(id);
        if (!Native.RegisterHotKey(Handle, id, (uint)gesture.Modifiers | Native.MOD_NOREPEAT, gesture.VirtualKey))
        {
            error = $"{gesture.Display} is already in use by Windows or another application. Choose a different shortcut in Settings.";
            return false;
        }
        _registrations[id] = new Registration(gesture, pressed, released);
        return true;
    }

    public void Unregister(int id)
    {
        if (!_registrations.Remove(id, out var registration)) return;
        Native.UnregisterHotKey(Handle, id);
        if (ReferenceEquals(_held, registration))
        {
            _releaseTimer.Stop();
            _held = null;
        }
    }

    protected override void WndProc(ref System.Windows.Forms.Message m)
    {
        if (m.Msg == Native.WM_HOTKEY && _registrations.TryGetValue(m.WParam.ToInt32(), out var registration))
        {
            if (registration.Released is null)
            {
                registration.Pressed();
            }
            else if (_held is null)
            {
                _held = registration;
                registration.Pressed();
                _releaseTimer.Start();
            }
            return;
        }
        base.WndProc(ref m);
    }

    private void CheckReleased()
    {
        var held = _held;
        if (held is null)
        {
            _releaseTimer.Stop();
            return;
        }
        var down = (Native.GetAsyncKeyState((int)held.Gesture.VirtualKey) & 0x8000) != 0;
        if (down) return;
        _releaseTimer.Stop();
        _held = null;
        held.Released?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _releaseTimer.Stop();
        _releaseTimer.Dispose();
        foreach (var id in _registrations.Keys.ToList()) Native.UnregisterHotKey(Handle, id);
        _registrations.Clear();
        DestroyHandle();
    }
}

/// <summary>
/// The notification-area icon (Open / Settings / Pause Tasks / Exit) and Windows notifications.
/// Balloon tips raised through the tray icon are delivered by Windows 10/11 as toast notifications.
/// </summary>
public sealed class TrayIcon : ITrayIcon, INotifier, IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon _icon;
    private readonly System.Windows.Forms.ToolStripMenuItem _pauseItem;
    private readonly SynchronizationContext? _ui;
    private bool _disposed;

    public TrayIcon()
    {
        _ui = SynchronizationContext.Current;

        var menu = new System.Windows.Forms.ContextMenuStrip();
        var open = new System.Windows.Forms.ToolStripMenuItem("Open JARVIS");
        open.Font = new System.Drawing.Font(open.Font, System.Drawing.FontStyle.Bold);
        open.Click += (sender, e) => OpenRequested?.Invoke();
        var settings = new System.Windows.Forms.ToolStripMenuItem("Settings");
        settings.Click += (sender, e) => SettingsRequested?.Invoke();
        _pauseItem = new System.Windows.Forms.ToolStripMenuItem("Pause Tasks") { CheckOnClick = true };
        _pauseItem.Click += (sender, e) => PauseTasksToggled?.Invoke(_pauseItem.Checked);
        var exit = new System.Windows.Forms.ToolStripMenuItem("Exit");
        exit.Click += (sender, e) => ExitRequested?.Invoke();
        menu.Items.Add(open);
        menu.Items.Add(settings);
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(exit);

        _icon = new System.Windows.Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "JARVIS",
            ContextMenuStrip = menu,
            Visible = true
        };
        _icon.MouseClick += (sender, e) =>
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left) OpenRequested?.Invoke();
        };
        _icon.BalloonTipClicked += (sender, e) => OpenRequested?.Invoke();
    }

    public event Action? OpenRequested;
    public event Action? SettingsRequested;
    public event Action<bool>? PauseTasksToggled;
    public event Action? ExitRequested;

    private static System.Drawing.Icon LoadIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exe))
            {
                var icon = System.Drawing.Icon.ExtractAssociatedIcon(exe);
                if (icon is not null) return icon;
            }
        }
        catch (Exception)
        {
            // Fall through to the stock icon.
        }
        return System.Drawing.SystemIcons.Application;
    }

    private void OnUi(Action action)
    {
        if (_disposed) return;
        if (_ui is null || SynchronizationContext.Current == _ui) action();
        else _ui.Post(_ => { if (!_disposed) action(); }, null);
    }

    public void SetPaused(bool paused) => OnUi(() => _pauseItem.Checked = paused);

    public void SetStatus(string tooltip)
    {
        // NotifyIcon tooltips are limited to 127 characters.
        var text = string.IsNullOrWhiteSpace(tooltip) ? "JARVIS" : tooltip;
        if (text.Length > 120) text = text.Substring(0, 120);
        OnUi(() => _icon.Text = text);
    }

    public void Notify(string title, string message, NotificationKind kind = NotificationKind.Info)
    {
        var icon = kind switch
        {
            NotificationKind.Warning => System.Windows.Forms.ToolTipIcon.Warning,
            NotificationKind.Error => System.Windows.Forms.ToolTipIcon.Error,
            _ => System.Windows.Forms.ToolTipIcon.Info
        };
        var body = string.IsNullOrWhiteSpace(message) ? " " : (message.Length > 250 ? message.Substring(0, 249) + "…" : message);
        var heading = title.Length > 60 ? title.Substring(0, 60) : title;
        OnUi(() => _icon.ShowBalloonTip(8000, heading, body, icon));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _icon.Visible = false;
        _icon.Dispose();
    }
}
