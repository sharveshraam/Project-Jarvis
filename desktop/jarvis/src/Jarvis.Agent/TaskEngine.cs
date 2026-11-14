using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jarvis.Core.Abstractions;
using Jarvis.Core.AI;
using Jarvis.Core.Models;
using Jarvis.Core.Security;
using Jarvis.Core.Text;
using Jarvis.Core.Tools;
using Microsoft.Extensions.Logging;

namespace Jarvis.Agent;

/// <summary>
/// Runs multi-step work in the background. A task is a persisted plan of tool calls; every step goes through
/// <see cref="ToolExecutor"/> (so validation, permissions, approvals and auditing all apply) and the task is
/// checkpointed before and after each step so it survives a restart.
/// </summary>
public sealed class TaskEngine : IDisposable
{
    public const int MaxSteps = 12;
    public const int MaxAttempts = 3;

    private readonly IAgentTaskStore _store;
    private readonly ToolExecutor _executor;
    private readonly IAiClient _ai;
    private readonly ISettingsStore _settings;
    private readonly INotifier _notifier;
    private readonly ILogger<TaskEngine> _log;
    private readonly ConcurrentDictionary<string, Runner> _runners = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _slots = new(2, 2);
    private volatile bool _shuttingDown;

    /// <summary>Delay between retries of an idempotent step; shortened by tests.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan PausePollInterval { get; set; } = TimeSpan.FromMilliseconds(400);

    public TaskEngine(IAgentTaskStore store, ToolExecutor executor, IAiClient ai, ISettingsStore settings, INotifier notifier, ILogger<TaskEngine> log)
    {
        _store = store;
        _executor = executor;
        _ai = ai;
        _settings = settings;
        _notifier = notifier;
        _log = log;
    }

    private sealed class Runner
    {
        public Runner(AgentTask job)
        {
            Job = job;
        }

        public AgentTask Job { get; }
        public CancellationTokenSource Cts { get; } = new CancellationTokenSource();
        public Task Completion { get; set; } = Task.CompletedTask;
    }

    /// <summary>Raised (on a background thread) whenever a task's state or progress changes.</summary>
    public event Action<AgentTask>? TaskChanged;

    public bool AllPaused => _settings.Current.TasksPaused;

    public int ActiveCount => _runners.Count;

    public IReadOnlyList<AgentTask> List(int limit)
    {
        // Prefer the live instance of a running task over its last checkpoint.
        return _store.List(limit).Select(t => _runners.TryGetValue(t.Id, out var r) ? r.Job : t).ToList();
    }

    /// <summary>Finds a task by full id or unique id prefix.</summary>
    public AgentTask? Find(string idOrPrefix)
    {
        if (string.IsNullOrWhiteSpace(idOrPrefix)) return null;
        var key = idOrPrefix.Trim().TrimStart('#');
        var matches = List(200).Where(t => t.Id.StartsWith(key, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count == 1 ? matches[0] : matches.FirstOrDefault(t => string.Equals(t.Id, key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Starts a task from a natural-language goal. Gemini drafts the plan; JARVIS validates every step before running any.</summary>
    public AgentTask StartGoal(string goal, JarvisMode mode)
    {
        var task = new AgentTask
        {
            Title = Relevance.Truncate(goal.Trim(), 80),
            Description = goal.Trim(),
            Mode = mode,
            State = AgentTaskState.Pending
        };
        _store.Save(task);
        StartRunner(task);
        return task;
    }

    /// <summary>Starts a task whose steps are already known (built-in workflows). Needs no network.</summary>
    public AgentTask StartPlanned(string title, string description, JarvisMode mode, IEnumerable<AgentStep> steps)
    {
        var task = new AgentTask { Title = title, Description = description, Mode = mode, State = AgentTaskState.Pending };
        var index = 0;
        foreach (var step in steps)
        {
            step.Index = index++;
            task.Steps.Add(step);
        }
        if (task.Steps.Count == 0) throw new ArgumentException("A task needs at least one step.");
        _store.Save(task);
        StartRunner(task);
        return task;
    }

    public bool Cancel(string id)
    {
        if (_runners.TryGetValue(id, out var runner))
        {
            try
            {
                runner.Cts.Cancel();
                return true;
            }
            catch (ObjectDisposedException)
            {
                // The runner finished between the lookup and the cancel; fall through to the store.
            }
        }
        // Not running in this process (held, or left over from a previous session): cancel it in the store.
        foreach (var state in new[] { AgentTaskState.Pending, AgentTaskState.Running, AgentTaskState.WaitingForApproval, AgentTaskState.Paused })
        {
            if (!_store.TryTransition(id, state, AgentTaskState.Cancelled)) continue;
            var task = _store.Get(id);
            if (task is not null) TaskChanged?.Invoke(task);
            return true;
        }
        return false;
    }

    public bool Pause(string id)
    {
        if (_runners.TryGetValue(id, out var runner))
        {
            runner.Job.Held = true;
            Save(runner.Job);
            return true;
        }
        return false;
    }

    public bool Resume(string id)
    {
        if (_runners.TryGetValue(id, out var runner))
        {
            runner.Job.Held = false;
            Save(runner.Job);
            return true;
        }
        var task = _store.Get(id);
        if (task is null || task.State != AgentTaskState.Paused) return false;
        task.Held = false;
        foreach (var step in task.Steps) step.Interrupted = false;
        _store.Save(task);
        StartRunner(task);
        return true;
    }

    /// <summary>"Pause Tasks" from the tray: running tasks stop before their next step until resumed.</summary>
    public void SetAllPaused(bool paused)
    {
        if (_settings.Current.TasksPaused == paused) return;
        var updated = _settings.Current.Clone();
        updated.TasksPaused = paused;
        _settings.Save(updated);
        _log.LogInformation("Background tasks {State}.", paused ? "paused" : "resumed");
    }

    /// <summary>Waits for a task's runner to finish. Completes immediately if it is not running.</summary>
    public Task WaitAsync(string id) => _runners.TryGetValue(id, out var runner) ? runner.Completion : Task.CompletedTask;

    /// <summary>
    /// Restart recovery. A step that was mid-flight when JARVIS stopped is checked for its side effect before
    /// anything is re-run: already applied → marked done; safe to repeat → retried; otherwise the task is held
    /// for the user to decide.
    /// </summary>
    public async Task<int> RecoverAsync(CancellationToken ct)
    {
        var resumed = 0;
        foreach (var task in _store.List(200).Where(t => t.IsActive).OrderBy(t => t.CreatedUtc))
        {
            ct.ThrowIfCancellationRequested();
            if (_runners.ContainsKey(task.Id)) continue;

            var approvalWasPending = task.State == AgentTaskState.WaitingForApproval;
            var step = task.CurrentStep >= 0 && task.CurrentStep < task.Steps.Count ? task.Steps[task.CurrentStep] : null;
            if (step is not null && step.State == StepState.Running)
            {
                if (!_executor.Registry.TryGet(step.Tool, out var tool))
                {
                    step.State = StepState.Failed;
                    Finish(task, AgentTaskState.Failed, null, $"Step {step.Index + 1} uses a tool that no longer exists ({step.Tool}).");
                    continue;
                }

                bool applied;
                try { applied = await tool.IsAlreadyAppliedAsync((JsonObject)step.Args.DeepClone(), ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException) { applied = false; }

                if (applied)
                {
                    step.State = StepState.Completed;
                    step.Result = "Completed before JARVIS restarted (confirmed during recovery).";
                    task.CurrentStep++;
                }
                else if (approvalWasPending || tool.Definition.Idempotent || tool.Definition.DefaultRisk == ToolRisk.Safe)
                {
                    // Nothing had run yet (approval was still pending) or the step is safe to repeat.
                    step.State = StepState.Pending;
                }
                else
                {
                    step.State = StepState.Pending;
                    step.Interrupted = true;
                    step.Result = "Interrupted when JARVIS stopped; it is not known whether this step took effect.";
                    task.Held = true;
                    task.State = AgentTaskState.Paused;
                    Save(task);
                    _notifier.Notify("Task needs your decision",
                        $"'{task.Title}' was interrupted during step {step.Index + 1} ({step.Description}). Resume to run that step again, or cancel the task.",
                        NotificationKind.Warning);
                    continue;
                }
            }

            if (task.State == AgentTaskState.Paused && task.Held) continue;
            _store.Save(task);
            StartRunner(task);
            resumed++;
        }
        if (resumed > 0) _log.LogInformation("Recovered {Count} background task(s).", resumed);
        return resumed;
    }

    private void StartRunner(AgentTask task)
    {
        var runner = new Runner(task);
        if (!_runners.TryAdd(task.Id, runner)) return;
        runner.Completion = Task.Run(() => RunAsync(runner));
    }

    private async Task RunAsync(Runner runner)
    {
        var task = runner.Job;
        var ct = runner.Cts.Token;
        var hasSlot = false;
        try
        {
            await _slots.WaitAsync(ct).ConfigureAwait(false);
            hasSlot = true;

            if (task.Steps.Count == 0)
            {
                await WaitWhilePausedAsync(task, ct).ConfigureAwait(false);
                SetState(task, AgentTaskState.Running);
                var planError = await PlanAsync(task, ct).ConfigureAwait(false);
                if (planError is not null)
                {
                    Finish(task, AgentTaskState.Failed, null, planError);
                    return;
                }
                Save(task);
            }

            while (task.CurrentStep < task.Steps.Count)
            {
                ct.ThrowIfCancellationRequested();
                await WaitWhilePausedAsync(task, ct).ConfigureAwait(false);

                var step = task.Steps[task.CurrentStep];
                if (step.State is StepState.Completed or StepState.Skipped)
                {
                    task.CurrentStep++;
                    continue;
                }

                // Checkpoint "about to run" so a crash here is detected as an interrupted step.
                step.State = StepState.Running;
                step.Interrupted = false;
                step.Attempts++;
                task.State = AgentTaskState.Running;
                Save(task);

                var context = new ToolContext
                {
                    Mode = task.Mode,
                    IsBackground = true,
                    TaskId = task.Id,
                    BypassModeFilter = true,
                    ApprovalStateChanged = waiting => SetState(task, waiting ? AgentTaskState.WaitingForApproval : AgentTaskState.Running)
                };
                var result = await _executor.ExecuteAsync(step.Tool, (JsonObject)step.Args.DeepClone(), context, ct).ConfigureAwait(false);
                step.Result = Relevance.Truncate(result.Message, 1500) + (result.Success && !result.Verified ? " (requested; outcome not confirmed)" : "");

                if (result.Success)
                {
                    step.State = StepState.Completed;
                    task.CurrentStep++;
                    Save(task);
                    continue;
                }

                if (result.Denied)
                {
                    step.State = StepState.Failed;
                    Finish(task, AgentTaskState.Cancelled, null, $"Stopped at step {step.Index + 1}: {result.Message}");
                    return;
                }

                var retryable = _executor.Registry.TryGet(step.Tool, out var tool) && tool.Definition.Idempotent;
                if (retryable && step.Attempts < MaxAttempts)
                {
                    step.State = StepState.Pending;
                    Save(task);
                    await Task.Delay(TimeSpan.FromTicks(RetryDelay.Ticks * step.Attempts), ct).ConfigureAwait(false);
                    continue;
                }

                step.State = StepState.Failed;
                Finish(task, AgentTaskState.Failed, null, $"Step {step.Index + 1} ({step.Description}) failed: {result.Message}");
                return;
            }

            Finish(task, AgentTaskState.Completed, Summarise(task), null);
        }
        catch (OperationCanceledException)
        {
            if (_shuttingDown)
            {
                // Leave the checkpoint as it is; RecoverAsync inspects it on the next start.
                _log.LogInformation("Task {Id} suspended for shutdown.", task.Id);
            }
            else
            {
                var current = task.CurrentStep < task.Steps.Count ? task.Steps[task.CurrentStep] : null;
                if (current is not null && current.State == StepState.Running)
                {
                    current.State = StepState.Failed;
                    current.Result = "Cancelled while this step was running.";
                }
                Finish(task, AgentTaskState.Cancelled, null, "Cancelled by the user.");
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Task {Id} crashed.", task.Id);
            Finish(task, AgentTaskState.Failed, null, "The task stopped unexpectedly: " + SecretDetector.Redact(ex.Message));
        }
        finally
        {
            if (hasSlot) _slots.Release();
            _runners.TryRemove(task.Id, out _);
            runner.Cts.Dispose();
        }
    }

    private async Task WaitWhilePausedAsync(AgentTask task, CancellationToken ct)
    {
        var wasPaused = false;
        while (task.Held || _settings.Current.TasksPaused)
        {
            if (!wasPaused)
            {
                wasPaused = true;
                SetState(task, AgentTaskState.Paused);
            }
            await Task.Delay(PausePollInterval, ct).ConfigureAwait(false);
        }
        if (wasPaused) SetState(task, AgentTaskState.Running);
    }

    private void SetState(AgentTask task, AgentTaskState state)
    {
        if (task.State == state) return;
        task.State = state;
        Save(task);
    }

    private void Save(AgentTask task)
    {
        try
        {
            _store.Save(task);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Task {Id} could not be checkpointed.", task.Id);
        }
        TaskChanged?.Invoke(task);
    }

    private void Finish(AgentTask task, AgentTaskState state, string? result, string? error)
    {
        task.State = state;
        task.Result = result;
        task.Error = error;
        task.Held = false;
        Save(task);

        switch (state)
        {
            case AgentTaskState.Completed:
                _notifier.Notify("Task completed", $"{task.Title} — {task.Steps.Count} steps done.");
                break;
            case AgentTaskState.Failed:
                _notifier.Notify("Task failed", $"{task.Title}: {Relevance.Truncate(error ?? "unknown error", 200)}", NotificationKind.Error);
                break;
            case AgentTaskState.Cancelled:
                if (error is not null && !error.StartsWith("Cancelled by the user", StringComparison.Ordinal))
                    _notifier.Notify("Task stopped", $"{task.Title}: {Relevance.Truncate(error, 200)}", NotificationKind.Warning);
                break;
        }
    }

    private static string Summarise(AgentTask task)
    {
        var sb = new StringBuilder();
        sb.Append("All ").Append(task.Steps.Count).Append(task.Steps.Count == 1 ? " step completed." : " steps completed.");
        foreach (var step in task.Steps)
        {
            sb.Append('\n').Append(step.Index + 1).Append(". ").Append(step.Description);
            if (!string.IsNullOrWhiteSpace(step.Result)) sb.Append(" — ").Append(Relevance.Truncate(step.Result!, 300));
        }
        return sb.ToString();
    }

    /// <summary>Tools a generated plan may use: the mode's tools, minus ones that only make sense interactively.</summary>
    public IReadOnlyList<ToolDefinition> PlannableTools(JarvisMode mode)
        => _executor.DefinitionsFor(mode)
            .Where(d => d.Category is not (ToolCategory.Agent or ToolCategory.Screen or ToolCategory.Clipboard))
            .ToList();

    private async Task<string?> PlanAsync(AgentTask task, CancellationToken ct)
    {
        if (!_ai.IsConfigured) return "Planning a task needs Gemini, and no API key is configured. Built-in workflows still work offline.";
        var tools = PlannableTools(task.Mode);
        if (tools.Count == 0) return "No tools are available to plan with in this mode.";

        var catalogue = new StringBuilder();
        foreach (var tool in tools)
            catalogue.Append("- ").Append(tool.Name).Append(": ").Append(tool.Description).Append(" Arguments schema: ")
                .Append(tool.ToJsonSchema().ToJsonString()).Append('\n');

        var names = new JsonArray();
        foreach (var tool in tools)
        {
            JsonNode name = JsonValue.Create(tool.Name)!;
            names.Add(name);
        }
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["title"] = new JsonObject { ["type"] = "string", ["description"] = "Short title for the task." },
                ["steps"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["description"] = new JsonObject { ["type"] = "string", ["description"] = "What this step does, in plain language." },
                            ["tool"] = new JsonObject { ["type"] = "string", ["enum"] = names },
                            ["arguments_json"] = new JsonObject { ["type"] = "string", ["description"] = "A JSON object with the tool's arguments, as a string." }
                        },
                        ["required"] = new JsonArray { "description", "tool", "arguments_json" }
                    }
                }
            },
            ["required"] = new JsonArray { "title", "steps" }
        };

        string? feedback = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var request = new AiRequest
            {
                SystemInstruction =
                    "You plan background tasks for JARVIS, a Windows desktop assistant. Break the user's goal into the smallest sequence of tool calls that achieves it. " +
                    $"Use at most {MaxSteps} steps and only the tools listed. Every step must be fully specified now: later steps cannot read earlier results. " +
                    "Do not add steps the user did not ask for. If the goal cannot be done with these tools, return zero steps.\n\nTools:\n" + catalogue,
                ResponseSchema = schema,
                MaxOutputTokens = 8192
            };
            request.Input.Add(new AiUserInput("Goal: " + task.Description + (feedback is null ? "" : "\n\nYour previous plan was rejected: " + feedback + " Correct it.")));

            AiResponse response;
            try
            {
                response = await _ai.GenerateAsync(request, ct).ConfigureAwait(false);
            }
            catch (AiException ex)
            {
                return "The plan could not be created: " + ex.Message;
            }

            var problems = TryReadPlan(response.Text, tools, out var title, out var steps);
            if (problems.Count == 0)
            {
                if (steps.Count == 0) return "This goal cannot be carried out with the tools available in the current mode.";
                if (!string.IsNullOrWhiteSpace(title)) task.Title = Relevance.Truncate(title.Trim(), 80);
                task.Steps = steps;
                task.CurrentStep = 0;
                return null;
            }
            feedback = string.Join(" ", problems.Take(6));
        }
        return "Gemini did not produce a valid plan: " + feedback;
    }

    /// <summary>Parses and validates a generated plan. Nothing runs unless every step is valid.</summary>
    public static List<string> TryReadPlan(string json, IReadOnlyList<ToolDefinition> tools, out string title, out List<AgentStep> steps)
    {
        title = "";
        steps = new List<AgentStep>();
        var problems = new List<string>();
        JsonObject? root;
        try { root = JsonNode.Parse(json) as JsonObject; }
        catch (JsonException) { root = null; }
        if (root is null) { problems.Add("The plan was not a JSON object."); return problems; }

        title = root.Str("title") ?? "";
        if (root["steps"] is not JsonArray array) { problems.Add("The plan has no 'steps' array."); return problems; }
        if (array.Count > MaxSteps) problems.Add($"The plan has {array.Count} steps; the maximum is {MaxSteps}.");

        var byName = tools.ToDictionary(t => t.Name, StringComparer.Ordinal);
        var index = 0;
        foreach (var node in array)
        {
            var number = index + 1;
            if (node is not JsonObject item) { problems.Add($"Step {number} is not an object."); index++; continue; }
            var toolName = item.Str("tool") ?? "";
            if (!byName.TryGetValue(toolName, out var definition))
            {
                problems.Add($"Step {number} uses '{toolName}', which is not an available tool.");
                index++;
                continue;
            }

            JsonObject? arguments = null;
            var raw = item.Str("arguments_json");
            if (item["arguments_json"] is JsonObject inline) arguments = (JsonObject)inline.DeepClone();
            else if (string.IsNullOrWhiteSpace(raw)) arguments = new JsonObject();
            else
            {
                try { arguments = JsonNode.Parse(raw) as JsonObject; }
                catch (JsonException) { arguments = null; }
            }
            if (arguments is null)
            {
                problems.Add($"Step {number}: arguments_json is not a JSON object.");
                index++;
                continue;
            }

            foreach (var error in SchemaValidator.Validate(definition, arguments)) problems.Add($"Step {number} ({toolName}): {error}");
            steps.Add(new AgentStep
            {
                Index = index,
                Description = string.IsNullOrWhiteSpace(item.Str("description")) ? toolName : item.Str("description")!.Trim(),
                Tool = toolName,
                Args = arguments
            });
            index++;
        }
        if (problems.Count > 0) steps = new List<AgentStep>();
        return problems;
    }

    /// <summary>Stops runners without changing task state, so they resume after the next start.</summary>
    public void Dispose()
    {
        _shuttingDown = true;
        var pending = new List<Task>();
        foreach (var runner in _runners.Values)
        {
            try { runner.Cts.Cancel(); }
            catch (ObjectDisposedException) { }
            pending.Add(runner.Completion);
        }
        try { Task.WaitAll(pending.ToArray(), TimeSpan.FromSeconds(3)); }
        catch (AggregateException) { }
    }
}
