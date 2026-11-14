using System.IO;
using System.Net.Http;
using Jarvis.Agent;
using Jarvis.Agent.Tools;
using Jarvis.AI;
using Jarvis.Core.Abstractions;
using Jarvis.Core.AI;
using Jarvis.Core.Files;
using Jarvis.Core.Security;
using Jarvis.Core.Tools;
using Jarvis.Desktop.Services;
using Jarvis.Desktop.ViewModels;
using Jarvis.Infrastructure;
using Jarvis.Infrastructure.Data;
using Jarvis.Infrastructure.Logging;
using Jarvis.Infrastructure.Platform;
using Jarvis.Infrastructure.Search;
using Jarvis.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jarvis.Desktop;

/// <summary>The composition root: every service is wired here and nowhere else.</summary>
public static class Composition
{
    public static ServiceProvider Build(AppPaths paths, INotifier notifier)
    {
        var services = new ServiceCollection();

        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddProvider(new FileLoggerProvider(paths.LogDirectory, LogLevel.Information));
        });

        // Storage
        services.AddSingleton(paths);
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton(_ => new Database(paths.DatabasePath));
        services.AddSingleton<ISettingsStore, SettingsStore>();
        services.AddSingleton<IConversationStore, ConversationStore>();
        services.AddSingleton<IMemoryStore, MemoryStore>();
        services.AddSingleton<IPlannerStore, PlannerStore>();
        services.AddSingleton<IAgentTaskStore, AgentTaskStore>();
        services.AddSingleton<IFocusStore, FocusStore>();
        services.AddSingleton<IFileOpJournal, FileOpJournal>();
        services.AddSingleton<IAuditLog, AuditLog>();
        services.AddSingleton<ISearchIndex, SearchIndex>();
        services.AddSingleton<ISecretStore>(_ => new DpapiSecretStore(paths.SecretsPath));

        // Platform
        services.AddSingleton(notifier);
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<IConnectivity, Connectivity>();
        services.AddSingleton<ISystemInfo, SystemInfoService>();
        services.AddSingleton<IAudioController, AudioController>();
        services.AddSingleton<IDisplayController, DisplayController>();
        services.AddSingleton<IAppController, AppController>();
        services.AddSingleton<IShellService, ShellService>();
        services.AddSingleton<IScreenCapture, ScreenCaptureService>();
        services.AddSingleton<IClipboardService, ClipboardService>();
        services.AddSingleton<ISpeechToText, WindowsSpeechToText>();
        services.AddSingleton<ITextToSpeech, WindowsTextToSpeech>();
        services.AddSingleton<IStartupManager, StartupManager>();

        // Policy
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<ISettingsStore>();
            return new PathPolicy(() => settings.Current,
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ProtectedRoots(paths));
        });
        services.AddSingleton<FileService>();

        // AI
        services.AddSingleton(_ => new HttpClient { Timeout = TimeSpan.FromSeconds(120) });
        services.AddSingleton<GeminiClient>();
        services.AddSingleton<IAiClient>(sp => sp.GetRequiredService<GeminiClient>());
        services.AddSingleton<ConversationSummarizer>();

        // Agent
        services.AddSingleton<ToolRegistry>();
        services.AddSingleton<ApprovalService>();
        services.AddSingleton<IApprovalService>(sp => sp.GetRequiredService<ApprovalService>());
        services.AddSingleton<ToolExecutor>();
        services.AddSingleton<PlannerSignal>();
        services.AddSingleton<ModeService>();
        services.AddSingleton<TaskEngine>();
        services.AddSingleton<SchedulerService>();
        services.AddSingleton<OfflineAssistant>();
        services.AddSingleton<AssistantService>();

        // UI
        services.AddSingleton<MainViewModel>();
        services.AddTransient<SettingsViewModel>();

        var provider = services.BuildServiceProvider();
        RegisterTools(provider);
        return provider;
    }

    /// <summary>
    /// Tools are registered after the container exists because some of them depend on services
    /// (the task engine) that themselves depend on the registry.
    /// </summary>
    private static void RegisterTools(IServiceProvider provider)
    {
        var registry = provider.GetRequiredService<ToolRegistry>();
        registry.RegisterRange(ActivatorUtilities.CreateInstance<PlannerTools>(provider).Build());
        registry.RegisterRange(ActivatorUtilities.CreateInstance<MemoryTools>(provider).Build());
        registry.RegisterRange(ActivatorUtilities.CreateInstance<AppTools>(provider).Build());
        registry.RegisterRange(ActivatorUtilities.CreateInstance<SystemTools>(provider).Build());
        registry.RegisterRange(ActivatorUtilities.CreateInstance<PerceptionTools>(provider).Build());
        registry.RegisterRange(ActivatorUtilities.CreateInstance<FileTools>(provider).Build());
        registry.RegisterRange(ActivatorUtilities.CreateInstance<SearchTools>(provider).Build());
        registry.RegisterRange(ActivatorUtilities.CreateInstance<DevTools>(provider).Build());
        registry.RegisterRange(ActivatorUtilities.CreateInstance<FocusTools>(provider).Build());
        registry.RegisterRange(ActivatorUtilities.CreateInstance<AgentTools>(provider).Build());
    }

    /// <summary>Locations no file tool may read or change, whatever the user's allowed folders say.</summary>
    private static IEnumerable<string> ProtectedRoots(AppPaths paths)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            paths.DataDirectory
        };
        if (!string.IsNullOrWhiteSpace(profile))
        {
            foreach (var folder in new[] { ".ssh", ".aws", ".azure", ".gnupg", ".kube", ".docker" })
                roots.Add(Path.Combine(profile, folder));
        }
        return roots.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
