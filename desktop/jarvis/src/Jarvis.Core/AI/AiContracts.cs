using System.Text.Json.Nodes;
using Jarvis.Core.Tools;

namespace Jarvis.Core.AI;

public enum AiErrorKind { NoApiKey, Network, Timeout, Authentication, Quota, Model, BadRequest, Blocked, Server, Unknown }

public sealed class AiException : Exception
{
    public AiException(AiErrorKind kind, string message, Exception? inner = null) : base(message, inner) => Kind = kind;
    public AiErrorKind Kind { get; }
    /// <summary>True when the failure means "Gemini is unreachable", so local/offline handling should take over.</summary>
    public bool IsConnectivity => Kind is AiErrorKind.Network or AiErrorKind.Timeout or AiErrorKind.NoApiKey;
}

public sealed record AiImage(byte[] Data, string MimeType);

/// <summary>One item of client-managed (stateless) conversation history.</summary>
public abstract record AiInput;
public sealed record AiUserInput(string Text, AiImage? Image = null) : AiInput;
public sealed record AiModelInput(string Text) : AiInput;
/// <summary>A model-generated step (thought, function_call, ...) echoed back exactly as received.</summary>
public sealed record AiRawStep(JsonNode Step) : AiInput;
public sealed record AiFunctionResult(string CallId, string Name, string ResultJson, AiImage? Image = null) : AiInput;

public sealed record AiToolCall(string CallId, string Name, JsonObject Arguments);
public sealed record AiCitation(string Title, string Url);

public sealed class AiRequest
{
    public string? SystemInstruction { get; set; }
    public List<AiInput> Input { get; } = new();
    public IReadOnlyList<ToolDefinition> Tools { get; set; } = Array.Empty<ToolDefinition>();
    /// <summary>Enables Gemini's server-side Google Search grounding for this request.</summary>
    public bool UseWebSearch { get; set; }
    /// <summary>JSON Schema; when set the reply is constrained to matching JSON.</summary>
    public JsonObject? ResponseSchema { get; set; }
    public int? MaxOutputTokens { get; set; }
}

public sealed class AiResponse
{
    public string Text { get; init; } = "";
    public IReadOnlyList<AiToolCall> ToolCalls { get; init; } = Array.Empty<AiToolCall>();
    public IReadOnlyList<JsonNode> RawSteps { get; init; } = Array.Empty<JsonNode>();
    public IReadOnlyList<AiCitation> Citations { get; init; } = Array.Empty<AiCitation>();
    public string Status { get; init; } = "completed";
}

public interface IAiClient
{
    /// <summary>True when an API key is stored. Says nothing about network reachability.</summary>
    bool IsConfigured { get; }
    Task<AiResponse> GenerateAsync(AiRequest request, CancellationToken ct);
}
