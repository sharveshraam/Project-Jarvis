using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Jarvis.Core.Abstractions;
using Jarvis.Core.Files;
using Jarvis.Core.Security;
using Jarvis.Core.Tools;

namespace Jarvis.Agent.Tools;

/// <summary>
/// File operations, confined by <see cref="PathPolicy"/> to the user's folders. Nothing here deletes files;
/// overwrites and bulk moves always go through an approval prompt that shows exactly what will change.
/// </summary>
public sealed class FileTools : ToolModule
{
    private const int PreviewLines = 40;

    private readonly FileService _files;
    private readonly PathPolicy _paths;
    private readonly IShellService _shell;
    private readonly IFileOpJournal _journal;

    public FileTools(FileService files, PathPolicy paths, IShellService shell, IFileOpJournal journal)
    {
        _files = files;
        _paths = paths;
        _shell = shell;
        _journal = journal;
    }

    private static JsonObject ToJson(FileDetails d) => new JsonObject
    {
        ["path"] = d.Path,
        ["is_folder"] = d.IsDirectory,
        ["size_bytes"] = d.SizeBytes,
        ["modified_local"] = d.ModifiedLocal.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        ["kind"] = d.Kind
    };

    private static string Line(FileDetails d)
        => d.IsDirectory
            ? $"📁 {Path.GetFileName(d.Path)}"
            : $"{Path.GetFileName(d.Path)} — {FileService.FormatSize(d.SizeBytes)}, {d.ModifiedLocal.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";

    private string RequireExisting(string? input)
    {
        var full = _paths.Require(input);
        if (!File.Exists(full) && !Directory.Exists(full)) throw new ToolArgumentException($"'{full}' does not exist.");
        return full;
    }

    private string RequireFolder(string? input)
    {
        var full = _paths.Require(input);
        if (!Directory.Exists(full)) throw new ToolArgumentException($"The folder '{full}' does not exist.");
        return full;
    }

    private static List<string> Preview(IReadOnlyList<Jarvis.Core.Models.FileMove> moves, string root)
    {
        var lines = moves.Take(PreviewLines)
            .Select(m => $"{Path.GetFileName(m.Source)}  →  {Path.GetRelativePath(root, m.Destination)}").ToList();
        if (moves.Count > PreviewLines) lines.Add($"… and {moves.Count - PreviewLines} more");
        return lines;
    }

    private ToolAssessment AssessTransfer(string verb, JsonObject args)
    {
        var source = RequireExisting(args.Str("source"));
        var destination = _paths.Require(args.Str("destination"));
        var target = FileService.ResolveTarget(source, destination);
        if (!_paths.IsAllowed(target, out var reason)) throw new ToolArgumentException(reason);
        var exists = FileService.TargetExists(source, destination);
        if (exists && !args.Bool("overwrite"))
            throw new ToolArgumentException($"'{target}' already exists. Nothing was changed; ask the user whether to overwrite it or choose another name.");
        if (exists && Directory.Exists(source))
            throw new ToolArgumentException($"'{target}' already exists. Folders are never merged or overwritten.");
        return exists
            ? ToolAssessment.Of(ToolRisk.Consequential, $"{verb} and OVERWRITE {target}", "Source: " + source, "This replaces the existing file at: " + target)
            : ToolAssessment.Of(ToolRisk.Routine, $"{verb} {source} → {target}");
    }

    public override IEnumerable<ITool> Build()
    {
        yield return Sync(
            Def("open_path", "Open a file or folder with its default application (folders open in File Explorer).", ToolCategory.Files, ToolRisk.Routine)
                .Str("path", "Full path, or a known folder name such as 'Downloads' or 'Documents'.", required: true).Retryable(),
            (args, context) => From(_shell.OpenPath(RequireExisting(args.Str("path")))),
            (args, context) => ToolAssessment.Of(ToolRisk.Routine, "Open " + RequireExisting(args.Str("path"))));

        yield return Sync(
            Def("list_folder", "List the contents of a folder.", ToolCategory.Files, ToolRisk.Safe)
                .Str("path", "Folder path or known folder name.", required: true).Retryable(),
            (args, context) =>
            {
                var folder = RequireFolder(args.Str("path"));
                var items = _files.List(folder, 200);
                if (items.Count == 0) return ToolResult.Ok($"{folder} is empty.");
                var sb = new StringBuilder();
                sb.Append(folder).Append(" — ").Append(items.Count).Append(items.Count == 200 ? "+ items:" : " items:");
                foreach (var item in items.Take(60)) sb.Append("\n• ").Append(Line(item));
                if (items.Count > 60) sb.Append("\n… and ").Append(items.Count - 60).Append(" more");
                return ToolResult.Ok(sb.ToString(), true, new JsonObject { ["items"] = ToJsonArray(items.Take(120).Select(ToJson)) });
            },
            (args, context) => ToolAssessment.Of(ToolRisk.Safe, "List " + RequireFolder(args.Str("path"))));

        yield return Sync(
            Def("get_file_info", "Show size, type and dates for a file or folder.", ToolCategory.Files, ToolRisk.Safe)
                .Str("path", "Full path.", required: true).Retryable(),
            (args, context) =>
            {
                var info = _files.GetInfo(RequireExisting(args.Str("path")));
                var text = info.IsDirectory
                    ? $"{info.Path}\nFolder with {info.ItemCount} items · created {info.CreatedLocal:yyyy-MM-dd} · modified {info.ModifiedLocal:yyyy-MM-dd HH:mm}"
                    : $"{info.Path}\n{info.Kind} · {FileService.FormatSize(info.SizeBytes)} · created {info.CreatedLocal:yyyy-MM-dd} · modified {info.ModifiedLocal:yyyy-MM-dd HH:mm}";
                return ToolResult.Ok(text, true, ToJson(info));
            },
            (args, context) => ToolAssessment.Of(ToolRisk.Safe, "Inspect " + RequireExisting(args.Str("path"))));

        yield return Background(
            Def("find_files", "Find files by name under a folder (live scan, wildcards * and ? allowed). For content search use search_files.", ToolCategory.Files, ToolRisk.Safe)
                .Str("folder", "Folder to search in, e.g. 'Documents'.", required: true)
                .Str("name", "File name or pattern, e.g. 'invoice*.pdf' or 'budget'.", required: true)
                .Retryable().WithTimeout(TimeSpan.FromSeconds(90)),
            (args, context, ct) =>
            {
                var folder = RequireFolder(args.Str("folder"));
                var found = _files.FindByName(folder, args.Required("name"), 50, ct);
                if (found.Count == 0) return ToolResult.Ok($"No files matching '{args.Str("name")}' were found under {folder}.");
                return ToolResult.Ok($"{found.Count}{(found.Count == 50 ? "+" : "")} matching files:\n• " + string.Join("\n• ", found), true,
                    new JsonObject { ["paths"] = ToJsonArray(found) });
            },
            (args, context) => ToolAssessment.Of(ToolRisk.Safe, $"Search {RequireFolder(args.Str("folder"))} for '{args.Str("name")}'"));

        yield return Sync(
            Def("read_text_file", "Read the text of a document or source file (TXT, MD, code, CSV, JSON...). The text is sent to Gemini to answer the user.", ToolCategory.Files, ToolRisk.Safe)
                .Str("path", "Full path to the file.", required: true).Retryable(),
            (args, context) =>
            {
                var path = RequireExisting(args.Str("path"));
                if (Directory.Exists(path)) return ToolResult.Fail("That is a folder, not a file.");
                var text = SecretDetector.Redact(_files.ReadText(path, 12000));
                return ToolResult.Ok($"Read {text.Length} characters from {Path.GetFileName(path)}.", true, new JsonObject { ["text"] = text });
            },
            (args, context) => ToolAssessment.Of(ToolRisk.Safe, "Read " + RequireExisting(args.Str("path"))));

        yield return Sync(
            Def("create_folder", "Create a new folder.", ToolCategory.Files, ToolRisk.Routine)
                .Str("path", "Full path of the folder to create.", required: true).Retryable(),
            (args, context) =>
            {
                var path = _paths.Require(args.Str("path"));
                if (Directory.Exists(path)) return ToolResult.Ok($"{path} already exists.");
                _files.CreateFolder(path);
                return ToolResult.Ok($"Created folder {path}.");
            },
            (args, context) => ToolAssessment.Of(ToolRisk.Routine, "Create folder " + _paths.Require(args.Str("path"))),
            args => _paths.TryResolve(args.Str("path"), out var full, out _) && Directory.Exists(full));

        yield return Sync(
            Def("rename_path", "Rename a file or folder in place. Never overwrites an existing item.", ToolCategory.Files, ToolRisk.Routine)
                .Str("path", "Full path of the item to rename.", required: true)
                .Str("new_name", "The new name (name only, not a path).", required: true),
            (args, context) =>
            {
                var source = RequireExisting(args.Str("path"));
                var target = _files.Rename(source, args.Required("new_name"));
                return ToolResult.Ok($"Renamed to {target}.");
            },
            (args, context) => ToolAssessment.Of(ToolRisk.Routine, $"Rename {RequireExisting(args.Str("path"))} to '{args.Str("new_name")}'"),
            args =>
            {
                if (!_paths.TryResolve(args.Str("path"), out var source, out _)) return false;
                var parent = Path.GetDirectoryName(source);
                if (parent is null || string.IsNullOrWhiteSpace(args.Str("new_name"))) return false;
                var target = Path.Combine(parent, args.Str("new_name")!);
                return !File.Exists(source) && !Directory.Exists(source) && (File.Exists(target) || Directory.Exists(target));
            });

        yield return Background(
            Def("copy_path", "Copy a file or folder. Overwriting an existing file requires overwrite=true and the user's approval.", ToolCategory.Files, ToolRisk.Routine)
                .Str("source", "Full path of the item to copy.", required: true)
                .Str("destination", "Destination folder or full target path.", required: true)
                .Bool("overwrite", "Replace an existing file at the destination. Only set when the user asked for it.")
                .WithTimeout(TimeSpan.FromMinutes(10)),
            (args, context, ct) =>
            {
                var source = RequireExisting(args.Str("source"));
                var destination = _paths.Require(args.Str("destination"));
                var target = _files.Copy(source, destination, args.Bool("overwrite"));
                return ToolResult.Ok($"Copied to {target}.");
            },
            (args, context) => AssessTransfer("Copy", args));

        yield return Background(
            Def("move_path", "Move a file or folder. Overwriting an existing file requires overwrite=true and the user's approval.", ToolCategory.Files, ToolRisk.Routine)
                .Str("source", "Full path of the item to move.", required: true)
                .Str("destination", "Destination folder or full target path.", required: true)
                .Bool("overwrite", "Replace an existing file at the destination. Only set when the user asked for it.")
                .WithTimeout(TimeSpan.FromMinutes(10)),
            (args, context, ct) =>
            {
                var source = RequireExisting(args.Str("source"));
                var destination = _paths.Require(args.Str("destination"));
                var target = _files.Move(source, destination, args.Bool("overwrite"));
                return ToolResult.Ok($"Moved to {target}.");
            },
            (args, context) => AssessTransfer("Move", args),
            args =>
            {
                if (!_paths.TryResolve(args.Str("source"), out var source, out _)) return false;
                if (!_paths.TryResolve(args.Str("destination"), out var destination, out _)) return false;
                if (File.Exists(source) || Directory.Exists(source)) return false;
                var target = Directory.Exists(destination) ? Path.Combine(destination, Path.GetFileName(source)) : destination;
                return File.Exists(target) || Directory.Exists(target);
            });

        yield return Background(
            Def("find_duplicates", "Find files with identical content in a folder. Reports only; nothing is removed.", ToolCategory.Files, ToolRisk.Safe)
                .Str("folder", "Folder to scan.", required: true)
                .Bool("include_subfolders", "Also scan subfolders.")
                .Retryable().WithTimeout(TimeSpan.FromMinutes(10)),
            (args, context, ct) =>
            {
                var folder = RequireFolder(args.Str("folder"));
                var groups = _files.FindDuplicates(folder, args.Bool("include_subfolders"), ct);
                if (groups.Count == 0) return ToolResult.Ok($"No duplicate files were found in {folder}.");
                long wasted = groups.Sum(g => g.SizeBytes * (g.Paths.Count - 1));
                var sb = new StringBuilder();
                sb.Append(groups.Count).Append(" sets of duplicates, ").Append(FileService.FormatSize(wasted)).Append(" could be reclaimed:");
                foreach (var g in groups.Take(15))
                {
                    sb.Append("\n• ").Append(FileService.FormatSize(g.SizeBytes)).Append(" × ").Append(g.Paths.Count).Append(": ")
                        .Append(string.Join(" | ", g.Paths.Select(p => Path.GetRelativePath(folder, p))));
                }
                if (groups.Count > 15) sb.Append("\n… and ").Append(groups.Count - 15).Append(" more sets");
                return ToolResult.Ok(sb.ToString(), true, new JsonObject
                {
                    ["groups"] = ToJsonArray(groups.Take(40).Select(g => new JsonObject { ["size_bytes"] = g.SizeBytes, ["paths"] = ToJsonArray(g.Paths) }))
                });
            },
            (args, context) => ToolAssessment.Of(ToolRisk.Safe, "Scan " + RequireFolder(args.Str("folder")) + " for duplicates"));

        yield return Background(
            Def("find_old_files", "List files in a folder that have not been modified for a long time. Reports only.", ToolCategory.Files, ToolRisk.Safe)
                .Str("folder", "Folder to scan.", required: true)
                .Int("older_than_days", "Minimum age in days (default 180).", false, 1, 10000)
                .Retryable().WithTimeout(TimeSpan.FromMinutes(5)),
            (args, context, ct) =>
            {
                var folder = RequireFolder(args.Str("folder"));
                var days = args.Int("older_than_days", 180);
                var old = _files.FindOldFiles(folder, days, 60, ct);
                if (old.Count == 0) return ToolResult.Ok($"Nothing in {folder} is older than {days} days.");
                var total = old.Sum(f => f.SizeBytes);
                var sb = new StringBuilder();
                sb.Append(old.Count).Append(old.Count == 60 ? "+" : "").Append(" files older than ").Append(days).Append(" days (")
                    .Append(FileService.FormatSize(total)).Append("):");
                foreach (var f in old.Take(30)) sb.Append("\n• ").Append(Line(f));
                return ToolResult.Ok(sb.ToString(), true, new JsonObject { ["files"] = ToJsonArray(old.Select(ToJson)) });
            },
            (args, context) => ToolAssessment.Of(ToolRisk.Safe, "Scan " + RequireFolder(args.Str("folder")) + " for old files"));

        yield return Background(
            Def("organise_folder", "Sort a folder's loose files into category subfolders (Documents, Images, Installers, Archives, ...). The user is shown the full list of moves and must approve it. Nothing is deleted and nothing is overwritten; it can be undone.", ToolCategory.Files, ToolRisk.Consequential)
                .Str("folder", "Folder to organise, e.g. 'Downloads'.", required: true)
                .WithTimeout(TimeSpan.FromMinutes(10)),
            (args, context, ct) =>
            {
                var folder = RequireFolder(args.Str("folder"));
                var plan = _files.PlanOrganise(folder);
                if (plan.Count == 0) return ToolResult.Ok($"{folder} has no loose files to organise.");
                var outcome = _files.ApplyMoves(plan, ct);
                var message = $"Moved {outcome.Moved} of {plan.Count} files in {folder} into category folders.";
                if (outcome.Errors.Count > 0) message += $" {outcome.Errors.Count} could not be moved: " + string.Join("; ", outcome.Errors.Take(5));
                message += " Say 'undo the last organise' to put them back.";
                var data = new JsonObject { ["moved"] = outcome.Moved, ["planned"] = plan.Count, ["failed"] = outcome.Errors.Count, ["batch_id"] = outcome.BatchId };
                return outcome.Moved > 0 ? ToolResult.Ok(message, outcome.Errors.Count == 0, data) : ToolResult.Fail(message);
            },
            (args, context) =>
            {
                var folder = RequireFolder(args.Str("folder"));
                var plan = _files.PlanOrganise(folder);
                if (plan.Count == 0) return ToolAssessment.Of(ToolRisk.Safe, $"Organise {folder} (nothing to move)");
                return new ToolAssessment(ToolRisk.Consequential, $"Move {plan.Count} files in {folder} into category folders", Preview(plan, folder));
            });

        yield return Sync(
            Def("undo_last_organise", "Put the files from the most recent organise run back where they were.", ToolCategory.Files, ToolRisk.Consequential),
            (args, context) =>
            {
                var batch = _journal.LatestBatchId();
                if (batch is null) return ToolResult.Fail("There is no organise run to undo.");
                var outcome = _files.UndoBatch(batch);
                var message = $"Restored {outcome.Moved} files to their original locations.";
                if (outcome.Errors.Count > 0) message += $" {outcome.Errors.Count} could not be restored: " + string.Join("; ", outcome.Errors.Take(5));
                return outcome.Moved > 0 || outcome.Errors.Count == 0 ? ToolResult.Ok(message, outcome.Errors.Count == 0) : ToolResult.Fail(message);
            },
            (args, context) =>
            {
                var batch = _journal.LatestBatchId();
                if (batch is null) return ToolAssessment.Of(ToolRisk.Safe, "Undo the last organise run (none recorded)");
                var moves = _journal.GetBatch(batch);
                var details = moves.Take(PreviewLines).Select(m => $"{m.Destination}  →  {m.Source}").ToList();
                if (moves.Count > PreviewLines) details.Add($"… and {moves.Count - PreviewLines} more");
                return new ToolAssessment(ToolRisk.Consequential, $"Move {moves.Count} files back to their original locations", details);
            });
    }
}
