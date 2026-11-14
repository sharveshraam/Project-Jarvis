using System.Globalization;
using System.Text.Json;
using Jarvis.Core.Abstractions;
using Jarvis.Core.Models;
using Jarvis.Core.Text;
using Microsoft.Data.Sqlite;

namespace Jarvis.Infrastructure.Data;

public sealed class SettingsStore : ISettingsStore
{
    private const string AppKey = "app_settings";
    private readonly Database _db;
    private AppSettings _current;

    public SettingsStore(Database db)
    {
        _db = db;
        _current = Load();
    }

    public AppSettings Current => _current;
    public event Action<AppSettings>? Changed;

    private AppSettings Load()
    {
        var json = GetValue(AppKey);
        if (string.IsNullOrWhiteSpace(json)) return new AppSettings();
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(json, JsonDefaults.Options) ?? new AppSettings();
        }
        catch (JsonException)
        {
            // A corrupt settings blob must never stop the app from starting.
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        SetValue(AppKey, JsonSerializer.Serialize(settings, JsonDefaults.Options));
        _current = settings;
        Changed?.Invoke(settings);
    }

    public string? GetValue(string key)
        => _db.Scalar("SELECT value FROM settings WHERE key=$k", ("$k", key)) as string;

    public void SetValue(string key, string value)
        => _db.Execute("INSERT INTO settings(key,value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=excluded.value",
            ("$k", key), ("$v", value));
}

public sealed class ConversationStore : IConversationStore
{
    private readonly Database _db;
    private readonly IClock _clock;

    public ConversationStore(Database db, IClock clock) { _db = db; _clock = clock; }

    private static Conversation Map(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        Title = r.GetString(1),
        Mode = Enum.TryParse<JarvisMode>(r.GetString(2), out var mode) ? mode : JarvisMode.Personal,
        Summary = r.GetString(3),
        SummarizedThrough = r.GetInt64(4),
        CreatedUtc = Database.ParseUtc(r.GetString(5)),
        UpdatedUtc = Database.ParseUtc(r.GetString(6))
    };

    private const string Columns = "id,title,mode,summary,summarized_through,created_utc,updated_utc";

    public Conversation Create(string title, JarvisMode mode)
    {
        var now = _clock.UtcNow;
        var id = _db.Insert("INSERT INTO conversations(title,mode,created_utc,updated_utc) VALUES($t,$m,$c,$c)",
            ("$t", title), ("$m", mode.ToString()), ("$c", Database.Iso(now)));
        return new Conversation { Id = id, Title = title, Mode = mode, CreatedUtc = now, UpdatedUtc = now };
    }

    public Conversation? Get(long id)
        => _db.Query($"SELECT {Columns} FROM conversations WHERE id=$id", Map, ("$id", id)).FirstOrDefault();

    public IReadOnlyList<Conversation> List(int limit)
        => _db.Query($"SELECT {Columns} FROM conversations ORDER BY updated_utc DESC LIMIT $n", Map, ("$n", limit));

    public void Update(Conversation c)
        => _db.Execute("UPDATE conversations SET title=$t, mode=$m, summary=$s, summarized_through=$st, updated_utc=$u WHERE id=$id",
            ("$t", c.Title), ("$m", c.Mode.ToString()), ("$s", c.Summary), ("$st", c.SummarizedThrough),
            ("$u", Database.Iso(_clock.UtcNow)), ("$id", c.Id));

    public void Delete(long id) => _db.Execute("DELETE FROM conversations WHERE id=$id", ("$id", id));

    public void DeleteAll() => _db.Execute("DELETE FROM conversations");

    public ChatMessage AddMessage(long conversationId, MessageRole role, string content)
    {
        var now = _clock.UtcNow;
        var id = _db.Insert("INSERT INTO messages(conversation_id,role,content,created_utc) VALUES($c,$r,$t,$n)",
            ("$c", conversationId), ("$r", role.ToString()), ("$t", content), ("$n", Database.Iso(now)));
        _db.Execute("UPDATE conversations SET updated_utc=$n WHERE id=$c", ("$n", Database.Iso(now)), ("$c", conversationId));
        return new ChatMessage { Id = id, ConversationId = conversationId, Role = role, Content = content, CreatedUtc = now };
    }

    public IReadOnlyList<ChatMessage> GetMessages(long conversationId, long afterId = 0, int limit = 1000)
        => _db.Query("SELECT id,conversation_id,role,content,created_utc FROM messages WHERE conversation_id=$c AND id>$a ORDER BY id LIMIT $n",
            r => new ChatMessage
            {
                Id = r.GetInt64(0),
                ConversationId = r.GetInt64(1),
                Role = Enum.TryParse<MessageRole>(r.GetString(2), out var role) ? role : MessageRole.Note,
                Content = r.GetString(3),
                CreatedUtc = Database.ParseUtc(r.GetString(4))
            }, ("$c", conversationId), ("$a", afterId), ("$n", limit));

    public int CountMessages(long conversationId, long afterId = 0)
        => Convert.ToInt32(_db.Scalar("SELECT COUNT(*) FROM messages WHERE conversation_id=$c AND id>$a",
            ("$c", conversationId), ("$a", afterId)), CultureInfo.InvariantCulture);
}

public sealed class MemoryStore : IMemoryStore
{
    private readonly Database _db;
    private readonly IClock _clock;

    public MemoryStore(Database db, IClock clock) { _db = db; _clock = clock; }

    private static MemoryItem Map(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        Category = r.GetString(1),
        Content = r.GetString(2),
        Source = Enum.TryParse<MemorySource>(r.GetString(3), out var s) ? s : MemorySource.Inferred,
        CreatedUtc = Database.ParseUtc(r.GetString(4)),
        UpdatedUtc = Database.ParseUtc(r.GetString(5))
    };

    public MemoryItem Add(string category, string content, MemorySource source)
    {
        var now = _clock.UtcNow;
        var id = _db.Insert("INSERT INTO memories(category,content,source,created_utc,updated_utc) VALUES($c,$t,$s,$n,$n)",
            ("$c", category), ("$t", content), ("$s", source.ToString()), ("$n", Database.Iso(now)));
        return new MemoryItem { Id = id, Category = category, Content = content, Source = source, CreatedUtc = now, UpdatedUtc = now };
    }

    public IReadOnlyList<MemoryItem> List()
        => _db.Query("SELECT id,category,content,source,created_utc,updated_utc FROM memories ORDER BY updated_utc DESC", Map);

    public IReadOnlyList<MemoryItem> Search(string query, int limit)
    {
        var tokens = Relevance.Tokens(query);
        return List()
            .Select(m => (Item: m, Score: Relevance.Score(tokens, m.Category + " " + m.Content)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score).ThenByDescending(x => x.Item.UpdatedUtc)
            .Take(limit).Select(x => x.Item).ToList();
    }

    public bool Update(long id, string category, string content)
        => _db.Execute("UPDATE memories SET category=$c, content=$t, updated_utc=$n WHERE id=$id",
            ("$c", category), ("$t", content), ("$n", Database.Iso(_clock.UtcNow)), ("$id", id)) > 0;

    public bool Delete(long id) => _db.Execute("DELETE FROM memories WHERE id=$id", ("$id", id)) > 0;

    public void Clear() => _db.Execute("DELETE FROM memories");
}

public sealed class PlannerStore : IPlannerStore
{
    private readonly Database _db;
    private readonly IClock _clock;

    public PlannerStore(Database db, IClock clock) { _db = db; _clock = clock; }

    private const string ReminderColumns = "id,text,due_utc,recurrence,active,last_fired_utc,created_utc";

    private static Reminder MapReminder(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        Text = r.GetString(1),
        DueUtc = Database.ParseUtc(r.GetString(2)),
        Recurrence = Enum.TryParse<RecurrenceKind>(r.GetString(3), out var k) ? k : RecurrenceKind.None,
        Active = r.GetInt64(4) != 0,
        LastFiredUtc = Database.ParseUtcOrNull(r, 5),
        CreatedUtc = Database.ParseUtc(r.GetString(6))
    };

    public Reminder AddReminder(string text, DateTime dueUtc, RecurrenceKind recurrence)
    {
        var now = _clock.UtcNow;
        var id = _db.Insert("INSERT INTO reminders(text,due_utc,recurrence,active,created_utc) VALUES($t,$d,$r,1,$n)",
            ("$t", text), ("$d", Database.Iso(dueUtc)), ("$r", recurrence.ToString()), ("$n", Database.Iso(now)));
        return new Reminder { Id = id, Text = text, DueUtc = dueUtc, Recurrence = recurrence, Active = true, CreatedUtc = now };
    }

    public IReadOnlyList<Reminder> ListReminders(bool activeOnly)
        => _db.Query(activeOnly
            ? $"SELECT {ReminderColumns} FROM reminders WHERE active=1 ORDER BY due_utc"
            : $"SELECT {ReminderColumns} FROM reminders ORDER BY due_utc", MapReminder);

    public IReadOnlyList<Reminder> DueReminders(DateTime nowUtc)
        => _db.Query($"SELECT {ReminderColumns} FROM reminders WHERE active=1 AND due_utc<=$n ORDER BY due_utc", MapReminder,
            ("$n", Database.Iso(nowUtc)));

    public DateTime? NextDueUtc()
    {
        var value = _db.Scalar("SELECT MIN(due_utc) FROM reminders WHERE active=1") as string;
        return value is null ? null : Database.ParseUtc(value);
    }

    public void UpdateReminder(Reminder r)
        => _db.Execute("UPDATE reminders SET text=$t, due_utc=$d, recurrence=$r, active=$a, last_fired_utc=$f WHERE id=$id",
            ("$t", r.Text), ("$d", Database.Iso(r.DueUtc)), ("$r", r.Recurrence.ToString()), ("$a", r.Active ? 1 : 0),
            ("$f", Database.IsoOrNull(r.LastFiredUtc)), ("$id", r.Id));

    public bool DeleteReminder(long id) => _db.Execute("DELETE FROM reminders WHERE id=$id", ("$id", id)) > 0;

    private static TodoItem MapTodo(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        Title = r.GetString(1),
        Notes = r.GetString(2),
        Priority = (TaskPriority)Math.Clamp((int)r.GetInt64(3), 0, 3),
        DeadlineUtc = Database.ParseUtcOrNull(r, 4),
        Completed = r.GetInt64(5) != 0,
        CompletedUtc = Database.ParseUtcOrNull(r, 6),
        CreatedUtc = Database.ParseUtc(r.GetString(7))
    };

    public TodoItem AddTodo(string title, string notes, TaskPriority priority, DateTime? deadlineUtc)
    {
        var now = _clock.UtcNow;
        var id = _db.Insert("INSERT INTO todos(title,notes,priority,deadline_utc,created_utc) VALUES($t,$o,$p,$d,$n)",
            ("$t", title), ("$o", notes), ("$p", (int)priority), ("$d", Database.IsoOrNull(deadlineUtc)), ("$n", Database.Iso(now)));
        return new TodoItem { Id = id, Title = title, Notes = notes, Priority = priority, DeadlineUtc = deadlineUtc, CreatedUtc = now };
    }

    public IReadOnlyList<TodoItem> ListTodos(bool includeCompleted)
        => _db.Query("SELECT id,title,notes,priority,deadline_utc,completed,completed_utc,created_utc FROM todos "
                     + (includeCompleted ? "" : "WHERE completed=0 ") + "ORDER BY completed, priority DESC, created_utc", MapTodo);

    public void UpdateTodo(TodoItem t)
        => _db.Execute("UPDATE todos SET title=$t, notes=$o, priority=$p, deadline_utc=$d, completed=$c, completed_utc=$cu WHERE id=$id",
            ("$t", t.Title), ("$o", t.Notes), ("$p", (int)t.Priority), ("$d", Database.IsoOrNull(t.DeadlineUtc)),
            ("$c", t.Completed ? 1 : 0), ("$cu", Database.IsoOrNull(t.CompletedUtc)), ("$id", t.Id));

    public bool DeleteTodo(long id) => _db.Execute("DELETE FROM todos WHERE id=$id", ("$id", id)) > 0;
}

public sealed class AgentTaskStore : IAgentTaskStore
{
    private readonly Database _db;
    private readonly IClock _clock;

    public AgentTaskStore(Database db, IClock clock) { _db = db; _clock = clock; }

    public void Save(AgentTask task)
    {
        if (task.CreatedUtc == default) task.CreatedUtc = _clock.UtcNow;
        task.UpdatedUtc = _clock.UtcNow;
        _db.Execute(@"INSERT INTO agent_tasks(id,title,description,state,mode,steps_json,current_step,result,error,held,created_utc,updated_utc)
VALUES($id,$t,$d,$s,$m,$j,$c,$r,$e,$h,$cu,$uu)
ON CONFLICT(id) DO UPDATE SET title=excluded.title, description=excluded.description, state=excluded.state, mode=excluded.mode,
 steps_json=excluded.steps_json, current_step=excluded.current_step, result=excluded.result, error=excluded.error, held=excluded.held,
 updated_utc=excluded.updated_utc",
            ("$id", task.Id), ("$t", task.Title), ("$d", task.Description), ("$s", task.State.ToString()), ("$m", task.Mode.ToString()),
            ("$j", JsonSerializer.Serialize(task.Steps, JsonDefaults.Options)), ("$c", task.CurrentStep),
            ("$r", task.Result), ("$e", task.Error), ("$h", task.Held ? 1 : 0),
            ("$cu", Database.Iso(task.CreatedUtc)), ("$uu", Database.Iso(task.UpdatedUtc)));
    }

    private static AgentTask Map(SqliteDataReader r)
    {
        List<AgentStep> steps;
        try { steps = JsonSerializer.Deserialize<List<AgentStep>>(r.GetString(5), JsonDefaults.Options) ?? new List<AgentStep>(); }
        catch (JsonException) { steps = new List<AgentStep>(); }
        return new AgentTask
        {
            Id = r.GetString(0),
            Title = r.GetString(1),
            Description = r.GetString(2),
            State = Enum.TryParse<AgentTaskState>(r.GetString(3), out var s) ? s : AgentTaskState.Failed,
            Mode = Enum.TryParse<JarvisMode>(r.GetString(4), out var m) ? m : JarvisMode.Personal,
            Steps = steps,
            CurrentStep = (int)r.GetInt64(6),
            Result = r.IsDBNull(7) ? null : r.GetString(7),
            Error = r.IsDBNull(8) ? null : r.GetString(8),
            Held = r.GetInt64(9) != 0,
            CreatedUtc = Database.ParseUtc(r.GetString(10)),
            UpdatedUtc = Database.ParseUtc(r.GetString(11))
        };
    }

    private const string Columns = "id,title,description,state,mode,steps_json,current_step,result,error,held,created_utc,updated_utc";

    public AgentTask? Get(string id)
        => _db.Query($"SELECT {Columns} FROM agent_tasks WHERE id=$id", Map, ("$id", id)).FirstOrDefault();

    public IReadOnlyList<AgentTask> List(int limit)
        => _db.Query($"SELECT {Columns} FROM agent_tasks ORDER BY created_utc DESC LIMIT $n", Map, ("$n", limit));

    public bool TryTransition(string id, AgentTaskState from, AgentTaskState to)
        => _db.Execute("UPDATE agent_tasks SET state=$to, updated_utc=$u WHERE id=$id AND state=$from",
            ("$to", to.ToString()), ("$u", Database.Iso(_clock.UtcNow)), ("$id", id), ("$from", from.ToString())) == 1;

    public void Delete(string id) => _db.Execute("DELETE FROM agent_tasks WHERE id=$id", ("$id", id));
}

public sealed class FocusStore : IFocusStore
{
    private readonly Database _db;
    private readonly IClock _clock;

    public FocusStore(Database db, IClock clock) { _db = db; _clock = clock; }

    private static FocusSession Map(SqliteDataReader r)
    {
        List<string> apps;
        try { apps = JsonSerializer.Deserialize<List<string>>(r.GetString(5)) ?? new List<string>(); }
        catch (JsonException) { apps = new List<string>(); }
        return new FocusSession
        {
            Id = r.GetInt64(0),
            Goal = r.GetString(1),
            PlannedMinutes = (int)r.GetInt64(2),
            StartUtc = Database.ParseUtc(r.GetString(3)),
            EndUtc = Database.ParseUtcOrNull(r, 4),
            ClosedApps = apps
        };
    }

    private const string Columns = "id,goal,planned_minutes,start_utc,end_utc,closed_apps";

    public FocusSession Start(string goal, int plannedMinutes, IReadOnlyList<string> closedApps)
    {
        var now = _clock.UtcNow;
        var id = _db.Insert("INSERT INTO focus_sessions(goal,planned_minutes,start_utc,closed_apps) VALUES($g,$p,$s,$a)",
            ("$g", goal), ("$p", plannedMinutes), ("$s", Database.Iso(now)), ("$a", JsonSerializer.Serialize(closedApps)));
        return new FocusSession { Id = id, Goal = goal, PlannedMinutes = plannedMinutes, StartUtc = now, ClosedApps = closedApps.ToList() };
    }

    public FocusSession? Active()
        => _db.Query($"SELECT {Columns} FROM focus_sessions WHERE end_utc IS NULL ORDER BY id DESC LIMIT 1", Map).FirstOrDefault();

    public void End(long id, DateTime endUtc)
        => _db.Execute("UPDATE focus_sessions SET end_utc=$e WHERE id=$id", ("$e", Database.Iso(endUtc)), ("$id", id));

    public IReadOnlyList<FocusSession> Recent(int limit)
        => _db.Query($"SELECT {Columns} FROM focus_sessions ORDER BY id DESC LIMIT $n", Map, ("$n", limit));
}

public sealed class FileOpJournal : IFileOpJournal
{
    private readonly Database _db;
    private readonly IClock _clock;

    public FileOpJournal(Database db, IClock clock) { _db = db; _clock = clock; }

    public void Record(string batchId, string source, string destination)
        => _db.Execute("INSERT INTO file_ops(batch_id,source,destination,created_utc) VALUES($b,$s,$d,$n)",
            ("$b", batchId), ("$s", source), ("$d", destination), ("$n", Database.Iso(_clock.UtcNow)));

    public string? LatestBatchId()
        => _db.Scalar("SELECT batch_id FROM file_ops WHERE undone=0 ORDER BY id DESC LIMIT 1") as string;

    public IReadOnlyList<FileMove> GetBatch(string batchId)
        => _db.Query("SELECT source,destination FROM file_ops WHERE batch_id=$b AND undone=0 ORDER BY id",
            r => new FileMove(r.GetString(0), r.GetString(1)), ("$b", batchId));

    public void MarkUndone(string batchId) => _db.Execute("UPDATE file_ops SET undone=1 WHERE batch_id=$b", ("$b", batchId));
}

public sealed class AuditLog : IAuditLog
{
    private readonly Database _db;
    private readonly IClock _clock;

    public AuditLog(Database db, IClock clock) { _db = db; _clock = clock; }

    public void Record(string tool, string risk, string decision, bool success, string summary)
    {
        _db.Execute("INSERT INTO audit(at_utc,tool,risk,decision,success,summary) VALUES($a,$t,$r,$d,$s,$m)",
            ("$a", Database.Iso(_clock.UtcNow)), ("$t", tool), ("$r", risk), ("$d", decision), ("$s", success ? 1 : 0),
            ("$m", Jarvis.Core.Security.SecretDetector.Redact(Relevance.Truncate(summary, 400))));
        // Keep the audit trail bounded.
        _db.Execute("DELETE FROM audit WHERE id <= (SELECT MAX(id) FROM audit) - 5000");
    }

    public IReadOnlyList<AuditEntry> Recent(int limit)
        => _db.Query("SELECT at_utc,tool,risk,decision,success,summary FROM audit ORDER BY id DESC LIMIT $n",
            r => new AuditEntry(Database.ParseUtc(r.GetString(0)), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt64(4) != 0, r.GetString(5)),
            ("$n", limit));
}
