using System.Text.Json.Nodes;
using Jarvis.Agent;
using Jarvis.Core.AI;
using Jarvis.Core.Models;
using Jarvis.Core.Tools;
using Jarvis.Infrastructure.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jarvis.Tests;

/// <summary>
/// The background task runner, wired to the real executor and a real SQLite checkpoint store.
/// "Restart" means: dispose the engine, reopen the database file and build a new engine on it.
/// </summary>
public class TaskEngineTests : IDisposable
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    private readonly TempFolder _temp = new TempFolder();
    private readonly FakeClock _clock = new FakeClock(new DateTime(2026, 11, 14, 10, 0, 0));
    private readonly ExecutorHarness _harness = new ExecutorHarness();
    private readonly RecordingNotifier _notifier = new RecordingNotifier();
    private readonly FakeAi _ai = new FakeAi();
    private readonly List<TaskEngine> _engines = new List<TaskEngine>();
    private readonly string _path;
    private Database _db;
    private AgentTaskStore _store;

    public TaskEngineTests()
    {
        _path = Path.Combine(_temp.Root, "tasks.db");
        _db = new Database(_path);
        _store = new AgentTaskStore(_db, _clock);
    }

    public void Dispose()
    {
        foreach (var engine in _engines) engine.Dispose();
        _db.Dispose();
        _temp.Dispose();
    }

    private TaskEngine NewEngine()
    {
        var engine = new TaskEngine(_store, _harness.Executor, _ai, _harness.Settings, _notifier, NullLogger<TaskEngine>.Instance)
        {
            RetryDelay = TimeSpan.FromMilliseconds(1),
            PausePollInterval = TimeSpan.FromMilliseconds(10)
        };
        _engines.Add(engine);
        return engine;
    }

    /// <summary>Simulates JARVIS being closed and started again.</summary>
    private TaskEngine Restart(TaskEngine running)
    {
        running.Dispose();
        _db.Dispose();
        _db = new Database(_path);
        _store = new AgentTaskStore(_db, _clock);
        return NewEngine();
    }

    private void Register(string name, ToolRisk risk, Func<JsonObject, CancellationToken, Task<ToolResult>> run,
        bool retryable = false, Func<bool>? alreadyApplied = null)
    {
        var definition = new ToolDefinition(name, "A tool used by tests.", ToolCategory.General, risk).Str("value", "Any text.");
        if (retryable) definition.Retryable();
        Func<JsonObject, CancellationToken, Task<bool>>? applied = null;
        if (alreadyApplied is not null) applied = (args, ct) => Task.FromResult(alreadyApplied());
        _harness.Registry.Register(new DelegateTool(definition, (args, context, ct) => run(args, ct), null, applied));
    }

    private static AgentStep Step(string tool, string? value = null)
    {
        var args = new JsonObject();
        if (value is not null) args["value"] = value;
        return new AgentStep { Tool = tool, Description = "Run " + tool, Args = args };
    }

    private static TaskCompletionSource<bool> Signal() => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

    private async Task WaitUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Limit;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Timed out waiting until " + what + ".");
            await Task.Delay(20);
        }
    }

    private AgentTask Stored(string id) => _store.Get(id) ?? throw new InvalidOperationException("The task is not in the store.");

    [Fact]
    public async Task A_planned_task_runs_its_steps_in_order_and_reports_completion()
    {
        var order = new List<string>();
        Register("alpha", ToolRisk.Safe, (args, ct) => { order.Add("alpha:" + args.Str("value")); return Task.FromResult(ToolResult.Ok("alpha done")); });
        Register("beta", ToolRisk.Safe, (args, ct) => { order.Add("beta"); return Task.FromResult(ToolResult.Ok("beta done")); });
        var engine = NewEngine();
        var changes = 0;
        engine.TaskChanged += _ => Interlocked.Increment(ref changes);

        var task = engine.StartPlanned("Two steps", "do both", JarvisMode.Personal, new[] { Step("alpha", "x"), Step("beta") });
        await engine.WaitAsync(task.Id).WaitAsync(Limit);

        var stored = Stored(task.Id);
        Assert.Equal(AgentTaskState.Completed, stored.State);
        Assert.Equal(new[] { "alpha:x", "beta" }, order);
        Assert.Equal(2, stored.CompletedSteps);
        Assert.Equal("alpha done", stored.Steps[0].Result);
        Assert.Contains("All 2 steps completed.", stored.Result);
        Assert.Null(stored.Error);
        Assert.True(changes > 0);
        Assert.Contains(_notifier.Notifications, n => n.Title == "Task completed");
        Assert.Equal(0, engine.ActiveCount);
    }

    [Fact]
    public async Task A_failing_step_stops_the_task_and_later_steps_never_run()
    {
        var laterRuns = 0;
        Register("broken", ToolRisk.Safe, (args, ct) => Task.FromResult(ToolResult.Fail("the disk is full")));
        Register("later", ToolRisk.Safe, (args, ct) => { laterRuns++; return Task.FromResult(ToolResult.Ok("ok")); });
        var engine = NewEngine();

        var task = engine.StartPlanned("Fails", "fails", JarvisMode.Personal, new[] { Step("broken"), Step("later") });
        await engine.WaitAsync(task.Id).WaitAsync(Limit);

        var stored = Stored(task.Id);
        Assert.Equal(AgentTaskState.Failed, stored.State);
        Assert.Contains("Step 1", stored.Error);
        Assert.Contains("the disk is full", stored.Error);
        Assert.Equal(StepState.Failed, stored.Steps[0].State);
        Assert.Equal(1, stored.Steps[0].Attempts);
        Assert.Equal(StepState.Pending, stored.Steps[1].State);
        Assert.Equal(0, laterRuns);
        Assert.Contains(_notifier.Notifications, n => n.Title == "Task failed");
    }

    [Fact]
    public async Task Retryable_steps_are_retried_a_limited_number_of_times()
    {
        var flakyRuns = 0;
        Register("flaky", ToolRisk.Safe, (args, ct) =>
        {
            flakyRuns++;
            return Task.FromResult(flakyRuns < 3 ? ToolResult.Fail("temporary glitch") : ToolResult.Ok("worked"));
        }, retryable: true);
        var hopelessRuns = 0;
        Register("hopeless", ToolRisk.Safe, (args, ct) => { hopelessRuns++; return Task.FromResult(ToolResult.Fail("never works")); }, retryable: true);
        var engine = NewEngine();

        var first = engine.StartPlanned("Flaky", "flaky", JarvisMode.Personal, new[] { Step("flaky") });
        await engine.WaitAsync(first.Id).WaitAsync(Limit);
        Assert.Equal(AgentTaskState.Completed, Stored(first.Id).State);
        Assert.Equal(3, flakyRuns);

        var second = engine.StartPlanned("Hopeless", "hopeless", JarvisMode.Personal, new[] { Step("hopeless") });
        await engine.WaitAsync(second.Id).WaitAsync(Limit);
        Assert.Equal(AgentTaskState.Failed, Stored(second.Id).State);
        Assert.Equal(TaskEngine.MaxAttempts, hopelessRuns);
    }

    [Fact]
    public async Task A_consequential_step_waits_for_approval_and_a_refusal_stops_the_task()
    {
        var runs = 0;
        Register("erase", ToolRisk.Consequential, (args, ct) => { runs++; return Task.FromResult(ToolResult.Ok("erased")); });
        var engine = NewEngine();
        var states = new List<AgentTaskState>();
        engine.TaskChanged += t => { lock (states) states.Add(t.State); };

        var task = engine.StartPlanned("Erase", "erase", JarvisMode.Personal, new[] { Step("erase") });
        await engine.WaitAsync(task.Id).WaitAsync(Limit);

        var stored = Stored(task.Id);
        Assert.Equal(AgentTaskState.Cancelled, stored.State);
        Assert.Contains("Stopped at step 1", stored.Error);
        Assert.Equal(0, runs);
        lock (states) Assert.Contains(AgentTaskState.WaitingForApproval, states);
        var request = Assert.Single(_harness.Approvals.Requests);
        Assert.True(request.IsBackground);
        Assert.Equal(task.Id, request.TaskId);
        Assert.Contains(_notifier.Notifications, n => n.Title == "Task stopped");
    }

    [Fact]
    public async Task An_approved_consequential_step_runs()
    {
        var runs = 0;
        Register("erase", ToolRisk.Consequential, (args, ct) => { runs++; return Task.FromResult(ToolResult.Ok("erased")); });
        _harness.Approvals.Answer = new ApprovalResult(true);
        var engine = NewEngine();

        var task = engine.StartPlanned("Erase", "erase", JarvisMode.Personal, new[] { Step("erase") });
        await engine.WaitAsync(task.Id).WaitAsync(Limit);

        Assert.Equal(AgentTaskState.Completed, Stored(task.Id).State);
        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task A_task_can_be_paused_between_steps_and_resumed()
    {
        var firstStarted = Signal();
        var release = Signal();
        var secondRuns = 0;
        Register("first", ToolRisk.Safe, async (args, ct) =>
        {
            firstStarted.TrySetResult(true);
            await release.Task.WaitAsync(ct);
            return ToolResult.Ok("first done");
        });
        Register("second", ToolRisk.Safe, (args, ct) => { secondRuns++; return Task.FromResult(ToolResult.Ok("second done")); });
        var engine = NewEngine();

        var task = engine.StartPlanned("Pausable", "pausable", JarvisMode.Personal, new[] { Step("first"), Step("second") });
        await firstStarted.Task.WaitAsync(Limit);
        Assert.True(engine.Pause(task.Id));
        release.TrySetResult(true);

        await WaitUntil(() => Stored(task.Id).State == AgentTaskState.Paused, "the task is paused");
        await Task.Delay(150);
        Assert.Equal(0, secondRuns);
        Assert.Equal(StepState.Completed, Stored(task.Id).Steps[0].State);

        Assert.True(engine.Resume(task.Id));
        await engine.WaitAsync(task.Id).WaitAsync(Limit);

        Assert.Equal(AgentTaskState.Completed, Stored(task.Id).State);
        Assert.Equal(1, secondRuns);
    }

    [Fact]
    public async Task Pause_tasks_holds_every_task_until_it_is_switched_off()
    {
        var runs = 0;
        Register("work", ToolRisk.Safe, (args, ct) => { runs++; return Task.FromResult(ToolResult.Ok("ok")); });
        var engine = NewEngine();

        engine.SetAllPaused(true);
        Assert.True(engine.AllPaused);
        var task = engine.StartPlanned("Held", "held", JarvisMode.Personal, new[] { Step("work") });

        await WaitUntil(() => Stored(task.Id).State == AgentTaskState.Paused, "the task is paused");
        await Task.Delay(150);
        Assert.Equal(0, runs);

        engine.SetAllPaused(false);
        await engine.WaitAsync(task.Id).WaitAsync(Limit);

        Assert.False(engine.AllPaused);
        Assert.Equal(AgentTaskState.Completed, Stored(task.Id).State);
        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task Cancelling_stops_the_running_step_and_skips_the_rest()
    {
        var started = Signal();
        var laterRuns = 0;
        Register("slow", ToolRisk.Safe, async (args, ct) =>
        {
            started.TrySetResult(true);
            await Task.Delay(TimeSpan.FromMinutes(5), ct);
            return ToolResult.Ok("finished");
        });
        Register("later", ToolRisk.Safe, (args, ct) => { laterRuns++; return Task.FromResult(ToolResult.Ok("ok")); });
        var engine = NewEngine();

        var task = engine.StartPlanned("Cancellable", "cancellable", JarvisMode.Personal, new[] { Step("slow"), Step("later") });
        await started.Task.WaitAsync(Limit);
        Assert.True(engine.Cancel(task.Id));
        await engine.WaitAsync(task.Id).WaitAsync(Limit);
        await WaitUntil(() => Stored(task.Id).State == AgentTaskState.Cancelled, "the task is cancelled");

        var stored = Stored(task.Id);
        Assert.Equal("Cancelled by the user.", stored.Error);
        Assert.Equal(StepState.Failed, stored.Steps[0].State);
        Assert.Equal(0, laterRuns);
        Assert.False(engine.Cancel("no-such-task"));
    }

    [Fact]
    public async Task After_a_restart_an_interrupted_risky_step_is_held_for_the_user_instead_of_being_repeated()
    {
        var started = Signal();
        var release = Signal();
        var runs = 0;
        Register("send_invoice", ToolRisk.Routine, async (args, ct) =>
        {
            Interlocked.Increment(ref runs);
            started.TrySetResult(true);
            await release.Task.WaitAsync(ct);
            return ToolResult.Ok("sent");
        });
        var engine = NewEngine();
        var task = engine.StartPlanned("Send invoice", "send it", JarvisMode.Personal, new[] { Step("send_invoice") });
        await started.Task.WaitAsync(Limit);

        engine = Restart(engine);
        var resumed = await engine.RecoverAsync(CancellationToken.None);

        Assert.Equal(0, resumed);
        var held = Stored(task.Id);
        Assert.Equal(AgentTaskState.Paused, held.State);
        Assert.True(held.Held);
        Assert.True(held.Steps[0].Interrupted);
        Assert.Equal(1, runs);
        Assert.Contains(_notifier.Notifications, n => n.Title == "Task needs your decision");

        release.TrySetResult(true);
        Assert.True(engine.Resume(task.Id));
        await engine.WaitAsync(task.Id).WaitAsync(Limit);

        Assert.Equal(AgentTaskState.Completed, Stored(task.Id).State);
        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task After_a_restart_a_held_task_can_be_cancelled_without_running_anything()
    {
        var started = Signal();
        var runs = 0;
        Register("send_invoice", ToolRisk.Routine, async (args, ct) =>
        {
            Interlocked.Increment(ref runs);
            started.TrySetResult(true);
            await Task.Delay(TimeSpan.FromMinutes(5), ct);
            return ToolResult.Ok("sent");
        });
        var engine = NewEngine();
        var task = engine.StartPlanned("Send invoice", "send it", JarvisMode.Personal, new[] { Step("send_invoice") });
        await started.Task.WaitAsync(Limit);

        engine = Restart(engine);
        await engine.RecoverAsync(CancellationToken.None);

        Assert.True(engine.Cancel(task.Id));
        Assert.Equal(AgentTaskState.Cancelled, Stored(task.Id).State);
        Assert.Equal(1, runs);
        Assert.Equal(0, await engine.RecoverAsync(CancellationToken.None));
    }

    [Fact]
    public async Task After_a_restart_a_step_that_already_took_effect_is_not_repeated()
    {
        var started = Signal();
        var moveRuns = 0;
        var nextRuns = 0;
        Register("move_file", ToolRisk.Routine, async (args, ct) =>
        {
            Interlocked.Increment(ref moveRuns);
            started.TrySetResult(true);
            await Task.Delay(TimeSpan.FromMinutes(5), ct);
            return ToolResult.Ok("moved");
        }, alreadyApplied: () => true);
        Register("next", ToolRisk.Safe, (args, ct) => { Interlocked.Increment(ref nextRuns); return Task.FromResult(ToolResult.Ok("ok")); });
        var engine = NewEngine();
        var task = engine.StartPlanned("Move then report", "move", JarvisMode.Personal, new[] { Step("move_file"), Step("next") });
        await started.Task.WaitAsync(Limit);

        engine = Restart(engine);
        var resumed = await engine.RecoverAsync(CancellationToken.None);
        await engine.WaitAsync(task.Id).WaitAsync(Limit);
        await WaitUntil(() => Stored(task.Id).State == AgentTaskState.Completed, "the task completes");

        Assert.Equal(1, resumed);
        Assert.Equal(1, moveRuns);
        Assert.Equal(1, nextRuns);
        Assert.Contains("confirmed during recovery", Stored(task.Id).Steps[0].Result);
    }

    [Fact]
    public async Task After_a_restart_a_read_only_step_is_simply_run_again()
    {
        var started = Signal();
        var release = Signal();
        var runs = 0;
        Register("scan", ToolRisk.Safe, async (args, ct) =>
        {
            Interlocked.Increment(ref runs);
            started.TrySetResult(true);
            await release.Task.WaitAsync(ct);
            return ToolResult.Ok("scanned");
        });
        var engine = NewEngine();
        var task = engine.StartPlanned("Scan", "scan", JarvisMode.Personal, new[] { Step("scan") });
        await started.Task.WaitAsync(Limit);

        engine = Restart(engine);
        release.TrySetResult(true);
        var resumed = await engine.RecoverAsync(CancellationToken.None);
        await engine.WaitAsync(task.Id).WaitAsync(Limit);
        await WaitUntil(() => Stored(task.Id).State == AgentTaskState.Completed, "the task completes");

        Assert.Equal(1, resumed);
        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task Finished_tasks_are_left_alone_by_recovery()
    {
        Register("work", ToolRisk.Safe, (args, ct) => Task.FromResult(ToolResult.Ok("ok")));
        var engine = NewEngine();
        var task = engine.StartPlanned("Done", "done", JarvisMode.Personal, new[] { Step("work") });
        await engine.WaitAsync(task.Id).WaitAsync(Limit);
        await WaitUntil(() => Stored(task.Id).State == AgentTaskState.Completed, "the task completes");

        engine = Restart(engine);

        Assert.Equal(0, await engine.RecoverAsync(CancellationToken.None));
        Assert.Equal(AgentTaskState.Completed, Stored(task.Id).State);
    }

    [Fact]
    public async Task A_goal_is_planned_by_the_model_and_then_executed()
    {
        var values = new List<string?>();
        Register("alpha", ToolRisk.Safe, (args, ct) => { values.Add(args.Str("value")); return Task.FromResult(ToolResult.Ok("ok")); });
        _ai.ReplyText("{\"title\":\"Tidy up\",\"steps\":[{\"description\":\"First\",\"tool\":\"alpha\",\"arguments_json\":\"{\\\"value\\\":\\\"one\\\"}\"},"
                      + "{\"description\":\"Second\",\"tool\":\"alpha\",\"arguments_json\":\"{\\\"value\\\":\\\"two\\\"}\"}]}");
        var engine = NewEngine();

        var task = engine.StartGoal("tidy everything up", JarvisMode.Personal);
        await engine.WaitAsync(task.Id).WaitAsync(Limit);

        var stored = Stored(task.Id);
        Assert.Equal(AgentTaskState.Completed, stored.State);
        Assert.Equal("Tidy up", stored.Title);
        Assert.Equal(new string?[] { "one", "two" }, values);
        Assert.Equal("First", stored.Steps[0].Description);
        Assert.Contains("- alpha:", Assert.Single(_ai.Requests).System);
    }

    [Fact]
    public async Task An_invalid_plan_is_sent_back_once_and_nothing_runs_until_it_is_valid()
    {
        var runs = 0;
        Register("alpha", ToolRisk.Safe, (args, ct) => { runs++; return Task.FromResult(ToolResult.Ok("ok")); });
        _ai.ReplyText("{\"title\":\"Bad\",\"steps\":[{\"description\":\"x\",\"tool\":\"format_disk\",\"arguments_json\":\"{}\"}]}");
        _ai.ReplyText("{\"title\":\"Good\",\"steps\":[{\"description\":\"x\",\"tool\":\"alpha\",\"arguments_json\":\"{}\"}]}");
        var engine = NewEngine();

        var task = engine.StartGoal("do the thing", JarvisMode.Personal);
        await engine.WaitAsync(task.Id).WaitAsync(Limit);

        Assert.Equal(AgentTaskState.Completed, Stored(task.Id).State);
        Assert.Equal(1, runs);
        Assert.Equal(2, _ai.Requests.Count);
        var retry = Assert.IsType<AiUserInput>(_ai.Requests[1].Input[0]);
        Assert.Contains("rejected", retry.Text);
        Assert.Contains("format_disk", retry.Text);
    }

    [Fact]
    public async Task A_plan_that_stays_invalid_fails_without_running_anything()
    {
        var runs = 0;
        Register("alpha", ToolRisk.Safe, (args, ct) => { runs++; return Task.FromResult(ToolResult.Ok("ok")); });
        _ai.ReplyText("{\"title\":\"Bad\",\"steps\":[{\"description\":\"x\",\"tool\":\"alpha\",\"arguments_json\":\"{\\\"bogus\\\":1}\"}]}");
        _ai.ReplyText("not json");
        var engine = NewEngine();

        var task = engine.StartGoal("do the thing", JarvisMode.Personal);
        await engine.WaitAsync(task.Id).WaitAsync(Limit);

        var stored = Stored(task.Id);
        Assert.Equal(AgentTaskState.Failed, stored.State);
        Assert.Contains("valid plan", stored.Error);
        Assert.Equal(0, runs);
    }

    [Fact]
    public async Task Planning_a_goal_without_gemini_fails_with_a_clear_reason()
    {
        _ai.IsConfigured = false;
        var engine = NewEngine();

        var task = engine.StartGoal("do the thing", JarvisMode.Personal);
        await engine.WaitAsync(task.Id).WaitAsync(Limit);

        var stored = Stored(task.Id);
        Assert.Equal(AgentTaskState.Failed, stored.State);
        Assert.Contains("no API key", stored.Error);
        Assert.Empty(_ai.Requests);
    }

    [Fact]
    public async Task A_planning_request_that_cannot_reach_gemini_fails_cleanly()
    {
        Register("alpha", ToolRisk.Safe, (args, ct) => Task.FromResult(ToolResult.Ok("ok")));
        _ai.Fail(AiErrorKind.Network, "Gemini could not be reached.");
        var engine = NewEngine();

        var task = engine.StartGoal("do the thing", JarvisMode.Personal);
        await engine.WaitAsync(task.Id).WaitAsync(Limit);

        Assert.Equal(AgentTaskState.Failed, Stored(task.Id).State);
        Assert.Contains("could not be reached", Stored(task.Id).Error);
    }

    [Fact]
    public void A_task_without_steps_is_rejected()
    {
        var engine = NewEngine();
        Assert.Throws<ArgumentException>(() => engine.StartPlanned("Empty", "empty", JarvisMode.Personal, Array.Empty<AgentStep>()));
    }

    [Fact]
    public void Plans_are_validated_before_anything_runs()
    {
        var tools = new[]
        {
            new ToolDefinition("add_task", "Adds a task.", ToolCategory.Planner, ToolRisk.Routine).Str("title", "Title.", required: true)
        };

        var good = TaskEngine.TryReadPlan(
            "{\"title\":\"Plan\",\"steps\":[{\"description\":\"Add it\",\"tool\":\"add_task\",\"arguments_json\":\"{\\\"title\\\":\\\"Buy milk\\\"}\"}]}",
            tools, out var title, out var steps);
        Assert.Empty(good);
        Assert.Equal("Plan", title);
        Assert.Equal("Buy milk", Assert.Single(steps).Args["title"]!.GetValue<string>());

        var inlineArguments = TaskEngine.TryReadPlan(
            "{\"title\":\"Plan\",\"steps\":[{\"tool\":\"add_task\",\"arguments_json\":{\"title\":\"Inline\"}}]}", tools, out _, out steps);
        Assert.Empty(inlineArguments);
        Assert.Equal("add_task", Assert.Single(steps).Description);

        var unknownTool = TaskEngine.TryReadPlan(
            "{\"title\":\"Plan\",\"steps\":[{\"description\":\"x\",\"tool\":\"format_disk\",\"arguments_json\":\"{}\"}]}", tools, out _, out steps);
        Assert.Contains("not an available tool", Assert.Single(unknownTool));
        Assert.Empty(steps);

        var missingArgument = TaskEngine.TryReadPlan(
            "{\"title\":\"Plan\",\"steps\":[{\"description\":\"x\",\"tool\":\"add_task\",\"arguments_json\":\"{}\"}]}", tools, out _, out steps);
        Assert.Contains("Missing required argument 'title'.", Assert.Single(missingArgument));
        Assert.Empty(steps);

        var badArguments = TaskEngine.TryReadPlan(
            "{\"title\":\"Plan\",\"steps\":[{\"description\":\"x\",\"tool\":\"add_task\",\"arguments_json\":\"[1,2]\"}]}", tools, out _, out steps);
        Assert.Contains("not a JSON object", Assert.Single(badArguments));

        Assert.Contains("not a JSON object", Assert.Single(TaskEngine.TryReadPlan("nonsense", tools, out _, out _)));
        Assert.Contains("no 'steps' array", Assert.Single(TaskEngine.TryReadPlan("{\"title\":\"x\"}", tools, out _, out _)));

        var many = string.Join(",", Enumerable.Repeat("{\"description\":\"x\",\"tool\":\"add_task\",\"arguments_json\":\"{\\\"title\\\":\\\"t\\\"}\"}", TaskEngine.MaxSteps + 1));
        var tooLong = TaskEngine.TryReadPlan("{\"title\":\"x\",\"steps\":[" + many + "]}", tools, out _, out steps);
        Assert.Contains("the maximum is", Assert.Single(tooLong));
        Assert.Empty(steps);
    }
}
