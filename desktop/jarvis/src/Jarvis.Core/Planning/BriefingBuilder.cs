using System.Text;
using Jarvis.Core.Models;

namespace Jarvis.Core.Planning;

/// <summary>Builds daily plans, morning briefings and end-of-day summaries from local data only.</summary>
public static class BriefingBuilder
{
    public static IReadOnlyList<TodoItem> Overdue(IEnumerable<TodoItem> todos, DateTime nowLocal)
        => todos.Where(t => !t.Completed && t.DeadlineUtc.HasValue && TimeText.ToLocal(t.DeadlineUtc.Value) < nowLocal)
            .OrderBy(t => t.DeadlineUtc).ToList();

    public static IReadOnlyList<TodoItem> DueToday(IEnumerable<TodoItem> todos, DateTime nowLocal)
        => todos.Where(t => !t.Completed && t.DeadlineUtc.HasValue
                            && TimeText.ToLocal(t.DeadlineUtc.Value).Date == nowLocal.Date
                            && TimeText.ToLocal(t.DeadlineUtc.Value) >= nowLocal)
            .OrderBy(t => t.DeadlineUtc).ToList();

    public static IReadOnlyList<TodoItem> Prioritised(IEnumerable<TodoItem> todos)
        => todos.Where(t => !t.Completed)
            .OrderByDescending(t => t.Priority)
            .ThenBy(t => t.DeadlineUtc ?? DateTime.MaxValue)
            .ThenBy(t => t.CreatedUtc).ToList();

    public static string DailyPlan(IReadOnlyList<TodoItem> todos, IReadOnlyList<Reminder> reminders, DateTime nowLocal)
    {
        var sb = new StringBuilder();
        var overdue = Overdue(todos, nowLocal);
        var today = DueToday(todos, nowLocal);
        var open = Prioritised(todos);
        var todaysReminders = reminders
            .Where(r => r.Active && TimeText.ToLocal(r.DueUtc).Date == nowLocal.Date)
            .OrderBy(r => r.DueUtc).ToList();

        if (open.Count == 0 && todaysReminders.Count == 0)
            return "Nothing is scheduled for today. No open tasks and no reminders.";

        if (overdue.Count > 0)
        {
            sb.AppendLine($"Overdue ({overdue.Count}):");
            foreach (var t in overdue) sb.AppendLine("  • " + Describe(t, nowLocal));
        }
        if (today.Count > 0)
        {
            sb.AppendLine($"Due today ({today.Count}):");
            foreach (var t in today) sb.AppendLine("  • " + Describe(t, nowLocal));
        }

        var rest = open.Where(t => !overdue.Contains(t) && !today.Contains(t)).Take(8).ToList();
        if (rest.Count > 0)
        {
            sb.AppendLine("Next by priority:");
            foreach (var t in rest) sb.AppendLine("  • " + Describe(t, nowLocal));
        }
        if (todaysReminders.Count > 0)
        {
            sb.AppendLine("Reminders today:");
            foreach (var r in todaysReminders)
                sb.AppendLine($"  • {TimeText.ToLocal(r.DueUtc):HH:mm} — {r.Text}");
        }
        return sb.ToString().TrimEnd();
    }

    public static string MorningBriefing(string userName, IReadOnlyList<TodoItem> todos, IReadOnlyList<Reminder> reminders, DateTime nowLocal)
    {
        var greeting = string.IsNullOrWhiteSpace(userName) ? "Good morning." : $"Good morning, {userName.Trim()}.";
        return $"{greeting} It is {nowLocal:dddd, d MMMM}.\n{DailyPlan(todos, reminders, nowLocal)}";
    }

    public static string EndOfDay(IReadOnlyList<TodoItem> todos, IReadOnlyList<Reminder> reminders, DateTime nowLocal)
    {
        var completedToday = todos.Where(t => t.Completed && t.CompletedUtc.HasValue
                                              && TimeText.ToLocal(t.CompletedUtc.Value).Date == nowLocal.Date).ToList();
        var open = Prioritised(todos);
        var overdue = Overdue(todos, nowLocal);
        var tomorrow = reminders.Where(r => r.Active && TimeText.ToLocal(r.DueUtc).Date == nowLocal.Date.AddDays(1))
            .OrderBy(r => r.DueUtc).ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"End-of-day summary for {nowLocal:dddd, d MMMM}.");
        sb.AppendLine($"Completed today: {completedToday.Count}.");
        foreach (var t in completedToday.Take(10)) sb.AppendLine("  ✓ " + t.Title);
        sb.AppendLine($"Still open: {open.Count}" + (overdue.Count > 0 ? $" ({overdue.Count} overdue)." : "."));
        foreach (var t in open.Take(6)) sb.AppendLine("  • " + Describe(t, nowLocal));
        if (tomorrow.Count > 0)
        {
            sb.AppendLine("Tomorrow's reminders:");
            foreach (var r in tomorrow) sb.AppendLine($"  • {TimeText.ToLocal(r.DueUtc):HH:mm} — {r.Text}");
        }
        return sb.ToString().TrimEnd();
    }

    public static string Describe(TodoItem t, DateTime nowLocal)
    {
        var text = t.Title;
        if (t.Priority != TaskPriority.Normal) text += $" [{t.Priority}]";
        if (t.DeadlineUtc.HasValue) text += $" — due {TimeText.Friendly(TimeText.ToLocal(t.DeadlineUtc.Value), nowLocal)}";
        return text;
    }
}
