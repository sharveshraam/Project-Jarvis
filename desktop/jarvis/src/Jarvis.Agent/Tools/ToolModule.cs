using System.Text.Json.Nodes;
using Jarvis.Core.Abstractions;
using Jarvis.Core.Tools;

namespace Jarvis.Agent.Tools;

/// <summary>Shared helpers for tool modules.</summary>
public abstract class ToolModule : IToolModule
{
    public abstract IEnumerable<ITool> Build();

    protected static ToolDefinition Def(string name, string description, ToolCategory category, ToolRisk risk)
        => new ToolDefinition(name, description, category, risk);

    protected static ITool Tool(
        ToolDefinition definition,
        Func<JsonObject, ToolContext, CancellationToken, Task<ToolResult>> run,
        Func<JsonObject, ToolContext, ToolAssessment>? assess = null,
        Func<JsonObject, CancellationToken, Task<bool>>? alreadyApplied = null)
        => new DelegateTool(definition, run, assess, alreadyApplied);

    /// <summary>A tool whose work is quick and synchronous.</summary>
    protected static ITool Sync(
        ToolDefinition definition,
        Func<JsonObject, ToolContext, ToolResult> run,
        Func<JsonObject, ToolContext, ToolAssessment>? assess = null,
        Func<JsonObject, bool>? alreadyApplied = null)
    {
        Func<JsonObject, CancellationToken, Task<bool>>? applied = null;
        if (alreadyApplied is not null) applied = (args, ct) => Task.FromResult(alreadyApplied(args));
        return new DelegateTool(definition, (args, context, ct) => Task.FromResult(run(args, context)), assess, applied);
    }

    /// <summary>A tool whose work blocks (disk scans, hashing) and therefore runs on the thread pool.</summary>
    protected static ITool Background(
        ToolDefinition definition,
        Func<JsonObject, ToolContext, CancellationToken, ToolResult> run,
        Func<JsonObject, ToolContext, ToolAssessment>? assess = null,
        Func<JsonObject, bool>? alreadyApplied = null)
    {
        Func<JsonObject, CancellationToken, Task<bool>>? applied = null;
        if (alreadyApplied is not null) applied = (args, ct) => Task.FromResult(alreadyApplied(args));
        return new DelegateTool(definition, (args, context, ct) => Task.Run(() => run(args, context, ct), ct), assess, applied);
    }

    protected static ToolResult From(ActionOutcome outcome)
        => outcome.Success ? ToolResult.Ok(outcome.Message, outcome.Verified) : ToolResult.Fail(outcome.Message);

    protected static JsonArray ToJsonArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            JsonNode node = JsonValue.Create(value)!;
            array.Add(node);
        }
        return array;
    }

    protected static JsonArray ToJsonArray(IEnumerable<JsonObject> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            JsonNode node = value;
            array.Add(node);
        }
        return array;
    }
}
