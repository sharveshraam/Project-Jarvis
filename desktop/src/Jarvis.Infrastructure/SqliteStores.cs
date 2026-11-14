using Jarvis.Core.Memory;
using Jarvis.Core.Models;
using Microsoft.Data.Sqlite;

namespace Jarvis.Infrastructure;

/// <summary>SQLite persistence for conversations, memories, tasks and reminders.
/// Single file under %LocalAppData%\JARVIS\jarvis.db. All access is serialised
/// through a semaphore so background tasks never corrupt the database.
/// Explicit interface implementations are used because the four store
/// interfaces share method names with different return types.</summary>
public sealed class SqliteStores : IMemoryStore, IConversationStore, ITaskStore, IReminderStore
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SqliteStores(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _connectionString = $"Data Source={dbPath};Pooling=True";
        Initialise();
    }

    private void Initialise()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS conversations (id TEXT PRIMARY KEY, title TEXT NOT NULL, created_at TEXT NOT NULL, updated_at TEXT NOT NULL, summary TEXT);
            CREATE TABLE IF NOT EXISTS messages (id TEXT PRIMARY KEY, conversation_id TEXT NOT NULL, role TEXT NOT NULL, text TEXT NOT NULL, created_at TEXT NOT NULL, provenance TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS memories (id TEXT PRIMARY KEY, kind TEXT NOT NULL, key TEXT NOT NULL, value TEXT NOT NULL, is_explicit INTEGER NOT NULL, created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS tasks (id TEXT PRIMARY KEY, title TEXT NOT NULL, description TEXT NOT NULL, status TEXT NOT NULL, steps_json TEXT NOT NULL, current_step INTEGER NOT NULL, result TEXT, error TEXT, created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS reminders (id TEXT PRIMARY KEY, text TEXT NOT NULL, due_at TEXT NOT NULL, kind TEXT NOT NULL, recurrence_days TEXT, is_completed INTEGER NOT NULL, created_at TEXT NOT NULL);
            """;
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private async Task<T> WithGateAsync<T>(Func<Task<T>> work)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { return await work().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task WithGateAsync(Func<Task> work)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await work().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    // ---- IMemoryStore ----
    async Task<IReadOnlyList<MemoryItem>> IMemoryStore.GetAllAsync(CancellationToken ct) => await WithGateAsync(async () =>
    {
        var list = new List<MemoryItem>();
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, kind, key, value, is_explicit, created_at, updated_at FROM memories ORDER BY updated_at DESC";
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
            list.Add(new MemoryItem(Guid.Parse(r.GetString(0)), Enum.Parse<MemoryKind>(r.GetString(1)), r.GetString(2), r.GetString(3), r.GetInt32(4) == 1, DateTimeOffset.Parse(r.GetString(5)), DateTimeOffset.Parse(r.GetString(6))));
        return (IReadOnlyList<MemoryItem>)list;
    }).ConfigureAwait(false);

    async Task IMemoryStore.AddAsync(MemoryItem item, CancellationToken ct) => await WithGateAsync(async () =>
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO memories VALUES ($id,$kind,$key,$value,$exp,$c,$u)";
        Add(cmd, "$id", item.Id.ToString()); Add(cmd, "$kind", item.Kind.ToString()); Add(cmd, "$key", item.Key);
        Add(cmd, "$value", item.Value); Add(cmd, "$exp", item.IsExplicit ? 1 : 0);
        Add(cmd, "$c", item.CreatedAt.ToString("o")); Add(cmd, "$u", item.UpdatedAt.ToString("o"));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }).ConfigureAwait(false);

    async Task IMemoryStore.UpdateAsync(MemoryItem item, CancellationToken ct) => await WithGateAsync(async () =>
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE memories SET kind=$kind,key=$key,value=$value,is_explicit=$exp,updated_at=$u WHERE id=$id";
        Add(cmd, "$id", item.Id.ToString()); Add(cmd, "$kind", item.Kind.ToString()); Add(cmd, "$key", item.Key);
        Add(cmd, "$value", item.Value); Add(cmd, "$exp", item.IsExplicit ? 1 : 0); Add(cmd, "$u", item.UpdatedAt.ToString("o"));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }).ConfigureAwait(false);

    async Task IMemoryStore.DeleteAsync(Guid id, CancellationToken ct) => await WithGateAsync(async () =>
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM memories WHERE id=$id"; Add(cmd, "$id", id.ToString());
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }).ConfigureAwait(false);

    async Task IMemoryStore.ClearAllAsync(CancellationToken ct) => await WithGateAsync(async () =>
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM memories";
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }).ConfigureAwait(false);

    // ---- IConversationStore ----
    async Task<IReadOnlyList<Conversation>> IConversationStore.ListAsync(CancellationToken ct) => await WithGateAsync(async () =>
    {
        var list = new List<Conversation>();
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id,title,created_at,updated_at,summary FROM conversations ORDER BY updated_at DESC";
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
            list.Add(new Conversation(Guid.Parse(r.GetString(0)), r.GetString(1), DateTimeOffset.Parse(r.GetString(2)), DateTimeOffset.Parse(r.GetString(3)), r.IsDBNull(4) ? null : r.GetString(4)));
        return (IReadOnlyList<Conversation>)list;
    }).ConfigureAwait(false);

    async Task<Conversation?> IConversationStore.GetAsync(Guid id, CancellationToken ct) => await WithGateAsync(async () =>
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id,title,created_at,updated_at,summary FROM conversations WHERE id=$id";
        Add(cmd, "$id", id.ToString());
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false)) return null;
        return new Conversation(Guid.Parse(r.GetString(0)), r.GetString(1), DateTimeOffset.Parse(r.GetString(2)), DateTimeOffset.Parse(r.GetString(3)), r.IsDBNull(4) ? null : r.GetString(4));
    }).ConfigureAwait(false);

    async Task IConversationStore.SaveAsync(Conversation conversation, CancellationToken ct) => await WithGateAsync(async () =>
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO conversations VALUES ($id,$title,$c,$u,$s)";
        Add(cmd, "$id", conversation.Id.ToString()); Add(cmd, "$title", conversation.Title);
        Add(cmd, "$c", conversation.CreatedAt.ToString("o")); Add(cmd, "$u", conversation.UpdatedAt.ToString("o"));
        Add(cmd, "$s", (object?)conversation.Summary ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }).ConfigureAwait(false);

    async Task IConversationStore.DeleteAsync(Guid id, CancellationToken ct) => await WithGateAsync(async () =>
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM conversations WHERE id=$id; DELETE FROM messages WHERE conversation_id=$id";
        Add(cmd, "$id", id.ToString());
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }).ConfigureAwait(false);

    async Task IConversationStore.ClearAllAsync(CancellationToken ct) => await WithGateAsync(async () =>
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM conversations; DELETE FROM messages";
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }).ConfigureAwait(false);

    async Task<IReadOnlyList<ChatMessage>> IConversationStore.GetMessagesAsync(Guid conversationId, CancellationToken ct) => await WithGateAsync(async () =>
    {
        var list = new List<ChatMessage>();
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id,conversation_id,role,text,created_at,provenance FROM messages WHERE conversation_id=$id ORDER BY created_at";
        Add(cmd, "$id", conversationId.ToString());
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
            list.Add(new ChatMessage(Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1)), Enum.Parse<MessageRole>(r.GetString(2)), r.GetString(3), DateTimeOffset.Parse(r.GetString(4)), Enum.Parse<ReplyProvenance>(r.GetString(5))));
        return (IReadOnlyList<ChatMessage>)list;
    }).ConfigureAwait(false);

    async Task IConversationStore.AddMessageAsync(ChatMessage message, CancellationToken ct) => await WithGateAsync(async () =>
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO messages VALUES ($id,$cid,$role,$text,$c,$p)";
        Add(cmd, "$id", message.Id.ToString()); Add(cmd, "$cid", message.ConversationId.ToString());
        Add(cmd, "$role", message.Role.ToString()); Add(cmd, "$text", message.Text);
        Add(cmd, "$c", message.CreatedAt.ToString("o")); Add(cmd, "$p", message.Provenance.ToString());
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }).ConfigureAwait(false);

    async Task IConversationStore.UpdateSummaryAsync(Guid conversationId, string summary, CancellationToken ct) => await WithGateAsync(async () =>
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE conversations SET summary=$s WHERE id=$id";
        Add(cmd, "$id", conversationId.ToString()); Add(cmd, "$s", summary);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }).ConfigureAwait(false);

    // ---- ITaskStore ----
    async Task<IReadOnlyList<AgentTask>> ITaskStore.ListAsync(CancellationToken ct) => await WithGateAsync(async () =>
    {
        var list = new List<AgentTask>();
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id,title,description,status,steps_json,current_step,result,error,created_at,updated_at FROM tasks ORDER BY created_at DESC";
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false)) list.Add(ReadTask(r));
        return (IReadOnlyList<AgentTask>)list;
    }).ConfigureAwait(false);

    async Task<AgentTask?> ITaskStore.GetAsync(Guid id, CancellationToken ct) => await WithGateAsync(async () =>
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id,title,description,status,steps_json,current_step,result,error,created_at,updated_at FROM tasks WHERE id=$id";
        Add(cmd, "$id", id.ToString());
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await r.ReadAsync(ct).ConfigureAwait(false) ? ReadTask(r) : null;
    }).ConfigureAwait(false);

    async Task ITaskStore.SaveAsync(AgentTask task, CancellationToken ct) => await WithGateAsync(async () =>
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO tasks VALUES ($id,$title,$desc,$status,$steps,$cur,$res,$err,$c,$u)";
        Add(cmd, "$id", task.Id.ToString()); Add(cmd, "$title", task.Title); Add(cmd, "$desc", task.Description);
        Add(cmd, "$status", task.Status.ToString());
        Add(cmd, "$steps", System.Text.Json.JsonSerializer.Serialize(task.Steps));
        Add(cmd, "$cur", task.CurrentStepIndex);
        Add(cmd, "$res", (object?)task.Result ?? DBNull.Value); Add(cmd, "$err", (object?)task.Error ?? DBNull.Value);
        Add(cmd, "$c", task.CreatedAt.ToString("o")); Add(cmd, "$u", task.UpdatedAt.ToString("o"));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }).ConfigureAwait(false);

    async Task ITaskStore.DeleteAsync(Guid id, CancellationToken ct) => await WithGateAsync(async () =>
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM tasks WHERE id=$id"; Add(cmd, "$id", id.ToString());
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }).ConfigureAwait(false);

    // ---- IReminderStore ----
    async Task<IReadOnlyList<Reminder>> IReminderStore.ListAsync(CancellationToken ct) => await WithGateAsync(async () =>
    {
        var list = new List<Reminder>();
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id,text,due_at,kind,recurrence_days,is_completed,created_at FROM reminders ORDER BY due_at";
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
            list.Add(new Reminder(Guid.Parse(r.GetString(0)), r.GetString(1), DateTimeOffset.Parse(r.GetString(2)), Enum.Parse<ReminderKind>(r.GetString(3)), r.IsDBNull(4) ? null : r.GetString(4), r.GetInt32(5) == 1, DateTimeOffset.Parse(r.GetString(6))));
        return (IReadOnlyList<Reminder>)list;
    }).ConfigureAwait(false);

    async Task IReminderStore.SaveAsync(Reminder reminder, CancellationToken ct) => await WithGateAsync(async () =>
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO reminders VALUES ($id,$text,$due,$kind,$rec,$done,$c)";
        Add(cmd, "$id", reminder.Id.ToString()); Add(cmd, "$text", reminder.Text);
        Add(cmd, "$due", reminder.DueAt.ToString("o")); Add(cmd, "$kind", reminder.Kind.ToString());
        Add(cmd, "$rec", (object?)reminder.RecurrenceDays ?? DBNull.Value);
        Add(cmd, "$done", reminder.IsCompleted ? 1 : 0); Add(cmd, "$c", reminder.CreatedAt.ToString("o"));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }).ConfigureAwait(false);

    async Task IReminderStore.DeleteAsync(Guid id, CancellationToken ct) => await WithGateAsync(async () =>
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM reminders WHERE id=$id"; Add(cmd, "$id", id.ToString());
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }).ConfigureAwait(false);

    async Task IReminderStore.MarkCompletedAsync(Guid id, CancellationToken ct) => await WithGateAsync(async () =>
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE reminders SET is_completed=1 WHERE id=$id"; Add(cmd, "$id", id.ToString());
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }).ConfigureAwait(false);

    private static AgentTask ReadTask(SqliteDataReader r)
    {
        var steps = System.Text.Json.JsonSerializer.Deserialize<List<TaskStep>>(r.GetString(4)) ?? [];
        return new AgentTask(Guid.Parse(r.GetString(0)), r.GetString(1), r.GetString(2),
            Enum.Parse<TaskStatus>(r.GetString(3)), steps, r.GetInt32(5),
            r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7),
            DateTimeOffset.Parse(r.GetString(8)), DateTimeOffset.Parse(r.GetString(9)));
    }

    private static void Add(SqliteCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }
}
