using System.Text.Json.Nodes;
using Jarvis.Core.Models;
using Jarvis.Infrastructure.Data;
using Jarvis.Infrastructure.Search;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jarvis.Tests;

/// <summary>Persistence, exercised against a real SQLite database file in a temporary folder.</summary>
public class StoreTests : IDisposable
{
    private static readonly DateTime Now = new DateTime(2026, 11, 14, 10, 0, 0);

    private readonly TempFolder _temp = new TempFolder();
    private readonly FakeClock _clock = new FakeClock(Now);
    private readonly string _path;
    private Database _db;

    public StoreTests()
    {
        _path = Path.Combine(_temp.Root, "jarvis.db");
        _db = new Database(_path);
    }

    public void Dispose()
    {
        _db.Dispose();
        _temp.Dispose();
    }

    /// <summary>Simulates closing and restarting the application.</summary>
    private void Reopen()
    {
        _db.Dispose();
        _db = new Database(_path);
    }

    [Fact]
    public void Settings_survive_a_restart()
    {
        var store = new SettingsStore(_db);
        var settings = store.Current.Clone();
        settings.UserName = "Sharvesh";
        settings.CurrentMode = JarvisMode.Development;
        settings.ToolPermissions["delete_path"] = PermissionLevel.Deny;
        settings.GetMode(JarvisMode.Study).Apps.Add("notepad");
        settings.AppAliases["code"] = @"C:\Tools\Code.exe";
        store.Save(settings);

        Reopen();
        var loaded = new SettingsStore(_db).Current;

        Assert.Equal("Sharvesh", loaded.UserName);
        Assert.Equal(JarvisMode.Development, loaded.CurrentMode);
        Assert.Equal(PermissionLevel.Deny, loaded.ToolPermissions["DELETE_PATH"]);
        Assert.Contains("notepad", loaded.GetMode(JarvisMode.Study).Apps);
        Assert.Equal(@"C:\Tools\Code.exe", loaded.AppAliases["Code"]);
    }

    [Fact]
    public void Saving_settings_notifies_listeners()
    {
        var store = new SettingsStore(_db);
        AppSettings? seen = null;
        store.Changed += s => seen = s;

        var settings = store.Current.Clone();
        settings.SpeechRate = 3;
        store.Save(settings);

        Assert.NotNull(seen);
        Assert.Equal(3, store.Current.SpeechRate);
    }

    [Fact]
    public void Corrupt_settings_fall_back_to_defaults()
    {
        new SettingsStore(_db).SetValue("app_settings", "{ this is not json");

        var loaded = new SettingsStore(_db).Current;

        Assert.Equal(new AppSettings().GeminiModel, loaded.GeminiModel);
        Assert.Equal(JarvisMode.Personal, loaded.CurrentMode);
    }

    [Fact]
    public void Settings_never_contain_the_api_key()
    {
        // "Hotkey" settings are keyboard shortcuts, not credentials.
        var names = typeof(AppSettings).GetProperties().Select(p => p.Name.ToLowerInvariant().Replace("hotkey", "")).ToList();
        Assert.DoesNotContain(names, n => n.Contains("key") || n.Contains("secret") || n.Contains("token") || n.Contains("password"));
    }

    [Fact]
    public void Conversations_and_messages_survive_a_restart()
    {
        var store = new ConversationStore(_db, _clock);
        var conversation = store.Create("Thesis planning", JarvisMode.Study);
        store.AddMessage(conversation.Id, MessageRole.User, "hello");
        store.AddMessage(conversation.Id, MessageRole.Assistant, "Hi there.");
        conversation.Summary = "Greeting exchanged.";
        conversation.SummarizedThrough = 1;
        store.Update(conversation);

        Reopen();
        store = new ConversationStore(_db, _clock);

        var loaded = store.Get(conversation.Id);
        Assert.NotNull(loaded);
        Assert.Equal("Thesis planning", loaded!.Title);
        Assert.Equal(JarvisMode.Study, loaded.Mode);
        Assert.Equal("Greeting exchanged.", loaded.Summary);
        Assert.Equal(1, loaded.SummarizedThrough);

        var messages = store.GetMessages(conversation.Id);
        Assert.Equal(2, messages.Count);
        Assert.Equal(MessageRole.User, messages[0].Role);
        Assert.Equal("Hi there.", messages[1].Content);
        Assert.Equal(1, store.CountMessages(conversation.Id, messages[0].Id));
        Assert.Single(store.List(10));
    }

    [Fact]
    public void Deleting_a_conversation_removes_its_messages()
    {
        var store = new ConversationStore(_db, _clock);
        var keep = store.Create("Keep", JarvisMode.Personal);
        var drop = store.Create("Drop", JarvisMode.Personal);
        store.AddMessage(keep.Id, MessageRole.User, "one");
        store.AddMessage(drop.Id, MessageRole.User, "two");

        store.Delete(drop.Id);

        Assert.Null(store.Get(drop.Id));
        Assert.Empty(store.GetMessages(drop.Id));
        Assert.Single(store.GetMessages(keep.Id));

        store.DeleteAll();
        Assert.Empty(store.List(10));
        Assert.Empty(store.GetMessages(keep.Id));
    }

    [Fact]
    public void Memory_is_persisted_searchable_editable_and_deletable()
    {
        var store = new MemoryStore(_db, _clock);
        var thesis = store.Add("project", "Thesis deadline is 30 November", MemorySource.UserProvided);
        store.Add("preference", "Likes filter coffee", MemorySource.Inferred);

        Reopen();
        store = new MemoryStore(_db, _clock);

        Assert.Equal(2, store.List().Count);
        var hits = store.Search("when is my thesis deadline?", 5);
        Assert.Single(hits);
        Assert.Equal(thesis.Id, hits[0].Id);
        Assert.Equal(MemorySource.UserProvided, hits[0].Source);
        Assert.Empty(store.Search("quantum chromodynamics", 5));

        Assert.True(store.Update(thesis.Id, "project", "Thesis deadline moved to 5 December"));
        Assert.Contains("5 December", store.List().Single(m => m.Id == thesis.Id).Content);

        Assert.True(store.Delete(thesis.Id));
        Assert.False(store.Delete(thesis.Id));
        Assert.Single(store.List());

        store.Clear();
        Assert.Empty(store.List());
    }

    [Fact]
    public void Reminders_survive_a_restart_and_become_due()
    {
        var store = new PlannerStore(_db, _clock);
        var soon = store.AddReminder("Call mom", _clock.UtcNow.AddMinutes(30), RecurrenceKind.Weekly);
        store.AddReminder("Dentist", _clock.UtcNow.AddDays(3), RecurrenceKind.None);

        Reopen();
        store = new PlannerStore(_db, _clock);

        Assert.Equal(2, store.ListReminders(activeOnly: true).Count);
        Assert.Empty(store.DueReminders(_clock.UtcNow));
        Assert.Equal(soon.DueUtc, store.NextDueUtc());

        var due = store.DueReminders(_clock.UtcNow.AddHours(1));
        var reminder = Assert.Single(due);
        Assert.Equal("Call mom", reminder.Text);
        Assert.Equal(RecurrenceKind.Weekly, reminder.Recurrence);
        Assert.Equal(DateTimeKind.Utc, reminder.DueUtc.Kind);

        reminder.Active = false;
        store.UpdateReminder(reminder);
        Assert.Single(store.ListReminders(activeOnly: true));
        Assert.Equal(2, store.ListReminders(activeOnly: false).Count);

        Assert.True(store.DeleteReminder(reminder.Id));
        Assert.False(store.DeleteReminder(reminder.Id));
    }

    [Fact]
    public void Tasks_keep_priority_deadline_and_completion()
    {
        var store = new PlannerStore(_db, _clock);
        store.AddTodo("Low thing", "", TaskPriority.Low, null);
        var urgent = store.AddTodo("Fix login bug", "see issue 12", TaskPriority.Urgent, _clock.UtcNow.AddDays(1));

        Reopen();
        store = new PlannerStore(_db, _clock);

        var open = store.ListTodos(includeCompleted: false);
        Assert.Equal(2, open.Count);
        Assert.Equal("Fix login bug", open[0].Title);
        Assert.Equal(TaskPriority.Urgent, open[0].Priority);
        Assert.Equal("see issue 12", open[0].Notes);
        Assert.Equal(urgent.DeadlineUtc, open[0].DeadlineUtc);
        Assert.Null(open[1].DeadlineUtc);

        var done = open[0];
        done.Completed = true;
        done.CompletedUtc = _clock.UtcNow;
        store.UpdateTodo(done);

        Assert.Single(store.ListTodos(includeCompleted: false));
        Assert.Equal(2, store.ListTodos(includeCompleted: true).Count);
        Assert.True(store.DeleteTodo(done.Id));
        Assert.Single(store.ListTodos(includeCompleted: true));
    }

    [Fact]
    public void Background_tasks_are_checkpointed_with_their_steps()
    {
        var store = new AgentTaskStore(_db, _clock);
        var task = new AgentTask { Title = "Organise downloads", Description = "tidy", Mode = JarvisMode.System, State = AgentTaskState.Running, CurrentStep = 1 };
        task.Steps.Add(new AgentStep { Index = 0, Tool = "find_duplicates", Description = "Look for duplicates", State = StepState.Completed, Result = "none" });
        task.Steps.Add(new AgentStep
        {
            Index = 1, Tool = "organise_folder", Description = "Sort files", State = StepState.Running,
            Args = new JsonObject { ["folder"] = "Downloads" }, Attempts = 1
        });
        store.Save(task);

        Reopen();
        store = new AgentTaskStore(_db, _clock);

        var loaded = store.Get(task.Id);
        Assert.NotNull(loaded);
        Assert.Equal(AgentTaskState.Running, loaded!.State);
        Assert.Equal(JarvisMode.System, loaded.Mode);
        Assert.Equal(1, loaded.CurrentStep);
        Assert.Equal(2, loaded.Steps.Count);
        Assert.Equal(StepState.Completed, loaded.Steps[0].State);
        Assert.Equal("none", loaded.Steps[0].Result);
        Assert.Equal("Downloads", loaded.Steps[1].Args["folder"]!.GetValue<string>());
        Assert.Equal(1, loaded.Steps[1].Attempts);
        Assert.False(loaded.Held);
    }

    [Fact]
    public void Task_state_transitions_are_atomic_and_conditional()
    {
        var store = new AgentTaskStore(_db, _clock);
        var task = new AgentTask { Title = "t", State = AgentTaskState.Pending };
        store.Save(task);

        Assert.True(store.TryTransition(task.Id, AgentTaskState.Pending, AgentTaskState.Running));
        Assert.False(store.TryTransition(task.Id, AgentTaskState.Pending, AgentTaskState.Running));
        Assert.Equal(AgentTaskState.Running, store.Get(task.Id)!.State);

        task.State = AgentTaskState.Paused;
        task.Held = true;
        store.Save(task);
        Assert.True(store.Get(task.Id)!.Held);
        Assert.Single(store.List(10));

        store.Delete(task.Id);
        Assert.Null(store.Get(task.Id));
    }

    [Fact]
    public void Focus_sessions_resume_after_a_restart()
    {
        var store = new FocusStore(_db, _clock);
        Assert.Null(store.Active());
        var session = store.Start("Write chapter 3", 50, new[] { "discord", "steam" });

        Reopen();
        store = new FocusStore(_db, _clock);

        var active = store.Active();
        Assert.NotNull(active);
        Assert.Equal(session.Id, active!.Id);
        Assert.Equal("Write chapter 3", active.Goal);
        Assert.Equal(50, active.PlannedMinutes);
        Assert.Equal(new[] { "discord", "steam" }, active.ClosedApps);

        store.End(active.Id, _clock.UtcNow.AddMinutes(50));
        Assert.Null(store.Active());
        Assert.NotNull(Assert.Single(store.Recent(5)).EndUtc);
    }

    [Fact]
    public void File_moves_are_journalled_per_batch_until_undone()
    {
        var journal = new FileOpJournal(_db, _clock);
        Assert.Null(journal.LatestBatchId());
        journal.Record("batch-1", @"C:\a\one.txt", @"C:\a\Documents\one.txt");
        journal.Record("batch-1", @"C:\a\two.jpg", @"C:\a\Images\two.jpg");
        journal.Record("batch-2", @"C:\b\three.txt", @"C:\b\Documents\three.txt");

        Reopen();
        journal = new FileOpJournal(_db, _clock);

        Assert.Equal("batch-2", journal.LatestBatchId());
        Assert.Equal(2, journal.GetBatch("batch-1").Count);
        Assert.Equal(@"C:\a\one.txt", journal.GetBatch("batch-1")[0].Source);

        journal.MarkUndone("batch-2");
        Assert.Equal("batch-1", journal.LatestBatchId());
        Assert.Empty(journal.GetBatch("batch-2"));
    }

    [Fact]
    public void Audit_entries_are_kept_newest_first()
    {
        var audit = new AuditLog(_db, _clock);
        audit.Record("launch_app", "Routine", "allowed", true, "Opened Notepad");
        audit.Record("delete_path", "Consequential", "declined", false, "Delete report.docx");

        var entries = audit.Recent(10);

        Assert.Equal(2, entries.Count);
        Assert.Equal("delete_path", entries[0].Tool);
        Assert.Equal("declined", entries[0].Decision);
        Assert.False(entries[0].Success);
        Assert.True(entries[1].Success);
    }

    [Fact]
    public void Search_index_finds_files_by_name_and_by_content()
    {
        var docs = _temp.Sub("docs");
        _temp.File(Path.Combine("docs", "meeting-notes.txt"), "We agreed to ship the photosynthesis report on Friday.");
        _temp.File(Path.Combine("docs", "budget.csv"), "item,cost\nlaptop,900");
        _temp.File(Path.Combine("docs", "holiday.jpg"), "not really an image");
        var index = new SearchIndex(_db, new SettingsStore(_db), NullLogger<SearchIndex>.Instance);

        var stats = index.Rebuild(new[] { docs }, CancellationToken.None);

        Assert.Equal(3, stats.Files);
        Assert.Equal(2, stats.FilesWithContent);
        Assert.NotNull(stats.LastBuiltUtc);

        var byName = index.Search("budget", 10);
        Assert.Equal("budget.csv", Assert.Single(byName).Name);
        Assert.Equal("filename", byName[0].MatchKind);

        var byContent = index.Search("photosynthesis", 10);
        var hit = Assert.Single(byContent);
        Assert.Equal("meeting-notes.txt", hit.Name);
        Assert.Equal("content", hit.MatchKind);
        Assert.Contains("photosynthesis", hit.Excerpt, StringComparison.OrdinalIgnoreCase);

        Assert.Equal("holiday.jpg", Assert.Single(index.Search("holiday", 10)).Name);
        Assert.Empty(index.Search("holiday", 10, new[] { ".pdf" }));
        Assert.Empty(index.Search("zebra", 10));
        Assert.Empty(index.Search("   ", 10));
    }

    [Fact]
    public void Search_index_follows_changes_on_rebuild_and_can_be_cleared()
    {
        var docs = _temp.Sub("docs");
        var first = _temp.File(Path.Combine("docs", "alpha.txt"), "contains walrus");
        _temp.File(Path.Combine("docs", "beta.txt"), "contains nothing special");
        var index = new SearchIndex(_db, new SettingsStore(_db), NullLogger<SearchIndex>.Instance);
        index.Rebuild(new[] { docs }, CancellationToken.None);
        Assert.Single(index.Search("walrus", 10));

        File.Delete(first);
        _temp.File(Path.Combine("docs", "gamma.txt"), "contains narwhal");
        var stats = index.Rebuild(new[] { docs }, CancellationToken.None);

        Assert.Equal(2, stats.Files);
        Assert.Empty(index.Search("walrus", 10));
        Assert.Single(index.Search("narwhal", 10));

        index.Clear();
        Assert.Equal(0, index.Stats().Files);
        Assert.Empty(index.Search("narwhal", 10));
    }

    [Fact]
    public void Search_text_is_always_turned_into_a_safe_query()
    {
        Assert.Equal("\"hello\" \"wor\"*", SearchIndex.BuildMatchExpression("hello wor"));
        Assert.Null(SearchIndex.BuildMatchExpression("  \"\" ** "));

        var index = new SearchIndex(_db, new SettingsStore(_db), NullLogger<SearchIndex>.Instance);
        Assert.Empty(index.Search("\" OR 1=1 --", 10));
        Assert.Empty(index.Search("NEAR(a b) AND *", 10));
    }
}
