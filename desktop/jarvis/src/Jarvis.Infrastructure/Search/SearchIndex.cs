using System.Globalization;
using System.Text.RegularExpressions;
using Jarvis.Core.Abstractions;
using Jarvis.Core.Models;
using Jarvis.Core.Text;
using Jarvis.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jarvis.Infrastructure.Search;

/// <summary>
/// Local universal search: file names plus extracted content (SQLite FTS5) for the folders the user selected.
/// The index never leaves the machine; it can be rebuilt or cleared at any time.
/// </summary>
public sealed class SearchIndex : ISearchIndex
{
    public const int MaxFiles = 200_000;
    public const long MaxContentBytes = 25L * 1024 * 1024;
    public const int MaxContentChars = 400_000;
    private const string LastBuiltKey = "index_last_built_utc";

    private static readonly HashSet<string> SkipDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "bin", "obj", "build", "dist", "target", "__pycache__", "venv", "packages", "AppData",
        "$RECYCLE.BIN", "System Volume Information"
    };

    private static readonly Regex TokenRx = new(@"[\p{L}\p{N}_]+", RegexOptions.Compiled);

    private readonly Database _db;
    private readonly ISettingsStore _settings;
    private readonly ILogger<SearchIndex> _log;

    public SearchIndex(Database db, ISettingsStore settings, ILogger<SearchIndex> log)
    {
        _db = db;
        _settings = settings;
        _log = log;
    }

    public IndexStats Rebuild(IEnumerable<string> roots, CancellationToken ct)
    {
        var existing = new Dictionary<string, (long Id, long Size, string Modified)>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in _db.Query("SELECT id,path,size,mtime_utc FROM indexed_files",
                     r => (Id: r.GetInt64(0), Path: r.GetString(1), Size: r.GetInt64(2), Modified: r.GetString(3))))
            existing[row.Path] = (row.Id, row.Size, row.Modified);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var indexed = 0;
        foreach (var root in roots.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root)) { _log.LogWarning("Indexed folder is missing and was skipped."); continue; }
            foreach (var file in Walk(root))
            {
                ct.ThrowIfCancellationRequested();
                if (seen.Count >= MaxFiles) break;
                seen.Add(file.FullName);
                var modified = Database.Iso(file.LastWriteTimeUtc);
                var known = existing.TryGetValue(file.FullName, out var e);
                if (known && e.Size == file.Length && e.Modified == modified) continue;
                IndexFile(file, modified, known ? e.Id : (long?)null);
                indexed++;
            }
        }

        var removed = 0;
        foreach (var stale in existing.Where(kv => !seen.Contains(kv.Key)).ToList())
        {
            ct.ThrowIfCancellationRequested();
            RemoveById(stale.Value.Id);
            removed++;
        }

        _settings.SetValue(LastBuiltKey, Database.Iso(DateTime.UtcNow));
        _log.LogInformation("Search index rebuilt: {Indexed} updated, {Removed} removed, {Total} files.", indexed, removed, seen.Count);
        return Stats();
    }

    private void IndexFile(FileInfo file, string modified, long? existingId)
    {
        var ext = file.Extension.ToLowerInvariant();
        string? content = null;
        if (TextExtractor.Supports(ext) && file.Length <= MaxContentBytes)
            content = TextExtractor.Extract(file.FullName, MaxContentChars);

        _db.InTransaction(db =>
        {
            long id;
            if (existingId.HasValue)
            {
                id = existingId.Value;
                db.Execute("DELETE FROM file_fts WHERE rowid=$id", ("$id", id));
                db.Execute("UPDATE indexed_files SET name=$n, ext=$e, size=$s, mtime_utc=$m, has_content=$h WHERE id=$id",
                    ("$n", file.Name), ("$e", ext), ("$s", file.Length), ("$m", modified), ("$h", content is null ? 0 : 1), ("$id", id));
            }
            else
            {
                id = db.Insert("INSERT INTO indexed_files(path,name,ext,size,mtime_utc,has_content) VALUES($p,$n,$e,$s,$m,$h)",
                    ("$p", file.FullName), ("$n", file.Name), ("$e", ext), ("$s", file.Length), ("$m", modified), ("$h", content is null ? 0 : 1));
            }
            db.Execute("INSERT INTO file_fts(rowid,path,name,content) VALUES($id,$p,$n,$c)",
                ("$id", id), ("$p", file.FullName), ("$n", file.Name), ("$c", content ?? ""));
        });
    }

    private void RemoveById(long id)
    {
        _db.InTransaction(db =>
        {
            db.Execute("DELETE FROM file_fts WHERE rowid=$id", ("$id", id));
            db.Execute("DELETE FROM indexed_files WHERE id=$id", ("$id", id));
        });
    }

    public void Clear()
    {
        _db.InTransaction(db =>
        {
            db.Execute("DELETE FROM file_fts");
            db.Execute("DELETE FROM indexed_files");
        });
        _settings.SetValue(LastBuiltKey, "");
    }

    public IReadOnlyList<SearchHit> Search(string query, int limit, IReadOnlyCollection<string>? extensions = null)
    {
        var hits = new List<SearchHit>();
        if (string.IsNullOrWhiteSpace(query)) return hits;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool ExtOk(string ext) => extensions is null || extensions.Count == 0 || extensions.Contains(ext, StringComparer.OrdinalIgnoreCase);

        // 1. File-name matches.
        var like = "%" + query.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
        foreach (var row in _db.Query("SELECT path,name,ext FROM indexed_files WHERE name LIKE $l ESCAPE '\\' ORDER BY length(name) LIMIT $n",
                     r => (Path: r.GetString(0), Name: r.GetString(1), Ext: r.GetString(2)), ("$l", like), ("$n", limit * 4)))
        {
            if (!ExtOk(row.Ext) || !seen.Add(row.Path)) continue;
            hits.Add(new SearchHit(row.Path, row.Name, "", "filename"));
            if (hits.Count >= limit) return hits;
        }

        // 2. Indexed-content matches with excerpts.
        var match = BuildMatchExpression(query);
        if (match is null) return hits;
        try
        {
            foreach (var row in _db.Query(
                         @"SELECT f.path, f.name, f.ext, snippet(file_fts, 2, '[', ']', ' … ', 18)
                           FROM file_fts JOIN indexed_files f ON f.id = file_fts.rowid
                           WHERE file_fts MATCH $q ORDER BY rank LIMIT $n",
                         r => (Path: r.GetString(0), Name: r.GetString(1), Ext: r.GetString(2), Excerpt: r.GetString(3)),
                         ("$q", match), ("$n", limit * 4)))
            {
                if (!ExtOk(row.Ext) || !seen.Add(row.Path)) continue;
                var excerpt = Regex.Replace(row.Excerpt, @"\s+", " ").Trim();
                hits.Add(new SearchHit(row.Path, row.Name, excerpt, "content"));
                if (hits.Count >= limit) break;
            }
        }
        catch (SqliteException ex)
        {
            _log.LogWarning("Content search failed ({Code}); returning file-name matches only.", ex.SqliteErrorCode);
        }
        return hits;
    }

    /// <summary>Turns free text into a safe FTS5 expression: every word quoted, the last one a prefix match.</summary>
    public static string? BuildMatchExpression(string query)
    {
        var tokens = TokenRx.Matches(query).Select(m => m.Value).Where(t => t.Length > 0).Take(12).ToList();
        if (tokens.Count == 0) return null;
        var parts = tokens.Select(t => "\"" + t + "\"").ToList();
        parts[parts.Count - 1] += "*";
        return string.Join(" ", parts);
    }

    public IndexStats Stats()
    {
        var files = Convert.ToInt32(_db.Scalar("SELECT COUNT(*) FROM indexed_files"), CultureInfo.InvariantCulture);
        var withContent = Convert.ToInt32(_db.Scalar("SELECT COUNT(*) FROM indexed_files WHERE has_content=1"), CultureInfo.InvariantCulture);
        var last = _settings.GetValue(LastBuiltKey);
        return new IndexStats(files, withContent, string.IsNullOrWhiteSpace(last) ? null : Database.ParseUtc(last));
    }

    private static IEnumerable<FileInfo> Walk(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            string[] files;
            string[] subdirectories;
            try
            {
                files = Directory.GetFiles(dir);
                subdirectories = Directory.GetDirectories(dir);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                continue;
            }

            foreach (var path in files)
            {
                var info = TryGetInfo(path);
                if (info is not null) yield return info;
            }

            foreach (var sub in subdirectories)
            {
                var name = Path.GetFileName(sub);
                if (name.StartsWith('.') || SkipDirectories.Contains(name)) continue;
                if (IsLinkOrHidden(sub)) continue;
                pending.Push(sub);
            }
        }
    }

    private static FileInfo? TryGetInfo(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if ((info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) return null;
            return info;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private static bool IsLinkOrHidden(string directory)
    {
        try
        {
            var attributes = File.GetAttributes(directory);
            return (attributes & (FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System)) != 0;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return true;
        }
    }
}
