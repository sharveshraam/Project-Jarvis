using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Jarvis.AI;
using Jarvis.Core.Config;
using Jarvis.Core.Memory;
using Jarvis.Core.Models;
using Jarvis.Core.Modes;
using Jarvis.Core.Scheduling;
using Jarvis.Core.Tasks;
using Jarvis.Windows;

namespace Jarvis.Desktop.ViewModels;

public sealed class ChatLine(string speaker, string text, string provenance)
{
    public string Speaker { get; } = speaker;
    public string Text { get; } = text;
    public string Provenance { get; } = provenance;
}

public sealed class MainViewModel(
    ConversationService conversations,
    IConversationStore store,
    MemoryService memory,
    TaskEngine tasks,
    ReminderScheduler scheduler,
    ITextToSpeech tts,
    ISpeechToText stt,
    JarvisSettings settings,
    INotifier notifier) : INotifyPropertyChanged
{
    public ObservableCollection<ChatLine> Messages { get; } = [];
    public IReadOnlyList<string> Modes { get; } = OperatingModes.All.Select(m => m.DisplayName).ToList();

    private string _inputText = "";
    public string InputText { get => _inputText; set { _inputText = value; OnPropertyChanged(); } }

    private string _statusText = "Ready";
    public string StatusText { get => _statusText; set { _statusText = value; OnPropertyChanged(); } }

    private string _connectionText = "● Local";
    public string ConnectionText { get => _connectionText; set { _connectionText = value; OnPropertyChanged(); } }

    private string _taskStatusText = "";
    public string TaskStatusText { get => _taskStatusText; set { _taskStatusText = value; OnPropertyChanged(); } }

    private string _micIndicatorText = "";
    public string MicIndicatorText { get => _micIndicatorText; set { _micIndicatorText = value; OnPropertyChanged(); } }

    private string _selectedMode = "Personal";
    public string SelectedMode
    {
        get => _selectedMode;
        set
        {
            _selectedMode = value;
            settings.ActiveMode = OperatingModes.Parse(value);
            OnPropertyChanged();
            StatusText = $"Mode: {value}";
        }
    }

    public Guid CurrentConversationId { get; private set; } = Guid.NewGuid();

    public ICommand SendCommand => new RelayCommand(async _ => await SendAsync());
    public ICommand PushToTalkCommand => new RelayCommand(async _ => await PushToTalkAsync());
    public ICommand StopSpeechCommand => new RelayCommand(_ => tts.Stop());
    public ICommand NewConversationCommand => new RelayCommand(_ => NewConversation());
    public ICommand HistoryCommand => new RelayCommand(async _ => await LoadHistoryAsync());
    public ICommand SettingsCommand => new RelayCommand(_ => SettingsRequested?.Invoke());
    public ICommand HideCommand => new RelayCommand(_ => HideRequested?.Invoke());

    public event Action? SettingsRequested;
    public event Action? HideRequested;

    public async Task InitialiseAsync()
    {
        await store.SaveAsync(new Conversation(CurrentConversationId, "New conversation", DateTimeOffset.Now, DateTimeOffset.Now)).ConfigureAwait(false);
        SelectedMode = OperatingModes.Get(settings.ActiveMode).DisplayName;
        Messages.Add(new ChatLine("JARVIS", "Hello. I'm JARVIS, running locally. Type a request or press Talk. Configure a Gemini key in Settings for full AI.", "LOCAL"));
        await RefreshTaskStatusAsync().ConfigureAwait(false);
    }

    public async Task SendAsync()
    {
        var text = InputText.Trim();
        if (text.Length == 0) return;
        InputText = "";
        StatusText = "Thinking…";
        Messages.Add(new ChatLine("You", text, ""));
        var result = await conversations.HandleUserMessageAsync(CurrentConversationId, text, confirmAsync: name =>
        {
            // In the floating UI, non-trusted tools are declined here; the
            // Settings screen lists trusted actions. (A modal confirm dialog
            // can be added without changing the service contract.)
            StatusText = $"Tool '{name}' needs approval — add it to Trusted actions in Settings to allow it.";
            return Task.FromResult(false);
        }).ConfigureAwait(false);
        Messages.Add(new ChatLine("JARVIS", result.ReplyText, result.Provenance.ToString()));
        StatusText = "Ready";
        ConnectionText = result.Provenance == ReplyProvenance.Gemini ? "● Gemini online" : "● Local / offline";
        if (settings.SpeakResponses) _ = tts.SpeakAsync(result.ReplyText, settings.SpeechCulture);
        await RefreshTaskStatusAsync().ConfigureAwait(false);
    }

    public async Task PushToTalkAsync()
    {
        MicIndicatorText = "● Listening… (push-to-talk)";
        var heard = await stt.ListenOnceAsync(settings.SpeechCulture).ConfigureAwait(false);
        MicIndicatorText = "";
        if (string.IsNullOrWhiteSpace(heard))
        {
            StatusText = "Didn't catch that — check microphone/language pack, or type instead.";
            return;
        }
        InputText = heard;
        await SendAsync().ConfigureAwait(false);
    }

    public void NewConversation()
    {
        CurrentConversationId = Guid.NewGuid();
        Messages.Clear();
        _ = store.SaveAsync(new Conversation(CurrentConversationId, "New conversation", DateTimeOffset.Now, DateTimeOffset.Now));
        StatusText = "New conversation started.";
    }

    public async Task LoadHistoryAsync()
    {
        var list = await store.ListAsync().ConfigureAwait(false);
        Messages.Clear();
        Messages.Add(new ChatLine("JARVIS", list.Count == 0 ? "No previous conversations." :
            "Previous conversations:\n" + string.Join("\n", list.Select(c => $"- {c.Title} ({c.UpdatedAt:g})")), "LOCAL"));
    }

    public async Task RefreshTaskStatusAsync()
    {
        var tasks = await tasks.ListAsync().ConfigureAwait(false);
        var running = tasks.Count(t => t.Status == TaskStatus.Running);
        var pending = tasks.Count(t => t.Status is TaskStatus.Pending or TaskStatus.WaitingForApproval);
        TaskStatusText = running + pending == 0 ? "" : $"{running} running, {pending} pending";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class SettingsViewModel(
    JarvisSettings settings,
    ISecretVault vault,
    MemoryService memory,
    IConversationStore conversations)
{
    public string Model { get => settings.GeminiModel; set => settings.GeminiModel = value; }
    public bool StartWithWindows { get => settings.StartWithWindows; set => settings.StartWithWindows = value; }
    public bool SpeakResponses { get => settings.SpeakResponses; set => settings.SpeakResponses = value; }
    public string PushToTalkShortcut { get => settings.PushToTalkShortcut; set => settings.PushToTalkShortcut = value; }
    public IReadOnlyList<string> TrustedActions => settings.TrustedActions;
    public string IndexedFoldersText { get => string.Join("\n", settings.IndexedFolders); set => settings.IndexedFolders = value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(); }
    public string KeyStatusText { get; set; } = "";
    public ObservableCollection<string> Memories { get; } = [];

    public async Task RefreshAsync()
    {
        KeyStatusText = await vault.GetApiKeyAsync() is { } k && k.Length > 0 ? "Key configured ✓" : "No key configured (local mode)";
        Memories.Clear();
        foreach (var m in await memory.ListAllAsync()) Memories.Add($"{m.Key}: {m.Value} [{(m.IsExplicit ? "explicit" : "inferred")}]");
    }

    public async Task DeleteMemoryAtAsync(int index)
    {
        var all = await memory.ListAllAsync();
        if (index >= 0 && index < all.Count) await memory.DeleteAsync(all[index].Id);
        await RefreshAsync();
    }

    public async Task ClearMemoriesAsync() { await memory.ClearAllAsync(); await RefreshAsync(); }
    public async Task ClearConversationsAsync() { await conversations.ClearAllAsync(); await RefreshAsync(); }

    public async Task SaveKeyAsync(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        await vault.SetApiKeyAsync(key.Trim());
        settings.HasApiKey = true;
        settings.GeminiApiKeyHint = key.Trim()[^4..];
        await RefreshAsync();
    }

    public async Task RemoveKeyAsync()
    {
        await vault.RemoveApiKeyAsync();
        settings.HasApiKey = false;
        await RefreshAsync();
    }
}

public sealed class RelayCommand(Action<object?> execute) : ICommand
{
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => execute(parameter);
    public event EventHandler? CanExecuteChanged { add { } remove { } }
}

// SettingsViewModel memory/conversation management (partial via extension file pattern kept simple):
// Actual delete/clear are wired in SettingsWindow code-behind through these helpers on the VM instance.
