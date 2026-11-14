using System.Text;
using System.Text.Json.Nodes;
using Jarvis.Core.Abstractions;
using Jarvis.Core.Models;
using Jarvis.Core.Security;
using Jarvis.Core.Text;
using Jarvis.Core.Tools;

namespace Jarvis.Agent.Tools;

/// <summary>
/// Development workflow: IDE, terminal, project inspection, read-only Git, and commands the user pre-approved.
/// Gemini can never supply an executable or arguments: Git calls use fixed argument lists, and custom commands
/// are referenced by the name the user gave them in Settings.
/// </summary>
public sealed class DevTools : ToolModule
{
    private static readonly (string Marker, string Kind)[] ProjectMarkers =
    {
        ("*.sln", ".NET solution"), ("*.csproj", ".NET project"), ("package.json", "Node.js"), ("pyproject.toml", "Python"),
        ("requirements.txt", "Python"), ("Cargo.toml", "Rust"), ("go.mod", "Go"), ("pom.xml", "Java (Maven)"),
        ("build.gradle", "Gradle"), ("build.gradle.kts", "Gradle (Kotlin DSL)"), ("CMakeLists.txt", "CMake"), ("pubspec.yaml", "Flutter/Dart")
    };

    private readonly IProcessRunner _runner;
    private readonly ISettingsStore _settings;
    private readonly IShellService _shell;
    private readonly PathPolicy _paths;

    public DevTools(IProcessRunner runner, ISettingsStore settings, IShellService shell, PathPolicy paths)
    {
        _runner = runner;
        _settings = settings;
        _shell = shell;
        _paths = paths;
    }

    /// <summary>The explicit path, or the project configured for Development mode.</summary>
    private string ResolveProject(JsonObject args)
    {
        var input = args.Str("path");
        if (string.IsNullOrWhiteSpace(input)) input = _settings.Current.GetMode(JarvisMode.Development).ProjectPath;
        if (string.IsNullOrWhiteSpace(input))
            throw new ToolArgumentException("No project folder was given and none is configured. Set one under Settings › Modes › Development.");
        var full = _paths.Require(input);
        if (!Directory.Exists(full)) throw new ToolArgumentException($"The project folder '{full}' does not exist.");
        return full;
    }

    private async Task<ToolResult> GitAsync(string project, string what, CancellationToken ct, params string[] gitArguments)
    {
        var arguments = new List<string> { "-C", project, "--no-pager" };
        arguments.AddRange(gitArguments);
        var result = await _runner.RunAsync("git", arguments, project, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            var error = string.IsNullOrWhiteSpace(result.Error) ? "Git returned an error." : result.Error.Trim();
            if (error.Contains("could not be started", StringComparison.OrdinalIgnoreCase)) error = "Git is not installed or not on the PATH.";
            return ToolResult.Fail($"{what} failed: {Relevance.Truncate(error, 400)}");
        }
        var output = string.IsNullOrWhiteSpace(result.Output) ? "(no output)" : Relevance.Truncate(SecretDetector.Redact(result.Output.TrimEnd()), 6000);
        return ToolResult.Ok($"{what} for {project}:\n{output}");
    }

    private ApprovedCommand RequireCommand(JsonObject args)
    {
        var name = args.Required("name");
        var command = _settings.Current.ApprovedCommands.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        if (command is null)
        {
            var known = _settings.Current.ApprovedCommands.Select(c => c.Name).ToList();
            throw new ToolArgumentException(known.Count == 0
                ? "No commands have been approved. The user can add them under Settings › Permissions › Approved commands."
                : $"'{name}' is not an approved command. Approved: {string.Join(", ", known)}.");
        }
        if (string.IsNullOrWhiteSpace(command.Executable)) throw new ToolArgumentException($"The approved command '{command.Name}' has no executable configured.");
        return command;
    }

    public override IEnumerable<ITool> Build()
    {
        yield return Sync(
            Def("open_project", "Open a project folder in the IDE (VS Code). Defaults to the project configured for Development mode.", ToolCategory.Development, ToolRisk.Routine)
                .Str("path", "Project folder; omit to use the configured project.").Retryable(),
            (args, context) => From(_shell.OpenInIde(ResolveProject(args))),
            (args, context) => ToolAssessment.Of(ToolRisk.Routine, "Open " + ResolveProject(args) + " in the IDE"));

        yield return Sync(
            Def("open_terminal", "Open Windows Terminal in a folder. Defaults to the configured project. No command is run in it.", ToolCategory.Development, ToolRisk.Routine)
                .Str("path", "Folder to open the terminal in; omit to use the configured project."),
            (args, context) => From(_shell.OpenTerminal(ResolveProject(args))),
            (args, context) => ToolAssessment.Of(ToolRisk.Routine, "Open a terminal in " + ResolveProject(args)));

        yield return Sync(
            Def("get_project_overview", "Inspect a project folder: detected project type and top-level contents.", ToolCategory.Development, ToolRisk.Safe)
                .Str("path", "Project folder; omit to use the configured project.").Retryable(),
            (args, context) =>
            {
                var project = ResolveProject(args);
                var kinds = new List<string>();
                foreach (var (marker, kind) in ProjectMarkers)
                {
                    if (Directory.EnumerateFiles(project, marker, SearchOption.TopDirectoryOnly).Any() && !kinds.Contains(kind)) kinds.Add(kind);
                }
                var directories = Directory.EnumerateDirectories(project).Select(Path.GetFileName).Where(n => n is not null && !n.StartsWith('.')).Take(40).ToList();
                var files = Directory.EnumerateFiles(project).Select(Path.GetFileName).Take(40).ToList();
                var sb = new StringBuilder();
                sb.Append(project).Append('\n');
                sb.Append("Type: ").Append(kinds.Count == 0 ? "not recognised" : string.Join(", ", kinds)).Append('\n');
                sb.Append("Git repository: ").Append(Directory.Exists(Path.Combine(project, ".git")) ? "yes" : "no").Append('\n');
                sb.Append("Folders: ").Append(directories.Count == 0 ? "none" : string.Join(", ", directories)).Append('\n');
                sb.Append("Files: ").Append(files.Count == 0 ? "none" : string.Join(", ", files));
                return ToolResult.Ok(sb.ToString());
            },
            (args, context) => ToolAssessment.Of(ToolRisk.Safe, "Inspect " + ResolveProject(args)));

        yield return Tool(
            Def("git_status", "Show the Git branch and working-tree status of a project (read-only).", ToolCategory.Development, ToolRisk.Safe)
                .Str("path", "Project folder; omit to use the configured project.").Retryable(),
            (args, context, ct) => GitAsync(ResolveProject(args), "Git status", ct, "status", "--short", "--branch"),
            (args, context) => ToolAssessment.Of(ToolRisk.Safe, "git status in " + ResolveProject(args)));

        yield return Tool(
            Def("git_log", "Show recent Git commits of a project (read-only).", ToolCategory.Development, ToolRisk.Safe)
                .Str("path", "Project folder; omit to use the configured project.")
                .Int("count", "Number of commits (default 10).", false, 1, 50).Retryable(),
            (args, context, ct) => GitAsync(ResolveProject(args), "Recent commits", ct,
                "log", "--oneline", "--decorate", "-n", Math.Clamp(args.Int("count", 10), 1, 50).ToString(System.Globalization.CultureInfo.InvariantCulture)),
            (args, context) => ToolAssessment.Of(ToolRisk.Safe, "git log in " + ResolveProject(args)));

        yield return Tool(
            Def("git_diff_summary", "Summarise uncommitted changes in a project: which files changed and by how much (read-only).", ToolCategory.Development, ToolRisk.Safe)
                .Str("path", "Project folder; omit to use the configured project.").Retryable(),
            (args, context, ct) => GitAsync(ResolveProject(args), "Uncommitted changes", ct, "diff", "--stat", "HEAD"),
            (args, context) => ToolAssessment.Of(ToolRisk.Safe, "git diff --stat in " + ResolveProject(args)));

        yield return Tool(
            Def("git_branches", "List the local Git branches of a project (read-only).", ToolCategory.Development, ToolRisk.Safe)
                .Str("path", "Project folder; omit to use the configured project.").Retryable(),
            (args, context, ct) => GitAsync(ResolveProject(args), "Branches", ct, "branch", "--list", "-vv"),
            (args, context) => ToolAssessment.Of(ToolRisk.Safe, "git branch in " + ResolveProject(args)));

        yield return Sync(
            Def("list_approved_commands", "List the commands the user has approved for JARVIS to run by name.", ToolCategory.Development, ToolRisk.Safe).Retryable(),
            (args, context) =>
            {
                var commands = _settings.Current.ApprovedCommands;
                if (commands.Count == 0) return ToolResult.Ok("No commands have been approved. They can be added under Settings › Permissions › Approved commands.");
                return ToolResult.Ok("Approved commands:\n" + string.Join("\n", commands.Select(c =>
                    $"• {c.Name} — {Path.GetFileName(c.Executable)} {string.Join(" ", c.Arguments)}{(c.Trusted ? " (trusted)" : " (asks each time)")}")));
            });

        yield return Tool(
            Def("run_approved_command", "Run one of the user's pre-approved commands by its name. You cannot pass an executable or arguments.", ToolCategory.Development, ToolRisk.Consequential)
                .Str("name", "The name of an approved command (see list_approved_commands).", required: true)
                .WithTimeout(TimeSpan.FromMinutes(10)),
            async (args, context, ct) =>
            {
                var command = RequireCommand(args);
                string? workingDirectory = null;
                if (!string.IsNullOrWhiteSpace(command.WorkingDirectory))
                {
                    workingDirectory = Environment.ExpandEnvironmentVariables(command.WorkingDirectory);
                    if (!Directory.Exists(workingDirectory)) return ToolResult.Fail($"The working folder '{workingDirectory}' does not exist.");
                }
                var result = await _runner.RunAsync(command.Executable, command.Arguments, workingDirectory, TimeSpan.FromMinutes(9), ct).ConfigureAwait(false);
                var output = Relevance.Truncate(SecretDetector.Redact((result.Output + "\n" + result.Error).Trim()), 4000);
                var data = new JsonObject { ["exit_code"] = result.ExitCode, ["output"] = output };
                if (result.TimedOut) return ToolResult.Fail($"'{command.Name}' was stopped after running too long.");
                return result.ExitCode == 0
                    ? ToolResult.Ok($"'{command.Name}' finished successfully (exit code 0).\n{output}", true, data)
                    : ToolResult.Fail($"'{command.Name}' exited with code {result.ExitCode}.\n{output}");
            },
            (args, context) =>
            {
                var command = RequireCommand(args);
                var line = (command.Executable + " " + string.Join(" ", command.Arguments)).Trim();
                return command.Trusted
                    ? ToolAssessment.Of(ToolRisk.Routine, $"Run approved command '{command.Name}'", line)
                    : ToolAssessment.Of(ToolRisk.Consequential, $"Run command '{command.Name}'", line,
                        string.IsNullOrWhiteSpace(command.WorkingDirectory) ? "Working folder: default" : "Working folder: " + command.WorkingDirectory);
            });
    }
}
