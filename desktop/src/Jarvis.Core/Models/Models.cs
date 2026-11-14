namespace Jarvis.Core.Models;

/// <summary>Conversation message roles. Provenance is tracked separately so an
/// offline/local reply is never presented as a Gemini reply.</summary>
public enum MessageRole { User, Assistant, System }

/// <summary>Where an assistant reply came from.</summary>
public enum ReplyProvenance { Local, Gemini, Error }

public sealed record ChatMessage(
    Guid Id,
    Guid ConversationId,
    MessageRole Role,
    string Text,
    DateTimeOffset CreatedAt,
    ReplyProvenance Provenance = ReplyProvenance.Local);

public sealed record Conversation(
    Guid Id,
    string Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? Summary = null);

public enum MemoryKind { Preference, Project, Decision, RecurringTask, WorkContext, AppFolder, Other }

/// <summary>Long-term memory. Explicit memories are user-stated; inferred ones
/// are proposed by the assistant and must be reviewable. Secrets are never stored here.</summary>
public sealed record MemoryItem(
    Guid Id,
    MemoryKind Kind,
    string Key,
    string Value,
    bool IsExplicit,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public enum TaskStatus { Pending, Running, WaitingForApproval, Paused, Completed, Failed, Cancelled }

public sealed record TaskStep(string Description, string? ToolName = null, string? ArgumentsJson = null);

public sealed record AgentTask(
    Guid Id,
    string Title,
    string Description,
    TaskStatus Status,
    IReadOnlyList<TaskStep> Steps,
    int CurrentStepIndex,
    string? Result,
    string? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public enum ReminderKind { OneTime, Daily, Weekly }

public sealed record Reminder(
    Guid Id,
    string Text,
    DateTimeOffset DueAt,
    ReminderKind Kind,
    string? RecurrenceDays, // comma-separated DayOfWeek names for Weekly
    bool IsCompleted,
    DateTimeOffset CreatedAt);

public enum OperatingMode { Study, Development, Research, Focus, System, Personal }

public sealed record ModeProfile(
    OperatingMode Mode,
    string DisplayName,
    string Description,
    IReadOnlyList<string> AllowedTools,
    string? WorkspaceFolder,
    string? PreferredApp);

public sealed record SearchHit(string FilePath, string FileName, string? Excerpt, double Score);

public sealed record FileInfoRecord(string Path, string Name, long Length, DateTimeOffset LastWrite, string Extension);

public sealed record DuplicateGroup(string ContentHash, IReadOnlyList<string> Paths);
