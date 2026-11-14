using System.Text;
using Jarvis.AI;
using Jarvis.Core.Abstractions;
using Jarvis.Core.AI;
using Jarvis.Core.Models;
using Jarvis.Core.Modes;
using Jarvis.Core.Planning;
using Jarvis.Core.Text;
using Jarvis.Core.Tools;
using Microsoft.Extensions.Logging;

namespace Jarvis.Agent;

public enum GeminiState { NotConfigured, Ready, Offline, Error }

public sealed record GeminiStatus(GeminiState State, string Detail)
{
    public string Display => State switch
    {
        GeminiState.Ready => "Gemini ready",
        GeminiState.NotConfigured => "No API key — local mode",
        GeminiState.Offline => "Offline — local mode",
        _ => string.IsNullOrWhiteSpace(Detail) ? "Gemini error" : Detail
    };
}

public sealed class AssistantReply
{
    public string Text { get; init; } = "";
    /// <summary>The reply was produced locally because Gemini was unavailable.</summary>
    public bool Local { get; init; }
    public bool Failed { get; init; }
    public bool Cancelled { get; init; }
    public IReadOnlyList<AiCitation> Citations { get; init; } = Array.Empty<AiCitation>();
}

/// <summary>
/// The conversation loop: persists the transcript, assembles context (mode, memory, plans, summary), runs the
/// Gemini ↔ tool cycle, and falls back to the local command interpreter whenever Gemini cannot be used.
/// </summary>
public sealed class AssistantService
{
    public const int MaxToolRounds = 8;

    private readonly IAiClient _ai;
    private readonly ToolExecutor _executor;
    private readonly IConversationStore _conversations;
    private readonly IMemoryStore _memory;
    private readonly IPlannerStore _planner;
    private readonly TaskEngine _tasks;
    private readonly ModeService _modes;
    private readonly ISettingsStore _settings;
    private readonly IConnectivity _connectivity;
    private readonly OfflineAssistant _offline;
    private readonly ConversationSummarizer _summarizer;
    private readonly IClock _clock;
    private readonly ILogger<AssistantService> _log;
    private GeminiStatus _gemini;

    public AssistantService(IAiClient ai, ToolExecutor executor, IConversationStore conversations, IMemoryStore memory, IPlannerStore planner,
        TaskEngine tasks, ModeService modes, ISettingsStore settings, IConnectivity connectivity, OfflineAssistant offline,
        ConversationSummarizer summarizer, IClock clock, ILogger<AssistantService> log)
    {
        _ai = ai;
        _executor = executor;
        _conversations = conversations;
        _memory = memory;
        _planner = planner;
        _tasks = tasks;
        _modes = modes;
        _settings = settings;
        _connectivity = connectivity;
        _offline = offline;
        _summarizer = summarizer;
        _clock = clock;
        _log = log;
        _gemini = ComputeIdleStatus();
        _connectivity.Changed += RefreshGeminiStatus;
    }

    public long? ConversationId { get; private set; }

    /// <summary>Every message added to the current conversation (user, assistant and notes).</summary>
    public event Action<ChatMessage>? MessageAdded;
    /// <summary>Short activity text ("Thinking…", "Opening VS Code…"); empty when idle.</summary>
    public event Action<string>? ActivityChanged;
    public event Action<GeminiStatus>? GeminiStatusChanged;

    public GeminiStatus Gemini => _gemini;

    private GeminiStatus ComputeIdleStatus()
    {
        if (!_ai.IsConfigured) return new GeminiStatus(GeminiState.NotConfigured, "");
        if (!_connectivity.IsNetworkAvailable) return new GeminiStatus(GeminiState.Offline, "");
        return new GeminiStatus(GeminiState.Ready, "");
    }

    /// <summary>Re-evaluates the status after the API key or the network changed.</summary>
    public void RefreshGeminiStatus() => SetGemini(ComputeIdleStatus());

    private void SetGemini(GeminiStatus status)
    {
        if (_gemini == status) return;
        _gemini = status;
        GeminiStatusChanged?.Invoke(status);
    }

    public void StartNewConversation() => ConversationId = null;

    /// <summary>Continues an earlier conversation; its summary and recent turns become the context again.</summary>
    public IReadOnlyList<ChatMessage> Resume(long conversationId)
    {
        var conversation = _conversations.Get(conversationId);
        if (conversation is null) return Array.Empty<ChatMessage>();
        ConversationId = conversation.Id;
        return _conversations.GetMessages(conversation.Id);
    }

    /// <summary>Adds a note (reminder fired, task finished) to the current conversation without involving Gemini.</summary>
    public void AddNote(string text)
    {
        if (ConversationId is null)
        {
            MessageAdded?.Invoke(new ChatMessage { Role = MessageRole.Note, Content = text, CreatedUtc = _clock.UtcNow });
            return;
        }
        Add(ConversationId.Value, MessageRole.Note, text);
    }

    private ChatMessage Add(long conversationId, MessageRole role, string content)
    {
        var message = _conversations.AddMessage(conversationId, role, content);
        MessageAdded?.Invoke(message);
        return message;
    }

    private Conversation EnsureConversation(string firstMessage)
    {
        if (ConversationId.HasValue)
        {
            var existing = _conversations.Get(ConversationId.Value);
            if (existing is not null) return existing;
        }
        var created = _conversations.Create(Relevance.Truncate(firstMessage.Replace('\n', ' '), 60), _modes.Current);
        ConversationId = created.Id;
        return created;
    }

    public async Task<AssistantReply> SendAsync(string text, CancellationToken ct)
    {
        var input = text.Trim();
        if (input.Length == 0) return new AssistantReply { Text = "", Failed = true };

        var conversation = EnsureConversation(input);
        Add(conversation.Id, MessageRole.User, input);

        AssistantReply reply;
        try
        {
            reply = await RespondAsync(conversation, input, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            reply = new AssistantReply { Text = "Stopped.", Cancelled = true };
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "The assistant could not complete a request.");
            reply = new AssistantReply { Text = "Something went wrong while handling that request. The details are in the log.", Failed = true };
        }
        finally
        {
            ActivityChanged?.Invoke("");
        }

        Add(conversation.Id, MessageRole.Assistant, reply.Text);
        var latest = _conversations.Get(conversation.Id);
        if (latest is not null)
        {
            latest.Mode = _modes.Current;
            _conversations.Update(latest);
        }

        if (!reply.Local && !reply.Cancelled && !reply.Failed) _ = SummarizeInBackgroundAsync(conversation.Id);
        return reply;
    }

    private async Task SummarizeInBackgroundAsync(long conversationId)
    {
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await _summarizer.SummarizeIfNeededAsync(conversationId, limit.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogInformation("Conversation summary skipped: {Reason}", ex.GetType().Name);
        }
    }

    private async Task<AssistantReply> RespondAsync(Conversation conversation, string input, CancellationToken ct)
    {
        if (!_ai.IsConfigured)
        {
            SetGemini(new GeminiStatus(GeminiState.NotConfigured, ""));
            return await LocalReplyAsync(input, "Gemini is not configured yet (add an API key under Settings › Gemini).", ct).ConfigureAwait(false);
        }
        if (!_connectivity.IsNetworkAvailable)
        {
            SetGemini(new GeminiStatus(GeminiState.Offline, ""));
            return await LocalReplyAsync(input, "There is no network connection.", ct).ConfigureAwait(false);
        }

        try
        {
            var reply = await RunGeminiAsync(conversation, input, ct).ConfigureAwait(false);
            SetGemini(new GeminiStatus(GeminiState.Ready, ""));
            return reply;
        }
        catch (AiException ex)
        {
            _log.LogWarning("Gemini request failed: {Kind}", ex.Kind);
            SetGemini(ex.Kind switch
            {
                AiErrorKind.NoApiKey => new GeminiStatus(GeminiState.NotConfigured, ""),
                AiErrorKind.Network or AiErrorKind.Timeout => new GeminiStatus(GeminiState.Offline, ""),
                AiErrorKind.Authentication => new GeminiStatus(GeminiState.Error, "API key rejected"),
                AiErrorKind.Quota => new GeminiStatus(GeminiState.Error, "Quota reached"),
                AiErrorKind.Model => new GeminiStatus(GeminiState.Error, "Model unavailable"),
                _ => new GeminiStatus(GeminiState.Error, "Gemini error")
            });
            return await LocalReplyAsync(input, ex.Message, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Handles the request with the local interpreter and says plainly why Gemini was not used.</summary>
    private async Task<AssistantReply> LocalReplyAsync(string input, string reason, CancellationToken ct)
    {
        ActivityChanged?.Invoke("Working locally…");
        var handled = await _offline.TryHandleAsync(input, ct).ConfigureAwait(false);
        if (handled is not null) return new AssistantReply { Text = handled, Local = true };
        return new AssistantReply
        {
            Text = reason + " I cannot answer that without Gemini.\n\n" + OfflineAssistant.CapabilitiesHint,
            Local = true,
            Failed = true
        };
    }

    private async Task<AssistantReply> RunGeminiAsync(Conversation conversation, string input, CancellationToken ct)
    {
        var request = new AiRequest();
        var transcript = _conversations.GetMessages(conversation.Id, conversation.SummarizedThrough);
        request.Input.AddRange(ContextBuilder.BuildHistory(transcript));
        if (request.Input.Count == 0) request.Input.Add(new AiUserInput(input));

        var citations = new List<AiCitation>();
        for (var round = 0; round < MaxToolRounds; round++)
        {
            // Re-read the mode every round: a switch_mode call changes both the instructions and the tool set.
            var mode = _modes.Current;
            request.SystemInstruction = BuildSystemInstruction(conversation, input, mode);
            request.Tools = _executor.DefinitionsFor(mode);

            ActivityChanged?.Invoke("Thinking…");
            var response = await _ai.GenerateAsync(request, ct).ConfigureAwait(false);
            citations.AddRange(response.Citations);

            if (response.ToolCalls.Count == 0)
                return new AssistantReply { Text = response.Text.Trim(), Citations = citations };

            foreach (var step in response.RawSteps) request.Input.Add(new AiRawStep(step));
            foreach (var call in response.ToolCalls)
            {
                ct.ThrowIfCancellationRequested();
                ActivityChanged?.Invoke(Describe(call.Name));
                var context = new ToolContext { Mode = _modes.Current, UserRequest = input };
                var result = await _executor.ExecuteAsync(call.Name, call.Arguments, context, ct).ConfigureAwait(false);
                request.Input.Add(new AiFunctionResult(call.CallId, call.Name, result.ToModelJson()));

                if (result.Success && _executor.Registry.TryGet(call.Name, out var tool))
                {
                    // Visible feedback whenever the screen or clipboard was actually read.
                    if (tool.Definition.Category == ToolCategory.Screen) AddNote("Screen captured once for this request.");
                    else if (tool.Definition.Category == ToolCategory.Clipboard) AddNote("Clipboard read once for this request.");
                }
            }
        }

        return new AssistantReply
        {
            Text = $"I stopped after {MaxToolRounds} rounds of actions without reaching a final answer. The actions that ran are listed under Settings › Permissions › Activity.",
            Failed = true,
            Citations = citations
        };
    }

    private static string Describe(string toolName)
    {
        var words = toolName.Replace('_', ' ');
        return "Running: " + words + "…";
    }

    private string BuildSystemInstruction(Conversation conversation, string input, JarvisMode mode)
    {
        var definition = ModeCatalog.Get(mode);
        var nowLocal = _clock.LocalNow;

        var memories = new List<MemoryItem>(_memory.Search(input, 8));
        foreach (var preference in _memory.List().Where(m => m.Category == "preference").Take(4))
        {
            if (memories.All(m => m.Id != preference.Id)) memories.Add(preference);
        }

        var planner = "";
        if (definition.Allows(ToolCategory.Planner))
        {
            var todos = _planner.ListTodos(false);
            var reminders = _planner.ListReminders(true);
            var sb = new StringBuilder();
            var overdue = BriefingBuilder.Overdue(todos, nowLocal).Count;
            sb.Append(todos.Count).Append(" open tasks");
            if (overdue > 0) sb.Append(" (").Append(overdue).Append(" overdue)");
            sb.Append(", ").Append(reminders.Count).Append(" active reminders.");
            foreach (var t in BriefingBuilder.Prioritised(todos).Take(3)) sb.Append("\n- task: ").Append(BriefingBuilder.Describe(t, nowLocal));
            foreach (var r in reminders.OrderBy(r => r.DueUtc).Take(3))
                sb.Append("\n- reminder: ").Append(TimeText.Friendly(TimeText.ToLocal(r.DueUtc), nowLocal)).Append(" ").Append(r.Text);
            planner = sb.ToString();
        }

        var active = _tasks.List(20).Where(t => t.IsActive).Take(5).ToList();
        var tasks = active.Count == 0 ? "" : string.Join("\n", active.Select(t => "- " + t.ProgressText + " [id " + t.Id.Substring(0, Math.Min(8, t.Id.Length)) + "]"));
        if (_tasks.AllPaused && active.Count > 0) tasks = "Background tasks are paused by the user.\n" + tasks;

        return PromptBuilder.Build(new PromptContext
        {
            Mode = mode,
            UserName = _settings.Current.UserName,
            NowLocal = nowLocal,
            Memories = memories,
            ConversationSummary = conversation.Summary,
            PlannerDigest = planner,
            TaskDigest = tasks,
            WorkspaceDigest = _modes.Describe(mode)
        });
    }
}
