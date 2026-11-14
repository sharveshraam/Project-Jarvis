using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using Jarvis.Agent;
using Jarvis.AI;
using Jarvis.Core.Abstractions;
using Jarvis.Core.Models;
using Jarvis.Core.Modes;
using Jarvis.Core.Planning;
using Jarvis.Core.Security;
using Jarvis.Core.Shell;
using Jarvis.Core.Text;
using Jarvis.Core.Tools;
using Jarvis.Desktop.Mvvm;
using Jarvis.Infrastructure;
using Jarvis.Windows;
using Microsoft.Extensions.Logging;

namespace Jarvis.Desktop.ViewModels;

/// <summary>One tool's permission row on the Permissions page.</summary>
public sealed class ToolPermissionRow : ObservableObject
{
    private PermissionLevel _level;

    public ToolPermissionRow(ToolDefinition definition, PermissionLevel level)
    {
        Name = definition.Name;
        Description = definition.Description;
        Category = definition.Category.ToString();
        Risk = definition.DefaultRisk;
        Levels = PermissionPolicy.SelectableLevels(definition.DefaultRisk);
        _level = Levels.Contains(level) ? level : Levels[0];
    }

    public string Name { get; }
    public string Description { get; }
    public string Category { get; }
    public ToolRisk Risk { get; }
    public IReadOnlyList<PermissionLevel> Levels { get; }

    public string RiskText => Risk switch
    {
        ToolRisk.Safe => "read-only",
        ToolRisk.Routine => "routine",
        _ => "always asks"
    };

    public PermissionLevel Level
    {
        get => _level;
        set => Set(ref _level, value);
    }
}

public sealed class MemoryRow
{
    public MemoryRow(MemoryItem item)
    {
        Id = item.Id;
        Content = item.Content;
        Detail = $"{item.Category} · {(item.Source == MemorySource.UserProvided ? "you told me" : "inferred")} · {TimeText.ToLocal(item.UpdatedUtc):d MMM yyyy}";
    }

    public long Id { get; }
    public string Content { get; }
    public string Detail { get; }
}

public sealed class TaskRow
{
    public TaskRow(AgentTask task)
    {
        Id = task.Id;
        Title = task.Title;
        Status = $"{task.State} · {task.CompletedSteps}/{task.Steps.Count} steps · {TimeText.ToLocal(task.UpdatedUtc):d MMM HH:mm}";
        Detail = task.Error ?? task.Result ?? "";
        HasDetail = !string.IsNullOrWhiteSpace(Detail);
        IsActive = task.IsActive;
        CanPause = task.IsActive && task.State != AgentTaskState.Paused;
        CanResume = task.State == AgentTaskState.Paused;
    }

    public string Id { get; }
    public string Title { get; }
    public string Status { get; }
    public string Detail { get; }
    public bool HasDetail { get; }
    public bool IsActive { get; }
    public bool CanPause { get; }
    public bool CanResume { get; }
}

public sealed class AuditRow
{
    public AuditRow(AuditEntry entry)
    {
        When = TimeText.ToLocal(entry.AtUtc).ToString("d MMM HH:mm:ss");
        Tool = entry.Tool;
        Outcome = $"{entry.Decision} · {(entry.Success ? "ok" : "not done")}";
        Summary = entry.Summary;
    }

    public string When { get; }
    public string Tool { get; }
    public string Outcome { get; }
    public string Summary { get; }
}

/// <summary>Editable copy of the settings plus the data-management pages (memory, tasks, activity).</summary>
public sealed class SettingsViewModel : ObservableObject
{
    public const string DefaultVoice = "(Windows default)";

    private readonly ISettingsStore _settings;
    private readonly ISecretStore _secrets;
    private readonly GeminiClient _gemini;
    private readonly AssistantService _assistant;
    private readonly IStartupManager _startup;
    private readonly IMemoryStore _memory;
    private readonly TaskEngine _tasks;
    private readonly IAuditLog _audit;
    private readonly IConversationStore _conversations;
    private readonly ISearchIndex _index;
    private readonly ITextToSpeech _speech;
    private readonly IShellService _shell;
    private readonly AppPaths _paths;
    private readonly ILogger<SettingsViewModel> _log;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<JarvisMode, ModeConfig> _modeConfigs = new Dictionary<JarvisMode, ModeConfig>();

    private string _userName = "";
    private bool _launchAtStartup;
    private bool _keepOnTop;
    private string _toggleHotkey = "";
    private string _pushToTalkHotkey = "";
    private string _geminiModel = "";
    private string _geminiApiVersion = "";
    private string _apiKeyStatus = "";
    private string _geminiTestResult = "";
    private bool _speakResponses;
    private bool _speakTypedReplies;
    private string _voiceName = DefaultVoice;
    private int _speechRate;
    private ModeDefinition _selectedMode;
    private string _modeApps = "";
    private string _modeFolders = "";
    private string _modeFiles = "";
    private string _modeProjectPath = "";
    private bool _trustRoutineActions;
    private string _indexedFolders = "";
    private string _allowedFolders = "";
    private string _indexStatus = "";
    private string _appAliases = "";
    private string _distractingApps = "";
    private string _approvedCommands = "";
    private string _morningBriefingTime = "";
    private string _endOfDaySummaryTime = "";
    private bool _tasksPaused;
    private string _status = "";
    private bool _statusIsError;
    private bool _indexing;

    public SettingsViewModel(ISettingsStore settings, ISecretStore secrets, GeminiClient gemini, AssistantService assistant,
        IStartupManager startup, IMemoryStore memory, TaskEngine tasks, IAuditLog audit, IConversationStore conversations,
        ISearchIndex index, ITextToSpeech speech, IShellService shell, AppPaths paths, ToolRegistry registry,
        ILogger<SettingsViewModel> log)
    {
        _settings = settings;
        _secrets = secrets;
        _gemini = gemini;
        _assistant = assistant;
        _startup = startup;
        _memory = memory;
        _tasks = tasks;
        _audit = audit;
        _conversations = conversations;
        _index = index;
        _speech = speech;
        _shell = shell;
        _paths = paths;
        _log = log;
        _dispatcher = Dispatcher.CurrentDispatcher;

        ModeList = ModeCatalog.All;
        _selectedMode = ModeList[0];
        Voices = new[] { DefaultVoice }.Concat(WindowsTextToSpeech.InstalledVoices()).ToList();

        SaveCommand = new RelayCommand(Save);
        SaveApiKeyCommand = new RelayCommand(SaveApiKey);
        RemoveApiKeyCommand = new RelayCommand(RemoveApiKey);
        TestGeminiCommand = new AsyncCommand(TestGeminiAsync, ex => GeminiTestResult = "Test failed: " + ex.Message);
        TestVoiceCommand = new RelayCommand(TestVoice);
        RebuildIndexCommand = new AsyncCommand(RebuildIndexAsync, ex => IndexStatus = "Indexing failed: " + ex.Message);
        ClearIndexCommand = new RelayCommand(ClearIndex);
        OpenDataFolderCommand = new RelayCommand(() => _shell.OpenPath(_paths.DataDirectory));
        OpenLogFolderCommand = new RelayCommand(() => _shell.OpenPath(_paths.LogDirectory));
        DeleteMemoryCommand = new RelayCommand(DeleteMemory);
        PauseTaskCommand = new RelayCommand(id => TaskAction(id, _tasks.Pause));
        ResumeTaskCommand = new RelayCommand(id => TaskAction(id, _tasks.Resume));
        CancelTaskCommand = new RelayCommand(id => TaskAction(id, _tasks.Cancel));
        RefreshCommand = new RelayCommand(RefreshData);

        var current = settings.Current;
        foreach (var mode in ModeList)
        {
            var source = current.GetMode(mode.Mode);
            _modeConfigs[mode.Mode] = new ModeConfig
            {
                Apps = source.Apps.ToList(),
                Folders = source.Folders.ToList(),
                Files = source.Files.ToList(),
                ProjectPath = source.ProjectPath
            };
        }

        Load(current);
        foreach (var tool in registry.All)
        {
            var definition = tool.Definition;
            var level = current.ToolPermissions.TryGetValue(definition.Name, out var configured)
                ? configured
                : DefaultLevel(definition.DefaultRisk);
            Permissions.Add(new ToolPermissionRow(definition, level));
        }
        ShowMode(_selectedMode.Mode);
        RefreshApiKeyStatus();
        RefreshData();

        _tasks.TaskChanged += OnTaskChanged;
    }

    public IReadOnlyList<ModeDefinition> ModeList { get; }
    public IReadOnlyList<string> Voices { get; }
    public ObservableCollection<ToolPermissionRow> Permissions { get; } = new ObservableCollection<ToolPermissionRow>();
    public ObservableCollection<MemoryRow> Memories { get; } = new ObservableCollection<MemoryRow>();
    public ObservableCollection<TaskRow> Tasks { get; } = new ObservableCollection<TaskRow>();
    public ObservableCollection<AuditRow> Activity { get; } = new ObservableCollection<AuditRow>();

    public ICommand SaveCommand { get; }
    public ICommand SaveApiKeyCommand { get; }
    public ICommand RemoveApiKeyCommand { get; }
    public ICommand TestGeminiCommand { get; }
    public ICommand TestVoiceCommand { get; }
    public ICommand RebuildIndexCommand { get; }
    public ICommand ClearIndexCommand { get; }
    public ICommand OpenDataFolderCommand { get; }
    public ICommand OpenLogFolderCommand { get; }
    public ICommand DeleteMemoryCommand { get; }
    public ICommand PauseTaskCommand { get; }
    public ICommand ResumeTaskCommand { get; }
    public ICommand CancelTaskCommand { get; }
    public ICommand RefreshCommand { get; }

    /// <summary>The window supplies the key from its password box, so the key never sits in a bindable string.</summary>
    public Func<string>? ReadApiKey { get; set; }
    public Action? ClearApiKeyBox { get; set; }
    /// <summary>Asks the user to confirm a destructive action; returns true to proceed.</summary>
    public Func<string, bool>? Confirm { get; set; }

    public string DataFolder => _paths.DataDirectory;

    public string UserName { get => _userName; set => Set(ref _userName, value); }
    public bool LaunchAtStartup { get => _launchAtStartup; set => Set(ref _launchAtStartup, value); }
    public bool KeepOnTop { get => _keepOnTop; set => Set(ref _keepOnTop, value); }
    public string ToggleHotkey { get => _toggleHotkey; set => Set(ref _toggleHotkey, value); }
    public string PushToTalkHotkey { get => _pushToTalkHotkey; set => Set(ref _pushToTalkHotkey, value); }
    public string GeminiModel { get => _geminiModel; set => Set(ref _geminiModel, value); }
    public string GeminiApiVersion { get => _geminiApiVersion; set => Set(ref _geminiApiVersion, value); }
    public string ApiKeyStatus { get => _apiKeyStatus; private set => Set(ref _apiKeyStatus, value); }
    public string GeminiTestResult { get => _geminiTestResult; private set => Set(ref _geminiTestResult, value); }
    public bool SpeakResponses { get => _speakResponses; set => Set(ref _speakResponses, value); }
    public bool SpeakTypedReplies { get => _speakTypedReplies; set => Set(ref _speakTypedReplies, value); }
    public string VoiceName { get => _voiceName; set => Set(ref _voiceName, value ?? DefaultVoice); }
    public int SpeechRate { get => _speechRate; set => Set(ref _speechRate, value); }
    public string ModeApps { get => _modeApps; set => Set(ref _modeApps, value); }
    public string ModeFolders { get => _modeFolders; set => Set(ref _modeFolders, value); }
    public string ModeFiles { get => _modeFiles; set => Set(ref _modeFiles, value); }
    public string ModeProjectPath { get => _modeProjectPath; set => Set(ref _modeProjectPath, value); }
    public bool TrustRoutineActions { get => _trustRoutineActions; set => Set(ref _trustRoutineActions, value); }
    public string IndexedFolders { get => _indexedFolders; set => Set(ref _indexedFolders, value); }
    public string AllowedFolders { get => _allowedFolders; set => Set(ref _allowedFolders, value); }
    public string IndexStatus { get => _indexStatus; private set => Set(ref _indexStatus, value); }
    public string AppAliases { get => _appAliases; set => Set(ref _appAliases, value); }
    public string DistractingApps { get => _distractingApps; set => Set(ref _distractingApps, value); }
    public string ApprovedCommands { get => _approvedCommands; set => Set(ref _approvedCommands, value); }
    public string MorningBriefingTime { get => _morningBriefingTime; set => Set(ref _morningBriefingTime, value); }
    public string EndOfDaySummaryTime { get => _endOfDaySummaryTime; set => Set(ref _endOfDaySummaryTime, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public bool StatusIsError { get => _statusIsError; private set => Set(ref _statusIsError, value); }

    public bool TasksPaused
    {
        get => _tasksPaused;
        set
        {
            if (!Set(ref _tasksPaused, value)) return;
            PauseAllRequested?.Invoke(value);
        }
    }

    /// <summary>Routed to the lifecycle controller so the tray menu's "Pause Tasks" stays in sync.</summary>
    public event Action<bool>? PauseAllRequested;

    public ModeDefinition SelectedMode
    {
        get => _selectedMode;
        set
        {
            if (value is null || ReferenceEquals(value, _selectedMode)) return;
            StoreMode(_selectedMode.Mode);
            _selectedMode = value;
            Raise();
            Raise(nameof(SelectedModePurpose));
            ShowMode(value.Mode);
        }
    }

    public string SelectedModePurpose => _selectedMode.Purpose;

    private static PermissionLevel DefaultLevel(ToolRisk risk)
        => risk == ToolRisk.Consequential ? PermissionLevel.Ask : PermissionLevel.Allow;

    private void Load(AppSettings s)
    {
        UserName = s.UserName;
        LaunchAtStartup = s.LaunchAtStartup;
        KeepOnTop = s.KeepOnTop;
        ToggleHotkey = s.ToggleHotkey;
        PushToTalkHotkey = s.PushToTalkHotkey;
        GeminiModel = s.GeminiModel;
        GeminiApiVersion = s.GeminiApiVersion;
        SpeakResponses = s.SpeakResponses;
        SpeakTypedReplies = s.SpeakTypedReplies;
        VoiceName = string.IsNullOrWhiteSpace(s.VoiceName) || !Voices.Contains(s.VoiceName) ? DefaultVoice : s.VoiceName;
        SpeechRate = Math.Clamp(s.SpeechRate, -10, 10);
        TrustRoutineActions = s.TrustRoutineActions;
        IndexedFolders = SettingsText.Join(s.IndexedFolders);
        AllowedFolders = SettingsText.Join(s.AllowedFolders);
        AppAliases = SettingsText.FormatAliases(s.AppAliases);
        DistractingApps = SettingsText.Join(s.DistractingApps);
        ApprovedCommands = SettingsText.FormatCommands(s.ApprovedCommands);
        MorningBriefingTime = s.MorningBriefingTime;
        EndOfDaySummaryTime = s.EndOfDaySummaryTime;
        _tasksPaused = s.TasksPaused;
        Raise(nameof(TasksPaused));
    }

    private void ShowMode(JarvisMode mode)
    {
        var config = _modeConfigs[mode];
        ModeApps = SettingsText.Join(config.Apps);
        ModeFolders = SettingsText.Join(config.Folders);
        ModeFiles = SettingsText.Join(config.Files);
        ModeProjectPath = config.ProjectPath;
    }

    private void StoreMode(JarvisMode mode)
    {
        var config = _modeConfigs[mode];
        config.Apps = SettingsText.Lines(ModeApps);
        config.Folders = SettingsText.Lines(ModeFolders);
        config.Files = SettingsText.Lines(ModeFiles);
        config.ProjectPath = (ModeProjectPath ?? "").Trim().Trim('"');
    }

    private void SetStatus(string text, bool error)
    {
        StatusIsError = error;
        Status = text;
    }

    private void Save()
    {
        var problems = new List<string>();
        if (!HotkeyGesture.TryParse(ToggleHotkey, out var toggle, out var toggleError)) problems.Add("Show/hide shortcut: " + toggleError);
        if (!HotkeyGesture.TryParse(PushToTalkHotkey, out var talk, out var talkError)) problems.Add("Push-to-talk shortcut: " + talkError);
        if (problems.Count == 0 && toggle.Modifiers == talk.Modifiers && toggle.VirtualKey == talk.VirtualKey)
            problems.Add("The show/hide and push-to-talk shortcuts must be different.");
        if (!string.IsNullOrWhiteSpace(MorningBriefingTime) && !TimeText.TryParseClock(MorningBriefingTime, out _))
            problems.Add("Morning briefing time must look like 08:30 (or be empty).");
        if (!string.IsNullOrWhiteSpace(EndOfDaySummaryTime) && !TimeText.TryParseClock(EndOfDaySummaryTime, out _))
            problems.Add("End-of-day summary time must look like 18:00 (or be empty).");
        var commands = SettingsText.ParseCommands(ApprovedCommands, out var commandErrors);
        problems.AddRange(commandErrors.Select(e => "Approved commands — " + e));
        if (string.IsNullOrWhiteSpace(GeminiModel)) problems.Add("The Gemini model name cannot be empty.");

        if (problems.Count > 0)
        {
            SetStatus(string.Join(Environment.NewLine, problems), error: true);
            return;
        }

        try
        {
            StoreMode(_selectedMode.Mode);
            var s = _settings.Current.Clone();
            s.UserName = (UserName ?? "").Trim();
            s.LaunchAtStartup = LaunchAtStartup;
            s.KeepOnTop = KeepOnTop;
            s.ToggleHotkey = toggle.Display;
            s.PushToTalkHotkey = talk.Display;
            s.GeminiModel = GeminiModel.Trim();
            s.GeminiApiVersion = string.IsNullOrWhiteSpace(GeminiApiVersion) ? "v1beta" : GeminiApiVersion.Trim();
            s.SpeakResponses = SpeakResponses;
            s.SpeakTypedReplies = SpeakTypedReplies;
            s.VoiceName = VoiceName == DefaultVoice ? "" : VoiceName;
            s.SpeechRate = Math.Clamp(SpeechRate, -10, 10);
            s.TrustRoutineActions = TrustRoutineActions;
            s.IndexedFolders = SettingsText.Lines(IndexedFolders);
            s.AllowedFolders = SettingsText.Lines(AllowedFolders);
            s.AppAliases = SettingsText.ParseAliases(AppAliases);
            s.DistractingApps = SettingsText.Lines(DistractingApps);
            s.ApprovedCommands = commands;
            s.MorningBriefingTime = (MorningBriefingTime ?? "").Trim();
            s.EndOfDaySummaryTime = (EndOfDaySummaryTime ?? "").Trim();
            foreach (var pair in _modeConfigs) s.Modes[pair.Key.ToString()] = pair.Value;

            s.ToolPermissions.Clear();
            foreach (var row in Permissions)
            {
                // Only deviations from the default are stored, so the "trusted routine actions" switch keeps working.
                if (row.Level != DefaultLevel(row.Risk)) s.ToolPermissions[row.Name] = row.Level;
            }

            _settings.Save(s);

            var startupNote = "";
            try
            {
                if (_startup.IsEnabled() != s.LaunchAtStartup) _startup.SetEnabled(s.LaunchAtStartup);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "The startup entry could not be updated.");
                startupNote = " The Windows startup entry could not be updated: " + ex.Message;
            }

            ToggleHotkey = s.ToggleHotkey;
            PushToTalkHotkey = s.PushToTalkHotkey;
            SetStatus("Settings saved." + startupNote, error: startupNote.Length > 0);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Saving settings failed.");
            SetStatus("Settings could not be saved: " + ex.Message, error: true);
        }
    }

    private void RefreshApiKeyStatus()
        => ApiKeyStatus = _gemini.IsConfigured
            ? "A Gemini API key is stored (encrypted with Windows DPAPI for your user account)."
            : "No API key is stored. JARVIS works in local mode until you add one.";

    private void SaveApiKey()
    {
        var key = (ReadApiKey?.Invoke() ?? "").Trim();
        if (key.Length < 20 || key.Any(char.IsWhiteSpace))
        {
            GeminiTestResult = "That does not look like a Gemini API key.";
            return;
        }
        try
        {
            _secrets.Set(SecretNames.GeminiApiKey, key);
            ClearApiKeyBox?.Invoke();
            RefreshApiKeyStatus();
            _assistant.RefreshGeminiStatus();
            GeminiTestResult = "Key saved. Use “Test connection” to verify it.";
        }
        catch (Exception ex)
        {
            _log.LogError("The API key could not be stored: {Type}", ex.GetType().Name);
            GeminiTestResult = "The key could not be stored securely: " + ex.Message;
        }
    }

    private void RemoveApiKey()
    {
        if (Confirm is not null && !Confirm("Remove the stored Gemini API key?")) return;
        try
        {
            _secrets.Remove(SecretNames.GeminiApiKey);
            ClearApiKeyBox?.Invoke();
            RefreshApiKeyStatus();
            _assistant.RefreshGeminiStatus();
            GeminiTestResult = "Key removed.";
        }
        catch (Exception ex)
        {
            GeminiTestResult = "The key could not be removed: " + ex.Message;
        }
    }

    private async Task TestGeminiAsync()
    {
        if (!_gemini.IsConfigured)
        {
            GeminiTestResult = "Save an API key first.";
            return;
        }
        GeminiTestResult = "Testing…";
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            var answer = await _gemini.TestAsync(limit.Token);
            GeminiTestResult = $"Connected to {_settings.Current.GeminiModel}. Reply: {Relevance.Truncate(answer, 80)}";
        }
        catch (OperationCanceledException)
        {
            GeminiTestResult = "The test timed out.";
        }
        catch (Exception ex)
        {
            GeminiTestResult = "Test failed: " + SecretDetector.Redact(ex.Message);
        }
        _assistant.RefreshGeminiStatus();
    }

    private void TestVoice()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _speech.SpeakAsync("This is how I will sound.", CancellationToken.None);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Voice test failed.");
            }
        });
    }

    private async Task RebuildIndexAsync()
    {
        if (_indexing) return;
        var roots = SettingsText.Lines(IndexedFolders);
        if (roots.Count == 0)
        {
            IndexStatus = "Add at least one folder to index, then save.";
            return;
        }
        _indexing = true;
        IndexStatus = "Indexing… this can take a while for large folders.";
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(20));
            var token = limit.Token;
            var stats = await Task.Run(() => _index.Rebuild(roots, token), token);
            IndexStatus = Describe(stats);
        }
        catch (OperationCanceledException)
        {
            IndexStatus = "Indexing was stopped after 20 minutes; what was indexed so far is searchable.";
        }
        finally
        {
            _indexing = false;
        }
    }

    private void ClearIndex()
    {
        if (Confirm is not null && !Confirm("Clear the search index? Your files are not touched.")) return;
        try
        {
            _index.Clear();
            IndexStatus = "Index cleared.";
        }
        catch (Exception ex)
        {
            IndexStatus = "The index could not be cleared: " + ex.Message;
        }
    }

    private static string Describe(IndexStats stats)
        => stats.Files == 0
            ? "The search index is empty."
            : $"{stats.Files:N0} files indexed, {stats.FilesWithContent:N0} with searchable text"
              + (stats.LastBuiltUtc.HasValue ? $" · last built {TimeText.ToLocal(stats.LastBuiltUtc.Value):d MMM HH:mm}" : "") + ".";

    private void OnTaskChanged(AgentTask task)
    {
        if (_dispatcher.CheckAccess()) RefreshTasks();
        else _dispatcher.BeginInvoke(new Action(RefreshTasks));
    }

    public void Detach() => _tasks.TaskChanged -= OnTaskChanged;

    private void RefreshData()
    {
        try
        {
            Memories.Clear();
            foreach (var item in _memory.List()) Memories.Add(new MemoryRow(item));
            Activity.Clear();
            foreach (var entry in _audit.Recent(200)) Activity.Add(new AuditRow(entry));
            RefreshTasks();
            IndexStatus = Describe(_index.Stats());
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Settings data could not be refreshed.");
        }
    }

    private void RefreshTasks()
    {
        try
        {
            Tasks.Clear();
            foreach (var task in _tasks.List(40)) Tasks.Add(new TaskRow(task));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "The task list could not be refreshed.");
        }
    }

    private void DeleteMemory(object? parameter)
    {
        if (parameter is not long id) return;
        _memory.Delete(id);
        var row = Memories.FirstOrDefault(m => m.Id == id);
        if (row is not null) Memories.Remove(row);
    }

    public void ClearMemory()
    {
        if (Confirm is not null && !Confirm("Delete everything JARVIS remembers about you? This cannot be undone.")) return;
        _memory.Clear();
        Memories.Clear();
        SetStatus("Memory cleared.", error: false);
    }

    public void DeleteConversations()
    {
        if (Confirm is not null && !Confirm("Delete all saved conversations? This cannot be undone.")) return;
        _conversations.DeleteAll();
        _assistant.StartNewConversation();
        ConversationsDeleted?.Invoke();
        SetStatus("All conversations deleted.", error: false);
    }

    /// <summary>Raised so the assistant panel can clear its transcript.</summary>
    public event Action? ConversationsDeleted;

    private void TaskAction(object? parameter, Func<string, bool> action)
    {
        if (parameter is not string id) return;
        try
        {
            action(id);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "A task action failed.");
        }
        RefreshTasks();
    }
}
