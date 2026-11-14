using Jarvis.Core.Config;
using Jarvis.Core.Memory;
using Jarvis.Core.Models;
using Jarvis.Core.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jarvis.AI;

/// <summary>Orchestrates a user turn: local memory recall → Gemini (with tool
/// calling loop) → tool execution through the registry → final reply.
/// Offline (no key / no network) it answers from local memory + local tools
/// and clearly labels the reply as LOCAL, never as Gemini.</summary>
public sealed class ConversationService(
    GeminiClient? gemini,
    IConversationStore conversations,
    MemoryService memory,
    ToolRegistry tools,
    JarvisSettings settings,
    ILogger<ConversationService>? logger = null)
{
    private readonly ILogger<ConversationService> _log = logger ?? NullLogger<ConversationService>.Instance;

    public sealed record TurnResult(string ReplyText, ReplyProvenance Provenance, Guid ConversationId);

    public async Task<TurnResult> HandleUserMessageAsync(
        Guid conversationId,
        string userText,
        Func<string, Task<bool>>? confirmAsync = null,
        CancellationToken ct = default)
    {
        await conversations.AddMessageAsync(new ChatMessage(Guid.NewGuid(), conversationId, MessageRole.User, userText, DateTimeOffset.Now), ct).ConfigureAwait(false);

        // Local memory context (always available, offline too).
        var memories = await memory.RecallAsync(userText, 3, ct).ConfigureAwait(false);
        var memoryContext = memories.Count == 0 ? "" :
            "Relevant stored memories:\n" + string.Join("\n", memories.Select(m => $"- {m.Key}: {m.Value}"));

        string replyText;
        ReplyProvenance provenance;

        if (gemini is null)
        {
            (replyText, provenance) = await LocalFallbackAsync(userText, memoryContext, ct).ConfigureAwait(false);
        }
        else
        {
            var history = (await conversations.GetMessagesAsync(conversationId, ct).ConfigureAwait(false))
                .Where(m => m.Role != MessageRole.System)
                .TakeLast(settings.MaxConversationTurnsSentToGemini * 2)
                .Select(m => new GeminiClient.GeminiTurn(m.Role == MessageRole.User ? "user" : "model", m.Text))
                .ToList();
            var system = "You are JARVIS, a professional Windows desktop assistant. Be concise and direct. " +
                         "You may request tools via function calling; never claim an action succeeded unless the tool result confirms it. " +
                         memoryContext;
            var declarations = tools.ToGeminiFunctionDeclarationsJson();
            var reply = await gemini.SendAsync(history, system, userText, declarations, ct).ConfigureAwait(false);

            if (reply.Error is not null && reply.Text is null)
            {
                (replyText, provenance) = await LocalFallbackAsync(userText, memoryContext, ct).ConfigureAwait(false);
                replyText = reply.Error + "\n\n" + replyText;
            }
            else if (reply.FunctionCall is not null)
            {
                var call = reply.FunctionCall;
                var allowed = tools.MayExecuteWithoutConfirmation(call.Name);
                var approved = allowed || (confirmAsync is not null && await confirmAsync(call.Name).ConfigureAwait(false));
                if (!approved)
                {
                    replyText = $"I need your confirmation before running '{call.Name}'. Nothing was executed.";
                    provenance = ReplyProvenance.Gemini;
                }
                else
                {
                    var result = await tools.ExecuteAsync(call.Name, call.Arguments, ct).ConfigureAwait(false);
                    var followUp = await gemini.SendAsync(history, system,
                        $"Tool {call.Name} result: {(result.Success ? result.Output : "FAILED: " + result.Error)}. Summarise this for the user in one or two sentences.",
                        null, ct).ConfigureAwait(false);
                    replyText = followUp.Text ?? (result.Success ? result.Output : result.Error ?? "Tool failed.");
                    provenance = ReplyProvenance.Gemini;
                }
            }
            else
            {
                replyText = reply.Text ?? "I could not generate a response.";
                provenance = ReplyProvenance.Gemini;
            }
        }

        await conversations.AddMessageAsync(new ChatMessage(Guid.NewGuid(), conversationId, MessageRole.Assistant, replyText, DateTimeOffset.Now, provenance), ct).ConfigureAwait(false);
        var conv = await conversations.GetAsync(conversationId, ct).ConfigureAwait(false);
        if (conv is not null)
        {
            var title = conv.Title == "New conversation" && userText.Length > 0
                ? userText[..Math.Min(48, userText.Length)]
                : conv.Title;
            await conversations.SaveAsync(conv with { Title = title, UpdatedAt = DateTimeOffset.Now }, ct).ConfigureAwait(false);
        }
        return new TurnResult(replyText, provenance, conversationId);
    }

    private async Task<(string, ReplyProvenance)> LocalFallbackAsync(string userText, string memoryContext, CancellationToken ct)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        var lower = userText.ToLowerInvariant();
        if (lower.Contains("what do you remember") || lower.Contains("my preferences"))
        {
            var all = await memory.ListAllAsync(ct).ConfigureAwait(false);
            var text = all.Count == 0
                ? "I don't have any stored memories yet. Tell me something to remember, e.g. 'remember my project is called Jarvis'."
                : "Here's what I remember:\n" + string.Join("\n", all.Select(m => $"- {m.Key}: {m.Value} ({(m.IsExplicit ? "explicit" : "inferred")})"));
            return (text, ReplyProvenance.Local);
        }
        if (lower.StartsWith("remember "))
        {
            var rest = userText["remember ".Length..].Trim();
            var colon = rest.IndexOf(':');
            var key = colon > 0 ? rest[..colon].Trim() : "note";
            var value = colon > 0 ? rest[(colon + 1)..].Trim() : rest;
            try
            {
                await memory.RememberAsync(MemoryKind.Other, key, value, isExplicit: true, ct).ConfigureAwait(false);
                return ($"Saved to memory: {key}.", ReplyProvenance.Local);
            }
            catch (InvalidOperationException ex) { return (ex.Message, ReplyProvenance.Local); }
        }
        if (lower.Contains("what can you do") || lower.Contains("help"))
        {
            return ("I'm JARVIS, running locally (Gemini is offline or unconfigured). I can: remember facts, manage reminders and tasks, search your indexed folders, and run approved Windows commands. Configure a Gemini API key in Settings for full conversation, screenshot analysis and research.", ReplyProvenance.Local);
        }
        return ("I'm in offline/local mode, so I can't reason with Gemini right now. I can still remember things, manage reminders/tasks, search files and run approved local commands. Try 'remember ...', 'what do you remember', or configure a Gemini key in Settings.", ReplyProvenance.Local);
    }
}
