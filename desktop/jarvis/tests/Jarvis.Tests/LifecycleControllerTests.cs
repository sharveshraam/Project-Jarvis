using Jarvis.Core.Shell;

namespace Jarvis.Tests;

/// <summary>The tray rules: closing or hiding the window never stops JARVIS; only the tray's Exit does.</summary>
public class LifecycleControllerTests
{
    private readonly FakeWindow _window = new FakeWindow();
    private readonly FakeTray _tray = new FakeTray();
    private readonly List<bool> _pauseCalls = new List<bool>();
    private int _settingsOpened;
    private int _shutdowns;

    private LifecycleController Create()
        => new LifecycleController(_window, _tray, paused => _pauseCalls.Add(paused), () => _settingsOpened++, () => _shutdowns++);

    [Fact]
    public void Closing_the_window_hides_it_and_keeps_the_app_running()
    {
        var lifecycle = Create();
        lifecycle.ShowWindow();

        var cancelClose = lifecycle.OnWindowClosing();

        Assert.True(cancelClose);
        Assert.False(_window.IsVisible);
        Assert.Equal(0, _shutdowns);
        Assert.False(lifecycle.IsExiting);
    }

    [Fact]
    public void Tray_open_shows_the_window_again()
    {
        var lifecycle = Create();
        lifecycle.OnWindowClosing();

        _tray.ClickOpen();

        Assert.True(_window.IsVisible);
        Assert.Equal(1, _window.ShowCount);
    }

    [Fact]
    public void Tray_exit_is_the_only_thing_that_shuts_down()
    {
        var lifecycle = Create();

        _tray.ClickExit();
        _tray.ClickExit();

        Assert.Equal(1, _shutdowns);
        Assert.True(lifecycle.IsExiting);
        Assert.False(lifecycle.OnWindowClosing());
    }

    [Fact]
    public void The_window_is_not_reopened_while_exiting()
    {
        var lifecycle = Create();
        lifecycle.Exit();

        lifecycle.ShowWindow();
        lifecycle.ToggleWindow();
        _tray.ClickOpen();

        Assert.Equal(0, _window.ShowCount);
    }

    [Fact]
    public void The_hotkey_toggles_visibility()
    {
        var lifecycle = Create();

        lifecycle.ToggleWindow();
        Assert.True(_window.IsVisible);

        lifecycle.ToggleWindow();
        Assert.False(_window.IsVisible);
    }

    [Fact]
    public void Pause_tasks_reaches_the_task_engine_and_the_tray_menu()
    {
        var lifecycle = Create();

        _tray.ClickPause(true);
        Assert.True(lifecycle.TasksPaused);
        Assert.Equal(true, _tray.Paused);

        _tray.ClickPause(false);
        Assert.False(lifecycle.TasksPaused);
        Assert.Equal(false, _tray.Paused);

        Assert.Equal(new[] { true, false }, _pauseCalls);
    }

    [Fact]
    public void Tray_settings_opens_settings()
    {
        Create();
        _tray.ClickSettings();
        Assert.Equal(1, _settingsOpened);
    }
}
