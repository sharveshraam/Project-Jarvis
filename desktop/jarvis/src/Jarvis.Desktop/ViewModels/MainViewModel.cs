using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using Jarvis.Agent;
using Jarvis.Core.Abstractions;
using Jarvis.Core.Models;
using Jarvis.Core.Modes;
using Jarvis.Desktop.Mvvm;
using Jarvis.Desktop.Services;
using Microsoft.Extensions.Logging;

namespace Jarvis.Desktop.ViewModels;

public sealed record SourceLink(string Title, string Url);

public sealed class ChatItem : ObservableObject
{
    private bool _hasSources;

    public ChatItem(MessageRole role, string text, DateTime createdUtc)
    {
        Role = role;
        Text = text;
        Time = DateTime.SpecifyKind(createdUtc, DateTimeKind.Utc).ToLocalTime().ToString("HH:mm");
    }

    public MessageRole Role { get; }
    public string Text { get; }
    public string Time { get; }
    public bool IsUser => Role == MessageRole.User;
    public bool IsNote => Role == MessageRole.Note;
    public ObservableCollection<SourceLink> Sources { get; } = new ObservableCollection<SourceLink>();

    public bool HasSources
    {
        get => _hasSources;
        private set => Set(ref _hasSources, value);
    }

    public void SetSources(IEnumerable<SourceLink> sources)
    {
        Sources.Clear();
        foreach (var source in sources) Sources.Add(source);
        HasSources = Sources.Count > 0;
    }
}

public sealed record ModeOption(JarvisMode Mode, string Name, string Purpose);

/// <summary>State and commands of the floating assistant panel.</summary>
public sealed class MainViewModel : ObservableObject
{
    private const int MaxVisibleMessages = 300;

    private readonly AssistantService _assistant;
    private readonly ModeService _modes;
    private readonly TaskEngine _tasks;
    private readonly ApprovalService _approvals;
    private readonly ISpeechToText _speechToText;
    private readonly ITextToSpeech _textToSpeech;
    private readonly ISettingsStore _settings;
    private readonly IConversationStore _conversations;
    private readonly IShellService _shell;
    private readonly INotifier _notifier;
    private readonly ILogger<MainViewModel> _log;
    private readonly Dispatcher _dispatcher;

    private CancellationTokenSource? _requestCts;
    private CancellationTokenSource? _speechCts;
    private string _input = "";
    private string _activity = "";
    private string _geminiText = "";
    private GeminiState _geminiState;
    private string _taskSummary = "";
    private bool _isBusy;
    private bool _isListening;
    private ModeOption _selectedMode;
    private ApprovalPrompt? _approval;
    private bool _suppressModeSwitch;

    public MainViewModel(AssistantService assistant, ModeService modes, TaskEngine tasks, ApprovalService approvals,
        ISpeechToText speechToText, ITextToSpeech textToSpeech, ISettingsStore settings, IConversationStore conversations,
        IShellService shell, INotifier notifier, ILogger<MainViewModel> log)
    {
        _assistant = assistant;
        _modes = modes;
        _tasks = tasks;
        _approvals = approvals;
        _speechToText = speechToText;
        _textToSpeech = textToSpeech;
        _settings = settings;
        _conversations = conversations;
        _shell = shell;
        _notifier = notifier;
        _log = log;
        _dispatcher = Dispatcher.CurrentDispatcher;

        Modes = ModeCatalog.All.Select(m => new ModeOption(m.Mode, m.DisplayName, m.Purpose)).ToList();
        _selectedMode = Modes.First(m => m.Mode == _modes.Current);

        SendCommand = new RelayCommand(() => _ = SendAsync(InputText, fromVoice: false), () => !IsBusy && !string.IsNullOrWhiteSpace(InputText));
        StopCommand = new RelayCommand(StopEverything);
        MicCommand = new RelayCommand(ToggleListening);
        NewConversationCommand = new RelayCommand(NewConversation, () => !IsBusy);
        OpenWorkspaceCommand = new RelayCommand(OpenWorkspace, () => !IsBusy);
        ApproveCommand = new RelayCommand(() => ResolveApproval(true));
        DenyCommand = new RelayCommand(() => ResolveApproval(false));
        OpenLinkCommand = new RelayCommand(OpenLink);
        SettingsCommand = new RelayCommand(() => SettingsRequested?.Invoke());
        HideCommand = new RelayCommand(() => HideRequested?.Invoke());

        _assistant.MessageAdded += message => OnUi(() => AddMessage(message));
        _assistant.ActivityChanged += text => OnUi(() => Activity = text);
        _assistant.GeminiStatusChanged += status => OnUi(() => ApplyGemini(status));
        _modes.Changed += mode => OnUi(() => ApplyMode(mode));
        _tasks.TaskChanged += task => OnUi(RefreshTaskSummary);
        _approvals.PromptChanged += prompt => OnUi(() => ShowApproval(prompt));
        _speechToText.PartialResult += text => OnUi(() => { if (IsListening) InputText = text; });

        ApplyGemini(_assistant.Gemini);
        RefreshTaskSummary();
    }

    public ObservableCollection<ChatItem> Messages { get; } = new ObservableCollection<ChatItem>();
    public IReadOnlyList<ModeOption> Modes { get; }

    public ICommand SendCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand MicCommand { get; }
    public ICommand NewConversationCommand { get; }
    public ICommand OpenWorkspaceCommand { get; }
    public ICommand ApproveCommand { get; }
    public ICommand DenyCommand { get; }
    public ICommand OpenLinkCommand { get; }
    public ICommand SettingsCommand { get; }
    public ICommand HideCommand { get; }

    /// <summary>Raised when the newest message should be scrolled into view.</summary>
    public event Action? ScrollToEndRequested;
    /// <summary>Raised when the window should come to the front (an approval is waiting).</summary>
    public event Action? AttentionRequested;
    public event Action? SettingsRequested;
    public event Action? HideRequested;

    public string InputText
    {
        get => _input;
        set => Set(ref _input, value);
    }

    public string Activity
    {
        get => _activity;
        private set
        {
            if (Set(ref _activity, value)) Raise(nameof(HasActivity));
        }
    }

    public bool HasActivity => !string.IsNullOrWhiteSpace(_activity);

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value)) CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsListening
    {
        get => _isListening;
        private set
        {
            if (Set(ref _isListening, value)) Raise(nameof(MicToolTip));
        }
    }

    public string MicToolTip => IsListening
        ? "Listening — click to stop and send"
        : $"Push to talk (or hold {_settings.Current.PushToTalkHotkey})";

    public string GeminiText
    {
        get => _geminiText;
        private set => Set(ref _geminiText, value);
    }

    public bool GeminiReady => _geminiState == GeminiState.Ready;
    public bool GeminiFailed => _geminiState == GeminiState.Error;

    public string TaskSummary
    {
        get => _taskSummary;
        private set
        {
            if (Set(ref _taskSummary, value)) Raise(nameof(HasTaskSummary));
        }
    }

    public bool HasTaskSummary => !string.IsNullOrWhiteSpace(_taskSummary);

    public ModeOption SelectedMode
    {
        get => _selectedMode;
        set
        {
            if (value is null || !Set(ref _selectedMode, value)) return;
            Raise(nameof(ModePurpose));
            if (!_suppressModeSwitch && _modes.Current != value.Mode) _modes.Switch(value.Mode);
        }
    }

    public string ModePurpose => _selectedMode.Purpose;

    public ApprovalPrompt? Approval
    {
        get => _approval;
        private set
        {
            if (Set(ref _approval, value)) Raise(nameof(HasApproval));
        }
    }

    public bool HasApproval => _approval is not null;

    private void OnUi(Action action)
    {
        if (_dispatcher.CheckAccess()) action();
        else _dispatcher.BeginInvoke(action);
    }

    /// <summary>Shows the most recent conversation again so work can continue where it stopped.</summary>
    public void LoadLatestConversation()
    {
        try
        {
            var latest = _conversations.List(1).FirstOrDefault();
            if (latest is null) return;
            var messages = _assistant.Resume(latest.Id);
            foreach (var message in messages.Skip(Math.Max(0, messages.Count - 40))) AddMessage(message);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "The previous conversation could not be loaded.");
        }
    }

    public void ShowNote(string text) => _assistant.AddNote(text);

    private void AddMessage(ChatMessage message)
    {
        if (string.IsNullOrWhiteSpace(message.Content)) return;
        Messages.Add(new ChatItem(message.Role, message.Content, message.CreatedUtc));
        while (Messages.Count > MaxVisibleMessages) Messages.RemoveAt(0);
        ScrollToEndRequested?.Invoke();
    }

    private void ApplyGemini(GeminiStatus status)
    {
        _geminiState = status.State;
        GeminiText = status.Display;
        Raise(nameof(GeminiReady));
        Raise(nameof(GeminiFailed));
    }

    private void ApplyMode(JarvisMode mode)
    {
        _suppressModeSwitch = true;
        try
        {
            SelectedMode = Modes.First(m => m.Mode == mode);
        }
        finally
        {
            _suppressModeSwitch = false;
        }
    }

    private void RefreshTaskSummary()
    {
        try
        {
            var active = _tasks.List(50).Where(t => t.IsActive).ToList();
            if (active.Count == 0)
            {
                TaskSummary = "";
                return;
            }
            var waiting = active.Count(t => t.State == AgentTaskState.WaitingForApproval);
            var paused = active.Count(t => t.State == AgentTaskState.Paused);
            var text = active.Count == 1 ? active[0].ProgressText : $"{active.Count} background tasks";
            if (waiting > 0) text += " · waiting for approval";
            else if (paused == active.Count) text += " · paused";
            TaskSummary = text;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Task summary could not be refreshed.");
        }
    }

    private void ShowApproval(ApprovalPrompt? prompt)
    {
        Approval = prompt;
        if (prompt is null) return;
        AttentionRequested?.Invoke();
        _notifier.Notify("JARVIS needs your approval", prompt.Summary, NotificationKind.Warning);
    }

    private void ResolveApproval(bool approved)
    {
        var prompt = Approval;
        if (prompt is not null) _approvals.Resolve(prompt, approved);
    }

    public async Task SendAsync(string text, bool fromVoice)
    {
        var message = (text ?? "").Trim();
        if (message.Length == 0 || IsBusy) return;

        StopSpeaking();
        InputText = "";
        IsBusy = true;
        Activity = "Thinking…";
        var cts = new CancellationTokenSource();
        _requestCts = cts;
        try
        {
            var reply = await _assistant.SendAsync(message, cts.Token);
            if (reply.Citations.Count > 0)
            {
                var last = Messages.LastOrDefault(m => m.Role == MessageRole.Assistant);
                last?.SetSources(reply.Citations
                    .GroupBy(c => c.Url, StringComparer.OrdinalIgnoreCase)
                    .Select(g => new SourceLink(string.IsNullOrWhiteSpace(g.First().Title) ? g.Key : g.First().Title, g.Key))
                    .Take(8));
                ScrollToEndRequested?.Invoke();
            }

            var settings = _settings.Current;
            var speak = fromVoice ? settings.SpeakResponses : settings.SpeakTypedReplies;
            if (speak && !reply.Cancelled && !string.IsNullOrWhiteSpace(reply.Text)) Speak(reply.Text);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Sending a message failed.");
            _assistant.AddNote("That request could not be completed. The details are in the log.");
        }
        finally
        {
            if (ReferenceEquals(_requestCts, cts)) _requestCts = null;
            cts.Dispose();
            Activity = "";
            IsBusy = false;
        }
    }

    public void Speak(string text)
    {
        StopSpeaking();
        var cts = new CancellationTokenSource();
        _speechCts = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await _textToSpeech.SpeakAsync(text, cts.Token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Speaking a reply failed.");
            }
        });
    }

    private void StopSpeaking()
    {
        var cts = Interlocked.Exchange(ref _speechCts, null);
        if (cts is not null)
        {
            try { cts.Cancel(); }
            catch (ObjectDisposedException) { }
        }
        _textToSpeech.Stop();
    }

    /// <summary>Stops the request in progress and any speech. Background tasks are controlled separately.</summary>
    private void StopEverything()
    {
        StopSpeaking();
        try { _requestCts?.Cancel(); }
        catch (ObjectDisposedException) { }
        if (IsListening) _ = StopListeningAsync(send: false);
    }

    private void ToggleListening()
    {
        if (IsListening) _ = StopListeningAsync(send: true);
        else StartListening();
    }

    public void StartListening()
    {
        if (IsListening || IsBusy) return;
        if (!_speechToText.IsAvailable(out var reason))
        {
            _assistant.AddNote(reason);
            return;
        }
        try
        {
            StopSpeaking();
            InputText = "";
            _speechToText.Start();
            IsListening = true;
            Activity = "Listening…";
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "The microphone could not be opened.");
            _assistant.AddNote(ex.Message);
        }
    }

    public async Task StopListeningAsync(bool send)
    {
        if (!IsListening) return;
        IsListening = false;
        Activity = "";
        string text;
        try
        {
            text = await _speechToText.StopAsync();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Speech recognition failed.");
            _assistant.AddNote("Speech recognition failed: " + ex.Message);
            return;
        }

        if (!send)
        {
            InputText = "";
            return;
        }
        if (string.IsNullOrWhiteSpace(text))
        {
            InputText = "";
            _assistant.AddNote("I did not catch anything. Hold the push-to-talk shortcut while speaking, or type instead.");
            return;
        }
        await SendAsync(text, fromVoice: true);
    }

    private void NewConversation()
    {
        StopSpeaking();
        _assistant.StartNewConversation();
        Messages.Clear();
    }

    private void OpenWorkspace()
    {
        var mode = _modes.Current;
        _ = Task.Run(() =>
        {
            try
            {
                var (_, _, report) = _modes.OpenWorkspace(mode);
                _assistant.AddNote(report);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Opening the workspace failed.");
                _assistant.AddNote("The workspace could not be opened: " + ex.Message);
            }
        });
    }

    private void OpenLink(object? parameter)
    {
        if (parameter is not string url || string.IsNullOrWhiteSpace(url)) return;
        var outcome = _shell.OpenUrl(url);
        if (!outcome.Success) _assistant.AddNote(outcome.Message);
    }
}
