using System.Text.Json.Nodes;
using Jarvis.Agent;

namespace Jarvis.Tests;

/// <summary>The local command interpreter that keeps JARVIS useful without Gemini.</summary>
public class OfflineAssistantTests
{
    // Saturday 14 November 2026, 10:00 local time.
    private static readonly DateTime Now = new DateTime(2026, 11, 14, 10, 0, 0);

    private static OfflineAssistant.Intent Understand(string text)
    {
        var intent = OfflineAssistant.Interpret(text, Now, out var problem);
        Assert.True(intent is not null, $"'{text}' was not understood. {problem}");
        return intent!;
    }

    private static string? Text(JsonObject args, string name) => args[name]?.GetValue<string>();

    [Fact]
    public void Reminder_with_date_and_time()
    {
        var intent = Understand("remind me to submit the report tomorrow at 5 pm");
        Assert.Equal("add_reminder", intent.Tool);
        Assert.Equal("Submit the report", Text(intent.Args, "text"));
        Assert.Equal("2026-11-15T17:00", Text(intent.Args, "due_local"));
        Assert.Equal("none", Text(intent.Args, "recurrence"));
    }

    [Fact]
    public void Recurring_reminder()
    {
        var intent = Understand("Remind me every Sunday at 7 pm to call mom");
        Assert.Equal("add_reminder", intent.Tool);
        Assert.Equal("Call mom", Text(intent.Args, "text"));
        Assert.Equal("2026-11-15T19:00", Text(intent.Args, "due_local"));
        Assert.Equal("weekly", Text(intent.Args, "recurrence"));
    }

    [Fact]
    public void Reminder_without_a_time_explains_the_problem_instead_of_guessing()
    {
        var intent = OfflineAssistant.Interpret("remind me to buy milk", Now, out var problem);
        Assert.Null(intent);
        Assert.False(string.IsNullOrWhiteSpace(problem));
    }

    [Fact]
    public void Wake_word_and_politeness_are_ignored()
    {
        Assert.Equal("get_current_time", Understand("Hey Jarvis, what time is it?").Tool);
        Assert.Equal("list_tasks", Understand("Jarvis, please show my tasks").Tool);
        Assert.Equal("list_reminders", Understand("could you list my reminders").Tool);
    }

    [Fact]
    public void Task_with_priority_and_deadline()
    {
        var intent = Understand("add an urgent task to call the bank tomorrow at 3 pm");
        Assert.Equal("add_task", intent.Tool);
        Assert.Equal("Call the bank", Text(intent.Args, "title"));
        Assert.Equal("urgent", Text(intent.Args, "priority"));
        Assert.Equal("2026-11-15T15:00", Text(intent.Args, "deadline_local"));
    }

    [Fact]
    public void Plain_task_has_a_capitalised_title_and_no_deadline()
    {
        var intent = Understand("add task finish the physics assignment");
        Assert.Equal("add_task", intent.Tool);
        Assert.Equal("Finish the physics assignment", Text(intent.Args, "title"));
        Assert.False(intent.Args.ContainsKey("deadline_local"));
        Assert.False(intent.Args.ContainsKey("priority"));
    }

    [Fact]
    public void Task_titles_starting_with_to_are_not_truncated()
    {
        var intent = Understand("add task tokyo trip planning");
        Assert.Equal("Tokyo trip planning", Text(intent.Args, "title"));
    }

    [Fact]
    public void Task_and_reminder_ids_are_numbers()
    {
        var complete = Understand("complete task 4");
        Assert.Equal("complete_task", complete.Tool);
        Assert.Equal(4, complete.Args["id"]!.GetValue<long>());

        var remove = Understand("delete reminder #12");
        Assert.Equal("delete_reminder", remove.Tool);
        Assert.Equal(12, remove.Args["id"]!.GetValue<long>());

        Assert.Equal("delete_task", Understand("remove task 3").Tool);
    }

    [Theory]
    [InlineData("show my tasks", "list_tasks")]
    [InlineData("what are my reminders", "list_reminders")]
    [InlineData("show background tasks", "list_background_tasks")]
    [InlineData("plan my day", "get_daily_plan")]
    [InlineData("what should I work on", "get_daily_plan")]
    [InlineData("summarise my day", "get_day_summary")]
    [InlineData("open workspace", "open_workspace")]
    [InlineData("end focus session", "end_focus_session")]
    [InlineData("lock my computer", "lock_computer")]
    [InlineData("how is my laptop", "get_system_status")]
    [InlineData("what's using my memory", "get_top_processes")]
    [InlineData("what do you remember about me", "list_memories")]
    [InlineData("undo the last organise", "undo_last_organise")]
    [InlineData("rebuild the search index", "rebuild_search_index")]
    [InlineData("what is the date", "get_current_time")]
    public void Commands_without_arguments(string text, string tool)
    {
        var intent = Understand(text);
        Assert.Equal(tool, intent.Tool);
        Assert.Empty(intent.Args);
    }

    [Theory]
    [InlineData("switch to study mode", "study")]
    [InlineData("go into development mode", "development")]
    [InlineData("focus mode", "focus")]
    [InlineData("start research mode", "research")]
    public void Mode_switches(string text, string mode)
    {
        var intent = Understand(text);
        Assert.Equal("switch_mode", intent.Tool);
        Assert.Equal(mode, Text(intent.Args, "mode"));
    }

    [Theory]
    [InlineData("prepare my development environment", "prepare_dev_environment")]
    [InlineData("start a study session", "start_study_session")]
    [InlineData("run a system checkup", "system_checkup")]
    public void Built_in_workflows(string text, string workflow)
    {
        var intent = Understand(text);
        Assert.Equal("run_workflow", intent.Tool);
        Assert.Equal(workflow, Text(intent.Args, "name"));
        Assert.NotNull(Workflows.Find(workflow));
    }

    [Fact]
    public void Focus_session_with_duration_and_goal()
    {
        var intent = Understand("start focus for 25 minutes on thesis");
        Assert.Equal("start_focus_session", intent.Tool);
        Assert.Equal("thesis", Text(intent.Args, "goal"));
        Assert.Equal(25, intent.Args["minutes"]!.GetValue<int>());

        var plain = Understand("start a focus session");
        Assert.Equal("start_focus_session", plain.Tool);
        Assert.Equal("Focused work", Text(plain.Args, "goal"));
        Assert.False(plain.Args.ContainsKey("minutes"));
    }

    [Fact]
    public void Volume_brightness_and_mute()
    {
        var volume = Understand("set volume to 40");
        Assert.Equal("set_volume", volume.Tool);
        Assert.Equal(40, volume.Args["percent"]!.GetValue<int>());

        Assert.Equal(100, Understand("volume 250").Args["percent"]!.GetValue<int>());

        var brightness = Understand("set brightness to 60");
        Assert.Equal("set_brightness", brightness.Tool);
        Assert.Equal(60, brightness.Args["percent"]!.GetValue<int>());

        Assert.True(Understand("mute").Args["muted"]!.GetValue<bool>());
        Assert.False(Understand("unmute").Args["muted"]!.GetValue<bool>());
    }

    [Fact]
    public void Remember_keeps_the_whole_sentence_even_when_it_mentions_other_commands()
    {
        var intent = Understand("remember that my laptop has 16 GB RAM and a list of tasks");
        Assert.Equal("remember", intent.Tool);
        Assert.Equal("my laptop has 16 GB RAM and a list of tasks", Text(intent.Args, "content"));
        Assert.Equal("user_provided", Text(intent.Args, "source"));
        Assert.Equal("other", Text(intent.Args, "category"));
    }

    [Fact]
    public void Files_and_search()
    {
        var search = Understand("find my resume");
        Assert.Equal("search_files", search.Tool);
        Assert.Equal("resume", Text(search.Args, "query"));

        var duplicates = Understand("find duplicates in my downloads folder");
        Assert.Equal("find_duplicates", duplicates.Tool);
        Assert.Equal("downloads", Text(duplicates.Args, "folder"));

        var organise = Understand("organize my downloads folder");
        Assert.Equal("organise_folder", organise.Tool);
        Assert.Equal("downloads", Text(organise.Args, "folder"));
    }

    [Fact]
    public void Open_distinguishes_folders_websites_and_apps()
    {
        var folder = Understand("open downloads");
        Assert.Equal("open_path", folder.Tool);
        Assert.Equal("downloads", Text(folder.Args, "path"));

        var folderWithSuffix = Understand("open my documents folder");
        Assert.Equal("open_path", folderWithSuffix.Tool);
        Assert.Equal("documents", Text(folderWithSuffix.Args, "path"));

        var site = Understand("open github.com");
        Assert.Equal("open_url", site.Tool);
        Assert.Equal("github.com", Text(site.Args, "url"));

        var app = Understand("open VS Code");
        Assert.Equal("launch_app", app.Tool);
        Assert.Equal("VS Code", Text(app.Args, "name"));
    }

    [Fact]
    public void Closing_and_switching_apps()
    {
        var close = Understand("close chrome");
        Assert.Equal("close_app", close.Tool);
        Assert.Equal("chrome", Text(close.Args, "name"));

        var switchTo = Understand("switch to notepad");
        Assert.Equal("switch_to_app", switchTo.Tool);
        Assert.Equal("notepad", Text(switchTo.Args, "name"));
    }

    [Theory]
    [InlineData("tell me a joke")]
    [InlineData("why is the sky blue")]
    [InlineData("write an email to my professor")]
    public void Open_ended_requests_are_left_for_the_language_model(string text)
    {
        var intent = OfflineAssistant.Interpret(text, Now, out var problem);
        Assert.Null(intent);
        Assert.Null(problem);
    }

    [Fact]
    public void The_offline_hint_mentions_what_still_works()
    {
        Assert.Contains("reminders", OfflineAssistant.CapabilitiesHint);
        Assert.Contains("Without Gemini", OfflineAssistant.CapabilitiesHint);
    }
}
