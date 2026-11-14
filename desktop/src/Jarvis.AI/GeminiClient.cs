using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jarvis.AI;

/// <summary>Gemini API client using the officially documented REST
/// generateContent endpoint (v1beta) with function calling.
/// Docs: https://ai.google.dev/gemini-api/docs/get-started
///       https://ai.google.dev/gemini-api/docs/function-calling
/// The model name is configurable; default is a current stable Flash model.
/// No API key is ever logged. All calls honour timeout + cancellation.</summary>
public sealed class GeminiClient(
    HttpClient http,
    string apiKey,
    string model,
    ILogger<GeminiClient>? logger = null)
{
    private readonly ILogger<GeminiClient> _log = logger ?? NullLogger<GeminiClient>.Instance;
    public string Model { get; } = model;

    public sealed record GeminiTurn(string Role, string Text);
    public sealed record FunctionCallRequest(string Name, JsonElement Arguments);
    public sealed record GeminiReply(string? Text, FunctionCallRequest? FunctionCall, string? Error);

    public async Task<GeminiReply> SendAsync(
        IReadOnlyList<GeminiTurn> history,
        string systemInstruction,
        string userText,
        string? functionDeclarationsJson,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return new GeminiReply(null, null, "No Gemini API key configured. Add one in Settings.");

        var contents = new List<object>();
        foreach (var turn in history)
        {
            contents.Add(new
            {
                role = turn.Role == "user" ? "user" : "model",
                parts = new[] { new { text = turn.Text } }
            });
        }
        contents.Add(new
        {
            role = "user",
            parts = new[] { new { text = userText } }
        });

        var body = new Dictionary<string, object>
        {
            ["contents"] = contents,
            ["systemInstruction"] = new { parts = new[] { new { text = systemInstruction } } },
            ["generationConfig"] = new { temperature = 0.4, maxOutputTokens = 2048 }
        };
        if (!string.IsNullOrWhiteSpace(functionDeclarationsJson))
        {
            body["tools"] = new object[]
            {
                new { functionDeclarations = JsonDocument.Parse(functionDeclarationsJson).RootElement }
            };
        }

        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{Model}:generateContent?key={Uri.EscapeDataString(apiKey)}";
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            using var response = await http.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
            var json = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _log.LogWarning("Gemini HTTP {Status}", (int)response.StatusCode);
                return new GeminiReply(null, null, $"Gemini request failed (HTTP {(int)response.StatusCode}). Check your API key and quota.");
            }
            return ParseReply(json);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new GeminiReply(null, null, "Gemini request timed out. Please try again.");
        }
        catch (HttpRequestException ex)
        {
            _log.LogWarning(ex, "Gemini network failure");
            return new GeminiReply(null, null, "Network unavailable — Gemini is offline. Local features still work.");
        }
    }

    public async Task<GeminiReply> SendImageAsync(
        string systemInstruction,
        string userText,
        byte[] imageBytes,
        string mimeType,
        CancellationToken ct = default)
    {
        // On-demand screenshot understanding: image is sent only for this one request.
        var body = new Dictionary<string, object>
        {
            ["contents"] = new object[]
            {
                new
                {
                    role = "user",
                    parts = new object[]
                    {
                        new { text = userText },
                        new { inlineData = new { mimeType, data = Convert.ToBase64String(imageBytes) } }
                    }
                }
            },
            ["systemInstruction"] = new { parts = new[] { new { text = systemInstruction } } },
            ["generationConfig"] = new { temperature = 0.3, maxOutputTokens = 2048 }
        };
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{Model}:generateContent?key={Uri.EscapeDataString(apiKey)}";
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(90));
        try
        {
            using var response = await http.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
            var json = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new GeminiReply(null, null, $"Screenshot analysis failed (HTTP {(int)response.StatusCode}).");
            return ParseReply(json);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            return new GeminiReply(null, null, "Screenshot analysis unavailable (network/timeout).");
        }
    }

    private static GeminiReply ParseReply(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0)
                return new GeminiReply(null, null, "Gemini returned no candidates.");
            var content = candidates[0].GetProperty("content");
            if (!content.TryGetProperty("parts", out var parts)) return new GeminiReply("", null, null);
            var text = new StringBuilder();
            foreach (var part in parts.EnumerateArray())
            {
                if (part.TryGetProperty("text", out var t)) text.Append(t.GetString());
                if (part.TryGetProperty("functionCall", out var fc))
                {
                    var name = fc.GetProperty("name").GetString() ?? "";
                    var args = fc.TryGetProperty("args", out var a) ? a.Clone() : JsonDocument.Parse("{}").RootElement.Clone();
                    return new GeminiReply(text.ToString(), new FunctionCallRequest(name, args), null);
                }
            }
            return new GeminiReply(text.ToString(), null, null);
        }
        catch (Exception ex)
        {
            return new GeminiReply(null, null, $"Could not parse Gemini response: {ex.Message}");
        }
    }

    /// <summary>Connectivity probe used for the online/offline indicator.</summary>
    public async Task<bool> IsOnlineAsync(CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            using var response = await http.GetAsync("https://generativelanguage.googleapis.com/", cts.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode || (int)response.StatusCode < 500;
        }
        catch { return false; }
    }
}
