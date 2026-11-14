using System.Text.Json.Nodes;

namespace Jarvis.Core.Tools;

/// <summary>
/// Safe: read-only, runs without a prompt.
/// Routine: reversible everyday action; runs without a prompt when trusted by the user.
/// Consequential: destructive, bulk, external or otherwise hard to undo; always asks.
/// </summary>
public enum ToolRisk { Safe, Routine, Consequential }

public enum ToolCategory
{
    General, Apps, System, Files, Search, Clipboard, Screen, Web, Development, Focus, Planner, Memory, Agent
}

public sealed class ToolParam
{
    public string Name { get; init; } = "";
    /// <summary>string | integer | number | boolean | array (of strings)</summary>
    public string Type { get; init; } = "string";
    public string Description { get; init; } = "";
    public bool Required { get; init; }
    public IReadOnlyList<string>? EnumValues { get; init; }
    public double? Min { get; init; }
    public double? Max { get; init; }
}

/// <summary>Declares a tool: what Gemini is told about it and how JARVIS validates calls to it.</summary>
public sealed class ToolDefinition
{
    private readonly List<ToolParam> _params = new();

    public ToolDefinition(string name, string description, ToolCategory category, ToolRisk defaultRisk)
    {
        Name = name;
        Description = description;
        Category = category;
        DefaultRisk = defaultRisk;
    }

    public string Name { get; }
    public string Description { get; }
    public ToolCategory Category { get; }
    public ToolRisk DefaultRisk { get; }
    public IReadOnlyList<ToolParam> Parameters => _params;
    /// <summary>Safe to retry automatically after a failure.</summary>
    public bool Idempotent { get; private set; }
    public TimeSpan Timeout { get; private set; } = TimeSpan.FromSeconds(60);

    public ToolDefinition Str(string name, string description, bool required = false)
        => Add(new ToolParam { Name = name, Type = "string", Description = description, Required = required });

    public ToolDefinition Enum(string name, string description, bool required, params string[] values)
        => Add(new ToolParam { Name = name, Type = "string", Description = description, Required = required, EnumValues = values });

    public ToolDefinition Int(string name, string description, bool required = false, double? min = null, double? max = null)
        => Add(new ToolParam { Name = name, Type = "integer", Description = description, Required = required, Min = min, Max = max });

    public ToolDefinition Bool(string name, string description, bool required = false)
        => Add(new ToolParam { Name = name, Type = "boolean", Description = description, Required = required });

    public ToolDefinition StrArray(string name, string description, bool required = false)
        => Add(new ToolParam { Name = name, Type = "array", Description = description, Required = required });

    public ToolDefinition Retryable() { Idempotent = true; return this; }
    public ToolDefinition WithTimeout(TimeSpan timeout) { Timeout = timeout; return this; }

    private ToolDefinition Add(ToolParam p) { _params.Add(p); return this; }

    /// <summary>JSON Schema for the tool's arguments, as sent to Gemini.</summary>
    public JsonObject ToJsonSchema()
    {
        var properties = new JsonObject();
        var requiredNames = new JsonArray();
        foreach (var p in _params)
        {
            var prop = new JsonObject { ["type"] = p.Type, ["description"] = p.Description };
            if (p.Type == "array") prop["items"] = new JsonObject { ["type"] = "string" };
            if (p.EnumValues is { Count: > 0 })
            {
                var values = new JsonArray();
                foreach (var v in p.EnumValues) values.Add(v);
                prop["enum"] = values;
            }
            properties[p.Name] = prop;
            if (p.Required) requiredNames.Add(p.Name);
        }
        return new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = requiredNames };
    }
}
