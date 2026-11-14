using System.Windows;
using Jarvis.AI;
using Jarvis.Agent;
using Jarvis.Core.Config;
using Jarvis.Core.Memory;
using Jarvis.Core.Models;
using Jarvis.Core.Scheduling;
using Jarvis.Core.Search;
using Jarvis.Core.Tasks;
using Jarvis.Core.Tools;
using Jarvis.Desktop.ViewModels;
using Jarvis.Desktop.Views;
using Jarvis.Infrastructure;
using Jarvis.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jarvis.Desktop;

/// <summary>JARVIS desktop entry point. Single-instance, starts in the tray,
/// registers the global push-to-talk shortcut, recovers reminders/tasks,
/// and wires the full dependency graph. The floating window is the only UI.</summary>
public partial class App : Application
{
    private static Mutex? _singleInstanceMutex;
    private TrayNotifier? _tray;
    private MainWindow? _window;
    private GlobalHotKey? _hotKey;
    private ReminderScheduler? _scheduler;
    private IServiceProvider? _services;
    private JarvisSettings _settings = new();

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Single-instance: a second launch just focuses the first window.
        _singleInstanceMutex = new Mutex(initiallyOwned: true, "JARVIS-SingleInstance", out var createdNew);
        if (!createdNew)
        {
            Shutdown();
            return;
        }

        var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JARVIS");
        Directory.CreateDirectory(dataDir);
        var settingsPath = Path.Combine(dataDir, "settings.json");
        _settings = SettingsStore.Load(settingsPath);

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddDebug().SetMinimumLevel(LogLevel.Information));
        services.AddSingleton(_settings);
        services.AddSingleton<ISecretVault>(new DpapiVault(Path.Combine(dataDir, "gemini.key")));
        services.AddSingleton<SqliteStores>(sp => new SqliteStores(Path.Combine(dataDir, "jarvis.db")));
        services.AddSingleton<IMemoryStore>(sp => sp.GetRequiredService<SqliteStores>());
        services.AddSingleton<IConversationStore>(sp => sp.GetRequiredService<SqliteStores>());
        services.AddSingleton<ITaskStore>(sp => sp.GetRequiredService<SqliteStores>());
        services.AddSingleton<IReminderStore>(sp => sp.GetRequiredService<SqliteStores>());
        services.AddSingleton<MemoryService>();
        services.AddSingleton<FileSearchService>();
        services.AddSingleton<WindowsTools>();
        services.AddSingleton<ISpeechToText, WindowsSpeechToText>();
        services.AddSingleton<ITextToSpeech, WindowsTextToSpeech>();
        services.AddSingleton<INotifier, TrayNotifier>();
        services.AddSingleton(sp =>
        {
            var registry = new ToolRegistry(_settings.TrustedActions);
            foreach (var tool in sp.GetRequiredService<WindowsTools>().All) registry.Register(tool);
            foreach (var tool in FileTools.Create(sp.GetRequiredService<FileSearchService>(), _settings.IndexedFolders)) registry.Register(tool);
            registry.Register(new ToolDefinition("list_tasks", "List background tasks and their status.",
                RiskTier.Trusted, """{"type":"object","properties":{}}""",
                async (_, ct) =>
                {
                    var list = await sp.GetRequiredService<TaskEngine>().ListAsync(ct);
                    return list.Count == 0 ? "No tasks." : string.Join("\n", list.Select(t => $"- {t.Title}: {t.Status} ({t.CurrentStepIndex}/{t.Steps.Count} steps)"));
                }));
            registry.Register(new ToolDefinition("create_reminder", "Create a reminder from natural language text.",
                RiskTier.Trusted, """{"type":"object","properties":{"text":{"type":"string"}},"required":["text"]}""",
                async (args, ct) =>
                {
                    var text = args.GetProperty("text").GetString() ?? "";
                    var due = ReminderScheduler.TryParseDue(text, DateTimeOffset.Now) ?? DateTimeOffset.Now.AddHours(1);
                    var reminder = new Reminder(Guid.NewGuid(), text, due, ReminderKind.OneTime, null, false, DateTimeOffset.Now);
                    await sp.GetRequiredService<IReminderStore>().SaveAsync(reminder, ct);
                    return $"Reminder set for {due:g}: {text}";
                }));
            return registry;
        });
        services.AddSingleton<TaskEngine>();
        services.AddSingleton<WorkflowPlanner>();
        services.AddSingleton<GeminiClient?>(sp =>
        {
            var key = sp.GetRequiredService<ISecretVault>().GetApiKeyAsync().GetAwaiter().GetResult();
            if (string.IsNullOrWhiteSpace(key)) return null;
            return new GeminiClient(new HttpClient(), key, _settings.GeminiModel, sp.GetRequiredService<ILogger<GeminiClient>>());
        });
        services.AddSingleton<ConversationService>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<SettingsViewModel>();
        _services = services.BuildServiceProvider();

        // Recover background work after restart (tasks -> Paused, due reminders fire).
        await _services.GetRequiredService<TaskEngine>().RecoverAfterRestartAsync().ConfigureAwait(false);
        _scheduler = new ReminderScheduler(
            _services.GetRequiredService<IReminderStore>(),
            r => { _services.GetRequiredService<INotifier>().Notify("JARVIS reminder", r.Text); return Task.CompletedTask; },
            _services.GetRequiredService<ILogger<ReminderScheduler>>());
        _scheduler.Start();

        // Startup integration (per-user, no admin).
        try { StartupService.SetEnabled(_settings.StartWithWindows, Environment.ProcessPath ?? "JARVIS.exe"); } catch { /* registry unavailable */ }

        _window = new MainWindow(_services.GetRequiredService<MainViewModel>(), _services.GetRequiredService<SettingsViewModel>());
        _window.Show();
        await _window.InitialiseAsync().ConfigureAwait(false);

        _tray = (TrayNotifier)_services.GetRequiredService<INotifier>();
        _tray.EnsureIcon(null, "JARVIS — running in tray", () => _window.Show());

        // Global push-to-talk shortcut.
        var source = PresentationSource.FromVisual(_window);
        if (source is not null)
        {
            _hotKey = new GlobalHotKey(new System.Windows.Interop.WindowInteropHelper(_window).Handle, id: 1);
            if (!_hotKey.Register(_settings.PushToTalkShortcut))
            {
                _services.GetRequiredService<INotifier>().Notify("JARVIS", $"Shortcut {_settings.PushToTalkShortcut} is already in use. Change it in Settings.");
            }
        }

        // Start hidden in tray when launched with --tray (Windows startup).
        if (e.Args.Contains("--tray")) _window.Hide();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _scheduler?.Stop();
        _hotKey?.Dispose();
        _tray?.Dispose();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
