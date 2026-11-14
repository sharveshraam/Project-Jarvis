using System.Text.Json.Nodes;
using Jarvis.Agent;
using Jarvis.Agent.Tools;
using Jarvis.AI;
using Jarvis.Core.AI;
using Jarvis.Core.Models;
using Jarvis.Core.Planning;
using Jarvis.Core.Security;
using Jarvis.Core.Tools;
using Jarvis.Infrastructure.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jarvis.Tests;

/// <summary>
/// The conversation loop with Gemini replaced by a script: tool rounds, honest fallbacks when Gemini cannot be
/// used, context that survives a restart, and summarisation of long conversations.
/// </summary>
public class AssistantServiceTests : IDisposable
{
    private static readonly DateTime Now = new DateTime(2026, 11, 14, 10, 0, 0);

    private readonly TempFolder _temp = new TempFolder();
    private readonly FakeClock _clock = new FakeClock(Now);
    private readonly ExecutorHarness _harness = new ExecutorHarness();
    private readonly FakeAi _ai = new FakeAi();
    private readonly FakeConnectivity _network = new FakeConnectivity();
    private readonly FakeApps _apps = new FakeApps();
    private readonly FakeShell _shell = new FakeShell();
    private readonly PlannerSignal _signal = new PlannerSignal();
    private readonly string _path;
    private readonly List<IDisposable> _owned = new List<IDisposable>();
    private Database _db = null!;
    private ConversationStore _conversations = null!;
    private MemoryStore _memory = null!;
    private PlannerStore _planner = null!;
    private TaskEngine _engine = null!;
    private AssistantService _assistant = null!;

    public AssistantServiceTests()
    {
        _path = Path.Combine(_temp.Root, "jarvis.db");
        Open(registerTools: true);
    }

    /// <summary>Opens the database and builds the service on top of it, the way the application does at startup.</summary>
    private void Open(bool registerTools)
    {
        _db = new Database(_path);
        _conversations = new ConversationStore(_db, _clock);
        _memory = new MemoryStore(_db, _clock);
        _planner = new PlannerStore(_db, _clock);

        var paths = new PathPolicy(() => _harness.Settings.Current, _temp.Root, Array.Empty<string>());
        var modes = new ModeService(_harness.Settings, _apps, _shell, paths, NullLogger<ModeService>.Instance);
        _engine = new TaskEngine(new AgentTaskStore(_db, _clock), _harness.Executor, _ai, _harness.Settings, new RecordingNotifier(), NullLogger<TaskEngine>.Instance);

        if (registerTools)
        {
            _harness.Registry.RegisterRange(new PlannerTools(_planner, _clock, _signal).Build());
            _harness.Registry.RegisterRange(new MemoryTools(_memory).Build());
            _harness.Registry.RegisterRange(new AppTools(_apps, _shell).Build());
            _harness.Registry.RegisterRange(new AgentTools(_engine, modes, _harness.Settings, _clock).Build());
        }

        var offline = new OfflineAssistant(_harness.Executor, modes, _clock);
        var summarizer = new ConversationSummarizer(_ai, _conversations, NullLogger<ConversationSummarizer>.Instance);
        _assistant = new AssistantService(_ai, _harness.Executor, _conversations, _memory, _planner, _engine, modes, _harness.Settings,
            _network, offline, summarizer, _clock, NullLogger<AssistantService>.Instance);
        _owned.Add(_engine);
        _owned.Add(_db);
    }

    public void Dispose()
    {
        foreach (var item in _owned) item.Dispose();
        _temp.Dispose();
    }

    [Fact]
    public async Task A_plain_answer_is_returned_and_both_turns_are_saved()
    {
        _ai.ReplyText("  Photosynthesis turns light into chemical energy.  ");

        var reply = await _assistant.SendAsync("explain photosynthesis", CancellationToken.None);

        Assert.Equal("Photosynthesis turns light into chemical energy.", reply.Text);
        Assert.False(reply.Local);
        Assert.False(reply.Failed);
        var messages = _conversations.GetMessages(_assistant.ConversationId!.Value);
        Assert.Equal(new[] { MessageRole.User, MessageRole.Assistant }, messages.Select(m => m.Role));
        Assert.Equal("explain photosynthesis", _conversations.Get(_assistant.ConversationId.Value)!.Title);
        Assert.Equal(GeminiState.Ready, _assistant.Gemini.State);
    }

    [Fact]
    public async Task A_tool_call_runs_through_the_executor_and_its_result_goes_back_to_gemini()
    {
        _ai.ReplyCall("call-1", "add_reminder", new JsonObject { ["text"] = "Call mom", ["due_local"] = "2026-11-15T19:00" });
        _ai.ReplyText("Done. I will remind you tomorrow at 19:00.");

        var reply = await _assistant.SendAsync("remind me to call mom tomorrow evening at seven", CancellationToken.None);

        Assert.Equal("Done. I will remind you tomorrow at 19:00.", reply.Text);
        Assert.Equal("Call mom", Assert.Single(_planner.ListReminders(activeOnly: true)).Text);
        Assert.Equal(2, _ai.Requests.Count);

        // The second request carries the model's own call followed by the tool result.
        var second = _ai.Requests[1].Input;
        Assert.IsType<AiRawStep>(second[second.Count - 2]);
        var result = Assert.IsType<AiFunctionResult>(second[second.Count - 1]);
        Assert.Equal("call-1", result.CallId);
        Assert.Equal("add_reminder", result.Name);
        var json = JsonNode.Parse(result.ResultJson)!.AsObject();
        Assert.True(json["success"]!.GetValue<bool>());
        Assert.Contains("Reminder set for tomorrow 19:00", json["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_failed_action_is_reported_to_gemini_as_a_failure()
    {
        _apps.Missing.Add("ghostapp");
        _ai.ReplyCall("call-1", "launch_app", new JsonObject { ["name"] = "ghostapp" });
        _ai.ReplyText("I could not find an application called ghostapp.");

        await _assistant.SendAsync("open ghostapp please", CancellationToken.None);

        var result = Assert.IsType<AiFunctionResult>(_ai.Requests[1].Input[_ai.Requests[1].Input.Count - 1]);
        var json = JsonNode.Parse(result.ResultJson)!.AsObject();
        Assert.False(json["success"]!.GetValue<bool>());
        Assert.Empty(_apps.Launched);
    }

    [Fact]
    public async Task A_tool_the_model_invents_is_rejected_without_running_anything()
    {
        _ai.ReplyCall("call-1", "format_disk", new JsonObject { ["drive"] = "C:" });
        _ai.ReplyText("I do not have a tool for that.");

        await _assistant.SendAsync("wipe my disk", CancellationToken.None);

        var result = Assert.IsType<AiFunctionResult>(_ai.Requests[1].Input[_ai.Requests[1].Input.Count - 1]);
        Assert.False(JsonNode.Parse(result.ResultJson)!["success"]!.GetValue<bool>());
        Assert.Contains(_harness.Audit.Entries, e => e.Tool == "format_disk" && e.Decision == "rejected");
    }

    [Fact]
    public async Task Switching_mode_mid_request_changes_the_instructions_and_tools_of_the_next_round()
    {
        _ai.ReplyCall("call-1", "switch_mode", new JsonObject { ["mode"] = "focus" });
        _ai.ReplyText("Focus mode is on.");

        await _assistant.SendAsync("switch to focus mode", CancellationToken.None);

        Assert.Contains("Active mode: Personal", _ai.Requests[0].System);
        Assert.Contains("Active mode: Focus", _ai.Requests[1].System);
        Assert.Contains("start_background_task", _ai.Requests[0].Tools);
        Assert.DoesNotContain("start_background_task", _ai.Requests[1].Tools);
        Assert.Contains("switch_mode", _ai.Requests[1].Tools);
        Assert.Equal(JarvisMode.Focus, _conversations.Get(_assistant.ConversationId!.Value)!.Mode);
    }

    [Fact]
    public async Task The_loop_stops_after_the_round_limit_and_says_so()
    {
        for (var i = 0; i < AssistantService.MaxToolRounds + 2; i++)
            _ai.ReplyCall("call-" + i, "list_reminders", new JsonObject());

        var reply = await _assistant.SendAsync("keep checking my reminders", CancellationToken.None);

        Assert.True(reply.Failed);
        Assert.Contains($"stopped after {AssistantService.MaxToolRounds} rounds", reply.Text);
        Assert.Equal(AssistantService.MaxToolRounds, _ai.Requests.Count);
    }

    [Fact]
    public async Task Without_an_api_key_local_commands_still_work_and_gemini_is_never_called()
    {
        _ai.IsConfigured = false;

        var reply = await _assistant.SendAsync("remind me to call mom tomorrow at 7 pm", CancellationToken.None);

        Assert.True(reply.Local);
        Assert.False(reply.Failed);
        Assert.Contains("Reminder set for tomorrow 19:00", reply.Text);
        Assert.Empty(_ai.Requests);
        Assert.Equal(GeminiState.NotConfigured, _assistant.Gemini.State);
    }

    [Fact]
    public async Task Without_an_api_key_an_open_question_gets_an_honest_refusal_not_an_invented_answer()
    {
        _ai.IsConfigured = false;

        var reply = await _assistant.SendAsync("why is the sky blue?", CancellationToken.None);

        Assert.True(reply.Local);
        Assert.True(reply.Failed);
        Assert.StartsWith("Gemini is not configured yet", reply.Text);
        Assert.Contains("I cannot answer that without Gemini.", reply.Text);
        Assert.Contains(OfflineAssistant.CapabilitiesHint, reply.Text);
    }

    [Fact]
    public async Task With_no_network_the_request_is_handled_locally_and_the_status_says_offline()
    {
        _network.IsNetworkAvailable = false;
        Assert.Equal(GeminiState.Offline, _assistant.Gemini.State);

        var reply = await _assistant.SendAsync("add task buy milk", CancellationToken.None);

        Assert.True(reply.Local);
        Assert.Contains("Task added: Buy milk", reply.Text);
        Assert.Empty(_ai.Requests);

        _network.IsNetworkAvailable = true;
        Assert.Equal(GeminiState.Ready, _assistant.Gemini.State);
    }

    [Theory]
    [InlineData(AiErrorKind.Authentication, GeminiState.Error, "API key rejected")]
    [InlineData(AiErrorKind.Quota, GeminiState.Error, "Quota reached")]
    [InlineData(AiErrorKind.Model, GeminiState.Error, "Model unavailable")]
    [InlineData(AiErrorKind.Network, GeminiState.Offline, "Offline — local mode")]
    [InlineData(AiErrorKind.Timeout, GeminiState.Offline, "Offline — local mode")]
    public async Task A_gemini_failure_falls_back_to_local_handling_and_is_shown_in_the_status(AiErrorKind kind, GeminiState state, string display)
    {
        _ai.Fail(kind, "Gemini could not be reached.");

        var reply = await _assistant.SendAsync("add task buy milk", CancellationToken.None);

        Assert.True(reply.Local);
        Assert.False(reply.Failed);
        Assert.Contains("Task added: Buy milk", reply.Text);
        Assert.Equal(state, _assistant.Gemini.State);
        Assert.Equal(display, _assistant.Gemini.Display);
    }

    [Fact]
    public async Task A_gemini_failure_on_an_open_question_passes_the_reason_on()
    {
        _ai.Fail(AiErrorKind.Quota, "The Gemini quota for this key is used up.");

        var reply = await _assistant.SendAsync("write me a poem", CancellationToken.None);

        Assert.True(reply.Failed);
        Assert.StartsWith("The Gemini quota for this key is used up. I cannot answer that without Gemini.", reply.Text);
    }

    [Fact]
    public async Task Cancelling_stops_the_request_and_records_that_it_stopped()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var reply = await _assistant.SendAsync("explain photosynthesis", cancelled.Token);

        Assert.True(reply.Cancelled);
        Assert.Equal("Stopped.", reply.Text);
        Assert.Empty(_ai.Requests);
    }

    [Fact]
    public async Task Blank_input_is_ignored()
    {
        var reply = await _assistant.SendAsync("   ", CancellationToken.None);

        Assert.True(reply.Failed);
        Assert.Null(_assistant.ConversationId);
        Assert.Empty(_conversations.List(10));
    }

    [Fact]
    public async Task Stored_memory_and_the_planner_reach_the_system_instruction()
    {
        _memory.Add("preference", "Prefers concise answers", MemorySource.UserProvided);
        _memory.Add("project", "The thesis is about solar cells", MemorySource.Inferred);
        _planner.AddTodo("Fix the login bug", "", TaskPriority.Urgent, null);
        var settings = _harness.Settings.Current.Clone();
        settings.UserName = "Sharvesh";
        _harness.Settings.Save(settings);
        _ai.ReplyText("Noted.");

        await _assistant.SendAsync("how is my thesis going?", CancellationToken.None);

        var system = _ai.Requests[0].System!;
        Assert.Contains("The user's name is Sharvesh.", system);
        Assert.Contains("Current local time: Saturday, 2026-11-14 10:00", system);
        Assert.Contains("[preference, stated] Prefers concise answers", system);
        Assert.Contains("[project, inferred] The thesis is about solar cells", system);
        Assert.Contains("1 open tasks, 0 active reminders.", system);
        Assert.Contains("Fix the login bug", system);
    }

    [Fact]
    public async Task A_conversation_resumed_after_a_restart_sends_its_earlier_turns_as_context()
    {
        _ai.ReplyText("Your thesis is about perovskite solar cells.");
        await _assistant.SendAsync("my thesis is about perovskite solar cells", CancellationToken.None);
        var id = _assistant.ConversationId!.Value;

        // Close and reopen everything, as on the next launch.
        foreach (var item in _owned) item.Dispose();
        _owned.Clear();
        Open(registerTools: false);
        Assert.Null(_assistant.ConversationId);

        var restored = _assistant.Resume(id);
        Assert.Equal(2, restored.Count);

        _ai.ReplyText("Perovskite solar cells.");
        await _assistant.SendAsync("what is my thesis about?", CancellationToken.None);

        var input = _ai.Requests[1].Input;
        Assert.Equal(3, input.Count);
        Assert.Equal("my thesis is about perovskite solar cells", Assert.IsType<AiUserInput>(input[0]).Text);
        Assert.Equal("what is my thesis about?", Assert.IsType<AiUserInput>(input[2]).Text);
        Assert.Equal(4, _conversations.GetMessages(id).Count);
    }

    [Fact]
    public async Task Notes_are_stored_with_the_conversation_but_never_sent_to_gemini()
    {
        _ai.ReplyText("Hello.");
        await _assistant.SendAsync("hello there", CancellationToken.None);
        _assistant.AddNote("Reminder: Call mom");
        _ai.ReplyText("Yes?");

        await _assistant.SendAsync("are you still there?", CancellationToken.None);

        Assert.Contains(_conversations.GetMessages(_assistant.ConversationId!.Value), m => m.Role == MessageRole.Note);
        Assert.Equal(3, _ai.Requests[1].Input.Count);
        Assert.DoesNotContain(_ai.Requests[1].Input, i => i is AiUserInput u && u.Text.Contains("Call mom"));
    }

    [Fact]
    public async Task A_long_conversation_is_folded_into_a_summary_and_only_recent_turns_are_resent()
    {
        var conversation = _conversations.Create("Long chat", JarvisMode.Personal);
        var ids = new List<long>();
        for (var i = 0; i < ConversationSummarizer.Threshold + 4; i++)
            ids.Add(_conversations.AddMessage(conversation.Id, i % 2 == 0 ? MessageRole.User : MessageRole.Assistant, "turn " + i).Id);
        var summarizer = new ConversationSummarizer(_ai, _conversations, NullLogger<ConversationSummarizer>.Instance);
        _ai.ReplyText("  The user is planning a thesis on solar cells.  ");

        Assert.True(await summarizer.SummarizeIfNeededAsync(conversation.Id, CancellationToken.None));

        var updated = _conversations.Get(conversation.Id)!;
        Assert.Equal("The user is planning a thesis on solar cells.", updated.Summary);
        Assert.Equal(ids[ids.Count - ConversationSummarizer.KeepRecent - 1], updated.SummarizedThrough);
        Assert.Equal(ConversationSummarizer.KeepRecent, _conversations.GetMessages(conversation.Id, updated.SummarizedThrough).Count);
        Assert.Empty(_ai.Requests[0].Tools);

        // Too few new turns since the last summary: nothing more to do, and Gemini is not asked again.
        Assert.False(await summarizer.SummarizeIfNeededAsync(conversation.Id, CancellationToken.None));
        Assert.Single(_ai.Requests);

        // The next request in that conversation carries the summary instead of the folded turns.
        _assistant.Resume(conversation.Id);
        _ai.ReplyText("Sure.");
        await _assistant.SendAsync("carry on", CancellationToken.None);
        Assert.Contains("The user is planning a thesis on solar cells.", _ai.Requests[1].System);
        Assert.DoesNotContain(_ai.Requests[1].Input, i => i is AiUserInput u && u.Text == "turn 0");
    }

    [Fact]
    public async Task A_failed_summary_leaves_the_conversation_untouched()
    {
        var conversation = _conversations.Create("Long chat", JarvisMode.Personal);
        for (var i = 0; i < ConversationSummarizer.Threshold; i++)
            _conversations.AddMessage(conversation.Id, i % 2 == 0 ? MessageRole.User : MessageRole.Assistant, "turn " + i);
        var summarizer = new ConversationSummarizer(_ai, _conversations, NullLogger<ConversationSummarizer>.Instance);
        _ai.Fail(AiErrorKind.Network, "offline");

        Assert.False(await summarizer.SummarizeIfNeededAsync(conversation.Id, CancellationToken.None));

        var unchanged = _conversations.Get(conversation.Id)!;
        Assert.Equal("", unchanged.Summary ?? "");
        Assert.Equal(0, unchanged.SummarizedThrough);
    }
}
