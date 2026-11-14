using System.Text.Json.Nodes;

namespace Jarvis.Core.Models;

public enum JarvisMode { Personal, Study, Development, Research, Focus, System }
public enum MessageRole { User, Assistant, Note }
public enum MemorySource { UserProvided, Inferred }
public enum TaskPriority { Low = 0, Normal = 1, High = 2, Urgent = 3 }
public enum RecurrenceKind { None, Daily, Weekdays, Weekly, Monthly }
public enum AgentTaskState { Pending, Running, WaitingForApproval, Paused, Completed, Failed, Cancelled }
public enum StepState { Pending, Running, Completed, Failed, Skipped }
public enum NotificationKind { Info, Warning, Error }

public sealed class Conversation
{
    public long Id { get; set; }
    public string Title { get; set; } = "New conversation";
    public JarvisMode Mode { get; set; } = JarvisMode.Personal;
    /// <summary>Rolling summary of messages up to <see cref="SummarizedThrough"/>.</summary>
    public string Summary { get; set; } = "";
    public long SummarizedThrough { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

public sealed class ChatMessage
{
    public long Id { get; set; }
    public long ConversationId { get; set; }
    public MessageRole Role { get; set; }
    public string Content { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
}

public sealed class MemoryItem
{
    public long Id { get; set; }
    /// <summary>preference | project | decision | routine | context | app | other</summary>
    public string Category { get; set; } = "other";
    public string Content { get; set; } = "";
    public MemorySource Source { get; set; } = MemorySource.UserProvided;
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

public sealed class Reminder
{
    public long Id { get; set; }
    public string Text { get; set; } = "";
    public DateTime DueUtc { get; set; }
    public RecurrenceKind Recurrence { get; set; } = RecurrenceKind.None;
    public bool Active { get; set; } = true;
    public DateTime? LastFiredUtc { get; set; }
    public DateTime CreatedUtc { get; set; }
}

public sealed class TodoItem
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
    public string Notes { get; set; } = "";
    public TaskPriority Priority { get; set; } = TaskPriority.Normal;
    public DateTime? DeadlineUtc { get; set; }
    public bool Completed { get; set; }
    public DateTime? CompletedUtc { get; set; }
    public DateTime CreatedUtc { get; set; }
}

public sealed class AgentStep
{
    public int Index { get; set; }
    public string Description { get; set; } = "";
    public string Tool { get; set; } = "";
    public JsonObject Args { get; set; } = new();
    public StepState State { get; set; } = StepState.Pending;
    public string? Result { get; set; }
    public int Attempts { get; set; }
    /// <summary>True when the app stopped while this step was executing; its side effect is uncertain.</summary>
    public bool Interrupted { get; set; }
}

public sealed class AgentTask
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public AgentTaskState State { get; set; } = AgentTaskState.Pending;
    public JarvisMode Mode { get; set; } = JarvisMode.Personal;
    public List<AgentStep> Steps { get; set; } = new();
    public int CurrentStep { get; set; }
    public string? Result { get; set; }
    public string? Error { get; set; }
    /// <summary>Paused for this task specifically (by the user, or by recovery when a step's effect is uncertain).
    /// Such a task only continues after an explicit resume; a global "Pause Tasks" does not set this.</summary>
    public bool Held { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }

    public int CompletedSteps => Steps.Count(s => s.State is StepState.Completed or StepState.Skipped);
    public bool IsActive => State is AgentTaskState.Pending or AgentTaskState.Running
        or AgentTaskState.WaitingForApproval or AgentTaskState.Paused;

    public string ProgressText =>
        $"{Title} — {State} ({CompletedSteps}/{Steps.Count} steps)";
}

public sealed class FocusSession
{
    public long Id { get; set; }
    public string Goal { get; set; } = "";
    public int PlannedMinutes { get; set; }
    public DateTime StartUtc { get; set; }
    public DateTime? EndUtc { get; set; }
    /// <summary>Apps closed as distractions when the session started, so they can be reopened.</summary>
    public List<string> ClosedApps { get; set; } = new();
}

public sealed record SearchHit(string Path, string Name, string Excerpt, string MatchKind);
public sealed record IndexStats(int Files, int FilesWithContent, DateTime? LastBuiltUtc);
public sealed record FileMove(string Source, string Destination);
public sealed record AuditEntry(DateTime AtUtc, string Tool, string Risk, string Decision, bool Success, string Summary);
