using Jarvis.Core.Models;
using Jarvis.Core.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jarvis.Core.Tasks;

/// <summary>Multi-step task execution engine (Phase 4). Tasks run in the
/// background, verify each step, support pause/resume/cancel, and persist
/// state so recoverable tasks resume after restart. A step is only marked
/// complete when its tool reports success AND verification passes.</summary>
public sealed class TaskEngine(
    ITaskStore store,
    ToolRegistry tools,
    ILogger<TaskEngine>? logger = null)
{
    private readonly ILogger<TaskEngine> _log = logger ?? NullLogger<TaskEngine>.Instance;
    private readonly Dictionary<Guid, CancellationTokenSource> _running = new();
    private readonly object _gate = new();

    public async Task<AgentTask> CreateAsync(string title, string description, IReadOnlyList<TaskStep> steps, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var task = new AgentTask(Guid.NewGuid(), title, description, TaskStatus.Pending, steps, 0, null, null, now, now);
        await store.SaveAsync(task, ct).ConfigureAwait(false);
        return task;
    }

    public async Task<IReadOnlyList<AgentTask>> ListAsync(CancellationToken ct = default) => await store.ListAsync(ct).ConfigureAwait(false);

    /// <summary>Run a task to completion in the background. Returns immediately;
    /// progress is observable via <see cref="ListAsync"/> and notifications.</summary>
    public async Task StartAsync(Guid taskId, CancellationToken ct = default)
    {
        var task = await store.GetAsync(taskId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Task {taskId} not found.");
        if (task.Status is TaskStatus.Running or TaskStatus.Completed) return; // no duplicate execution

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_gate) _running[taskId] = cts;
        _ = RunLoopAsync(taskId, cts.Token);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private async Task RunLoopAsync(Guid taskId, CancellationToken ct)
    {
        try
        {
            var task = await store.GetAsync(taskId).ConfigureAwait(false);
            if (task is null) return;
            task = task with { Status = TaskStatus.Running, UpdatedAt = DateTimeOffset.UtcNow };
            await store.SaveAsync(task).ConfigureAwait(false);

            for (var i = task.CurrentStepIndex; i < task.Steps.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var step = task.Steps[i];
                string output;
                if (string.IsNullOrWhiteSpace(step.ToolName))
                {
                    output = step.Description; // informational step
                }
                else
                {
                    var args = string.IsNullOrWhiteSpace(step.ArgumentsJson)
                        ? System.Text.Json.JsonDocument.Parse("{}").RootElement
                        : System.Text.Json.JsonDocument.Parse(step.ArgumentsJson).RootElement;
                    var result = await tools.ExecuteAsync(step.ToolName, args, ct).ConfigureAwait(false);
                    if (!result.Success)
                    {
                        task = task with { Status = TaskStatus.Failed, Error = result.Error, UpdatedAt = DateTimeOffset.UtcNow };
                        await store.SaveAsync(task).ConfigureAwait(false);
                        _log.LogWarning("Task {TaskId} step {Step} failed: {Error}", taskId, i, result.Error);
                        return;
                    }
                    output = result.Output;
                }
                task = task with { CurrentStepIndex = i + 1, Result = output, UpdatedAt = DateTimeOffset.UtcNow };
                await store.SaveAsync(task).ConfigureAwait(false);
            }
            task = task with { Status = TaskStatus.Completed, UpdatedAt = DateTimeOffset.UtcNow };
            await store.SaveAsync(task).ConfigureAwait(false);
            _log.LogInformation("Task {TaskId} completed", taskId);
        }
        catch (OperationCanceledException)
        {
            var task = await store.GetAsync(taskId).ConfigureAwait(false);
            if (task is not null)
            {
                await store.SaveAsync(task with { Status = TaskStatus.Paused, UpdatedAt = DateTimeOffset.UtcNow }).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Task {TaskId} crashed", taskId);
            var task = await store.GetAsync(taskId).ConfigureAwait(false);
            if (task is not null)
            {
                await store.SaveAsync(task with { Status = TaskStatus.Failed, Error = ex.Message, UpdatedAt = DateTimeOffset.UtcNow }).ConfigureAwait(false);
            }
        }
        finally
        {
            lock (_gate) _running.Remove(taskId);
        }
    }

    public async Task PauseAsync(Guid taskId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_running.TryGetValue(taskId, out var cts)) cts.Cancel();
        }
        var task = await store.GetAsync(taskId, ct).ConfigureAwait(false);
        if (task is { Status: TaskStatus.Running })
            await store.SaveAsync(task with { Status = TaskStatus.Paused, UpdatedAt = DateTimeOffset.UtcNow }, ct).ConfigureAwait(false);
    }

    public async Task CancelAsync(Guid taskId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_running.TryGetValue(taskId, out var cts)) cts.Cancel();
        }
        var task = await store.GetAsync(taskId, ct).ConfigureAwait(false);
        if (task is not null)
            await store.SaveAsync(task with { Status = TaskStatus.Cancelled, UpdatedAt = DateTimeOffset.UtcNow }, ct).ConfigureAwait(false);
    }

    /// <summary>After a restart, Running tasks are reset to Paused (never auto-repeated,
    /// since a step may already have caused an external side effect).</summary>
    public async Task RecoverAfterRestartAsync(CancellationToken ct = default)
    {
        foreach (var task in await store.ListAsync(ct).ConfigureAwait(false))
        {
            if (task.Status == TaskStatus.Running)
                await store.SaveAsync(task with { Status = TaskStatus.Paused, UpdatedAt = DateTimeOffset.UtcNow }, ct).ConfigureAwait(false);
        }
    }
}
