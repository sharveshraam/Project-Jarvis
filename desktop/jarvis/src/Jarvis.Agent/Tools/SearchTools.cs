using System.Text;
using System.Text.Json.Nodes;
using Jarvis.Core.Abstractions;
using Jarvis.Core.Planning;
using Jarvis.Core.Tools;

namespace Jarvis.Agent.Tools;

/// <summary>Universal search over the local index (file names and document content).</summary>
public sealed class SearchTools : ToolModule
{
    private readonly ISearchIndex _index;
    private readonly ISettingsStore _settings;
    private readonly IClock _clock;

    public SearchTools(ISearchIndex index, ISettingsStore settings, IClock clock)
    {
        _index = index;
        _settings = settings;
        _clock = clock;
    }

    public override IEnumerable<ITool> Build()
    {
        yield return Background(
            Def("search_files", "Search the user's indexed folders by file name and by document content (PDF, DOCX, TXT, Markdown, source code). Returns paths with excerpts. Entirely local.", ToolCategory.Search, ToolRisk.Safe)
                .Str("query", "Words to look for.", required: true)
                .StrArray("extensions", "Optional file extensions to restrict to, e.g. ['.pdf', '.md'].")
                .Retryable(),
            (args, context, ct) =>
            {
                var stats = _index.Stats();
                if (stats.Files == 0)
                {
                    return ToolResult.Ok(_settings.Current.IndexedFolders.Count == 0
                        ? "No folders are indexed yet. Add folders under Settings › Search, then rebuild the index."
                        : "The search index is empty. Rebuild it first (rebuild_search_index).", false);
                }
                var extensions = args.StrList("extensions").Select(e => e.StartsWith('.') ? e.ToLowerInvariant() : "." + e.ToLowerInvariant()).ToList();
                var hits = _index.Search(args.Required("query"), 12, extensions.Count > 0 ? extensions : null);
                if (hits.Count == 0) return ToolResult.Ok($"Nothing in the index matches '{args.Str("query")}'.");
                var sb = new StringBuilder();
                sb.Append(hits.Count).Append(hits.Count == 1 ? " result:" : " results:");
                foreach (var h in hits)
                {
                    sb.Append("\n• ").Append(h.Path);
                    if (!string.IsNullOrWhiteSpace(h.Excerpt)) sb.Append("\n    “").Append(h.Excerpt).Append('”');
                }
                return ToolResult.Ok(sb.ToString(), true, new JsonObject
                {
                    ["results"] = ToJsonArray(hits.Select(h => new JsonObject { ["path"] = h.Path, ["match"] = h.MatchKind, ["excerpt"] = h.Excerpt }))
                });
            });

        yield return Background(
            Def("rebuild_search_index", "Re-scan the folders selected for universal search and update the local index.", ToolCategory.Search, ToolRisk.Routine)
                .Retryable().WithTimeout(TimeSpan.FromMinutes(30)),
            (args, context, ct) =>
            {
                var roots = _settings.Current.IndexedFolders.Where(f => !string.IsNullOrWhiteSpace(f)).ToList();
                if (roots.Count == 0) return ToolResult.Fail("No folders are selected for indexing. Add them under Settings › Search.");
                var stats = _index.Rebuild(roots, ct);
                return ToolResult.Ok($"Index rebuilt: {stats.Files} files, {stats.FilesWithContent} with searchable content.", true,
                    new JsonObject { ["files"] = stats.Files, ["with_content"] = stats.FilesWithContent });
            },
            (args, context) => ToolAssessment.Of(ToolRisk.Routine, "Rebuild the local search index"));

        yield return Sync(
            Def("get_search_index_status", "Report how many files are indexed, which folders are covered and when the index was last built.", ToolCategory.Search, ToolRisk.Safe).Retryable(),
            (args, context) =>
            {
                var stats = _index.Stats();
                var folders = _settings.Current.IndexedFolders;
                var when = stats.LastBuiltUtc.HasValue ? TimeText.Friendly(TimeText.ToLocal(stats.LastBuiltUtc.Value), _clock.LocalNow) : "never";
                return ToolResult.Ok(
                    $"{stats.Files} files indexed ({stats.FilesWithContent} with content), last built {when}. " +
                    (folders.Count == 0 ? "No folders are selected." : "Folders: " + string.Join(", ", folders) + "."));
            });

        yield return Sync(
            Def("clear_search_index", "Erase the local search index. Files themselves are not touched.", ToolCategory.Search, ToolRisk.Routine),
            (args, context) =>
            {
                _index.Clear();
                return _index.Stats().Files == 0 ? ToolResult.Ok("The search index has been cleared.") : ToolResult.Fail("The index could not be cleared.");
            },
            (args, context) => ToolAssessment.Of(ToolRisk.Routine, "Clear the local search index"),
            args => _index.Stats().Files == 0);
    }
}
