using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Jarvis.Core.Abstractions;
using Jarvis.Core.Models;
using Jarvis.Core.Planning;
using Jarvis.Core.Tools;

namespace Jarvis.Agent.Tools;

/// <summary>Secretary tools: reminders, tasks, deadlines, priorities and daily plans. All data is local.</summary>
public sealed class PlannerTools : ToolModule
{
    private static readonly string[] IsoFormats =
    {
        "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd"
    };

    private readonly IPlannerStore _planner;
    private readonly IClock _clock;
    private readonly PlannerSignal _signal;

    public PlannerTools(IPlannerStore planner, IClock clock, PlannerSignal signal)
    {
        _planner = planner;
        _clock = clock;
        _signal = signal;
    }

    /// <summary>Accepts local ISO 8601 or natural language ("tomorrow at 7 pm", "in 20 minutes", "every Sunday 9am").</summary>
    public static bool TryParseWhen(string? text, DateTime nowLocal, out DateTime dueLocal, out RecurrenceKind recurrence)
    {
        dueLocal = default;
        recurrence = RecurrenceKind.None;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var trimmed = text.Trim();
        if (DateTime.TryParseExact(trimmed, IsoFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact))
        {
            dueLocal = trimmed.Length <= 10 ? exact.Date.AddHours(9) : exact;
            return true;
        }
        var parsed = ScheduleParser.Parse("remind me " + trimmed, nowLocal);
        if (parsed is null) return false;
        dueLocal = parsed.DueLocal;
        recurrence = parsed.Recurrence;
        return true;
    }

    private static RecurrenceKind ParseRecurrence(string? text) => (text ?? "").Trim().ToLowerInvariant() switch
    {
        "daily" => RecurrenceKind.Daily,
        "weekdays" => RecurrenceKind.Weekdays,
        "weekly" => RecurrenceKind.Weekly,
        "monthly" => RecurrenceKind.Monthly,
        _ => RecurrenceKind.None
    };

    private static TaskPriority ParsePriority(string? text) => (text ?? "").Trim().ToLowerInvariant() switch
    {
        "low" => TaskPriority.Low,
        "high" => TaskPriority.High,
        "urgent" => TaskPriority.Urgent,
        _ => TaskPriority.Normal
    };

    private static long RequireId(JsonObject args)
    {
        var id = args.Number("id");
        if (!id.HasValue || id.Value < 1) throw new ToolArgumentException("A valid id is required.");
        return (long)id.Value;
    }

    private JsonObject ReminderJson(Reminder r) => new JsonObject
    {
        ["id"] = r.Id,
        ["text"] = r.Text,
        ["due_local"] = TimeText.ToLocal(r.DueUtc).ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture),
        ["repeats"] = Recurrence.Describe(r.Recurrence),
        ["active"] = r.Active
    };

    private JsonObject TodoJson(TodoItem t) => new JsonObject
    {
        ["id"] = t.Id,
        ["title"] = t.Title,
        ["notes"] = t.Notes,
        ["priority"] = t.Priority.ToString().ToLowerInvariant(),
        ["deadline_local"] = t.DeadlineUtc.HasValue
            ? TimeText.ToLocal(t.DeadlineUtc.Value).ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture)
            : null,
        ["completed"] = t.Completed
    };

    public override IEnumerable<ITool> Build()
    {
        yield return Sync(
            Def("add_reminder", "Create a reminder that notifies the user at a time, optionally recurring. Works offline.", ToolCategory.Planner, ToolRisk.Routine)
                .Str("text", "What to remind the user about.", required: true)
                .Str("due_local", "When, as local ISO 8601 (yyyy-MM-ddTHH:mm) or natural language such as 'tomorrow at 7 pm' or 'in 20 minutes'.", required: true)
                .Enum("recurrence", "How it repeats. Omit for a one-off reminder.", false, "none", "daily", "weekdays", "weekly", "monthly"),
            (args, context) =>
            {
                var now = _clock.LocalNow;
                var text = args.Required("text");
                if (!TryParseWhen(args.Str("due_local"), now, out var due, out var parsedRecurrence))
                    return ToolResult.Fail($"I could not work out a time from '{args.Str("due_local")}'.");
                var recurrence = args.Str("recurrence") is null ? parsedRecurrence : ParseRecurrence(args.Str("recurrence"));
                if (due <= now)
                {
                    if (recurrence == RecurrenceKind.None)
                        return ToolResult.Fail($"{TimeText.Friendly(due, now)} is already in the past.");
                    due = Recurrence.Next(due, recurrence, now);
                }

                var created = _planner.AddReminder(text, TimeText.ToUtc(due), recurrence);
                _signal.Raise();
                var stored = _planner.ListReminders(activeOnly: true).Any(r => r.Id == created.Id);
                var repeat = recurrence == RecurrenceKind.None ? "" : ", repeating " + Recurrence.Describe(recurrence);
                return stored
                    ? ToolResult.Ok($"Reminder set for {TimeText.Friendly(due, now)}{repeat}: {text}", true, ReminderJson(created))
                    : ToolResult.Fail("The reminder could not be saved.");
            },
            (args, context) => ToolAssessment.Of(ToolRisk.Routine, $"Set a reminder: {args.Str("text")} ({args.Str("due_local")})"));

        yield return Sync(
            Def("list_reminders", "List the user's active reminders with their ids and due times.", ToolCategory.Planner, ToolRisk.Safe).Retryable(),
            (args, context) =>
            {
                var now = _clock.LocalNow;
                var reminders = _planner.ListReminders(activeOnly: true);
                if (reminders.Count == 0) return ToolResult.Ok("There are no active reminders.");
                var sb = new StringBuilder();
                sb.Append(reminders.Count == 1 ? "1 active reminder:" : $"{reminders.Count} active reminders:");
                foreach (var r in reminders)
                {
                    sb.Append("\n• ").Append(TimeText.Friendly(TimeText.ToLocal(r.DueUtc), now)).Append(" — ").Append(r.Text);
                    if (r.Recurrence != RecurrenceKind.None) sb.Append(" (").Append(Recurrence.Describe(r.Recurrence)).Append(')');
                    sb.Append(" [#").Append(r.Id.ToString(CultureInfo.InvariantCulture)).Append(']');
                }
                return ToolResult.Ok(sb.ToString(), true, new JsonObject { ["reminders"] = ToJsonArray(reminders.Select(ReminderJson)) });
            });

        yield return Sync(
            Def("delete_reminder", "Delete a reminder by id (use list_reminders to find the id).", ToolCategory.Planner, ToolRisk.Routine)
                .Int("id", "Reminder id.", required: true, min: 1),
            (args, context) =>
            {
                var id = RequireId(args);
                if (!_planner.DeleteReminder(id)) return ToolResult.Fail($"There is no reminder #{id}.");
                _signal.Raise();
                return ToolResult.Ok($"Reminder #{id} deleted.");
            },
            (args, context) => ToolAssessment.Of(ToolRisk.Routine, $"Delete reminder #{args.Str("id")}"),
            args => _planner.ListReminders(false).All(r => r.Id != RequireId(args)));

        yield return Sync(
            Def("add_task", "Add a task to the user's to-do list, optionally with a priority and deadline.", ToolCategory.Planner, ToolRisk.Routine)
                .Str("title", "Short task title.", required: true)
                .Str("notes", "Optional details.")
                .Enum("priority", "Priority; defaults to normal.", false, "low", "normal", "high", "urgent")
                .Str("deadline_local", "Optional deadline as local ISO 8601 (yyyy-MM-ddTHH:mm) or natural language."),
            (args, context) =>
            {
                var now = _clock.LocalNow;
                DateTime? deadlineUtc = null;
                var deadlineText = args.Str("deadline_local");
                if (!string.IsNullOrWhiteSpace(deadlineText))
                {
                    if (!TryParseWhen(deadlineText, now, out var deadline, out _))
                        return ToolResult.Fail($"I could not work out a deadline from '{deadlineText}'.");
                    deadlineUtc = TimeText.ToUtc(deadline);
                }
                var todo = _planner.AddTodo(args.Required("title"), args.Str("notes") ?? "", ParsePriority(args.Str("priority")), deadlineUtc);
                var stored = _planner.ListTodos(includeCompleted: false).Any(t => t.Id == todo.Id);
                return stored
                    ? ToolResult.Ok("Task added: " + BriefingBuilder.Describe(todo, now), true, TodoJson(todo))
                    : ToolResult.Fail("The task could not be saved.");
            },
            (args, context) => ToolAssessment.Of(ToolRisk.Routine, $"Add task: {args.Str("title")}"));

        yield return Sync(
            Def("list_tasks", "List the user's tasks ordered by priority and deadline, with their ids.", ToolCategory.Planner, ToolRisk.Safe)
                .Bool("include_completed", "Also include completed tasks.").Retryable(),
            (args, context) =>
            {
                var now = _clock.LocalNow;
                var includeCompleted = args.Bool("include_completed");
                var todos = _planner.ListTodos(includeCompleted);
                var open = BriefingBuilder.Prioritised(todos);
                if (todos.Count == 0) return ToolResult.Ok("The task list is empty.");
                var sb = new StringBuilder();
                sb.Append(open.Count == 0 ? "No open tasks." : open.Count == 1 ? "1 open task:" : $"{open.Count} open tasks:");
                foreach (var t in open)
                    sb.Append("\n• ").Append(BriefingBuilder.Describe(t, now)).Append(" [#").Append(t.Id.ToString(CultureInfo.InvariantCulture)).Append(']');
                if (includeCompleted)
                {
                    foreach (var t in todos.Where(t => t.Completed).Take(20))
                        sb.Append("\n✓ ").Append(t.Title).Append(" [#").Append(t.Id.ToString(CultureInfo.InvariantCulture)).Append(']');
                }
                return ToolResult.Ok(sb.ToString(), true, new JsonObject { ["tasks"] = ToJsonArray(todos.Select(TodoJson)) });
            });

        yield return Sync(
            Def("complete_task", "Mark a task as done by id.", ToolCategory.Planner, ToolRisk.Routine)
                .Int("id", "Task id.", required: true, min: 1).Retryable(),
            (args, context) =>
            {
                var id = RequireId(args);
                var todo = _planner.ListTodos(includeCompleted: true).FirstOrDefault(t => t.Id == id);
                if (todo is null) return ToolResult.Fail($"There is no task #{id}.");
                if (todo.Completed) return ToolResult.Ok($"'{todo.Title}' was already completed.");
                todo.Completed = true;
                todo.CompletedUtc = _clock.UtcNow;
                _planner.UpdateTodo(todo);
                var verified = _planner.ListTodos(true).Any(t => t.Id == id && t.Completed);
                return verified ? ToolResult.Ok($"Marked '{todo.Title}' as done.") : ToolResult.Fail("The task could not be updated.");
            },
            (args, context) => ToolAssessment.Of(ToolRisk.Routine, $"Complete task #{args.Str("id")}"),
            args => _planner.ListTodos(true).Any(t => t.Id == RequireId(args) && t.Completed));

        yield return Sync(
            Def("update_task", "Change a task's title, notes, priority or deadline.", ToolCategory.Planner, ToolRisk.Routine)
                .Int("id", "Task id.", required: true, min: 1)
                .Str("title", "New title.")
                .Str("notes", "New notes.")
                .Enum("priority", "New priority.", false, "low", "normal", "high", "urgent")
                .Str("deadline_local", "New deadline (local ISO 8601 or natural language); 'none' removes it."),
            (args, context) =>
            {
                var now = _clock.LocalNow;
                var id = RequireId(args);
                var todo = _planner.ListTodos(includeCompleted: true).FirstOrDefault(t => t.Id == id);
                if (todo is null) return ToolResult.Fail($"There is no task #{id}.");
                if (!string.IsNullOrWhiteSpace(args.Str("title"))) todo.Title = args.Str("title")!.Trim();
                if (args.Str("notes") is not null) todo.Notes = args.Str("notes")!;
                if (args.Str("priority") is not null) todo.Priority = ParsePriority(args.Str("priority"));
                var deadlineText = args.Str("deadline_local");
                if (!string.IsNullOrWhiteSpace(deadlineText))
                {
                    if (deadlineText.Trim().Equals("none", StringComparison.OrdinalIgnoreCase)) todo.DeadlineUtc = null;
                    else if (TryParseWhen(deadlineText, now, out var deadline, out _)) todo.DeadlineUtc = TimeText.ToUtc(deadline);
                    else return ToolResult.Fail($"I could not work out a deadline from '{deadlineText}'.");
                }
                _planner.UpdateTodo(todo);
                return ToolResult.Ok("Task updated: " + BriefingBuilder.Describe(todo, now), true, TodoJson(todo));
            },
            (args, context) => ToolAssessment.Of(ToolRisk.Routine, $"Update task #{args.Str("id")}"));

        yield return Sync(
            Def("delete_task", "Delete a task by id.", ToolCategory.Planner, ToolRisk.Routine)
                .Int("id", "Task id.", required: true, min: 1),
            (args, context) =>
            {
                var id = RequireId(args);
                return _planner.DeleteTodo(id) ? ToolResult.Ok($"Task #{id} deleted.") : ToolResult.Fail($"There is no task #{id}.");
            },
            (args, context) => ToolAssessment.Of(ToolRisk.Routine, $"Delete task #{args.Str("id")}"),
            args => _planner.ListTodos(true).All(t => t.Id != RequireId(args)));

        yield return Sync(
            Def("get_daily_plan", "Build today's plan from the user's tasks, deadlines, priorities and reminders.", ToolCategory.Planner, ToolRisk.Safe).Retryable(),
            (args, context) => ToolResult.Ok(BriefingBuilder.DailyPlan(_planner.ListTodos(false), _planner.ListReminders(true), _clock.LocalNow)));

        yield return Sync(
            Def("get_day_summary", "Summarise the day: what was completed, what is still pending or overdue, and what is coming up tomorrow.", ToolCategory.Planner, ToolRisk.Safe).Retryable(),
            (args, context) => ToolResult.Ok(BriefingBuilder.EndOfDay(_planner.ListTodos(true), _planner.ListReminders(true), _clock.LocalNow)));
    }
}
