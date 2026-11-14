using Jarvis.Core.Search;
using Jarvis.Core.Tools;

namespace Jarvis.Agent;

/// <summary>File tools registered in the tool registry: search, duplicate
/// detection, safe move (approval-gated by risk tier).</summary>
public static class FileTools
{
    public static IEnumerable<ToolDefinition> Create(FileSearchService search, IReadOnlyList<string> indexedFolders)
    {
        yield return new ToolDefinition("search_files", "Search indexed folders by file name and text content.",
            RiskTier.Trusted, """{"type":"object","properties":{"query":{"type":"string"}},"required":["query"]}""",
            async (args, ct) => await Task.Run(() =>
            {
                var hits = search.Search(indexedFolders, args.GetProperty("query").GetString() ?? "");
                return hits.Count == 0 ? "No matches in indexed folders."
                    : string.Join("\n", hits.Select(h => $"- {h.FileName} ({h.FilePath}){(h.Excerpt is null ? "" : ": " + h.Excerpt)}"));
            }, ct));

        yield return new ToolDefinition("find_duplicates", "Find duplicate files by content hash in a folder.",
            RiskTier.Trusted, """{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}""",
            async (args, ct) => await Task.Run(() =>
            {
                var groups = search.FindDuplicates(args.GetProperty("path").GetString() ?? "");
                return groups.Count == 0 ? "No duplicates found."
                    : string.Join("\n", groups.Select(g => $"{g.Paths.Count} copies of hash {g.ContentHash[..12]}…:\n" + string.Join("\n", g.Paths.Select(p => "  " + p))));
            }, ct));

        yield return new ToolDefinition("move_file", "Move a file to a new location (never overwrites; collisions are suffixed).",
            RiskTier.Destructive, """{"type":"object","properties":{"source":{"type":"string"},"destination":{"type":"string"}},"required":["source","destination"]}""",
            async (args, ct) => await Task.Run(() => SafeFileOps.MoveFile(
                args.GetProperty("source").GetString() ?? "",
                args.GetProperty("destination").GetString() ?? ""), ct));
    }
}
