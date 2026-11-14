using System.Windows;
using System.Windows.Input;
using Jarvis.Desktop.ViewModels;

namespace Jarvis.Desktop.Views;

public partial class MainWindow(MainViewModel viewModel, SettingsViewModel settingsViewModel) : Window
{
    public MainWindow() : this(null!, null!) { } // design-time fallback

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (viewModel is null) return; // design-time
        DataContext = viewModel;
        viewModel.SettingsRequested += () => new SettingsWindow(settingsViewModel).ShowDialog();
        viewModel.HideRequested += Hide;
    }

    public async Task InitialiseAsync() => await viewModel.InitialiseAsync();

    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) viewModel.SendCommand.Execute(null);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Closing the window hides to tray; only tray "Exit" fully quits.
        e.Cancel = true;
        Hide();
        base.OnClosing(e);
    }
}
