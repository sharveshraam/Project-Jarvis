using Jarvis.Core.Models;
using Jarvis.Core.Tasks;
using Jarvis.Core.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jarvis.Agent;

/// <summary>Natural-language workflow planner (Phase 4). Turns high-level
/// requests into explicit, inspectable task plans. File organisation always
/// produces a preview plan first — nothing is moved without approval.</summary>
public sealed class WorkflowPlanner(
    TaskEngine tasks,
    ILogger<WorkflowPlanner>? logger = null)
{
    private readonly ILogger<WorkflowPlanner> _log = logger ?? NullLogger<WorkflowPlanner>.Instance;

    /// <summary>"Prepare my development environment" → open IDE, project, terminal.</summary>
    public async Task<AgentTask> PlanDevEnvironmentAsync(string projectPath, string ideName, CancellationToken ct = default)
    {
        var steps = new List<TaskStep>
        {
            new($"Open {ideName}", "open_app", JsonArgs(("name", ideName))),
            new($"Open project folder {projectPath}", "open_folder", JsonArgs(("path", projectPath))),
            new("Open Windows Terminal", "open_app", JsonArgs(("name", "terminal"))),
            new("Verify environment", "get_system_info", "{}")
        };
        var task = await tasks.CreateAsync("Prepare development environment", $"Open {ideName} and {projectPath}", steps, ct).ConfigureAwait(false);
        _log.LogInformation("Planned dev environment task {TaskId}", task.Id);
        return task;
    }

    /// <summary>"Organise my Downloads folder" → inspect, categorise, propose.
    /// Returns the plan for approval; execution is a separate, approved step.</summary>
    public IReadOnlyList<string> PlanFileOrganisation(string folder)
    {
        var search = new Jarvis.Core.Search.FileSearchService();
        var plan = new List<string> { $"Inspect {folder} (read-only — no files will be moved yet)." };
        if (!Directory.Exists(folder)) { plan.Add("Folder does not exist."); return plan; }
        var byCategory = Directory.EnumerateFiles(folder)
            .GroupBy(f => search.Categorise(Path.GetFileName(f)))
            .OrderByDescending(g => g.Count());
        foreach (var group in byCategory)
            plan.Add($"Create '{group.Key}/' and move {group.Count()} file(s) into it.");
        plan.Add("Duplicates will be listed for your review — nothing will be deleted automatically.");
        return plan;
    }

    public async Task<AgentTask> CreateOrganisationTaskAsync(string folder, IReadOnlyList<string> approvedMoves, CancellationToken ct = default)
    {
        // approvedMoves are "source|destination" pairs the user approved in the UI.
        var steps = approvedMoves.Select(m =>
        {
            var parts = m.Split('|');
            return new TaskStep($"Move {parts[0]} → {parts[1]}", "move_file", JsonArgs(("source", parts[0]), ("destination", parts[1])));
        }).ToList();
        return await tasks.CreateAsync($"Organise {folder}", "Approved file organisation", steps, ct).ConfigureAwait(false);
    }

    private static string JsonArgs(params (string Key, string Value)[] pairs)
    {
        var dict = pairs.ToDictionary(p => p.Key, p => (object)p.Value);
        return System.Text.Json.JsonSerializer.Serialize(dict);
    }
}

/// <summary>Safe file operations used by organisation tasks. Never deletes,
/// never overwrites: collisions get a numeric suffix.</summary>
public static class SafeFileOps
{
    public static string MoveFile(string source, string destination)
    {
        if (!File.Exists(source)) return $"Source not found: {source}";
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var final = destination;
        var i = 1;
        while (File.Exists(final))
        {
            final = Path.Combine(Path.GetDirectoryName(destination)!,
                $"{Path.GetFileNameWithoutExtension(destination)} ({i}){Path.GetExtension(destination)}");
            i++;
        }
        File.Move(source, final);
        return $"Moved to {final}";
    }
}
