using System.Windows;
using Jarvis.Desktop.ViewModels;

namespace Jarvis.Desktop.Views;

public partial class SettingsWindow(SettingsViewModel vm) : Window
{
    public SettingsWindow() : this(null!) { }

    private async void Window_Loaded(object sender, RoutedEventArgs e) { if (vm is not null) await vm.RefreshAsync(); }

    private async void SaveKey_Click(object sender, RoutedEventArgs e) => await vm.SaveKeyAsync(ApiKeyBox.Password);
    private async void RemoveKey_Click(object sender, RoutedEventArgs e) => await vm.RemoveKeyAsync();
    private async void DeleteMemory_Click(object sender, RoutedEventArgs e)
    {
        var list = (System.Windows.Controls.ListBox)FindName("MemoriesList")!;
        await vm.DeleteMemoryAtAsync(list?.SelectedIndex ?? -1);
    }
    private async void ClearMemories_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Delete ALL stored memories?", "JARVIS", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            await vm.ClearMemoriesAsync();
    }
    private async void ClearConversations_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Delete ALL conversation history?", "JARVIS", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            await vm.ClearConversationsAsync();
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
