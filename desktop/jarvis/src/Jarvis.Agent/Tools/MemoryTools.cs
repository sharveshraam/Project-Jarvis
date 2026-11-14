using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Jarvis.Core.Abstractions;
using Jarvis.Core.Models;
using Jarvis.Core.Security;
using Jarvis.Core.Tools;

namespace Jarvis.Agent.Tools;

/// <summary>Long-term memory. Stored locally, inspectable and editable by the user; secrets are refused.</summary>
public sealed class MemoryTools : ToolModule
{
    public static readonly string[] Categories = { "preference", "project", "decision", "routine", "context", "app", "other" };

    private readonly IMemoryStore _memory;

    public MemoryTools(IMemoryStore memory) => _memory = memory;

    private static JsonObject ToJson(MemoryItem m) => new JsonObject
    {
        ["id"] = m.Id,
        ["category"] = m.Category,
        ["content"] = m.Content,
        ["source"] = m.Source == MemorySource.UserProvided ? "user_provided" : "inferred"
    };

    private static string Render(IReadOnlyList<MemoryItem> items)
    {
        var sb = new StringBuilder();
        foreach (var m in items)
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append("• [").Append(m.Category).Append(m.Source == MemorySource.Inferred ? ", inferred" : "").Append("] ")
                .Append(m.Content).Append(" [#").Append(m.Id.ToString(CultureInfo.InvariantCulture)).Append(']');
        }
        return sb.ToString();
    }

    public override IEnumerable<ITool> Build()
    {
        yield return Sync(
            Def("remember", "Save a durable fact about the user to long-term memory. Never use for passwords, keys or other secrets.", ToolCategory.Memory, ToolRisk.Routine)
                .Str("content", "The fact, in one self-contained sentence.", required: true)
                .Enum("category", "Kind of fact.", true, Categories)
                .Enum("source", "user_provided when the user stated it; inferred when you deduced it.", true, "user_provided", "inferred"),
            (args, context) =>
            {
                var content = args.Required("content");
                if (SecretDetector.LooksSensitive(content, out var reason))
                    return ToolResult.Refused($"Not stored: {reason}. JARVIS does not keep secrets in memory.");
                var source = args.Str("source") == "inferred" ? MemorySource.Inferred : MemorySource.UserProvided;
                var duplicate = _memory.List().FirstOrDefault(m => string.Equals(m.Content.Trim(), content, StringComparison.OrdinalIgnoreCase));
                if (duplicate is not null) return ToolResult.Ok("That is already in memory.", true, ToJson(duplicate));
                var item = _memory.Add(args.Str("category") ?? "other", content, source);
                var stored = _memory.List().Any(m => m.Id == item.Id);
                return stored ? ToolResult.Ok("Noted: " + content, true, ToJson(item)) : ToolResult.Fail("The memory could not be saved.");
            },
            (args, context) => ToolAssessment.Of(ToolRisk.Routine, "Remember: " + args.Str("content")),
            args => _memory.List().Any(m => string.Equals(m.Content.Trim(), (args.Str("content") ?? "").Trim(), StringComparison.OrdinalIgnoreCase)));

        yield return Sync(
            Def("recall", "Search long-term memory for facts relevant to a topic.", ToolCategory.Memory, ToolRisk.Safe)
                .Str("query", "Topic or keywords.", required: true).Retryable(),
            (args, context) =>
            {
                var found = _memory.Search(args.Required("query"), 10);
                return found.Count == 0
                    ? ToolResult.Ok("Nothing relevant is stored in memory.")
                    : ToolResult.Ok(Render(found), true, new JsonObject { ["memories"] = ToJsonArray(found.Select(ToJson)) });
            });

        yield return Sync(
            Def("list_memories", "List everything in long-term memory with ids.", ToolCategory.Memory, ToolRisk.Safe).Retryable(),
            (args, context) =>
            {
                var all = _memory.List();
                return all.Count == 0
                    ? ToolResult.Ok("Long-term memory is empty.")
                    : ToolResult.Ok($"{all.Count} stored memories:\n" + Render(all.Take(60).ToList()), true,
                        new JsonObject { ["memories"] = ToJsonArray(all.Take(60).Select(ToJson)) });
            });

        yield return Sync(
            Def("update_memory", "Correct or replace a stored memory by id.", ToolCategory.Memory, ToolRisk.Routine)
                .Int("id", "Memory id.", required: true, min: 1)
                .Str("content", "The corrected fact.", required: true),
            (args, context) =>
            {
                var id = (long)(args.Number("id") ?? 0);
                var content = args.Required("content");
                if (SecretDetector.LooksSensitive(content, out var reason))
                    return ToolResult.Refused($"Not stored: {reason}. JARVIS does not keep secrets in memory.");
                var existing = _memory.List().FirstOrDefault(m => m.Id == id);
                if (existing is null) return ToolResult.Fail($"There is no memory #{id}.");
                return _memory.Update(id, existing.Category, content)
                    ? ToolResult.Ok($"Memory #{id} updated.")
                    : ToolResult.Fail("The memory could not be updated.");
            },
            (args, context) => ToolAssessment.Of(ToolRisk.Routine, $"Update memory #{args.Str("id")}"));

        yield return Sync(
            Def("forget_memory", "Delete one stored memory by id.", ToolCategory.Memory, ToolRisk.Routine)
                .Int("id", "Memory id.", required: true, min: 1),
            (args, context) =>
            {
                var id = (long)(args.Number("id") ?? 0);
                return _memory.Delete(id) ? ToolResult.Ok($"Memory #{id} forgotten.") : ToolResult.Fail($"There is no memory #{id}.");
            },
            (args, context) => ToolAssessment.Of(ToolRisk.Routine, $"Forget memory #{args.Str("id")}"),
            args => _memory.List().All(m => m.Id != (long)(args.Number("id") ?? 0)));
    }
}
