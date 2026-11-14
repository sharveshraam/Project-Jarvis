using Jarvis.Agent;
using Jarvis.Core.Models;
using Jarvis.Core.Modes;
using Jarvis.Core.Planning;
using Jarvis.Core.Tools;

namespace Jarvis.Tests;

public class BriefingBuilderTests
{
    private static readonly DateTime Now = new DateTime(2026, 11, 14, 10, 0, 0);

    private static TodoItem Todo(long id, string title, TaskPriority priority = TaskPriority.Normal, DateTime? deadlineLocal = null,
        DateTime? completedLocal = null)
        => new TodoItem
        {
            Id = id,
            Title = title,
            Priority = priority,
            DeadlineUtc = deadlineLocal.HasValue ? TimeText.ToUtc(deadlineLocal.Value) : null,
            Completed = completedLocal.HasValue,
            CompletedUtc = completedLocal.HasValue ? TimeText.ToUtc(completedLocal.Value) : null,
            CreatedUtc = TimeText.ToUtc(Now.AddDays(-3)).AddMinutes(id)
        };

    private static Reminder ReminderAt(string text, DateTime dueLocal, bool active = true)
        => new Reminder { Id = 1, Text = text, DueUtc = TimeText.ToUtc(dueLocal), Active = active };

    [Fact]
    public void An_empty_day_says_so()
    {
        Assert.Equal("Nothing is scheduled for today. No open tasks and no reminders.",
            BriefingBuilder.DailyPlan(new List<TodoItem>(), new List<Reminder>(), Now));
    }

    [Fact]
    public void The_daily_plan_separates_overdue_due_today_and_the_rest()
    {
        var todos = new List<TodoItem>
        {
            Todo(1, "File taxes", deadlineLocal: Now.AddHours(-2)),
            Todo(2, "Submit assignment", deadlineLocal: Now.AddHours(3)),
            Todo(3, "Fix login bug", TaskPriority.Urgent),
            Todo(4, "Old chore", completedLocal: Now.AddHours(-1))
        };
        var reminders = new List<Reminder>
        {
            ReminderAt("Call the bank", Now.AddHours(5)),
            ReminderAt("Next week thing", Now.AddDays(7)),
            ReminderAt("Already fired", Now.AddHours(1), active: false)
        };

        var plan = BriefingBuilder.DailyPlan(todos, reminders, Now);

        Assert.Contains("Overdue (1):", plan);
        Assert.Contains("File taxes", plan);
        Assert.Contains("Due today (1):", plan);
        Assert.Contains("Submit assignment", plan);
        Assert.Contains("Next by priority:", plan);
        Assert.Contains("Fix login bug [Urgent]", plan);
        Assert.Contains("15:00 — Call the bank", plan);
        Assert.DoesNotContain("Old chore", plan);
        Assert.DoesNotContain("Next week thing", plan);
        Assert.DoesNotContain("Already fired", plan);
        Assert.True(plan.IndexOf("Overdue", StringComparison.Ordinal) < plan.IndexOf("Due today", StringComparison.Ordinal));
    }

    [Fact]
    public void Open_tasks_are_ordered_by_priority_then_deadline()
    {
        var todos = new List<TodoItem>
        {
            Todo(1, "normal"),
            Todo(2, "urgent", TaskPriority.Urgent),
            Todo(3, "high later", TaskPriority.High, Now.AddDays(5)),
            Todo(4, "high sooner", TaskPriority.High, Now.AddDays(1)),
            Todo(5, "done", TaskPriority.Urgent, completedLocal: Now)
        };

        var ordered = BriefingBuilder.Prioritised(todos).Select(t => t.Title).ToArray();

        Assert.Equal(new[] { "urgent", "high sooner", "high later", "normal" }, ordered);
    }

    [Fact]
    public void The_morning_briefing_greets_by_name()
    {
        var briefing = BriefingBuilder.MorningBriefing(" Sharvesh ", new List<TodoItem> { Todo(1, "Read chapter 4") }, new List<Reminder>(), Now);
        Assert.StartsWith("Good morning, Sharvesh. It is ", briefing);
        Assert.Contains("Read chapter 4", briefing);

        Assert.StartsWith("Good morning. It is", BriefingBuilder.MorningBriefing("", new List<TodoItem>(), new List<Reminder>(), Now));
    }

    [Fact]
    public void The_end_of_day_summary_counts_completed_and_open_work()
    {
        var todos = new List<TodoItem>
        {
            Todo(1, "Finished today", completedLocal: Now.AddHours(-1)),
            Todo(2, "Finished last week", completedLocal: Now.AddDays(-7)),
            Todo(3, "Still open"),
            Todo(4, "Late", deadlineLocal: Now.AddDays(-1))
        };
        var reminders = new List<Reminder> { ReminderAt("Dentist", Now.Date.AddDays(1).AddHours(9)) };

        var summary = BriefingBuilder.EndOfDay(todos, reminders, Now);

        Assert.Contains("Completed today: 1.", summary);
        Assert.Contains("Finished today", summary);
        Assert.DoesNotContain("Finished last week", summary);
        Assert.Contains("Still open: 2 (1 overdue).", summary);
        Assert.Contains("09:00 — Dentist", summary);
    }
}

public class ModeCatalogTests
{
    [Theory]
    [InlineData("study", JarvisMode.Study)]
    [InlineData("Dev Mode", JarvisMode.Development)]
    [InlineData("coding", JarvisMode.Development)]
    [InlineData("deep work", JarvisMode.Focus)]
    [InlineData(" RESEARCH ", JarvisMode.Research)]
    [InlineData("maintenance", JarvisMode.System)]
    [InlineData("personal", JarvisMode.Personal)]
    public void Mode_names_and_synonyms_are_understood(string text, JarvisMode expected)
    {
        Assert.True(ModeCatalog.TryParse(text, out var mode));
        Assert.Equal(expected, mode);
    }

    [Theory]
    [InlineData("banana")]
    [InlineData("")]
    [InlineData(null)]
    public void Unknown_modes_are_rejected(string? text)
    {
        Assert.False(ModeCatalog.TryParse(text, out _));
    }

    [Fact]
    public void Every_mode_is_defined_and_actually_differs_in_tool_access()
    {
        foreach (var mode in Enum.GetValues<JarvisMode>())
        {
            var definition = ModeCatalog.Get(mode);
            Assert.Equal(mode, definition.Mode);
            Assert.False(string.IsNullOrWhiteSpace(definition.Behaviour));
            Assert.True(definition.Allows(ToolCategory.General));
        }

        Assert.True(ModeCatalog.Get(JarvisMode.Development).Allows(ToolCategory.Development));
        Assert.False(ModeCatalog.Get(JarvisMode.Study).Allows(ToolCategory.Development));
        Assert.False(ModeCatalog.Get(JarvisMode.Focus).Allows(ToolCategory.Files));
        Assert.True(ModeCatalog.Get(JarvisMode.Focus).Allows(ToolCategory.Focus));
        Assert.True(ModeCatalog.Get(JarvisMode.Research).Allows(ToolCategory.Web));
        Assert.False(ModeCatalog.Get(JarvisMode.System).Allows(ToolCategory.Planner));
        Assert.True(ModeCatalog.Get(JarvisMode.System).Allows(ToolCategory.System));
    }
}

public class WorkflowTests
{
    private static AppSettings SettingsWithProject()
    {
        var settings = new AppSettings();
        settings.GetMode(JarvisMode.Development).ProjectPath = @"C:\Projects\demo";
        settings.ApprovedCommands.Add(new ApprovedCommand { Name = "restore", Executable = "dotnet", RunOnDevSetup = true });
        settings.ApprovedCommands.Add(new ApprovedCommand { Name = "deploy", Executable = "deploy.cmd", RunOnDevSetup = false });
        return settings;
    }

    [Fact]
    public void Every_listed_workflow_builds_steps()
    {
        Assert.Equal(5, Workflows.All.Count);
        foreach (var workflow in Workflows.All)
        {
            var steps = Workflows.Build(workflow.Name, SettingsWithProject());
            Assert.NotEmpty(steps);
            Assert.All(steps, step => Assert.False(string.IsNullOrWhiteSpace(step.Tool)));
            Assert.All(steps, step => Assert.False(string.IsNullOrWhiteSpace(step.Description)));
        }
    }

    [Fact]
    public void Dev_setup_runs_only_commands_marked_for_it()
    {
        var steps = Workflows.Build("prepare_dev_environment", SettingsWithProject());

        Assert.Equal("switch_mode", steps[0].Tool);
        Assert.Contains(steps, s => s.Tool == "open_terminal");
        Assert.Contains(steps, s => s.Tool == "git_status");
        var commands = steps.Where(s => s.Tool == "run_approved_command").ToList();
        Assert.Single(commands);
        Assert.Equal("restore", commands[0].Args["name"]!.GetValue<string>());
    }

    [Fact]
    public void Dev_setup_without_a_project_skips_project_steps()
    {
        var steps = Workflows.Build("prepare_dev_environment", new AppSettings());

        Assert.Single(steps);
        Assert.Equal("switch_mode", steps[0].Tool);
    }

    [Fact]
    public void No_workflow_contains_a_destructive_tool()
    {
        var destructive = new[] { "delete", "remove", "format", "shutdown", "kill" };
        foreach (var workflow in Workflows.All)
        {
            foreach (var step in Workflows.Build(workflow.Name, SettingsWithProject()))
                Assert.DoesNotContain(destructive, word => step.Tool.Contains(word, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void Workflows_are_found_by_name_ignoring_case()
    {
        Assert.NotNull(Workflows.Find("SYSTEM_CHECKUP"));
        Assert.Null(Workflows.Find("make_coffee"));
        Assert.Null(Workflows.Find(null));
        Assert.Empty(Workflows.Build("make_coffee", new AppSettings()));
    }
}
