using System.Text.Json.Nodes;
using Jarvis.Core.AI;
using Jarvis.Core.Models;

namespace Jarvis.Core.Tools;

public sealed class ToolArgumentException : Exception
{
    public ToolArgumentException(string message) : base(message) { }
}

/// <summary>What a specific call would do, computed before anything runs. Drives the permission decision and the approval preview.</summary>
public sealed record ToolAssessment(ToolRisk Risk, string Summary, IReadOnlyList<string> Details)
{
    public static ToolAssessment Of(ToolRisk risk, string summary, params string[] details) => new(risk, summary, details);
}

public sealed class ToolContext
{
    public JarvisMode Mode { get; init; } = JarvisMode.Personal;
    public bool IsBackground { get; init; }
    public string? TaskId { get; init; }
    /// <summary>Set for direct user commands and built-in workflows, which are not restricted to the mode's tool set.</summary>
    public bool BypassModeFilter { get; init; }
    /// <summary>The user's current message, when this call originates from one. Privacy-sensitive tools
    /// (screen, clipboard) run without a prompt only if this message explicitly asks for them.</summary>
    public string? UserRequest { get; init; }
    /// <summary>Called with true while the user is being asked for approval, false afterwards.</summary>
    public Action<bool>? ApprovalStateChanged { get; init; }
}

public sealed class ToolResult
{
    public bool Success { get; init; }
    /// <summary>The action was blocked by permissions or declined by the user; nothing was executed.</summary>
    public bool Denied { get; init; }
    /// <summary>The outcome was checked after execution (e.g. file exists, process is running).</summary>
    public bool Verified { get; init; }
    /// <summary>User-presentable description of what actually happened.</summary>
    public string Message { get; init; } = "";
    public JsonObject? Data { get; init; }
    public AiImage? Image { get; init; }

    public static ToolResult Ok(string message, bool verified = true, JsonObject? data = null)
        => new() { Success = true, Verified = verified, Message = message, Data = data };
    public static ToolResult Fail(string message) => new() { Success = false, Message = message };
    public static ToolResult Refused(string message) => new() { Success = false, Denied = true, Message = message };

    /// <summary>The payload returned to Gemini as the function result.</summary>
    public string ToModelJson()
    {
        var o = new JsonObject
        {
            ["success"] = Success,
            ["verified"] = Verified,
            ["message"] = Message
        };
        if (Denied) o["denied"] = true;
        if (Data is not null) o["data"] = Data.DeepClone();
        return o.ToJsonString();
    }
}

public interface ITool
{
    ToolDefinition Definition { get; }
    /// <summary>Checks semantic validity and classifies the risk of this specific call. Must not have side effects.</summary>
    ToolAssessment Assess(JsonObject args, ToolContext context);
    Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext context, CancellationToken ct);
    /// <summary>Used during restart recovery: has this call's side effect already happened?</summary>
    Task<bool> IsAlreadyAppliedAsync(JsonObject args, CancellationToken ct);
}

/// <summary>A tool implemented with delegates; keeps tool modules compact.</summary>
public sealed class DelegateTool : ITool
{
    private readonly Func<JsonObject, ToolContext, CancellationToken, Task<ToolResult>> _execute;
    private readonly Func<JsonObject, ToolContext, ToolAssessment>? _assess;
    private readonly Func<JsonObject, CancellationToken, Task<bool>>? _alreadyApplied;

    public DelegateTool(
        ToolDefinition definition,
        Func<JsonObject, ToolContext, CancellationToken, Task<ToolResult>> execute,
        Func<JsonObject, ToolContext, ToolAssessment>? assess = null,
        Func<JsonObject, CancellationToken, Task<bool>>? alreadyApplied = null)
    {
        Definition = definition;
        _execute = execute;
        _assess = assess;
        _alreadyApplied = alreadyApplied;
    }

    public ToolDefinition Definition { get; }

    public ToolAssessment Assess(JsonObject args, ToolContext context)
        => _assess?.Invoke(args, context)
           ?? new ToolAssessment(Definition.DefaultRisk, $"{Definition.Name} {ToolArgs.Describe(args)}".Trim(), Array.Empty<string>());

    public Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext context, CancellationToken ct)
        => _execute(args, context, ct);

    public Task<bool> IsAlreadyAppliedAsync(JsonObject args, CancellationToken ct)
        => _alreadyApplied?.Invoke(args, ct) ?? Task.FromResult(false);
}

public interface IToolModule
{
    IEnumerable<ITool> Build();
}

public sealed class ToolRegistry
{
    private readonly Dictionary<string, ITool> _tools = new(StringComparer.Ordinal);

    public void Register(ITool tool)
    {
        if (!_tools.TryAdd(tool.Definition.Name, tool))
            throw new InvalidOperationException($"Tool '{tool.Definition.Name}' is already registered.");
    }

    public void RegisterRange(IEnumerable<ITool> tools)
    {
        foreach (var t in tools) Register(t);
    }

    public bool TryGet(string name, out ITool tool)
    {
        if (_tools.TryGetValue(name, out var found)) { tool = found; return true; }
        tool = null!;
        return false;
    }

    public IReadOnlyList<ITool> All => _tools.Values.OrderBy(t => t.Definition.Name, StringComparer.Ordinal).ToList();
}

public sealed record ApprovalRequest(
    string ToolName, ToolRisk Risk, string Summary, IReadOnlyList<string> Details, bool IsBackground, string? TaskId)
{
    /// <summary>Only routine actions can be remembered as trusted; consequential ones ask every time.</summary>
    public bool CanRemember => Risk == ToolRisk.Routine;
}

public sealed record ApprovalResult(bool Approved, bool RememberForTool = false);

public interface IApprovalService
{
    Task<ApprovalResult> RequestAsync(ApprovalRequest request, CancellationToken ct);
}
