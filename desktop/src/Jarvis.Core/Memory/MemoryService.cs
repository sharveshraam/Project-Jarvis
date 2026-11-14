using Jarvis.Core.Models;

namespace Jarvis.Core.Memory;

/// <summary>Storage abstraction for memories, conversations, tasks and reminders.
/// SQLite implementation lives in Jarvis.Infrastructure; in-memory for tests.</summary>
public interface IMemoryStore
{
    Task<IReadOnlyList<MemoryItem>> GetAllAsync(CancellationToken ct = default);
    Task AddAsync(MemoryItem item, CancellationToken ct = default);
    Task UpdateAsync(MemoryItem item, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
    Task ClearAllAsync(CancellationToken ct = default);
}

public interface IConversationStore
{
    Task<IReadOnlyList<Conversation>> ListAsync(CancellationToken ct = default);
    Task<Conversation?> GetAsync(Guid id, CancellationToken ct = default);
    Task SaveAsync(Conversation conversation, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
    Task ClearAllAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(Guid conversationId, CancellationToken ct = default);
    Task AddMessageAsync(ChatMessage message, CancellationToken ct = default);
    Task UpdateSummaryAsync(Guid conversationId, string summary, CancellationToken ct = default);
}

public interface ITaskStore
{
    Task<IReadOnlyList<AgentTask>> ListAsync(CancellationToken ct = default);
    Task<AgentTask?> GetAsync(Guid id, CancellationToken ct = default);
    Task SaveAsync(AgentTask task, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}

public interface IReminderStore
{
    Task<IReadOnlyList<Reminder>> ListAsync(CancellationToken ct = default);
    Task SaveAsync(Reminder reminder, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
    Task MarkCompletedAsync(Guid id, CancellationToken ct = default);
}

/// <summary>Secret storage (Gemini API key). Windows implementation uses DPAPI.</summary>
public interface ISecretVault
{
    Task<string?> GetApiKeyAsync(CancellationToken ct = default);
    Task SetApiKeyAsync(string apiKey, CancellationToken ct = default);
    Task RemoveApiKeyAsync(CancellationToken ct = default);
}

/// <summary>Local memory service: keyword-ranked retrieval over stored memories.
/// Secrets (passwords/keys) are rejected — they belong in the vault, not memory.</summary>
public sealed class MemoryService(IMemoryStore store)
{
    private static readonly string[] SecretHints = ["password", "api key", "apikey", "secret", "token"];

    public static bool LooksLikeSecret(string value) =>
        SecretHints.Any(h => value.Contains(h, StringComparison.OrdinalIgnoreCase));

    public async Task<MemoryItem> RememberAsync(MemoryKind kind, string key, string value, bool isExplicit, CancellationToken ct = default)
    {
        if (LooksLikeSecret(value))
            throw new InvalidOperationException("Refusing to store a possible secret in ordinary memory. Use Settings to store credentials.");
        var now = DateTimeOffset.UtcNow;
        var item = new MemoryItem(Guid.NewGuid(), kind, key.Trim(), value.Trim(), isExplicit, now, now);
        await store.AddAsync(item, ct).ConfigureAwait(false);
        return item;
    }

    public async Task<IReadOnlyList<MemoryItem>> RecallAsync(string query, int maxResults = 5, CancellationToken ct = default)
    {
        var all = await store.GetAllAsync(ct).ConfigureAwait(false);
        var terms = query.ToLowerInvariant().Split([' ', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries);
        return all
            .Select(m => (m, Score: terms.Count(t => m.Key.Contains(t, StringComparison.OrdinalIgnoreCase) || m.Value.Contains(t, StringComparison.OrdinalIgnoreCase))))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.m.UpdatedAt)
            .Take(maxResults)
            .Select(x => x.m)
            .ToList();
    }

    public Task<IReadOnlyList<MemoryItem>> ListAllAsync(CancellationToken ct = default) => store.GetAllAsync(ct);
    public Task DeleteAsync(Guid id, CancellationToken ct = default) => store.DeleteAsync(id, ct);
    public Task ClearAllAsync(CancellationToken ct = default) => store.ClearAllAsync(ct);
}
