using System.Globalization;
using System.Text;
using Jarvis.Core.AI;
using Jarvis.Core.Models;
using Jarvis.Core.Modes;
using Jarvis.Core.Text;

namespace Jarvis.AI;

public sealed class PromptContext
{
    public JarvisMode Mode { get; init; } = JarvisMode.Personal;
    public string UserName { get; init; } = "";
    public DateTime NowLocal { get; init; } = DateTime.Now;
    public IReadOnlyList<MemoryItem> Memories { get; init; } = Array.Empty<MemoryItem>();
    public string ConversationSummary { get; init; } = "";
    public string PlannerDigest { get; init; } = "";
    public string TaskDigest { get; init; } = "";
    public string WorkspaceDigest { get; init; } = "";
}

/// <summary>Builds the system instruction: persona, hard rules, the active mode, and only the local context relevant to this turn.</summary>
public static class PromptBuilder
{
    public const string Persona =
        "You are JARVIS, a personal desktop assistant running on the user's Windows 11 computer. " +
        "Your manner is professional, intelligent, composed and conversational: precise, lightly formal, never chatty, never servile. " +
        "Answer in plain language; keep spoken-style replies short unless detail is requested.";

    public const string Rules =
        "Operating rules:\n" +
        "1. You can affect the computer only through the provided tools. You have no shell, script or filesystem access beyond them.\n" +
        "2. Never say an action succeeded unless its tool result has success=true. If verified=false, say it was requested but could not be confirmed. Report failures and refusals plainly, with the reason given.\n" +
        "3. Do not ask the user to confirm routine actions yourself; the application enforces approvals. If a result has denied=true, accept it and do not retry.\n" +
        "4. Call capture_screen or read_clipboard only when the user's current message explicitly asks about the screen or the clipboard.\n" +
        "5. When the user shares a durable fact (a preference, project detail, decision, routine, frequently used app or folder), store it with the remember tool: source=user_provided when the user stated it, source=inferred when you deduced it. Never store passwords, PINs, API keys or tokens.\n" +
        "6. For reminders and deadlines pass due_local as local ISO 8601 (yyyy-MM-ddTHH:mm) computed from the current time below.\n" +
        "7. If a needed capability is not among your tools in the current mode, say so and name the mode that provides it; switch modes only when the user asks or clearly intends it.\n" +
        "8. For multi-step work the user wants done while they do something else, use start_background_task and report its task id.";

    public static string Build(PromptContext c)
    {
        var mode = ModeCatalog.Get(c.Mode);
        var offset = TimeZoneInfo.Local.GetUtcOffset(c.NowLocal);
        var offsetText = (offset < TimeSpan.Zero ? "-" : "+") + offset.ToString(@"hh\:mm", CultureInfo.InvariantCulture);

        var sb = new StringBuilder();
        sb.AppendLine(Persona);
        sb.AppendLine();
        sb.AppendLine(Rules);
        sb.AppendLine();
        sb.Append("Current local time: ").Append(c.NowLocal.ToString("dddd, yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))
            .Append(" (UTC").Append(offsetText).AppendLine(").");
        if (!string.IsNullOrWhiteSpace(c.UserName)) sb.Append("The user's name is ").Append(c.UserName.Trim()).AppendLine(".");
        sb.AppendLine();
        sb.Append("Active mode: ").Append(mode.DisplayName).Append(" — ").AppendLine(mode.Purpose);
        sb.AppendLine(mode.Behaviour);
        if (!string.IsNullOrWhiteSpace(c.WorkspaceDigest)) sb.AppendLine(c.WorkspaceDigest.Trim());

        if (c.Memories.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Relevant long-term memory (local; 'stated' = told by the user, 'inferred' = deduced, treat with less certainty):");
            foreach (var m in c.Memories)
            {
                sb.Append("- [").Append(m.Category).Append(", ")
                    .Append(m.Source == MemorySource.UserProvided ? "stated" : "inferred").Append("] ")
                    .AppendLine(Relevance.Truncate(m.Content, 400));
            }
        }
        if (!string.IsNullOrWhiteSpace(c.PlannerDigest))
        {
            sb.AppendLine();
            sb.AppendLine("Tasks and reminders:");
            sb.AppendLine(c.PlannerDigest.Trim());
        }
        if (!string.IsNullOrWhiteSpace(c.TaskDigest))
        {
            sb.AppendLine();
            sb.AppendLine("Background tasks:");
            sb.AppendLine(c.TaskDigest.Trim());
        }
        if (!string.IsNullOrWhiteSpace(c.ConversationSummary))
        {
            sb.AppendLine();
            sb.AppendLine("Summary of the earlier part of this conversation:");
            sb.AppendLine(c.ConversationSummary.Trim());
        }
        return sb.ToString().TrimEnd();
    }
}

/// <summary>Selects how much recent transcript to send. Older turns are represented by the stored summary instead.</summary>
public static class ContextBuilder
{
    public const int MaxMessages = 40;
    public const int MaxCharacters = 32_000;

    public static List<AiInput> BuildHistory(IReadOnlyList<ChatMessage> messages, int maxMessages = MaxMessages, int maxCharacters = MaxCharacters)
    {
        var selected = new List<ChatMessage>();
        var budget = maxCharacters;
        for (var i = messages.Count - 1; i >= 0 && selected.Count < maxMessages; i--)
        {
            var message = messages[i];
            if (message.Role == MessageRole.Note || string.IsNullOrWhiteSpace(message.Content)) continue;
            var cost = message.Content.Length + 16;
            if (cost > budget && selected.Count > 0) break;
            budget -= cost;
            selected.Insert(0, message);
        }

        // History must open with a user turn.
        while (selected.Count > 0 && selected[0].Role != MessageRole.User) selected.RemoveAt(0);

        var inputs = new List<AiInput>(selected.Count);
        foreach (var message in selected)
        {
            var content = message.Content.Length > maxCharacters ? message.Content.Substring(0, maxCharacters) : message.Content;
            if (message.Role == MessageRole.User) inputs.Add(new AiUserInput(content));
            else inputs.Add(new AiModelInput(content));
        }
        return inputs;
    }
}
