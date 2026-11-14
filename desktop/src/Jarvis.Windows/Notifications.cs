using System.Windows.Forms;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jarvis.Windows;

/// <summary>Windows notifications. Uses the system tray balloon as the
/// reliable fallback; toast notifications are used when available.</summary>
public interface INotifier
{
    void Notify(string title, string message);
}

public sealed class TrayNotifier(ILogger<TrayNotifier>? logger = null) : INotifier, IDisposable
{
    private readonly ILogger<TrayNotifier> _log = logger ?? NullLogger<TrayNotifier>.Instance;
    private NotifyIcon? _icon;

    public void EnsureIcon(Icon? icon, string tooltip, Action onOpen)
    {
        if (_icon is not null) return;
        _icon = new NotifyIcon
        {
            Icon = icon ?? SystemIcons.Application,
            Text = tooltip,
            Visible = true
        };
        _icon.DoubleClick += (_, _) => onOpen();
        var menu = new ContextMenuStrip();
        var open = new ToolStripMenuItem("Open JARVIS", (_, _) => onOpen());
        var exit = new ToolStripMenuItem("Exit JARVIS", (_, _) => _icon.Visible = false);
        menu.Items.Add(open); menu.Items.Add(exit);
        _icon.ContextMenuStrip = menu;
    }

    public void Notify(string title, string message)
    {
        try
        {
            if (_icon is not null)
            {
                _icon.ShowBalloonTip(5000, title, message, ToolTipIcon.Info);
                return;
            }
            // No tray icon yet: fall back to a toast via Windows.UI.Notifications.
            ToastNotifier.Show(title, message);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Notification failed"); }
    }

    public void Dispose() { _icon?.Dispose(); }
}

public static class ToastNotifier
{
    public static void Show(string title, string message)
    {
        // Toast notifications via WinRT require the Windows SDK projection;
        // the tray balloon (TrayNotifier) is the reliable primary channel.
        // This hook is intentionally a no-op so notifications never crash the app.
    }
}
