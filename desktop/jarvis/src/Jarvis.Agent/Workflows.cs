using System.Text.Json.Nodes;
using Jarvis.Core.Models;

namespace Jarvis.Agent;

public sealed record WorkflowDefinition(string Name, string Title, string Description, JarvisMode Mode);

/// <summary>
/// Built-in automations. Their steps are assembled locally from the user's settings, so they run offline,
/// and each step still passes through the same permission checks as any other tool call.
/// </summary>
public static class Workflows
{
    public static readonly IReadOnlyList<WorkflowDefinition> All = new[]
    {
        new WorkflowDefinition("prepare_dev_environment", "Prepare development environment",
            "Switch to Development mode, open the project in the IDE and a terminal, run the setup commands you approved, and report Git status.", JarvisMode.Development),
        new WorkflowDefinition("start_study_session", "Start study session",
            "Switch to Study mode, open the study workspace, start a focus session and show study tasks.", JarvisMode.Study),
        new WorkflowDefinition("organise_downloads", "Organise Downloads",
            "Report duplicates in Downloads, then sort loose files into category folders after you approve the preview.", JarvisMode.Personal),
        new WorkflowDefinition("system_checkup", "System check-up",
            "Check system health, list the heaviest processes and find old files in Downloads.", JarvisMode.System),
        new WorkflowDefinition("end_of_day_review", "End-of-day review",
            "Summarise the day, end any running focus session and report uncommitted work in the project.", JarvisMode.Personal)
    };

    public static WorkflowDefinition? Find(string? name)
        => All.FirstOrDefault(w => string.Equals(w.Name, (name ?? "").Trim(), StringComparison.OrdinalIgnoreCase));

    private static AgentStep Step(string description, string tool, JsonObject? args = null)
        => new AgentStep { Description = description, Tool = tool, Args = args ?? new JsonObject() };

    public static List<AgentStep> Build(string name, AppSettings settings)
    {
        var steps = new List<AgentStep>();
        var project = settings.GetMode(JarvisMode.Development).ProjectPath;
        var hasProject = !string.IsNullOrWhiteSpace(project);

        switch (name)
        {
            case "prepare_dev_environment":
                steps.Add(Step("Switch to Development mode and open its workspace", "switch_mode",
                    new JsonObject { ["mode"] = "development", ["open_workspace"] = true }));
                if (hasProject) steps.Add(Step("Open a terminal in the project folder", "open_terminal"));
                foreach (var command in settings.ApprovedCommands.Where(c => c.RunOnDevSetup && !string.IsNullOrWhiteSpace(c.Name)))
                    steps.Add(Step($"Run the approved command '{command.Name}'", "run_approved_command", new JsonObject { ["name"] = command.Name }));
                if (hasProject) steps.Add(Step("Check the project's Git status", "git_status"));
                break;

            case "start_study_session":
                steps.Add(Step("Switch to Study mode and open its workspace", "switch_mode",
                    new JsonObject { ["mode"] = "study", ["open_workspace"] = true }));
                steps.Add(Step("Start a 50-minute focus session", "start_focus_session",
                    new JsonObject { ["goal"] = "Study session", ["minutes"] = 50, ["close_distractions"] = true }));
                steps.Add(Step("List open tasks", "list_tasks"));
                break;

            case "organise_downloads":
                steps.Add(Step("Look for duplicate files in Downloads", "find_duplicates", new JsonObject { ["folder"] = "Downloads" }));
                steps.Add(Step("Sort Downloads into category folders (asks for approval)", "organise_folder", new JsonObject { ["folder"] = "Downloads" }));
                break;

            case "system_checkup":
                steps.Add(Step("Check CPU, memory, storage and battery", "get_system_status"));
                steps.Add(Step("List the heaviest processes", "get_top_processes", new JsonObject { ["count"] = 8 }));
                steps.Add(Step("Find files in Downloads untouched for six months", "find_old_files",
                    new JsonObject { ["folder"] = "Downloads", ["older_than_days"] = 180 }));
                break;

            case "end_of_day_review":
                steps.Add(Step("Summarise today's completed and pending work", "get_day_summary"));
                steps.Add(Step("End the focus session if one is running", "end_focus_session"));
                if (hasProject) steps.Add(Step("Report uncommitted changes in the project", "git_diff_summary"));
                break;
        }
        return steps;
    }
}
