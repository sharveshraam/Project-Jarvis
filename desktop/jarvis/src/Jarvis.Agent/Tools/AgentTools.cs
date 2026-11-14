using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Jarvis.Core.Abstractions;
using Jarvis.Core.Models;
using Jarvis.Core.Modes;
using Jarvis.Core.Tools;

namespace Jarvis.Agent.Tools;

/// <summary>Mode switching, workspaces, and control of background tasks and workflows.</summary>
public sealed class AgentTools : ToolModule
{
    private readonly TaskEngine _engine;
    private readonly ModeService _modes;
    private readonly ISettingsStore _settings;
    private readonly IClock _clock;

    public AgentTools(TaskEngine engine, ModeService modes, ISettingsStore settings, IClock clock)
    {
        _engine = engine;
        _modes = modes;
        _settings = settings;
        _clock = clock;
    }

    private static string ShortId(AgentTask task) => task.Id.Length > 8 ? task.Id.Substring(0, 8) : task.Id;

    private static JsonObject ToJson(AgentTask t) => new JsonObject
    {
        ["id"] = ShortId(t),
        ["title"] = t.Title,
        ["state"] = t.State.ToString(),
        ["steps_done"] = t.CompletedSteps,
        ["steps_total"] = t.Steps.Count,
        ["result"] = t.Result,
        ["error"] = t.Error
    };

    private AgentTask RequireTask(JsonObject args)
    {
        var task = _engine.Find(args.Required("id"));
        if (task is null) throw new ToolArgumentException($"There is no background task with id '{args.Str("id")}'.");
        return task;
    }

    private static JarvisMode RequireMode(string? text)
    {
        if (!ModeCatalog.TryParse(text, out var mode)) throw new ToolArgumentException($"'{text}' is not a mode. Modes: study, development, research, focus, system, personal.");
        return mode;
    }

    public override IEnumerable<ITool> Build()
    {
        yield return Sync(
            Def("get_current_time", "Get the current local date and time.", ToolCategory.General, ToolRisk.Safe).Retryable(),
            (args, context) => ToolResult.Ok("It is " + _clock.LocalNow.ToString("dddd d MMMM yyyy, HH:mm", CultureInfo.InvariantCulture) + "."));

        yield return Sync(
            Def("switch_mode", "Switch JARVIS to another operating mode, which changes the available tools and behaviour. Optionally open that mode's configured workspace (apps, folders, files, project).", ToolCategory.General, ToolRisk.Routine)
                .Enum("mode", "The mode to switch to.", true, "study", "development", "research", "focus", "system", "personal")
                .Bool("open_workspace", "Also open the apps, folders and files configured for the mode.").Retryable(),
            (args, context) =>
            {
                var mode = RequireMode(args.Str("mode"));
                _modes.Switch(mode);
                var definition = ModeCatalog.Get(mode);
                var message = $"{definition.DisplayName} mode is active. {definition.Purpose}";
                var verified = _modes.Current == mode;
                if (args.Bool("open_workspace"))
                {
                    var (opened, failed, report) = _modes.OpenWorkspace(mode);
                    message += " " + report;
                    if (failed > 0 && opened == 0) verified = false;
                }
                return verified ? ToolResult.Ok(message) : ToolResult.Ok(message, false);
            },
            (args, context) =>
            {
                var mode = RequireMode(args.Str("mode"));
                var details = args.Bool("open_workspace") ? _modes.PreviewWorkspace(mode).ToArray() : Array.Empty<string>();
                return ToolAssessment.Of(ToolRisk.Routine, $"Switch to {ModeCatalog.Get(mode).DisplayName} mode", details);
            });

        yield return Sync(
            Def("open_workspace", "Open the apps, folders, files and project configured for a mode (defaults to the current mode).", ToolCategory.General, ToolRisk.Routine)
                .Enum("mode", "Which mode's workspace; omit for the current mode.", false, "study", "development", "research", "focus", "system", "personal"),
            (args, context) =>
            {
                var mode = args.Str("mode") is null ? _modes.Current : RequireMode(args.Str("mode"));
                var (opened, failed, report) = _modes.OpenWorkspace(mode);
                if (opened == 0 && failed > 0) return ToolResult.Fail(report);
                return ToolResult.Ok(report, failed == 0);
            },
            (args, context) =>
            {
                var mode = args.Str("mode") is null ? _modes.Current : RequireMode(args.Str("mode"));
                return ToolAssessment.Of(ToolRisk.Routine, $"Open the {ModeCatalog.Get(mode).DisplayName} workspace", _modes.PreviewWorkspace(mode).ToArray());
            });

        yield return Sync(
            Def("start_background_task", "Start a multi-step task that runs in the background while the user does something else. JARVIS plans the steps, runs them with the usual permission checks, and notifies the user when done.", ToolCategory.Agent, ToolRisk.Routine)
                .Str("goal", "The complete goal, with every detail needed to carry it out.", required: true),
            (args, context) =>
            {
                var task = _engine.StartGoal(args.Required("goal"), context.Mode);
                return ToolResult.Ok($"Background task started (id {ShortId(task)}). I will notify you when it finishes or needs approval.", true, ToJson(task));
            },
            (args, context) => ToolAssessment.Of(ToolRisk.Routine, "Start a background task: " + args.Str("goal")));

        yield return Sync(
            Def("run_workflow", "Run a built-in automation in the background. Works offline.", ToolCategory.Agent, ToolRisk.Routine)
                .Enum("name", "Which workflow.", true, Workflows.All.Select(w => w.Name).ToArray()),
            (args, context) =>
            {
                var workflow = Workflows.Find(args.Str("name"));
                if (workflow is null) return ToolResult.Fail($"There is no workflow named '{args.Str("name")}'.");
                var steps = Workflows.Build(workflow.Name, _settings.Current);
                if (steps.Count == 0) return ToolResult.Fail($"'{workflow.Title}' has nothing to do with the current settings.");
                var task = _engine.StartPlanned(workflow.Title, workflow.Description, workflow.Mode, steps);
                return ToolResult.Ok($"'{workflow.Title}' started with {steps.Count} steps (id {ShortId(task)}).", true, ToJson(task));
            },
            (args, context) =>
            {
                var workflow = Workflows.Find(args.Str("name"));
                if (workflow is null) throw new ToolArgumentException($"There is no workflow named '{args.Str("name")}'.");
                var details = Workflows.Build(workflow.Name, _settings.Current).Select((s, i) => $"{i + 1}. {s.Description}").ToArray();
                return ToolAssessment.Of(ToolRisk.Routine, "Run workflow: " + workflow.Title, details);
            });

        yield return Sync(
            Def("list_background_tasks", "List recent background tasks with their state and progress.", ToolCategory.Agent, ToolRisk.Safe).Retryable(),
            (args, context) =>
            {
                var tasks = _engine.List(15);
                if (tasks.Count == 0) return ToolResult.Ok("There are no background tasks.");
                var sb = new StringBuilder();
                if (_engine.AllPaused) sb.Append("Background tasks are paused.\n");
                foreach (var t in tasks)
                {
                    sb.Append("• ").Append(t.ProgressText).Append(" [").Append(ShortId(t)).Append(']');
                    if (!string.IsNullOrWhiteSpace(t.Error)) sb.Append(" — ").Append(t.Error);
                    sb.Append('\n');
                }
                return ToolResult.Ok(sb.ToString().TrimEnd(), true, new JsonObject { ["tasks"] = ToJsonArray(tasks.Select(ToJson)) });
            });

        yield return Sync(
            Def("get_background_task", "Show one background task in detail: each step, its state and its result.", ToolCategory.Agent, ToolRisk.Safe)
                .Str("id", "Task id (or its first characters).", required: true).Retryable(),
            (args, context) =>
            {
                var task = RequireTask(args);
                var sb = new StringBuilder();
                sb.Append(task.ProgressText);
                foreach (var step in task.Steps)
                {
                    sb.Append('\n').Append(step.Index + 1).Append(". [").Append(step.State).Append("] ").Append(step.Description);
                    if (!string.IsNullOrWhiteSpace(step.Result)) sb.Append(" — ").Append(step.Result);
                }
                if (!string.IsNullOrWhiteSpace(task.Error)) sb.Append("\nStopped: ").Append(task.Error);
                return ToolResult.Ok(sb.ToString(), true, ToJson(task));
            });

        yield return Sync(
            Def("cancel_background_task", "Cancel a background task. Steps already completed are not undone.", ToolCategory.Agent, ToolRisk.Routine)
                .Str("id", "Task id (or its first characters).", required: true),
            (args, context) =>
            {
                var task = RequireTask(args);
                if (!task.IsActive) return ToolResult.Ok($"'{task.Title}' had already finished ({task.State}).");
                return _engine.Cancel(task.Id)
                    ? ToolResult.Ok($"Cancellation requested for '{task.Title}'.", false)
                    : ToolResult.Fail($"'{task.Title}' could not be cancelled.");
            },
            (args, context) => ToolAssessment.Of(ToolRisk.Routine, "Cancel background task " + args.Str("id")));

        yield return Sync(
            Def("pause_background_task", "Pause one background task before its next step.", ToolCategory.Agent, ToolRisk.Routine)
                .Str("id", "Task id (or its first characters).", required: true),
            (args, context) =>
            {
                var task = RequireTask(args);
                return _engine.Pause(task.Id)
                    ? ToolResult.Ok($"'{task.Title}' will pause before its next step.", false)
                    : ToolResult.Fail($"'{task.Title}' is not running, so there is nothing to pause.");
            },
            (args, context) => ToolAssessment.Of(ToolRisk.Routine, "Pause background task " + args.Str("id")));

        yield return Sync(
            Def("resume_background_task", "Resume a paused background task. For a task interrupted by a restart this re-runs the interrupted step.", ToolCategory.Agent, ToolRisk.Routine)
                .Str("id", "Task id (or its first characters).", required: true),
            (args, context) =>
            {
                var task = RequireTask(args);
                return _engine.Resume(task.Id)
                    ? ToolResult.Ok($"'{task.Title}' resumed.", false)
                    : ToolResult.Fail($"'{task.Title}' is not paused.");
            },
            (args, context) =>
            {
                var task = RequireTask(args);
                var interrupted = task.Steps.FirstOrDefault(s => s.Interrupted);
                return interrupted is null
                    ? ToolAssessment.Of(ToolRisk.Routine, $"Resume '{task.Title}'")
                    : ToolAssessment.Of(ToolRisk.Consequential, $"Resume '{task.Title}' and re-run its interrupted step",
                        $"Step {interrupted.Index + 1}: {interrupted.Description}",
                        "This step was running when JARVIS stopped. It may already have taken effect; running it again could repeat it.");
            });
    }
}
