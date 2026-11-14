using Jarvis.Core.Models;

namespace Jarvis.Core.Search;

/// <summary>Local file search over user-selected folders. Searches file names
/// and (for small text files) indexed content. Never uploads folders anywhere.</summary>
public sealed class FileSearchService
{
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".md", ".markdown", ".cs", ".py", ".js", ".ts", ".json", ".xml", ".html", ".htm", ".css", ".yml", ".yaml", ".log", ".ini", ".cfg", ".sql", ".java", ".kt", ".go", ".rs", ".cpp", ".c", ".h" };
    private static readonly HashSet<string> ExcludedDirs = new(StringComparer.OrdinalIgnoreCase)
        { "bin", "obj", ".git", "node_modules", ".vs", "packages" };
    private const long MaxTextFileBytes = 2_000_000;

    public IReadOnlyList<SearchHit> Search(IReadOnlyList<string> folders, string query, int maxResults = 20)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var terms = query.ToLowerInvariant().Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        var hits = new List<SearchHit>();
        foreach (var folder in folders.Where(Directory.Exists))
        {
            foreach (var file in EnumerateFiles(folder))
            {
                var name = Path.GetFileName(file);
                var nameScore = terms.Count(t => name.Contains(t, StringComparison.OrdinalIgnoreCase));
                string? excerpt = null;
                var contentScore = 0;
                if (TextExtensions.Contains(Path.GetExtension(file)))
                {
                    try
                    {
                        var info = new FileInfo(file);
                        if (info.Length <= MaxTextFileBytes)
                        {
                            var text = File.ReadAllText(file);
                            contentScore = terms.Count(t => text.Contains(t, StringComparison.OrdinalIgnoreCase));
                            if (contentScore > 0)
                            {
                                var idx = text.IndexOf(terms[0], StringComparison.OrdinalIgnoreCase);
                                excerpt = idx >= 0 ? text.Substring(idx, Math.Min(200, text.Length - idx)).Replace("\r", " ").Replace("\n", " ") : null;
                            }
                        }
                    }
                    catch { /* unreadable file: skip content, keep name match */ }
                }
                var score = nameScore * 2 + contentScore;
                if (score > 0) hits.Add(new SearchHit(file, name, excerpt, score));
                if (hits.Count >= maxResults * 4) break;
            }
        }
        return hits.OrderByDescending(h => h.Score).Take(maxResults).ToList();
    }

    private static IEnumerable<string> EnumerateFiles(string folder)
    {
        var stack = new Stack<string>();
        stack.Push(folder);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir); } catch { continue; }
            foreach (var f in files) yield return f;
            IEnumerable<string> subdirs;
            try { subdirs = Directory.EnumerateDirectories(dir); } catch { continue; }
            foreach (var d in subdirs)
            {
                if (!ExcludedDirs.Contains(Path.GetFileName(d))) stack.Push(d);
            }
        }
    }

    /// <summary>Duplicate detection by SHA-256 content hash (streamed, no full load).</summary>
    public IReadOnlyList<DuplicateGroup> FindDuplicates(string folder)
    {
        var byHash = new Dictionary<string, List<string>>();
        if (!Directory.Exists(folder)) return [];
        foreach (var file in EnumerateFiles(folder))
        {
            try
            {
                using var sha = System.Security.Cryptography.SHA256.Create();
                using var stream = File.OpenRead(file);
                var hash = Convert.ToHexString(sha.ComputeHash(stream));
                if (!byHash.TryGetValue(hash, out var list)) byHash[hash] = list = [];
                list.Add(file);
            }
            catch { /* skip unreadable */ }
        }
        return byHash.Where(kv => kv.Value.Count > 1)
            .Select(kv => new DuplicateGroup(kv.Key, kv.Value))
            .ToList();
    }

    public static string Categorise(string fileName) => Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant() switch
    {
        "pdf" or "doc" or "docx" or "txt" or "md" => "Documents",
        "jpg" or "jpeg" or "png" or "gif" or "webp" => "Images",
        "mp3" or "wav" or "flac" => "Audio",
        "mp4" or "mkv" or "avi" or "mov" => "Video",
        "zip" or "rar" or "7z" => "Archives",
        "cs" or "py" or "js" or "ts" or "java" or "kt" or "go" or "rs" => "Code",
        "exe" or "msi" or "apk" => "Installers",
        _ => "Other"
    };
}
