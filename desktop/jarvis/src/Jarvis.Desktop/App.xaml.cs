using System.IO;
using System.Windows;
using System.Windows.Threading;
using Jarvis.Agent;
using Jarvis.AI;
using Jarvis.Core.Abstractions;
using Jarvis.Core.Models;
using Jarvis.Core.Shell;
using Jarvis.Core.Tools;
using Jarvis.Desktop.Services;
using Jarvis.Desktop.ViewModels;
using Jarvis.Desktop.Views;
using Jarvis.Infrastructure;
using Jarvis.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jarvis.Desktop;

public partial class App : Application
{
    private const string InstanceName = @"Local\ProjectJarvis.Desktop.Instance";
    private const string ShowSignalName = @"Local\ProjectJarvis.Desktop.Show";
    private const int ToggleHotkeyId = 1;
    private const int PushToTalkHotkeyId = 2;

    private Mutex? _instance;
    private EventWaitHandle? _showSignal;
    private ServiceProvider? _services;
    private ILogger<App>? _log;
    private TrayIcon? _tray;
    private GlobalHotkeys? _hotkeys;
    private MainWindow? _window;
    private MainViewModel? _viewModel;
    private LifecycleController? _lifecycle;
    private SettingsWindow? _settingsWindow;
    private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();
    private string? _smokeResultPath;
    private string _lastHotkeys = "";
    private bool _cleanedUp;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var args = e.Args;
        var startInTray = args.Any(a => string.Equals(a, StartupManager.TrayArgument, StringComparison.OrdinalIgnoreCase));
        var smokeIndex = Array.FindIndex(args, a => string.Equals(a, "--smoke-test", StringComparison.OrdinalIgnoreCase));
        if (smokeIndex >= 0)
        {
            _smokeResultPath = smokeIndex + 1 < args.Length
                ? args[smokeIndex + 1]
                : Path.Combine(Path.GetTempPath(), "jarvis-smoke-test.log");
        }
        var smoke = _smokeResultPath is not null;

        if (!smoke && !AcquireSingleInstance())
        {
            Shutdown(0);
            return;
        }

        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (sender, ev) => _log?.LogCritical(ev.ExceptionObject as Exception, "Unhandled exception.");
        TaskScheduler.UnobservedTaskException += (sender, ev) =>
        {
            _log?.LogError(ev.Exception, "Unobserved task exception.");
            ev.SetObserved();
        };

        try
        {
            // The smoke test never touches the real profile: it runs against a throw-away data folder.
            var paths = smoke
                ? new AppPaths(Path.Combine(Path.GetTempPath(), "jarvis-smoke-" + Guid.NewGuid().ToString("N")))
                : new AppPaths();

            _tray = new TrayIcon();
            _services = Composition.Build(paths, _tray);
            _log = _services.GetRequiredService<ILogger<App>>();
            _log.LogInformation("JARVIS starting (version {Version}).", typeof(App).Assembly.GetName().Version);

            var settings = _services.GetRequiredService<ISettingsStore>();
            var tasks = _services.GetRequiredService<TaskEngine>();
            var scheduler = _services.GetRequiredService<SchedulerService>();

            _viewModel = _services.GetRequiredService<MainViewModel>();
            _window = new MainWindow(_viewModel, settings);
            MainWindow = _window;

            _lifecycle = new LifecycleController(_window, _tray, tasks.SetAllPaused, OpenSettings, ExitApplication);
            _window.Lifecycle = _lifecycle;
            _lifecycle.SetTasksPaused(settings.Current.TasksPaused);
            _viewModel.SettingsRequested += OpenSettings;
            _viewModel.HideRequested += () => _lifecycle.HideWindow();

            scheduler.Announced += OnAnnouncement;
            tasks.TaskChanged += task => Dispatcher.BeginInvoke(new Action(UpdateTrayStatus));
            settings.Changed += changed => Dispatcher.BeginInvoke(new Action(() =>
            {
                RegisterHotkeys(changed, announceProblems: true);
                _tray?.SetPaused(changed.TasksPaused);
                UpdateTrayStatus();
            }));
            SessionEnding += (sender, ev) => Cleanup();

            _viewModel.LoadLatestConversation();
            UpdateTrayStatus();

            if (smoke)
            {
                _ = RunSmokeTestAsync(_smokeResultPath!);
                return;
            }

            _hotkeys = new GlobalHotkeys();
            RegisterHotkeys(settings.Current, announceProblems: true);
            SyncStartupEntry(settings.Current);
            scheduler.Start();
            _ = Task.Run(async () =>
            {
                try
                {
                    var resumed = await tasks.RecoverAsync(_shutdown.Token);
                    if (resumed > 0) _log.LogInformation("Recovered {Count} background task(s).", resumed);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Task recovery failed.");
                }
            });
            ListenForShowSignal();

            if (!_services.GetRequiredService<GeminiClient>().IsConfigured && _viewModel.Messages.Count == 0)
            {
                _viewModel.ShowNote(
                    "Welcome. Add your Gemini API key under Settings › Gemini to enable full conversation, planning and web research. "
                    + "Until then I work in local mode: " + OfflineAssistant.CapabilitiesHint);
            }

            if (!startInTray) _lifecycle.ShowWindow();
        }
        catch (Exception ex)
        {
            _log?.LogCritical(ex, "JARVIS could not start.");
            if (smoke)
            {
                WriteSmokeResult(_smokeResultPath!, new[] { "FAIL (startup): " + ex });
                Cleanup();
                Shutdown(1);
                return;
            }
            MessageBox.Show("JARVIS could not start.\n\n" + ex.Message, "JARVIS", MessageBoxButton.OK, MessageBoxImage.Error);
            Cleanup();
            Shutdown(1);
        }
    }

    /// <summary>Returns false when another instance is already running (and asks that one to show its window).</summary>
    private bool AcquireSingleInstance()
    {
        _instance = new Mutex(initiallyOwned: true, InstanceName, out var created);
        if (created)
        {
            _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName);
            return true;
        }
        try
        {
            using var signal = EventWaitHandle.OpenExisting(ShowSignalName);
            signal.Set();
        }
        catch (Exception)
        {
            // The other instance is still starting; nothing more to do.
        }
        _instance.Dispose();
        _instance = null;
        return false;
    }

    private void ListenForShowSignal()
    {
        var signal = _showSignal;
        if (signal is null) return;
        var thread = new Thread(() =>
        {
            var handles = new WaitHandle[] { signal, _shutdown.Token.WaitHandle };
            while (true)
            {
                int index;
                try { index = WaitHandle.WaitAny(handles); }
                catch (ObjectDisposedException) { return; }
                if (index != 0) return;
                Dispatcher.BeginInvoke(new Action(() => _lifecycle?.ShowWindow()));
            }
        })
        {
            IsBackground = true,
            Name = "JARVIS show signal"
        };
        thread.Start();
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _log?.LogError(e.Exception, "Unhandled UI exception.");
        e.Handled = true;
        if (_smokeResultPath is not null)
        {
            WriteSmokeResult(_smokeResultPath, new[] { "FAIL (UI exception): " + e.Exception });
            Cleanup();
            Shutdown(1);
        }
    }

    private void RegisterHotkeys(AppSettings settings, bool announceProblems)
    {
        if (_hotkeys is null || _lifecycle is null || _viewModel is null) return;
        var signature = settings.ToggleHotkey + "|" + settings.PushToTalkHotkey;
        if (signature == _lastHotkeys) return;
        _lastHotkeys = signature;

        var problems = new List<string>();
        if (!HotkeyGesture.TryParse(settings.ToggleHotkey, out var toggle, out var toggleError))
            problems.Add("Show/hide shortcut: " + toggleError);
        else if (!_hotkeys.Register(ToggleHotkeyId, toggle, () => _lifecycle.ToggleWindow(), null, out var toggleRegisterError))
            problems.Add(toggleRegisterError);

        if (!HotkeyGesture.TryParse(settings.PushToTalkHotkey, out var talk, out var talkError))
            problems.Add("Push-to-talk shortcut: " + talkError);
        else if (!_hotkeys.Register(PushToTalkHotkeyId, talk,
                     () =>
                     {
                         _lifecycle.ShowWindow();
                         _viewModel.StartListening();
                     },
                     () => _ = _viewModel.StopListeningAsync(send: true), out var talkRegisterError))
            problems.Add(talkRegisterError);

        if (problems.Count == 0 || !announceProblems) return;
        var text = string.Join(" ", problems);
        _log?.LogWarning("Hotkey registration problem: {Problem}", text);
        _viewModel.ShowNote(text);
    }

    private void SyncStartupEntry(AppSettings settings)
    {
        try
        {
            var startup = _services!.GetRequiredService<IStartupManager>();
            if (startup.IsEnabled() != settings.LaunchAtStartup) startup.SetEnabled(settings.LaunchAtStartup);
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "The Windows startup entry could not be updated.");
        }
    }

    private void OnAnnouncement(Announcement announcement)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_viewModel is null || _services is null) return;
            _viewModel.ShowNote(announcement.Title + " — " + announcement.Text);
            var settings = _services.GetRequiredService<ISettingsStore>().Current;
            if (announcement.Speak && settings.SpeakResponses) _viewModel.Speak(announcement.Text);
        }));
    }

    private void UpdateTrayStatus()
    {
        if (_tray is null || _services is null) return;
        try
        {
            var tasks = _services.GetRequiredService<TaskEngine>();
            var active = tasks.List(50).Count(t => t.IsActive);
            var paused = tasks.AllPaused ? " (paused)" : "";
            _tray.SetStatus(active == 0 ? "JARVIS" : $"JARVIS — {active} background task{(active == 1 ? "" : "s")}{paused}");
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "The tray status could not be updated.");
        }
    }

    private void OpenSettings()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(OpenSettings));
            return;
        }
        if (_services is null || _lifecycle is null || _lifecycle.IsExiting) return;
        if (_settingsWindow is not null)
        {
            if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = CreateSettingsWindow();
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private SettingsWindow CreateSettingsWindow()
    {
        var viewModel = _services!.GetRequiredService<SettingsViewModel>();
        viewModel.PauseAllRequested += paused => _lifecycle?.SetTasksPaused(paused);
        viewModel.ConversationsDeleted += () => _viewModel?.Messages.Clear();
        var window = new SettingsWindow(viewModel);
        window.Closed += (sender, e) => _settingsWindow = null;
        return window;
    }

    /// <summary>The tray menu's Exit: the only way JARVIS stops.</summary>
    private void ExitApplication()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(ExitApplication));
            return;
        }
        _log?.LogInformation("JARVIS exiting.");
        Cleanup();
        Shutdown(0);
    }

    private void Cleanup()
    {
        if (_cleanedUp) return;
        _cleanedUp = true;
        try { _shutdown.Cancel(); } catch (Exception) { }
        try { _settingsWindow?.Close(); } catch (Exception) { }
        try { _hotkeys?.Dispose(); } catch (Exception) { }
        if (_services is not null)
        {
            try { _services.GetRequiredService<ITextToSpeech>().Stop(); } catch (Exception) { }
            try { _services.GetRequiredService<SchedulerService>().Dispose(); } catch (Exception) { }
            // Running tasks keep their checkpoints and resume on the next start.
            try { _services.GetRequiredService<TaskEngine>().Dispose(); } catch (Exception) { }
        }
        try { _tray?.Dispose(); } catch (Exception) { }
        try { _services?.Dispose(); } catch (Exception) { }
        try { _showSignal?.Dispose(); } catch (Exception) { }
        try
        {
            _instance?.ReleaseMutex();
            _instance?.Dispose();
        }
        catch (Exception)
        {
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Cleanup();
        base.OnExit(e);
    }

    // ---- Start-up self check (used by CI: Jarvis.exe --smoke-test <result file>) ----

    /// <summary>
    /// Builds the real object graph, shows both windows, runs local commands end to end and exercises the approval
    /// prompt. Exit code 0 means every XAML resource, binding target and service registration resolved at run time.
    /// </summary>
    private async Task RunSmokeTestAsync(string resultPath)
    {
        var lines = new List<string>();
        var code = 1;
        try
        {
            var services = _services!;
            var viewModel = _viewModel!;
            var window = _window!;

            var registry = services.GetRequiredService<ToolRegistry>();
            lines.Add($"tools registered: {registry.All.Count}");
            if (registry.All.Count < 50) throw new InvalidOperationException("Fewer tools than expected were registered.");

            // Every locally built tool call (offline commands and built-in workflows) must match the registered schemas.
            var callProblems = new List<string>();
            var validatedCalls = 0;
            foreach (var phrase in SmokePhrases)
            {
                var intent = OfflineAssistant.Interpret(phrase, DateTime.Now, out _);
                if (intent is null) { callProblems.Add($"'{phrase}' was not understood"); continue; }
                if (!registry.TryGet(intent.Tool, out var phraseTool)) { callProblems.Add($"'{phrase}' maps to the unknown tool '{intent.Tool}'"); continue; }
                foreach (var schemaError in SchemaValidator.Validate(phraseTool.Definition, intent.Args))
                    callProblems.Add($"'{phrase}' -> {intent.Tool}: {schemaError}");
                validatedCalls++;
            }
            var sampleSettings = new AppSettings();
            sampleSettings.GetMode(JarvisMode.Development).ProjectPath = @"C:\Projects\demo";
            sampleSettings.ApprovedCommands.Add(new ApprovedCommand { Name = "build", Executable = "dotnet", RunOnDevSetup = true });
            foreach (var workflow in Workflows.All)
            {
                var workflowSteps = Workflows.Build(workflow.Name, sampleSettings);
                if (workflowSteps.Count == 0) callProblems.Add($"workflow '{workflow.Name}' has no steps");
                foreach (var workflowStep in workflowSteps)
                {
                    if (!registry.TryGet(workflowStep.Tool, out var stepTool))
                    {
                        callProblems.Add($"workflow '{workflow.Name}' uses the unknown tool '{workflowStep.Tool}'");
                        continue;
                    }
                    foreach (var schemaError in SchemaValidator.Validate(stepTool.Definition, workflowStep.Args))
                        callProblems.Add($"workflow '{workflow.Name}' -> {workflowStep.Tool}: {schemaError}");
                    validatedCalls++;
                }
            }
            lines.Add($"local tool calls validated: {validatedCalls}");
            if (callProblems.Count > 0) throw new InvalidOperationException(string.Join("; ", callProblems));

            window.ShowAndActivate();
            await Task.Delay(300);
            if (!window.IsVisible) throw new InvalidOperationException("The assistant window did not become visible.");

            await viewModel.SendAsync("what time is it", fromVoice: false);
            await viewModel.SendAsync("remind me to stretch tomorrow at 9am", fromVoice: false);
            await viewModel.SendAsync("show my reminders", fromVoice: false);
            await Task.Delay(300);
            lines.Add($"chat messages: {viewModel.Messages.Count}");
            if (viewModel.Messages.Count < 6) throw new InvalidOperationException("Local commands did not produce replies.");
            var reminders = services.GetRequiredService<IPlannerStore>().ListReminders(true);
            lines.Add($"reminders stored: {reminders.Count}");
            if (reminders.Count != 1) throw new InvalidOperationException("The reminder was not stored.");
            lines.Add("last reply: " + viewModel.Messages.Last().Text.Replace('\n', ' '));

            var approvals = services.GetRequiredService<ApprovalService>();
            var pending = approvals.RequestAsync(
                new ApprovalRequest("smoke_test", ToolRisk.Routine, "Smoke test approval", new[] { "First detail", "Second detail" }, false, null),
                CancellationToken.None);
            await Task.Delay(300);
            if (!viewModel.HasApproval) throw new InvalidOperationException("The approval prompt was not shown.");
            viewModel.ApproveCommand.Execute(null);
            var decision = await pending;
            if (!decision.Approved) throw new InvalidOperationException("The approval was not recorded.");
            lines.Add("approval prompt: ok");

            var settingsWindow = CreateSettingsWindow();
            settingsWindow.Show();
            var pages = settingsWindow.ShowEveryPage();
            await Task.Delay(300);
            settingsWindow.Close();
            lines.Add($"settings pages rendered: {pages}");

            ((IWindowHost)window).Hide();
            if (window.IsVisible) throw new InvalidOperationException("The assistant window did not hide.");
            if (_lifecycle is null || !_lifecycle.OnWindowClosing()) throw new InvalidOperationException("Closing the window would exit the app.");
            lines.Add("hide to tray: ok");

            lines.Add("PASS");
            code = 0;
        }
        catch (Exception ex)
        {
            lines.Add("FAIL: " + ex);
        }

        WriteSmokeResult(resultPath, lines);
        string? dataDirectory = null;
        try { dataDirectory = _services?.GetRequiredService<AppPaths>().DataDirectory; } catch (Exception) { }
        Cleanup();
        try
        {
            if (dataDirectory is not null && Directory.Exists(dataDirectory)) Directory.Delete(dataDirectory, recursive: true);
        }
        catch (Exception)
        {
            // Left in the temp folder; harmless.
        }
        Shutdown(code);
    }

    /// <summary>One phrase per local command the offline interpreter understands.</summary>
    private static readonly string[] SmokePhrases =
    {
        "what time is it", "remind me to stretch tomorrow at 9am", "show my reminders", "delete reminder 1",
        "add task finish the report", "add an urgent task to call the bank tomorrow at 3 pm", "complete task 1", "delete task 1",
        "show my tasks", "show background tasks", "plan my day", "summarise my day",
        "switch to study mode", "open workspace", "start a study session", "prepare my development environment", "system checkup",
        "start focus for 25 minutes on thesis", "end focus session",
        "set volume to 40", "mute", "unmute", "set brightness to 60", "lock my computer", "how is my laptop", "what's using my memory",
        "remember that I prefer dark mode", "what do you remember about me",
        "find duplicates in my downloads folder", "organise my downloads folder", "undo the last organise", "rebuild the search index",
        "find my resume", "close notepad", "switch to chrome", "open downloads", "open github.com", "open notepad"
    };

    private static void WriteSmokeResult(string path, IEnumerable<string> lines)
    {
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.AppendAllLines(path, lines);
        }
        catch (Exception)
        {
            // The exit code still reports the outcome.
        }
    }
}
