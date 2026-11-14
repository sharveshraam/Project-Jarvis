using System.Text.Json.Nodes;
using Jarvis.Agent;
using Jarvis.Agent.Tools;
using Jarvis.Core.Models;
using Jarvis.Core.Planning;
using Jarvis.Core.Security;
using Jarvis.Core.Tools;
using Jarvis.Infrastructure.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jarvis.Tests;

/// <summary>
/// End-to-end behaviour without Gemini: typed commands go through the local interpreter, the real executor and
/// the real tools into a real SQLite database. Only Windows itself (apps, shell) is replaced by recording fakes.
/// </summary>
public class AgentFlowTests : IDisposable
{
    // Saturday 14 November 2026, 10:00 local time.
    private static readonly DateTime Now = new DateTime(2026, 11, 14, 10, 0, 0);
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    private readonly TempFolder _temp = new TempFolder();
    private readonly FakeClock _clock = new FakeClock(Now);
    private readonly ExecutorHarness _harness = new ExecutorHarness();
    private readonly RecordingNotifier _notifier = new RecordingNotifier();
    private readonly FakeApps _apps = new FakeApps();
    private readonly FakeShell _shell = new FakeShell();
    private readonly FakeAi _ai = new FakeAi { IsConfigured = false };
    private readonly PlannerSignal _signal = new PlannerSignal();
    private readonly string _path;
    private Database _db;
    private PlannerStore _planner;
    private MemoryStore _memory;
    private FocusStore _focus;
    private readonly ModeService _modes;
    private readonly TaskEngine _engine;
    private readonly OfflineAssistant _assistant;
    private int _signals;

    public AgentFlowTests()
    {
        _path = Path.Combine(_temp.Root, "jarvis.db");
        _db = new Database(_path);
        _planner = new PlannerStore(_db, _clock);
        _memory = new MemoryStore(_db, _clock);
        _focus = new FocusStore(_db, _clock);
        _signal.Changed += () => _signals++;

        var paths = new PathPolicy(() => _harness.Settings.Current, _temp.Root, Array.Empty<string>());
        _modes = new ModeService(_harness.Settings, _apps, _shell, paths, NullLogger<ModeService>.Instance);
        _engine = new TaskEngine(new AgentTaskStore(_db, _clock), _harness.Executor, _ai, _harness.Settings, _notifier, NullLogger<TaskEngine>.Instance)
        {
            PausePollInterval = TimeSpan.FromMilliseconds(10)
        };

        _harness.Registry.RegisterRange(new PlannerTools(_planner, _clock, _signal).Build());
        _harness.Registry.RegisterRange(new MemoryTools(_memory).Build());
        _harness.Registry.RegisterRange(new FocusTools(_focus, _apps, _harness.Settings, _planner, _clock, _signal).Build());
        _harness.Registry.RegisterRange(new AppTools(_apps, _shell).Build());
        _harness.Registry.RegisterRange(new AgentTools(_engine, _modes, _harness.Settings, _clock).Build());

        _assistant = new OfflineAssistant(_harness.Executor, _modes, _clock);
    }

    public void Dispose()
    {
        _engine.Dispose();
        _db.Dispose();
        _temp.Dispose();
    }

    private async Task<string> Say(string text)
    {
        var reply = await _assistant.TryHandleAsync(text, CancellationToken.None);
        Assert.True(reply is not null, $"'{text}' was not handled locally.");
        return reply!;
    }

    private SchedulerService NewScheduler()
        => new SchedulerService(_planner, _focus, _harness.Settings, _notifier, _clock, _signal, NullLogger<SchedulerService>.Instance);

    [Fact]
    public async Task A_spoken_reminder_is_stored_listed_and_delivered_when_due()
    {
        var reply = await Say("Remind me to call mom tomorrow at 7 pm");
        Assert.Contains("Reminder set for tomorrow 19:00", reply);
        Assert.Contains("Call mom", reply);
        Assert.Equal(1, _signals);

        var stored = Assert.Single(_planner.ListReminders(activeOnly: true));
        Assert.Equal(new DateTime(2026, 11, 15, 19, 0, 0), TimeText.ToLocal(stored.DueUtc));
        Assert.Contains("Call mom", await Say("show my reminders"));

        var announcements = new List<Announcement>();
        using var scheduler = NewScheduler();
        scheduler.Announced += announcements.Add;

        Assert.Equal(0, scheduler.RunDue());

        _clock.LocalNow = new DateTime(2026, 11, 15, 19, 0, 30);
        Assert.Equal(1, scheduler.RunDue());
        var announcement = Assert.Single(announcements);
        Assert.Equal("Reminder", announcement.Title);
        Assert.Equal("Call mom", announcement.Text);
        Assert.True(announcement.Speak);
        Assert.Contains(_notifier.Notifications, n => n.Title == "Reminder" && n.Message == "Call mom");

        Assert.Equal(0, scheduler.RunDue());
        Assert.Empty(_planner.ListReminders(activeOnly: true));
    }

    [Fact]
    public async Task A_reminder_missed_while_jarvis_was_closed_is_delivered_late_after_restart()
    {
        await Say("remind me to submit the report today at 11 am");

        // JARVIS is closed, the due time passes, and it is started again three hours later.
        _db.Dispose();
        _db = new Database(_path);
        _planner = new PlannerStore(_db, _clock);
        _focus = new FocusStore(_db, _clock);
        _clock.LocalNow = Now.AddHours(4);

        var announcements = new List<Announcement>();
        using var scheduler = NewScheduler();
        scheduler.Announced += announcements.Add;

        Assert.Equal(1, scheduler.RunDue());
        Assert.Equal("Submit the report (was due today 11:00)", Assert.Single(announcements).Text);
        Assert.Equal(0, scheduler.RunDue());
    }

    [Fact]
    public async Task A_recurring_reminder_moves_to_its_next_occurrence_instead_of_ending()
    {
        await Say("remind me every Sunday at 7 pm to call mom");
        _clock.LocalNow = new DateTime(2026, 11, 15, 19, 0, 10);
        using var scheduler = NewScheduler();

        Assert.Equal(1, scheduler.RunDue());

        var next = Assert.Single(_planner.ListReminders(activeOnly: true));
        Assert.Equal(RecurrenceKind.Weekly, next.Recurrence);
        Assert.Equal(new DateTime(2026, 11, 22, 19, 0, 0), TimeText.ToLocal(next.DueUtc));
        Assert.Equal(0, scheduler.RunDue());
    }

    [Fact]
    public async Task A_reminder_in_the_past_is_refused_with_the_reason()
    {
        var result = await _harness.Run("add_reminder", new JsonObject { ["text"] = "Too late", ["due_local"] = "2026-11-14T08:00" });

        Assert.False(result.Success);
        Assert.Contains("already in the past", result.Message);
        Assert.Empty(_planner.ListReminders(activeOnly: false));
    }

    [Fact]
    public async Task An_unreadable_time_is_reported_instead_of_guessed()
    {
        var result = await _harness.Run("add_reminder", new JsonObject { ["text"] = "Vague", ["due_local"] = "whenever" });

        Assert.False(result.Success);
        Assert.Contains("could not work out a time", result.Message);
    }

    [Fact]
    public async Task Tasks_can_be_added_listed_completed_and_deleted_by_voice()
    {
        Assert.Contains("Task added: Call the bank [Urgent] — due tomorrow 15:00", await Say("add an urgent task to call the bank tomorrow at 3 pm"));
        Assert.Contains("Task added: Finish the physics assignment", await Say("add task finish the physics assignment"));

        var listing = await Say("show my tasks");
        Assert.StartsWith("2 open tasks:", listing);
        Assert.True(listing.IndexOf("Call the bank", StringComparison.Ordinal) < listing.IndexOf("Finish the physics", StringComparison.Ordinal));

        var urgent = _planner.ListTodos(includeCompleted: false).Single(t => t.Priority == TaskPriority.Urgent);
        Assert.Contains("Marked 'Call the bank' as done.", await Say("complete task " + urgent.Id));
        Assert.Contains("already completed", await Say("complete task " + urgent.Id));
        Assert.StartsWith("1 open task:", await Say("show my tasks"));

        Assert.Contains("There is no task #999.", await Say("complete task 999"));

        var other = _planner.ListTodos(includeCompleted: false).Single();
        await Say("delete task " + other.Id);
        Assert.Empty(_planner.ListTodos(includeCompleted: false));
    }

    [Fact]
    public async Task The_daily_plan_and_day_summary_reflect_stored_work()
    {
        Assert.StartsWith("Nothing is scheduled for today", await Say("plan my day"));

        await Say("add an urgent task to fix the login bug");
        await Say("remind me to stretch today at 4 pm");

        var plan = await Say("plan my day");
        Assert.Contains("Fix the login bug [Urgent]", plan);
        Assert.Contains("Stretch", plan);

        Assert.Contains("Completed today: 0.", await Say("summarise my day"));
    }

    [Fact]
    public async Task Memory_stores_facts_once_and_recalls_them()
    {
        Assert.Equal("Noted: my thesis deadline is 30 November", await Say("remember that my thesis deadline is 30 November"));
        Assert.Equal("That is already in memory.", await Say("remember that my thesis deadline is 30 November"));
        var item = Assert.Single(_memory.List());
        Assert.Equal(MemorySource.UserProvided, item.Source);

        var recalled = await _harness.Run("recall", new JsonObject { ["query"] = "thesis deadline" });
        Assert.Contains("30 November", recalled.Message);

        var nothing = await _harness.Run("recall", new JsonObject { ["query"] = "quantum chromodynamics" });
        Assert.Equal("Nothing relevant is stored in memory.", nothing.Message);

        Assert.Contains("30 November", await Say("what do you remember about me"));
    }

    [Theory]
    [InlineData("remember that my password is hunter2abc")]
    [InlineData("remember that my api key is AIzaSyA1234567890abcdefghijklmnopqrstuvw")]
    [InlineData("remember my bank PIN is 4821")]
    public async Task Secrets_are_never_written_to_memory(string text)
    {
        var reply = await Say(text);

        Assert.StartsWith("Not stored:", reply);
        Assert.Empty(_memory.List());
    }

    [Fact]
    public async Task Inferred_memories_are_labelled()
    {
        await _harness.Run("remember", new JsonObject { ["content"] = "Probably prefers Rust", ["category"] = "preference", ["source"] = "inferred" });

        Assert.Equal(MemorySource.Inferred, Assert.Single(_memory.List()).Source);
        Assert.Contains("inferred", (await _harness.Run("list_memories")).Message);
    }

    [Fact]
    public async Task Switching_mode_is_persisted_and_announced()
    {
        var seen = new List<JarvisMode>();
        _modes.Changed += seen.Add;

        var reply = await Say("switch to study mode");

        Assert.StartsWith("Study mode is active.", reply);
        Assert.Equal(JarvisMode.Study, _harness.Settings.Current.CurrentMode);
        Assert.Equal(JarvisMode.Study, _modes.Current);
        Assert.Equal(new[] { JarvisMode.Study }, seen);
    }

    [Fact]
    public async Task Opening_a_workspace_reports_what_opened_and_what_could_not()
    {
        var notes = _temp.Sub("Notes");
        var settings = _harness.Settings.Current.Clone();
        var study = settings.GetMode(JarvisMode.Study);
        study.Apps.Add("notepad");
        study.Apps.Add("ghostapp");
        study.Folders.Add(notes);
        study.Folders.Add(Path.Combine(_temp.Root, "DoesNotExist"));
        settings.CurrentMode = JarvisMode.Study;
        _harness.Settings.Save(settings);
        _apps.Missing.Add("ghostapp");

        var (opened, failed, report) = _modes.OpenWorkspace(JarvisMode.Study);

        Assert.Equal(2, opened);
        Assert.Equal(2, failed);
        Assert.Equal(new[] { "notepad" }, _apps.Launched);
        Assert.Contains("path:" + notes, _shell.Opened);
        Assert.Contains("Could not open:", report);
        Assert.Contains("ghostapp", report);
        Assert.Contains("it does not exist", report);

        var again = _modes.OpenWorkspace(JarvisMode.Study);
        Assert.Contains("notepad (already running)", again.Report);
        Assert.Single(_apps.Launched);
    }

    [Fact]
    public void A_mode_without_a_workspace_says_so()
    {
        var (opened, failed, report) = _modes.OpenWorkspace(JarvisMode.Research);

        Assert.Equal(0, opened);
        Assert.Equal(0, failed);
        Assert.Contains("No workspace is configured for Research mode", report);
    }

    [Fact]
    public async Task A_focus_session_is_tracked_and_its_end_is_announced_once()
    {
        var reply = await Say("start focus for 25 minutes on thesis");
        Assert.StartsWith("Focus session started: thesis. 25 minutes, until 10:25.", reply);
        Assert.Contains("already running", await Say("start a focus session"));

        var announcements = new List<Announcement>();
        using var scheduler = NewScheduler();
        scheduler.Announced += announcements.Add;
        Assert.Equal(0, scheduler.RunDue());

        _clock.LocalNow = Now.AddMinutes(26);
        Assert.Equal(1, scheduler.RunDue());
        Assert.Equal(0, scheduler.RunDue());
        Assert.Contains("Time is up for: thesis", Assert.Single(announcements).Text);

        await Say("end focus session");
        Assert.Null(_focus.Active());
    }

    [Fact]
    public async Task Briefings_are_delivered_once_per_day_at_the_configured_time()
    {
        var settings = _harness.Settings.Current.Clone();
        settings.UserName = "Sharvesh";
        settings.MorningBriefingTime = "08:30";
        settings.EndOfDaySummaryTime = "18:00";
        _harness.Settings.Save(settings);
        await Say("add task read chapter 4");
        var announcements = new List<Announcement>();
        using var scheduler = NewScheduler();
        scheduler.Announced += announcements.Add;

        Assert.Equal(1, scheduler.RunDue());
        Assert.Equal("Morning briefing", announcements[0].Title);
        Assert.Contains("Good morning, Sharvesh.", announcements[0].Text);
        Assert.Contains("Read chapter 4", announcements[0].Text);
        Assert.Equal(0, scheduler.RunDue());

        _clock.LocalNow = Now.Date.AddHours(18).AddMinutes(5);
        Assert.Equal(1, scheduler.RunDue());
        Assert.Equal("End-of-day summary", announcements[1].Title);

        _clock.LocalNow = Now.Date.AddDays(1).AddHours(9);
        Assert.Equal(1, scheduler.RunDue());
        Assert.Equal("Morning briefing", announcements[2].Title);
    }

    [Theory]
    [InlineData("08:30", 8, 29, false)]
    [InlineData("08:30", 8, 30, true)]
    [InlineData("08:30", 14, 30, true)]
    [InlineData("08:30", 14, 31, false)]
    [InlineData("", 9, 0, false)]
    [InlineData("25:99", 9, 0, false)]
    public void Briefing_window_opens_at_the_set_time_and_closes_six_hours_later(string configured, int hour, int minute, bool due)
    {
        Assert.Equal(due, SchedulerService.IsBriefingDue(configured, new DateTime(2026, 11, 14, hour, minute, 0)));
    }

    [Fact]
    public async Task A_built_in_workflow_runs_in_the_background_without_gemini()
    {
        var reply = await Say("start a study session");
        Assert.Contains("'Start study session' started with 3 steps", reply);

        var task = Assert.Single(_engine.List(10));
        await _engine.WaitAsync(task.Id).WaitAsync(Limit);

        var finished = _engine.Find(task.Id);
        Assert.NotNull(finished);
        Assert.Equal(AgentTaskState.Completed, finished!.State);
        Assert.Equal(JarvisMode.Study, _harness.Settings.Current.CurrentMode);
        Assert.NotNull(_focus.Active());
        Assert.Empty(_ai.Requests);
        Assert.Contains(_notifier.Notifications, n => n.Title == "Task completed");
    }

    [Fact]
    public async Task App_commands_report_the_real_outcome()
    {
        Assert.Contains("Opened notepad", await Say("open notepad"));
        Assert.Equal(new[] { "notepad" }, _apps.Launched);

        _apps.Missing.Add("ghostapp");
        Assert.Contains("No application named 'ghostapp' was found", await Say("open ghostapp"));

        Assert.Contains("github.com", await Say("open github.com"));
        Assert.Contains(_shell.Opened, o => o.StartsWith("url:", StringComparison.Ordinal) && o.Contains("github.com"));
    }

    [Theory]
    [InlineData("2026-11-20T08:05", 2026, 11, 20, 8, 5, RecurrenceKind.None)]
    [InlineData("2026-11-20", 2026, 11, 20, 9, 0, RecurrenceKind.None)]
    [InlineData("tomorrow at 7 pm", 2026, 11, 15, 19, 0, RecurrenceKind.None)]
    [InlineData("in 20 minutes", 2026, 11, 14, 10, 20, RecurrenceKind.None)]
    [InlineData("every day at 9 am", 2026, 11, 15, 9, 0, RecurrenceKind.Daily)]
    public void Times_are_accepted_as_iso_or_natural_language(string text, int year, int month, int day, int hour, int minute, RecurrenceKind recurrence)
    {
        Assert.True(PlannerTools.TryParseWhen(text, Now, out var due, out var parsedRecurrence));
        Assert.Equal(new DateTime(year, month, day, hour, minute, 0), due);
        Assert.Equal(recurrence, parsedRecurrence);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("whenever")]
    public void Unreadable_times_are_rejected(string? text)
    {
        Assert.False(PlannerTools.TryParseWhen(text, Now, out _, out _));
    }
}
