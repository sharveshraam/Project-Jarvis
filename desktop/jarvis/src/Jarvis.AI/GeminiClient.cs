using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jarvis.Core.Abstractions;
using Jarvis.Core.AI;
using Jarvis.Core.Security;
using Microsoft.Extensions.Logging;

namespace Jarvis.AI;

/// <summary>
/// Google Gemini client over the Interactions REST API (the interface Google recommends for new projects).
/// Runs stateless (<c>store=false</c>): conversation history is managed locally and nothing is retained
/// server-side between requests. Request and response bodies are never logged.
/// </summary>
public sealed class GeminiClient : IAiClient
{
    public const string BaseUrl = "https://generativelanguage.googleapis.com";
    /// <summary>Pins the documented response shape (steps / model_output / function_call).</summary>
    public const string ApiRevision = "2026-05-20";
    public const string DefaultModel = "gemini-4-hydrogen";

    private readonly HttpClient _http;
    private readonly ISecretStore _secrets;
    private readonly ISettingsStore _settings;
    private readonly ILogger<GeminiClient> _log;

    public GeminiClient(HttpClient http, ISecretStore secrets, ISettingsStore settings, ILogger<GeminiClient> log)
    {
        _http = http;
        _secrets = secrets;
        _settings = settings;
        _log = log;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_secrets.Get(SecretNames.GeminiApiKey));

    public async Task<AiResponse> GenerateAsync(AiRequest request, CancellationToken ct)
    {
        var key = _secrets.Get(SecretNames.GeminiApiKey);
        if (string.IsNullOrWhiteSpace(key))
            throw new AiException(AiErrorKind.NoApiKey, "No Gemini API key is configured. Add one under Settings › Gemini.");

        var settings = _settings.Current;
        var model = string.IsNullOrWhiteSpace(settings.GeminiModel) ? DefaultModel : settings.GeminiModel.Trim();
        var version = string.IsNullOrWhiteSpace(settings.GeminiApiVersion) ? "v1beta" : settings.GeminiApiVersion.Trim();
        var url = $"{BaseUrl}/{version}/interactions";
        var payload = BuildRequestBody(request, model).ToJsonString();

        for (var attempt = 0; ; attempt++)
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, url);
            message.Headers.TryAddWithoutValidation("x-goog-api-key", key.Trim());
            message.Headers.TryAddWithoutValidation("Api-Revision", ApiRevision);
            message.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            var started = DateTime.UtcNow;
            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(message, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new AiException(AiErrorKind.Timeout, "Gemini did not respond in time.");
            }
            catch (HttpRequestException ex)
            {
                throw new AiException(AiErrorKind.Network, "Gemini could not be reached. Check the internet connection.", ex);
            }

            AiException error;
            using (response)
            {
                var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var status = (int)response.StatusCode;
                _log.LogInformation("Gemini {Model} responded {Status} in {Elapsed} ms.", model, status,
                    (int)(DateTime.UtcNow - started).TotalMilliseconds);

                if (response.IsSuccessStatusCode) return ParseResponse(text);
                error = MapError(status, text, model);
            }

            // One retry for transient server-side failures; everything else is reported immediately.
            if (error.Kind != AiErrorKind.Server || attempt > 0) throw error;
            await Task.Delay(TimeSpan.FromMilliseconds(1200), ct).ConfigureAwait(false);
        }
    }

    /// <summary>Makes a minimal request to verify the stored key and the configured model.</summary>
    public async Task<string> TestAsync(CancellationToken ct)
    {
        var request = new AiRequest { MaxOutputTokens = 64 };
        request.Input.Add(new AiUserInput("Reply with the single word: ONLINE"));
        var response = await GenerateAsync(request, ct).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(response.Text) ? "Connected." : response.Text.Trim();
    }

    public static JsonObject BuildRequestBody(AiRequest request, string model)
    {
        var body = new JsonObject { ["model"] = model, ["store"] = false };
        if (!string.IsNullOrWhiteSpace(request.SystemInstruction)) body["system_instruction"] = request.SystemInstruction;

        var input = new JsonArray();
        foreach (var item in request.Input)
        {
            JsonNode step = ToStep(item);
            input.Add(step);
        }
        body["input"] = input;

        var tools = new JsonArray();
        foreach (var tool in request.Tools)
        {
            JsonNode declaration = new JsonObject
            {
                ["type"] = "function",
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["parameters"] = tool.ToJsonSchema()
            };
            tools.Add(declaration);
        }
        if (request.UseWebSearch)
        {
            JsonNode search = new JsonObject { ["type"] = "google_search" };
            tools.Add(search);
        }
        if (tools.Count > 0) body["tools"] = tools;

        if (request.ResponseSchema is not null)
        {
            body["response_format"] = new JsonObject
            {
                ["type"] = "text",
                ["mime_type"] = "application/json",
                ["schema"] = request.ResponseSchema.DeepClone()
            };
        }
        if (request.MaxOutputTokens.HasValue)
            body["generation_config"] = new JsonObject { ["max_output_tokens"] = request.MaxOutputTokens.Value };
        return body;
    }

    private static JsonNode ToStep(AiInput item)
    {
        if (item is AiUserInput user)
        {
            var content = new JsonArray();
            JsonNode textPart = TextPart(user.Text);
            content.Add(textPart);
            if (user.Image is not null)
            {
                JsonNode imagePart = ImagePart(user.Image);
                content.Add(imagePart);
            }
            return new JsonObject { ["type"] = "user_input", ["content"] = content };
        }
        if (item is AiModelInput modelTurn)
        {
            var content = new JsonArray();
            JsonNode textPart = TextPart(modelTurn.Text);
            content.Add(textPart);
            return new JsonObject { ["type"] = "model_output", ["content"] = content };
        }
        if (item is AiRawStep raw)
        {
            return raw.Step.DeepClone();
        }
        if (item is AiFunctionResult result)
        {
            var parts = new JsonArray();
            JsonNode textPart = TextPart(result.ResultJson);
            parts.Add(textPart);
            if (result.Image is not null)
            {
                JsonNode imagePart = ImagePart(result.Image);
                parts.Add(imagePart);
            }
            return new JsonObject
            {
                ["type"] = "function_result",
                ["name"] = result.Name,
                ["call_id"] = result.CallId,
                ["result"] = parts
            };
        }
        throw new ArgumentException($"Unsupported input item {item.GetType().Name}.");
    }

    private static JsonObject TextPart(string text) => new JsonObject { ["type"] = "text", ["text"] = text };

    private static JsonObject ImagePart(AiImage image) => new JsonObject
    {
        ["type"] = "image",
        ["data"] = Convert.ToBase64String(image.Data),
        ["mime_type"] = image.MimeType
    };

    public static AiResponse ParseResponse(string json)
    {
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            root = null;
        }
        if (root is null) throw new AiException(AiErrorKind.Unknown, "Gemini returned an unreadable response.");

        var status = Str(root["status"]) ?? "completed";
        var text = new StringBuilder();
        var calls = new List<AiToolCall>();
        var rawSteps = new List<JsonNode>();
        var citations = new List<AiCitation>();

        if (root["steps"] is JsonArray steps)
        {
            foreach (var node in steps)
            {
                if (node is not JsonObject step) continue;
                rawSteps.Add(step.DeepClone());
                var type = Str(step["type"]);
                if (type == "function_call")
                {
                    var arguments = step["arguments"] is JsonObject a ? (JsonObject)a.DeepClone() : new JsonObject();
                    calls.Add(new AiToolCall(Str(step["id"]) ?? Str(step["call_id"]) ?? "", Str(step["name"]) ?? "", arguments));
                }
                else if (type == "model_output" && step["content"] is JsonArray content)
                {
                    foreach (var partNode in content)
                    {
                        if (partNode is not JsonObject part || Str(part["type"]) != "text") continue;
                        text.Append(Str(part["text"]) ?? "");
                        if (part["annotations"] is not JsonArray annotations) continue;
                        foreach (var annotationNode in annotations)
                        {
                            if (annotationNode is not JsonObject annotation || Str(annotation["type"]) != "url_citation") continue;
                            var url = Str(annotation["url"]);
                            if (string.IsNullOrWhiteSpace(url) || citations.Any(c => c.Url == url)) continue;
                            citations.Add(new AiCitation(Str(annotation["title"]) ?? url, url));
                        }
                    }
                }
            }
        }

        if (status == "failed")
        {
            var detail = root["error"] is JsonObject errorObject ? Str(errorObject["message"]) : null;
            throw new AiException(AiErrorKind.Server,
                "Gemini reported that the request failed." + (detail is null ? "" : " " + SecretDetector.Redact(detail)));
        }
        if (status == "cancelled") throw new AiException(AiErrorKind.Unknown, "The Gemini request was cancelled by the service.");
        if (calls.Count == 0 && text.Length == 0)
        {
            if (status == "incomplete") text.Append("The response was cut short before any text was produced.");
            else throw new AiException(AiErrorKind.Blocked, "Gemini returned no content for this request.");
        }

        return new AiResponse
        {
            Text = text.ToString(),
            ToolCalls = calls,
            RawSteps = rawSteps,
            Citations = citations,
            Status = status
        };
    }

    public static AiException MapError(int status, string body, string model)
    {
        string? message = null;
        try
        {
            if (JsonNode.Parse(body) is JsonObject root && root["error"] is JsonObject errorObject)
                message = Str(errorObject["message"]);
        }
        catch (JsonException)
        {
            // Non-JSON error bodies (proxies, captive portals) carry no usable detail.
        }
        var detail = string.IsNullOrWhiteSpace(message) ? "" : " " + SecretDetector.Redact(message);
        var mentionsKey = message is not null && message.Contains("API key", StringComparison.OrdinalIgnoreCase);

        if (status == 401 || status == 403 || (status == 400 && mentionsKey))
            return new AiException(AiErrorKind.Authentication, "The Gemini API key was rejected. Replace it under Settings › Gemini.");
        if (status == 404)
            return new AiException(AiErrorKind.Model, $"The model '{model}' is not available for this key. Choose another model under Settings › Gemini.");
        if (status == 429)
            return new AiException(AiErrorKind.Quota, "The Gemini quota or rate limit has been reached. Try again shortly." + detail);
        if (status == 400)
            return new AiException(AiErrorKind.BadRequest, "Gemini rejected the request." + detail);
        if (status >= 500)
            return new AiException(AiErrorKind.Server, "Gemini is temporarily unavailable." + detail);
        return new AiException(AiErrorKind.Unknown, $"Gemini returned HTTP {status}." + detail);
    }

    private static string? Str(JsonNode? node)
        => node is not null && node.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : null;
}
