using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using Jarvis.AI;
using Jarvis.Core.Abstractions;
using Jarvis.Core.AI;
using Jarvis.Core.Models;
using Jarvis.Core.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jarvis.Tests;

/// <summary>Gemini client behaviour, verified against a fake HTTP transport. No network access is used.</summary>
public class GeminiClientTests
{
    private const string TestKey = "AIzaSyTESTKEY0123456789abcdefghijklmno12";

    private sealed class FakeTransport : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _responses = new Queue<Func<HttpResponseMessage>>();

        public List<(Uri Uri, string? ApiKey, string Body)> Requests { get; } = new List<(Uri Uri, string? ApiKey, string Body)>();

        public void Reply(HttpStatusCode status, string body)
            => _responses.Enqueue(() => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });

        public void Throw(Exception exception) => _responses.Enqueue(() => throw exception);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            string? key = null;
            if (request.Headers.TryGetValues("x-goog-api-key", out var values)) key = values.FirstOrDefault();
            Requests.Add((request.RequestUri!, key, body));
            if (_responses.Count == 0) throw new InvalidOperationException("No response was queued.");
            return _responses.Dequeue()();
        }
    }

    private readonly FakeTransport _transport = new FakeTransport();
    private readonly InMemorySecrets _secrets = new InMemorySecrets();
    private readonly InMemorySettingsStore _settings = new InMemorySettingsStore();

    private GeminiClient Client()
        => new GeminiClient(new HttpClient(_transport), _secrets, _settings, NullLogger<GeminiClient>.Instance);

    private static AiRequest Ask(string text)
    {
        var request = new AiRequest();
        request.Input.Add(new AiUserInput(text));
        return request;
    }

    private const string TextReply =
        "{\"id\":\"i1\",\"status\":\"completed\",\"steps\":[{\"type\":\"model_output\",\"content\":[{\"type\":\"text\",\"text\":\"Hello there.\"}]}]}";

    [Fact]
    public async Task Without_a_key_no_request_is_sent()
    {
        var client = Client();
        Assert.False(client.IsConfigured);

        var error = await Assert.ThrowsAsync<AiException>(() => client.GenerateAsync(Ask("hi"), CancellationToken.None));

        Assert.Equal(AiErrorKind.NoApiKey, error.Kind);
        Assert.True(error.IsConnectivity);
        Assert.Empty(_transport.Requests);
    }

    [Fact]
    public async Task The_key_travels_in_a_header_and_never_in_the_url_or_body()
    {
        _secrets.Set(SecretNames.GeminiApiKey, TestKey);
        _transport.Reply(HttpStatusCode.OK, TextReply);
        var client = Client();
        Assert.True(client.IsConfigured);

        var response = await client.GenerateAsync(Ask("hi"), CancellationToken.None);

        Assert.Equal("Hello there.", response.Text);
        var sent = Assert.Single(_transport.Requests);
        Assert.Equal(TestKey, sent.ApiKey);
        Assert.DoesNotContain(TestKey, sent.Uri.ToString());
        Assert.DoesNotContain(TestKey, sent.Body);
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/interactions", sent.Uri.ToString());
    }

    [Fact]
    public async Task The_configured_model_and_stateless_mode_are_sent()
    {
        _secrets.Set(SecretNames.GeminiApiKey, TestKey);
        var settings = new AppSettings { GeminiModel = "gemini-4-argon" };
        _settings.Save(settings);
        _transport.Reply(HttpStatusCode.OK, TextReply);

        await Client().GenerateAsync(Ask("hi"), CancellationToken.None);

        var body = JsonNode.Parse(_transport.Requests[0].Body)!.AsObject();
        Assert.Equal("gemini-4-argon", body["model"]!.GetValue<string>());
        Assert.False(body["store"]!.GetValue<bool>());
    }

    [Fact]
    public void The_default_model_matches_the_default_setting()
    {
        Assert.Equal(GeminiClient.DefaultModel, new AppSettings().GeminiModel);
    }

    [Fact]
    public void Request_body_carries_instruction_history_tools_and_limits()
    {
        var request = new AiRequest
        {
            SystemInstruction = "Be brief.",
            MaxOutputTokens = 256,
            UseWebSearch = true,
            Tools = new[]
            {
                new ToolDefinition("add_task", "Adds a task.", ToolCategory.Planner, ToolRisk.Routine).Str("title", "Title.", required: true)
            }
        };
        request.Input.Add(new AiUserInput("add a task"));
        request.Input.Add(new AiRawStep(JsonNode.Parse("{\"type\":\"function_call\",\"id\":\"c1\",\"name\":\"add_task\",\"arguments\":{\"title\":\"x\"}}")!));
        request.Input.Add(new AiFunctionResult("c1", "add_task", "{\"success\":true}"));
        request.Input.Add(new AiModelInput("Added."));

        var body = GeminiClient.BuildRequestBody(request, "gemini-4-hydrogen");

        Assert.Equal("Be brief.", body["system_instruction"]!.GetValue<string>());
        Assert.Equal(256, body["generation_config"]!["max_output_tokens"]!.GetValue<int>());

        var input = body["input"]!.AsArray();
        Assert.Equal(4, input.Count);
        Assert.Equal("user_input", input[0]!["type"]!.GetValue<string>());
        Assert.Equal("add a task", input[0]!["content"]![0]!["text"]!.GetValue<string>());
        Assert.Equal("function_call", input[1]!["type"]!.GetValue<string>());
        Assert.Equal("function_result", input[2]!["type"]!.GetValue<string>());
        Assert.Equal("c1", input[2]!["call_id"]!.GetValue<string>());
        Assert.Equal("add_task", input[2]!["name"]!.GetValue<string>());
        Assert.Equal("model_output", input[3]!["type"]!.GetValue<string>());

        var tools = body["tools"]!.AsArray();
        Assert.Equal(2, tools.Count);
        Assert.Equal("function", tools[0]!["type"]!.GetValue<string>());
        Assert.Equal("add_task", tools[0]!["name"]!.GetValue<string>());
        Assert.Equal("object", tools[0]!["parameters"]!["type"]!.GetValue<string>());
        Assert.Equal("google_search", tools[1]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void Request_body_omits_optional_sections_when_unused()
    {
        var body = GeminiClient.BuildRequestBody(Ask("hi"), "m");

        Assert.False(body.ContainsKey("tools"));
        Assert.False(body.ContainsKey("system_instruction"));
        Assert.False(body.ContainsKey("response_format"));
        Assert.False(body.ContainsKey("generation_config"));
    }

    [Fact]
    public void Images_are_sent_inline_as_base64()
    {
        var request = new AiRequest();
        request.Input.Add(new AiUserInput("what is this", new AiImage(new byte[] { 1, 2, 3 }, "image/png")));

        var content = GeminiClient.BuildRequestBody(request, "m")["input"]![0]!["content"]!.AsArray();

        Assert.Equal(2, content.Count);
        Assert.Equal("image", content[1]!["type"]!.GetValue<string>());
        Assert.Equal("AQID", content[1]!["data"]!.GetValue<string>());
        Assert.Equal("image/png", content[1]!["mime_type"]!.GetValue<string>());
    }

    [Fact]
    public void Function_calls_are_parsed_and_kept_verbatim_for_the_next_turn()
    {
        var json = "{\"status\":\"requires_action\",\"steps\":["
                   + "{\"type\":\"thought\",\"signature\":\"abc\"},"
                   + "{\"type\":\"function_call\",\"id\":\"call_1\",\"name\":\"launch_app\",\"arguments\":{\"name\":\"notepad\"}}]}";

        var response = GeminiClient.ParseResponse(json);

        var call = Assert.Single(response.ToolCalls);
        Assert.Equal("call_1", call.CallId);
        Assert.Equal("launch_app", call.Name);
        Assert.Equal("notepad", call.Arguments["name"]!.GetValue<string>());
        Assert.Equal(2, response.RawSteps.Count);
        Assert.Equal("abc", response.RawSteps[0]["signature"]!.GetValue<string>());
        Assert.Equal("", response.Text);
    }

    [Fact]
    public void Text_parts_are_joined_and_citations_deduplicated()
    {
        var json = "{\"status\":\"completed\",\"steps\":[{\"type\":\"model_output\",\"content\":["
                   + "{\"type\":\"text\",\"text\":\"Part one. \",\"annotations\":[{\"type\":\"url_citation\",\"url\":\"https://a.example\",\"title\":\"A\"}]},"
                   + "{\"type\":\"text\",\"text\":\"Part two.\",\"annotations\":[{\"type\":\"url_citation\",\"url\":\"https://a.example\",\"title\":\"A\"},"
                   + "{\"type\":\"url_citation\",\"url\":\"https://b.example\"}]}]}]}";

        var response = GeminiClient.ParseResponse(json);

        Assert.Equal("Part one. Part two.", response.Text);
        Assert.Equal(2, response.Citations.Count);
        Assert.Equal("A", response.Citations[0].Title);
        Assert.Equal("https://b.example", response.Citations[1].Title);
    }

    [Theory]
    [InlineData("not json at all", AiErrorKind.Unknown)]
    [InlineData("[1,2,3]", AiErrorKind.Unknown)]
    [InlineData("{\"status\":\"completed\",\"steps\":[]}", AiErrorKind.Blocked)]
    [InlineData("{\"status\":\"failed\",\"error\":{\"message\":\"boom\"}}", AiErrorKind.Server)]
    [InlineData("{\"status\":\"cancelled\"}", AiErrorKind.Unknown)]
    public void Unusable_responses_become_typed_errors(string json, AiErrorKind kind)
    {
        var error = Assert.Throws<AiException>(() => GeminiClient.ParseResponse(json));
        Assert.Equal(kind, error.Kind);
    }

    [Fact]
    public void A_truncated_response_without_text_is_explained_not_thrown()
    {
        var response = GeminiClient.ParseResponse("{\"status\":\"incomplete\",\"steps\":[]}");
        Assert.Contains("cut short", response.Text);
    }

    [Theory]
    [InlineData(401, AiErrorKind.Authentication)]
    [InlineData(403, AiErrorKind.Authentication)]
    [InlineData(404, AiErrorKind.Model)]
    [InlineData(429, AiErrorKind.Quota)]
    [InlineData(400, AiErrorKind.BadRequest)]
    [InlineData(500, AiErrorKind.Server)]
    [InlineData(503, AiErrorKind.Server)]
    [InlineData(418, AiErrorKind.Unknown)]
    public void Http_failures_are_classified(int status, AiErrorKind kind)
    {
        var error = GeminiClient.MapError(status, "{\"error\":{\"message\":\"something went wrong\"}}", "gemini-4-hydrogen");
        Assert.Equal(kind, error.Kind);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    [Fact]
    public void An_invalid_key_reported_as_400_is_an_authentication_error()
    {
        var error = GeminiClient.MapError(400, "{\"error\":{\"message\":\"API key not valid. Please pass a valid API key.\"}}", "m");
        Assert.Equal(AiErrorKind.Authentication, error.Kind);
    }

    [Fact]
    public void Error_messages_never_echo_a_key()
    {
        var body = "{\"error\":{\"message\":\"Quota exceeded for key " + TestKey + "\"}}";
        var error = GeminiClient.MapError(429, body, "m");
        Assert.DoesNotContain(TestKey, error.Message);
    }

    [Fact]
    public void Non_json_error_pages_do_not_crash_the_mapping()
    {
        var error = GeminiClient.MapError(502, "<html>Bad gateway</html>", "m");
        Assert.Equal(AiErrorKind.Server, error.Kind);
    }

    [Fact]
    public async Task Network_failures_are_reported_as_connectivity_problems()
    {
        _secrets.Set(SecretNames.GeminiApiKey, TestKey);
        _transport.Throw(new HttpRequestException("no route to host"));

        var error = await Assert.ThrowsAsync<AiException>(() => Client().GenerateAsync(Ask("hi"), CancellationToken.None));

        Assert.Equal(AiErrorKind.Network, error.Kind);
        Assert.True(error.IsConnectivity);
    }

    [Fact]
    public async Task A_timeout_is_distinguished_from_user_cancellation()
    {
        _secrets.Set(SecretNames.GeminiApiKey, TestKey);
        _transport.Throw(new TaskCanceledException("timed out"));

        var error = await Assert.ThrowsAsync<AiException>(() => Client().GenerateAsync(Ask("hi"), CancellationToken.None));

        Assert.Equal(AiErrorKind.Timeout, error.Kind);
    }

    [Fact]
    public async Task A_rejected_key_is_not_retried()
    {
        _secrets.Set(SecretNames.GeminiApiKey, TestKey);
        _transport.Reply(HttpStatusCode.Unauthorized, "{\"error\":{\"message\":\"bad key\"}}");

        var error = await Assert.ThrowsAsync<AiException>(() => Client().GenerateAsync(Ask("hi"), CancellationToken.None));

        Assert.Equal(AiErrorKind.Authentication, error.Kind);
        Assert.Single(_transport.Requests);
    }

    [Fact]
    public async Task A_server_error_is_retried_once()
    {
        _secrets.Set(SecretNames.GeminiApiKey, TestKey);
        _transport.Reply(HttpStatusCode.ServiceUnavailable, "{}");
        _transport.Reply(HttpStatusCode.OK, TextReply);

        var response = await Client().GenerateAsync(Ask("hi"), CancellationToken.None);

        Assert.Equal("Hello there.", response.Text);
        Assert.Equal(2, _transport.Requests.Count);
    }
}

public class PromptBuilderTests
{
    private static readonly DateTime Now = new DateTime(2026, 11, 14, 10, 0, 0);

    [Fact]
    public void The_prompt_contains_time_mode_user_and_memory()
    {
        var prompt = PromptBuilder.Build(new PromptContext
        {
            Mode = JarvisMode.Development,
            UserName = "Sharvesh",
            NowLocal = Now,
            Memories = new[]
            {
                new MemoryItem { Category = "preference", Content = "Prefers dark themes", Source = MemorySource.UserProvided },
                new MemoryItem { Category = "project", Content = "Probably uses Rust", Source = MemorySource.Inferred }
            },
            PlannerDigest = "1 overdue task",
            ConversationSummary = "Talked about the thesis."
        });

        Assert.Contains("Saturday, 2026-11-14 10:00", prompt);
        Assert.Contains("The user's name is Sharvesh.", prompt);
        Assert.Contains("Active mode: Development", prompt);
        Assert.Contains("stated] Prefers dark themes", prompt);
        Assert.Contains("inferred] Probably uses Rust", prompt);
        Assert.Contains("1 overdue task", prompt);
        Assert.Contains("Talked about the thesis.", prompt);
    }

    [Fact]
    public void Each_mode_produces_a_different_prompt()
    {
        var prompts = Enum.GetValues<JarvisMode>()
            .Select(mode => PromptBuilder.Build(new PromptContext { Mode = mode, NowLocal = Now }))
            .Distinct()
            .Count();
        Assert.Equal(Enum.GetValues<JarvisMode>().Length, prompts);
    }

    [Fact]
    public void Empty_sections_are_left_out()
    {
        var prompt = PromptBuilder.Build(new PromptContext { NowLocal = Now });
        Assert.DoesNotContain("Relevant long-term memory", prompt);
        Assert.DoesNotContain("Background tasks:", prompt);
        Assert.DoesNotContain("The user's name is", prompt);
    }

    private static ChatMessage Message(MessageRole role, string content) => new ChatMessage { Role = role, Content = content };

    [Fact]
    public void History_starts_with_a_user_turn_and_skips_notes()
    {
        var messages = new List<ChatMessage>
        {
            Message(MessageRole.Assistant, "Good morning."),
            Message(MessageRole.User, "hello"),
            Message(MessageRole.Note, "Gemini is offline."),
            Message(MessageRole.Assistant, "Hi."),
            Message(MessageRole.User, "   ")
        };

        var history = ContextBuilder.BuildHistory(messages);

        Assert.Equal(2, history.Count);
        Assert.Equal("hello", Assert.IsType<AiUserInput>(history[0]).Text);
        Assert.Equal("Hi.", Assert.IsType<AiModelInput>(history[1]).Text);
    }

    [Fact]
    public void History_is_limited_by_message_count_and_size()
    {
        var messages = new List<ChatMessage>();
        for (var i = 0; i < 30; i++)
        {
            messages.Add(Message(MessageRole.User, "question " + i));
            messages.Add(Message(MessageRole.Assistant, "answer " + i));
        }

        var byCount = ContextBuilder.BuildHistory(messages, maxMessages: 10);
        Assert.Equal(10, byCount.Count);
        Assert.Equal("question 25", Assert.IsType<AiUserInput>(byCount[0]).Text);
        Assert.Equal("answer 29", Assert.IsType<AiModelInput>(byCount[9]).Text);

        var bySize = ContextBuilder.BuildHistory(messages, maxMessages: 40, maxCharacters: 120);
        Assert.True(bySize.Count < 10);
        Assert.IsType<AiUserInput>(bySize[0]);
    }
}
