using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Jarvis.Core.Abstractions;
using Jarvis.Core.Models;
using Jarvis.Core.Shell;
using Jarvis.Desktop.ViewModels;

namespace Jarvis.Desktop.Views;

/// <summary>The compact floating assistant panel. Closing or hiding it never exits JARVIS.</summary>
public partial class MainWindow : Window, IWindowHost
{
    private const string PlacementKey = "window.placement";
    private readonly MainViewModel _viewModel;
    private readonly ISettingsStore _settings;
    private bool _placed;

    public MainWindow(MainViewModel viewModel, ISettingsStore settings)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _settings = settings;
        DataContext = viewModel;
        Topmost = settings.Current.KeepOnTop;

        viewModel.ScrollToEndRequested += () => Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => ChatScroll.ScrollToEnd()));
        viewModel.AttentionRequested += ShowAndActivate;
        settings.Changed += OnSettingsChanged;
        PreviewKeyDown += OnWindowKeyDown;
    }

    /// <summary>Set by the application once the tray icon exists.</summary>
    public LifecycleController? Lifecycle { get; set; }

    private void OnSettingsChanged(AppSettings settings)
        => Dispatcher.BeginInvoke(new Action(() => Topmost = settings.KeepOnTop));

    public void ShowAndActivate()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(ShowAndActivate));
            return;
        }
        if (!_placed) Place();
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        InputBox.Focus();
        ChatScroll.ScrollToEnd();
    }

    void IWindowHost.Hide()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => ((IWindowHost)this).Hide()));
            return;
        }
        SavePlacement();
        Hide();
    }

    /// <summary>Restores the last position, or docks to the bottom-right corner of the work area.</summary>
    private void Place()
    {
        _placed = true;
        var area = SystemParameters.WorkArea;
        var saved = _settings.GetValue(PlacementKey);
        if (!string.IsNullOrWhiteSpace(saved))
        {
            var parts = saved.Split(',');
            if (parts.Length == 4
                && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var left)
                && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var top)
                && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var width)
                && double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var height)
                && width >= MinWidth && height >= MinHeight
                && left >= SystemParameters.VirtualScreenLeft - 20
                && top >= SystemParameters.VirtualScreenTop - 20
                && left + 80 <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth
                && top + 80 <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight)
            {
                Left = left;
                Top = top;
                Width = width;
                Height = height;
                return;
            }
        }
        Height = Math.Min(Height, Math.Max(MinHeight, area.Height - 32));
        Left = area.Right - Width - 16;
        Top = area.Bottom - Height - 16;
    }

    private void SavePlacement()
    {
        if (!_placed || WindowState != WindowState.Normal) return;
        try
        {
            _settings.SetValue(PlacementKey, string.Join(",",
                Left.ToString("F0", CultureInfo.InvariantCulture), Top.ToString("F0", CultureInfo.InvariantCulture),
                Width.ToString("F0", CultureInfo.InvariantCulture), Height.ToString("F0", CultureInfo.InvariantCulture)));
        }
        catch (Exception)
        {
            // Remembering the position is a convenience only.
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        SavePlacement();
        if (Lifecycle is not null && Lifecycle.OnWindowClosing()) e.Cancel = true;
        base.OnClosing(e);
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        ((IWindowHost)this).Hide();
    }

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || (Keyboard.Modifiers & ModifierKeys.Shift) != 0) return;
        e.Handled = true;
        if (_viewModel.SendCommand.CanExecute(null)) _viewModel.SendCommand.Execute(null);
    }
}
