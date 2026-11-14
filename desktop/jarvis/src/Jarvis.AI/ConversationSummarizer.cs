using System.Text;
using Jarvis.Core.Abstractions;
using Jarvis.Core.AI;
using Jarvis.Core.Models;
using Jarvis.Core.Text;
using Microsoft.Extensions.Logging;

namespace Jarvis.AI;

/// <summary>
/// Keeps long conversations affordable: once enough unsummarised turns accumulate, the oldest ones are folded
/// into a rolling summary so later requests send "summary + recent turns" instead of the whole transcript.
/// </summary>
public sealed class ConversationSummarizer
{
    public const int Threshold = 40;
    public const int KeepRecent = 16;

    private readonly IAiClient _ai;
    private readonly IConversationStore _store;
    private readonly ILogger<ConversationSummarizer> _log;

    public ConversationSummarizer(IAiClient ai, IConversationStore store, ILogger<ConversationSummarizer> log)
    {
        _ai = ai;
        _store = store;
        _log = log;
    }

    public async Task<bool> SummarizeIfNeededAsync(long conversationId, CancellationToken ct)
    {
        var conversation = _store.Get(conversationId);
        if (conversation is null || !_ai.IsConfigured) return false;

        var pending = _store.GetMessages(conversationId, conversation.SummarizedThrough)
            .Where(m => m.Role != MessageRole.Note).ToList();
        if (pending.Count < Threshold) return false;

        var toFold = pending.Take(pending.Count - KeepRecent).ToList();
        var transcript = new StringBuilder();
        foreach (var m in toFold)
            transcript.Append(m.Role == MessageRole.User ? "User: " : "JARVIS: ").AppendLine(Relevance.Truncate(m.Content, 1500));

        var request = new AiRequest
        {
            SystemInstruction =
                "You maintain a running summary of a conversation between a user and their assistant JARVIS. " +
                "Produce an updated summary in at most 180 words. Keep: goals, decisions, facts about the user's projects, open questions and commitments. " +
                "Omit pleasantries. Never include passwords, keys or tokens. Output the summary text only.",
            MaxOutputTokens = 2048
        };
        var existing = string.IsNullOrWhiteSpace(conversation.Summary) ? "" : "Existing summary:\n" + conversation.Summary + "\n\n";
        request.Input.Add(new AiUserInput(existing + "New turns to fold in:\n" + transcript.ToString()));

        try
        {
            var response = await _ai.GenerateAsync(request, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(response.Text)) return false;
            conversation.Summary = response.Text.Trim();
            conversation.SummarizedThrough = toFold[toFold.Count - 1].Id;
            _store.Update(conversation);
            return true;
        }
        catch (AiException ex)
        {
            // Summarisation is best-effort; the transcript simply stays unsummarised until next time.
            _log.LogInformation("Conversation summary skipped: {Kind}.", ex.Kind);
            return false;
        }
    }
}
