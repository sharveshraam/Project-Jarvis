using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Jarvis.Desktop.ViewModels;

namespace Jarvis.Desktop.Views;

public partial class SettingsWindow : Window
{
    private const int DwmUseImmersiveDarkMode = 20;
    private readonly SettingsViewModel _viewModel;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        viewModel.ReadApiKey = () => ApiKeyBox.Password;
        viewModel.ClearApiKeyBox = () => ApiKeyBox.Clear();
        viewModel.Confirm = question => MessageBox.Show(this, question, "JARVIS", MessageBoxButton.YesNo, MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;

        SourceInitialized += (sender, e) => UseDarkTitleBar();
        Closed += (sender, e) =>
        {
            ApiKeyBox.Clear();
            _viewModel.Detach();
        };
    }

    private void UseDarkTitleBar()
    {
        try
        {
            var enabled = 1;
            DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, DwmUseImmersiveDarkMode, ref enabled, sizeof(int));
        }
        catch (Exception)
        {
            // Older Windows 10 builds do not support a dark title bar; the default one is fine.
        }
    }

    /// <summary>Selects every page in turn so all templates are instantiated; returns how many pages were shown.</summary>
    public int ShowEveryPage()
    {
        var pages = new[] { NavGeneral, NavGemini, NavVoice, NavModes, NavPermissions, NavFiles, NavAutomation, NavMemory, NavTasks, NavActivity };
        foreach (var page in pages)
        {
            page.IsChecked = true;
            UpdateLayout();
        }
        NavGeneral.IsChecked = true;
        return pages.Length;
    }

    private void OnClearMemory(object sender, RoutedEventArgs e) => _viewModel.ClearMemory();

    private void OnDeleteConversations(object sender, RoutedEventArgs e) => _viewModel.DeleteConversations();

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
