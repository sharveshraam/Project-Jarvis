using System.Text.Json.Nodes;
using Jarvis.Agent;
using Jarvis.Core.Abstractions;
using Jarvis.Core.AI;
using Jarvis.Core.Models;
using Jarvis.Core.Planning;
using Jarvis.Core.Shell;
using Jarvis.Core.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jarvis.Tests;

/// <summary>A clock the test controls. UTC is always derived from the local value.</summary>
internal sealed class FakeClock : IClock
{
    public FakeClock(DateTime localNow) => LocalNow = localNow;
    public DateTime LocalNow { get; set; }
    public DateTime UtcNow => TimeText.ToUtc(LocalNow);
}

internal sealed class InMemorySettingsStore : ISettingsStore
{
    private readonly Dictionary<string, string> _values = new Dictionary<string, string>(StringComparer.Ordinal);

    public AppSettings Current { get; private set; } = new AppSettings();
    public event Action<AppSettings>? Changed;
    public int SaveCount { get; private set; }

    public void Save(AppSettings settings)
    {
        Current = settings;
        SaveCount++;
        Changed?.Invoke(settings);
    }

    public string? GetValue(string key) => _values.TryGetValue(key, out var value) ? value : null;
    public void SetValue(string key, string value) => _values[key] = value;
}

internal sealed class FakeApprovals : IApprovalService
{
    public List<ApprovalRequest> Requests { get; } = new List<ApprovalRequest>();
    public ApprovalResult Answer { get; set; } = new ApprovalResult(false);

    public Task<ApprovalResult> RequestAsync(ApprovalRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        return Task.FromResult(Answer);
    }
}

internal sealed class RecordingAudit : IAuditLog
{
    public List<AuditEntry> Entries { get; } = new List<AuditEntry>();

    public void Record(string tool, string risk, string decision, bool success, string summary)
        => Entries.Add(new AuditEntry(DateTime.UtcNow, tool, risk, decision, success, summary));

    public IReadOnlyList<AuditEntry> Recent(int limit) => Entries.AsEnumerable().Reverse().Take(limit).ToList();
}

internal sealed class InMemoryJournal : IFileOpJournal
{
    private readonly List<(string Batch, FileMove Move)> _moves = new List<(string Batch, FileMove Move)>();
    public List<string> Undone { get; } = new List<string>();

    public void Record(string batchId, string source, string destination) => _moves.Add((batchId, new FileMove(source, destination)));
    public string? LatestBatchId() => _moves.Count == 0 ? null : _moves[_moves.Count - 1].Batch;
    public IReadOnlyList<FileMove> GetBatch(string batchId) => _moves.Where(m => m.Batch == batchId).Select(m => m.Move).ToList();
    public void MarkUndone(string batchId) => Undone.Add(batchId);
}

internal sealed class RecordingNotifier : INotifier
{
    public List<(string Title, string Message)> Notifications { get; } = new List<(string Title, string Message)>();
    public void Notify(string title, string message, NotificationKind kind = NotificationKind.Info) => Notifications.Add((title, message));
}

internal sealed class InMemorySecrets : ISecretStore
{
    private readonly Dictionary<string, string> _values = new Dictionary<string, string>(StringComparer.Ordinal);
    public string? Get(string name) => _values.TryGetValue(name, out var value) ? value : null;
    public void Set(string name, string value) => _values[name] = value;
    public void Remove(string name) => _values.Remove(name);
}

internal sealed class FakeWindow : IWindowHost
{
    public bool IsVisible { get; private set; }
    public int ShowCount { get; private set; }
    public int HideCount { get; private set; }

    public void ShowAndActivate()
    {
        IsVisible = true;
        ShowCount++;
    }

    public void Hide()
    {
        IsVisible = false;
        HideCount++;
    }
}

internal sealed class FakeTray : ITrayIcon
{
    public event Action? OpenRequested;
    public event Action? SettingsRequested;
    public event Action<bool>? PauseTasksToggled;
    public event Action? ExitRequested;

    public bool? Paused { get; private set; }
    public string Status { get; private set; } = "";

    public void SetPaused(bool paused) => Paused = paused;
    public void SetStatus(string tooltip) => Status = tooltip;

    public void ClickOpen() => OpenRequested?.Invoke();
    public void ClickSettings() => SettingsRequested?.Invoke();
    public void ClickPause(bool paused) => PauseTasksToggled?.Invoke(paused);
    public void ClickExit() => ExitRequested?.Invoke();
}

/// <summary>A throw-away folder that is removed when the test finishes.</summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Root = Path.Combine(Path.GetTempPath(), "jarvis-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string File(string relativePath, string content = "x")
    {
        var path = Path.Combine(Root, relativePath);
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
        System.IO.File.WriteAllText(path, content);
        return path;
    }

    public string Sub(string relativePath)
    {
        var path = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left behind in the temp folder; harmless for the test result.
        }
    }
}

/// <summary>Records app requests instead of touching real processes.</summary>
internal sealed class FakeApps : IAppController
{
    public HashSet<string> Running { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Missing { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public List<string> Launched { get; } = new List<string>();
    public List<string> Closed { get; } = new List<string>();

    public ActionOutcome Launch(string nameOrPath)
    {
        if (Missing.Contains(nameOrPath)) return new ActionOutcome(false, "No application named '" + nameOrPath + "' was found.");
        Launched.Add(nameOrPath);
        Running.Add(nameOrPath);
        return new ActionOutcome(true, "Opened " + nameOrPath + ".", true);
    }

    public Task<ActionOutcome> CloseAsync(string name, CancellationToken ct)
    {
        if (!Running.Remove(name)) return Task.FromResult(new ActionOutcome(false, name + " is not running."));
        Closed.Add(name);
        return Task.FromResult(new ActionOutcome(true, "Closed " + name + ".", true));
    }

    public ActionOutcome SwitchTo(string name)
        => Running.Contains(name) ? new ActionOutcome(true, "Switched to " + name + ".", true) : new ActionOutcome(false, name + " is not running.");

    public IReadOnlyList<RunningApp> ListRunning() => Running.Select((name, index) => new RunningApp(index + 1, name, name)).ToList();

    public bool IsRunning(string name) => Running.Contains(name);
}

/// <summary>Records shell requests instead of opening anything.</summary>
internal sealed class FakeShell : IShellService
{
    public List<string> Opened { get; } = new List<string>();
    public bool Locked { get; private set; }

    private ActionOutcome Record(string what)
    {
        Opened.Add(what);
        return new ActionOutcome(true, "Opened " + what + ".", true);
    }

    public ActionOutcome OpenPath(string path) => Record("path:" + path);
    public ActionOutcome OpenUrl(string url) => Record("url:" + url);
    public ActionOutcome OpenSettingsPage(string page) => Record("settings:" + page);
    public ActionOutcome OpenTerminal(string? workingDirectory) => Record("terminal:" + workingDirectory);
    public ActionOutcome OpenInIde(string path) => Record("ide:" + path);

    public ActionOutcome LockWorkstation()
    {
        Locked = true;
        return new ActionOutcome(true, "Locked.", true);
    }
}

internal sealed class FakeConnectivity : IConnectivity
{
    private bool _available = true;

    public bool IsNetworkAvailable
    {
        get => _available;
        set
        {
            if (_available == value) return;
            _available = value;
            Changed?.Invoke();
        }
    }

    public event Action? Changed;
}

/// <summary>A scripted stand-in for Gemini. Replies are consumed in the order they were queued.</summary>
internal sealed class FakeAi : IAiClient
{
    private readonly Queue<Func<AiResponse>> _replies = new Queue<Func<AiResponse>>();

    public bool IsConfigured { get; set; } = true;

    /// <summary>For every request: the system instruction, the names of the offered tools and a snapshot of the input items.</summary>
    public List<(string? System, List<string> Tools, List<AiInput> Input, bool WebSearch)> Requests { get; }
        = new List<(string? System, List<string> Tools, List<AiInput> Input, bool WebSearch)>();

    public void ReplyText(string text) => _replies.Enqueue(() => new AiResponse { Text = text });

    public void ReplyCall(string callId, string tool, JsonObject args)
    {
        var step = new JsonObject { ["type"] = "function_call", ["id"] = callId, ["name"] = tool, ["arguments"] = args.DeepClone() };
        _replies.Enqueue(() => new AiResponse
        {
            ToolCalls = new[] { new AiToolCall(callId, tool, (JsonObject)args.DeepClone()) },
            RawSteps = new JsonNode[] { step.DeepClone() },
            Status = "requires_action"
        });
    }

    public void Fail(AiErrorKind kind, string message) => _replies.Enqueue(() => throw new AiException(kind, message));

    public Task<AiResponse> GenerateAsync(AiRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Requests.Add((request.SystemInstruction, request.Tools.Select(t => t.Name).ToList(), request.Input.ToList(), request.UseWebSearch));
        if (_replies.Count == 0) throw new AiException(AiErrorKind.Unknown, "The test queued no reply for this request.");
        return Task.FromResult(_replies.Dequeue()());
    }
}

/// <summary>A real ToolExecutor wired to in-memory collaborators.</summary>
internal sealed class ExecutorHarness
{
    public ExecutorHarness()
    {
        Executor = new ToolExecutor(Registry, Settings, Approvals, Audit, NullLogger<ToolExecutor>.Instance);
    }

    public InMemorySettingsStore Settings { get; } = new InMemorySettingsStore();
    public FakeApprovals Approvals { get; } = new FakeApprovals();
    public RecordingAudit Audit { get; } = new RecordingAudit();
    public ToolRegistry Registry { get; } = new ToolRegistry();
    public ToolExecutor Executor { get; }
    public int Runs { get; private set; }

    public ToolDefinition Add(string name, ToolRisk risk, ToolCategory category = ToolCategory.General,
        Func<JsonObject, CancellationToken, Task<ToolResult>>? run = null, TimeSpan? timeout = null)
    {
        var definition = new ToolDefinition(name, "A tool used by tests.", category, risk).Str("value", "Any text.");
        if (timeout.HasValue) definition.WithTimeout(timeout.Value);
        Registry.Register(new DelegateTool(definition, (args, context, ct) =>
        {
            Runs++;
            return run is null ? Task.FromResult(ToolResult.Ok("done")) : run(args, ct);
        }));
        return definition;
    }

    public Task<ToolResult> Run(string name, JsonObject? args = null, ToolContext? context = null)
        => Executor.ExecuteAsync(name, args ?? new JsonObject(), context ?? new ToolContext(), CancellationToken.None);
}
