namespace Jarvis.Core.Shell;

public interface IWindowHost
{
    bool IsVisible { get; }
    void ShowAndActivate();
    void Hide();
}

public interface ITrayIcon
{
    event Action? OpenRequested;
    event Action? SettingsRequested;
    event Action<bool>? PauseTasksToggled;
    event Action? ExitRequested;
    void SetPaused(bool paused);
    void SetStatus(string tooltip);
}

/// <summary>
/// Tray/window lifecycle rules, kept free of UI types so they can be unit tested:
/// closing or hiding the window never exits JARVIS; only the tray's Exit does.
/// </summary>
public sealed class LifecycleController
{
    private readonly IWindowHost _window;
    private readonly ITrayIcon _tray;
    private readonly Action<bool> _setTasksPaused;
    private readonly Action _openSettings;
    private readonly Action _shutdown;

    public LifecycleController(IWindowHost window, ITrayIcon tray, Action<bool> setTasksPaused, Action openSettings, Action shutdown)
    {
        _window = window;
        _tray = tray;
        _setTasksPaused = setTasksPaused;
        _openSettings = openSettings;
        _shutdown = shutdown;

        _tray.OpenRequested += ShowWindow;
        _tray.SettingsRequested += () => _openSettings();
        _tray.PauseTasksToggled += SetTasksPaused;
        _tray.ExitRequested += Exit;
    }

    public bool IsExiting { get; private set; }
    public bool TasksPaused { get; private set; }

    public void ShowWindow()
    {
        if (!IsExiting) _window.ShowAndActivate();
    }

    public void HideWindow() => _window.Hide();

    public void ToggleWindow()
    {
        if (IsExiting) return;
        if (_window.IsVisible) _window.Hide();
        else _window.ShowAndActivate();
    }

    /// <summary>Call when the user tries to close the window. Returns true when the close must be cancelled (hidden to tray instead).</summary>
    public bool OnWindowClosing()
    {
        if (IsExiting) return false;
        _window.Hide();
        return true;
    }

    public void SetTasksPaused(bool paused)
    {
        TasksPaused = paused;
        _setTasksPaused(paused);
        _tray.SetPaused(paused);
    }

    public void Exit()
    {
        if (IsExiting) return;
        IsExiting = true;
        _shutdown();
    }
}
