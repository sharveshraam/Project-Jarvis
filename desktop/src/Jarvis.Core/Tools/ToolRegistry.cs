using System.Text.Json;

namespace Jarvis.Core.Tools;

/// <summary>Risk tier for a tool. Trusted = may run without per-call confirmation
/// when the user has approved it in Settings. ApprovalRequired = always confirm.
/// Destructive = confirm + explicit preview.</summary>
public enum RiskTier { Trusted, ApprovalRequired, Destructive }

public sealed record ToolDefinition(
    string Name,
    string Description,
    RiskTier Risk,
    string ParametersSchemaJson,
    Func<JsonElement, CancellationToken, Task<string>>? Handler = null);

public sealed record ToolResult(bool Success, string Output, string? Error = null);

/// <summary>Central registry. The AI model can only request tools by name;
/// execution always goes through <see cref="ExecuteAsync"/> which validates
/// name, risk and trusted-action policy. There is no shell/PowerShell tool.</summary>
public sealed class ToolRegistry
{
    private readonly Dictionary<string, ToolDefinition> _tools = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _trusted;

    public ToolRegistry(IEnumerable<string> trustedActions)
    {
        _trusted = new HashSet<string>(trustedActions, StringComparer.OrdinalIgnoreCase);
    }

    public void Register(ToolDefinition tool) => _tools[tool.Name] = tool;
    public IReadOnlyDictionary<string, ToolDefinition> Tools => _tools;
    public bool IsTrusted(string name) => _trusted.Contains(name);

    /// <summary>Returns true if the tool may execute without asking the user.</summary>
    public bool MayExecuteWithoutConfirmation(string name)
    {
        if (!_tools.TryGetValue(name, out var def)) return false;
        return def.Risk == RiskTier.Trusted && _trusted.Contains(name);
    }

    public async Task<ToolResult> ExecuteAsync(string name, JsonElement args, CancellationToken ct)
    {
        if (!_tools.TryGetValue(name, out var def))
            return new ToolResult(false, "", $"Unknown tool: {name}");
        if (def.Handler is null)
            return new ToolResult(false, "", $"Tool {name} has no handler registered.");
        try
        {
            var output = await def.Handler(args, ct).ConfigureAwait(false);
            return new ToolResult(true, output);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new ToolResult(false, "", $"{name} failed: {ex.Message}");
        }
    }

    /// <summary>Gemini functionDeclarations payload (JSON array string).</summary>
    public string ToGeminiFunctionDeclarationsJson()
    {
        var list = _tools.Values.Select(t => new
        {
            name = t.Name,
            description = t.Description,
            parameters = JsonDocument.Parse(t.ParametersSchemaJson).RootElement
        }).ToList();
        return JsonSerializer.Serialize(list);
    }
}
