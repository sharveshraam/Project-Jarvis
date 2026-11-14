using Jarvis.Core.Models;

namespace Jarvis.Core.Abstractions;

public interface IClock
{
    DateTime UtcNow { get; }
    DateTime LocalNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
    public DateTime LocalNow => DateTime.Now;
}

/// <summary>Encrypted at-rest storage for credentials (DPAPI on Windows). Never used for ordinary memory.</summary>
public interface ISecretStore
{
    string? Get(string name);
    void Set(string name, string value);
    void Remove(string name);
}

public static class SecretNames
{
    public const string GeminiApiKey = "gemini_api_key";
}

public interface ISettingsStore
{
    /// <summary>Live settings instance. Mutate a <see cref="AppSettings.Clone"/> and call <see cref="Save"/>.</summary>
    AppSettings Current { get; }
    void Save(AppSettings settings);
    event Action<AppSettings>? Changed;
    string? GetValue(string key);
    void SetValue(string key, string value);
}

public interface IConversationStore
{
    Conversation Create(string title, JarvisMode mode);
    Conversation? Get(long id);
    IReadOnlyList<Conversation> List(int limit);
    void Update(Conversation conversation);
    void Delete(long id);
    void DeleteAll();
    ChatMessage AddMessage(long conversationId, MessageRole role, string content);
    IReadOnlyList<ChatMessage> GetMessages(long conversationId, long afterId = 0, int limit = 1000);
    int CountMessages(long conversationId, long afterId = 0);
}

public interface IMemoryStore
{
    MemoryItem Add(string category, string content, MemorySource source);
    IReadOnlyList<MemoryItem> List();
    /// <summary>Keyword relevance search over local memory; never leaves the machine.</summary>
    IReadOnlyList<MemoryItem> Search(string query, int limit);
    bool Update(long id, string category, string content);
    bool Delete(long id);
    void Clear();
}

public interface IPlannerStore
{
    Reminder AddReminder(string text, DateTime dueUtc, RecurrenceKind recurrence);
    IReadOnlyList<Reminder> ListReminders(bool activeOnly);
    IReadOnlyList<Reminder> DueReminders(DateTime nowUtc);
    DateTime? NextDueUtc();
    void UpdateReminder(Reminder reminder);
    bool DeleteReminder(long id);

    TodoItem AddTodo(string title, string notes, TaskPriority priority, DateTime? deadlineUtc);
    IReadOnlyList<TodoItem> ListTodos(bool includeCompleted);
    void UpdateTodo(TodoItem todo);
    bool DeleteTodo(long id);
}

public interface IAgentTaskStore
{
    void Save(AgentTask task);
    AgentTask? Get(string id);
    IReadOnlyList<AgentTask> List(int limit);
    /// <summary>Atomically moves a task between states; false when it was not in <paramref name="from"/>.</summary>
    bool TryTransition(string id, AgentTaskState from, AgentTaskState to);
    void Delete(string id);
}

public interface IFocusStore
{
    FocusSession Start(string goal, int plannedMinutes, IReadOnlyList<string> closedApps);
    FocusSession? Active();
    void End(long id, DateTime endUtc);
    IReadOnlyList<FocusSession> Recent(int limit);
}

/// <summary>Undo journal for bulk file moves, so organisation runs are reversible.</summary>
public interface IFileOpJournal
{
    void Record(string batchId, string source, string destination);
    string? LatestBatchId();
    IReadOnlyList<FileMove> GetBatch(string batchId);
    void MarkUndone(string batchId);
}

public interface IAuditLog
{
    void Record(string tool, string risk, string decision, bool success, string summary);
    IReadOnlyList<AuditEntry> Recent(int limit);
}

public interface ISearchIndex
{
    /// <summary>Incrementally (re)builds the local index for the given folders.</summary>
    IndexStats Rebuild(IEnumerable<string> roots, CancellationToken ct);
    void Clear();
    IReadOnlyList<SearchHit> Search(string query, int limit, IReadOnlyCollection<string>? extensions = null);
    IndexStats Stats();
}
