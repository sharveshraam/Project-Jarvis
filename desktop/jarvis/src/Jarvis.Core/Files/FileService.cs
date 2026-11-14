using System.Security.Cryptography;
using Jarvis.Core.Abstractions;
using Jarvis.Core.Models;

namespace Jarvis.Core.Files;

public sealed record FileDetails(string Path, bool IsDirectory, long SizeBytes, DateTime CreatedLocal, DateTime ModifiedLocal, int? ItemCount, string Kind);
public sealed record DuplicateGroup(long SizeBytes, string Hash, IReadOnlyList<string> Paths);
public sealed record MoveOutcome(string BatchId, int Moved, IReadOnlyList<string> Errors);

public static class FileCategorizer
{
    private static readonly Dictionary<string, string> Map = Build();

    private static Dictionary<string, string> Build()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Add(string category, params string[] extensions) { foreach (var e in extensions) map[e] = category; }
        Add("Images", ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".svg", ".heic", ".tif", ".tiff", ".ico");
        Add("Documents", ".pdf", ".doc", ".docx", ".txt", ".md", ".rtf", ".odt", ".ppt", ".pptx", ".xls", ".xlsx", ".csv", ".epub");
        Add("Videos", ".mp4", ".mkv", ".mov", ".avi", ".webm", ".wmv", ".m4v");
        Add("Audio", ".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg", ".wma");
        Add("Archives", ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz", ".iso");
        Add("Installers", ".exe", ".msi", ".msix", ".appx", ".apk", ".dmg");
        Add("Code", ".cs", ".py", ".js", ".ts", ".tsx", ".jsx", ".java", ".kt", ".c", ".cpp", ".h", ".hpp", ".go", ".rs",
            ".rb", ".php", ".html", ".css", ".sql", ".sh", ".ps1", ".bat", ".json", ".xml", ".yml", ".yaml", ".ipynb");
        return map;
    }

    public static string CategoryFor(string path)
        => Map.TryGetValue(System.IO.Path.GetExtension(path), out var category) ? category : "Other";

    public static bool IsTransient(string path)
    {
        var ext = System.IO.Path.GetExtension(path);
        var name = System.IO.Path.GetFileName(path);
        return ext.Equals(".crdownload", StringComparison.OrdinalIgnoreCase) || ext.Equals(".part", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".tmp", StringComparison.OrdinalIgnoreCase) || ext.Equals(".download", StringComparison.OrdinalIgnoreCase)
               || name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) || name.StartsWith('.');
    }
}

/// <summary>
/// File operations with safety built in: nothing is ever deleted, nothing is overwritten unless
/// explicitly requested, and bulk moves are journalled so they can be undone.
/// Callers are responsible for authorising paths with <see cref="Security.PathPolicy"/>.
/// </summary>
public sealed class FileService
{
    private readonly IFileOpJournal _journal;

    public FileService(IFileOpJournal journal) => _journal = journal;

    private static EnumerationOptions Options(bool recursive) => new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = recursive,
        AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint
    };

    public FileDetails GetInfo(string path)
    {
        if (Directory.Exists(path))
        {
            var dir = new DirectoryInfo(path);
            var count = dir.EnumerateFileSystemInfos("*", Options(false)).Take(100000).Count();
            return new FileDetails(dir.FullName, true, 0, dir.CreationTime, dir.LastWriteTime, count, "Folder");
        }
        if (File.Exists(path))
        {
            var file = new FileInfo(path);
            return new FileDetails(file.FullName, false, file.Length, file.CreationTime, file.LastWriteTime, null, FileCategorizer.CategoryFor(path));
        }
        throw new FileNotFoundException($"'{path}' does not exist.");
    }

    public IReadOnlyList<FileDetails> List(string folder, int max)
    {
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException($"Folder '{folder}' does not exist.");
        return new DirectoryInfo(folder).EnumerateFileSystemInfos("*", Options(false))
            .OrderByDescending(i => i is DirectoryInfo).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .Select(i => i is FileInfo f
                ? new FileDetails(f.FullName, false, f.Length, f.CreationTime, f.LastWriteTime, null, FileCategorizer.CategoryFor(f.Name))
                : new FileDetails(i.FullName, true, 0, i.CreationTime, i.LastWriteTime, null, "Folder"))
            .ToList();
    }

    public string CreateFolder(string path)
    {
        if (File.Exists(path)) throw new IOException($"A file already exists at '{path}'.");
        Directory.CreateDirectory(path);
        if (!Directory.Exists(path)) throw new IOException($"The folder '{path}' could not be created.");
        return path;
    }

    public string Rename(string path, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName) || newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || newName.Contains(Path.DirectorySeparatorChar) || newName.Contains(Path.AltDirectorySeparatorChar))
            throw new ArgumentException($"'{newName}' is not a valid file name.");
        var parent = Path.GetDirectoryName(path) ?? throw new ArgumentException("That item cannot be renamed.");
        var target = Path.Combine(parent, newName);
        return Move(path, target, overwrite: false);
    }

    /// <summary>The path a copy/move would actually write to (an existing folder destination means "into it").</summary>
    public static string ResolveTarget(string source, string destination)
        => Directory.Exists(destination) && !PathsEqual(source, destination)
            ? Path.Combine(destination, Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar)))
            : destination;

    public static bool TargetExists(string source, string destination)
    {
        var target = ResolveTarget(source, destination);
        return File.Exists(target) || (Directory.Exists(target) && !PathsEqual(target, destination));
    }

    public string Copy(string source, string destination, bool overwrite)
    {
        var target = ResolveTarget(source, destination);
        if (File.Exists(source))
        {
            if (File.Exists(target) && !overwrite) throw new IOException($"'{target}' already exists. Nothing was overwritten.");
            EnsureParent(target);
            File.Copy(source, target, overwrite);
            if (!File.Exists(target)) throw new IOException("The copy could not be verified.");
            return target;
        }
        if (Directory.Exists(source))
        {
            if (IsSameOrChild(target, source)) throw new IOException("A folder cannot be copied into itself.");
            if (Directory.Exists(target) && !overwrite) throw new IOException($"'{target}' already exists. Nothing was overwritten.");
            CopyDirectory(source, target, overwrite);
            return target;
        }
        throw new FileNotFoundException($"'{source}' does not exist.");
    }

    public string Move(string source, string destination, bool overwrite)
    {
        var target = ResolveTarget(source, destination);
        if (PathsEqual(source, target)) throw new IOException("The source and destination are the same.");
        if (File.Exists(source))
        {
            if ((File.Exists(target) || Directory.Exists(target)) && !overwrite)
                throw new IOException($"'{target}' already exists. Nothing was overwritten.");
            EnsureParent(target);
            File.Move(source, target, overwrite);
            if (!File.Exists(target) || File.Exists(source)) throw new IOException("The move could not be verified.");
            return target;
        }
        if (Directory.Exists(source))
        {
            if (IsSameOrChild(target, source)) throw new IOException("A folder cannot be moved into itself.");
            if (Directory.Exists(target) || File.Exists(target))
                throw new IOException($"'{target}' already exists. Folders are never merged or overwritten.");
            EnsureParent(target);
            Directory.Move(source, target);
            if (!Directory.Exists(target)) throw new IOException("The move could not be verified.");
            return target;
        }
        throw new FileNotFoundException($"'{source}' does not exist.");
    }

    public IReadOnlyList<string> FindByName(string root, string pattern, int max, CancellationToken ct)
    {
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Folder '{root}' does not exist.");
        var needle = pattern.Trim();
        var wildcard = needle.Contains('*') || needle.Contains('?');
        var results = new List<string>();
        var source = wildcard
            ? Directory.EnumerateFileSystemEntries(root, needle, Options(true))
            : Directory.EnumerateFileSystemEntries(root, "*", Options(true))
                .Where(p => Path.GetFileName(p).Contains(needle, StringComparison.OrdinalIgnoreCase));
        foreach (var path in source)
        {
            ct.ThrowIfCancellationRequested();
            results.Add(path);
            if (results.Count >= max) break;
        }
        return results;
    }

    public IReadOnlyList<DuplicateGroup> FindDuplicates(string root, bool recursive, CancellationToken ct)
    {
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Folder '{root}' does not exist.");
        var bySize = new Dictionary<long, List<string>>();
        foreach (var file in new DirectoryInfo(root).EnumerateFiles("*", Options(recursive)))
        {
            ct.ThrowIfCancellationRequested();
            if (file.Length == 0) continue;
            if (!bySize.TryGetValue(file.Length, out var list)) bySize[file.Length] = list = new List<string>();
            list.Add(file.FullName);
        }

        var groups = new List<DuplicateGroup>();
        foreach (var (size, paths) in bySize.Where(kv => kv.Value.Count > 1))
        {
            var byHash = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var path in paths)
            {
                ct.ThrowIfCancellationRequested();
                string hash;
                try { hash = HashFile(path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                if (!byHash.TryGetValue(hash, out var same)) byHash[hash] = same = new List<string>();
                same.Add(path);
            }
            foreach (var kv in byHash.Where(kv => kv.Value.Count > 1))
                groups.Add(new DuplicateGroup(size, kv.Key, kv.Value.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList()));
        }
        return groups.OrderByDescending(g => g.SizeBytes * (g.Paths.Count - 1)).ToList();
    }

    public IReadOnlyList<FileDetails> FindOldFiles(string root, int olderThanDays, int max, CancellationToken ct)
    {
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Folder '{root}' does not exist.");
        var cutoff = DateTime.Now.AddDays(-olderThanDays);
        var results = new List<FileDetails>();
        foreach (var file in new DirectoryInfo(root).EnumerateFiles("*", Options(true)))
        {
            ct.ThrowIfCancellationRequested();
            if (file.LastWriteTime < cutoff)
                results.Add(new FileDetails(file.FullName, false, file.Length, file.CreationTime, file.LastWriteTime, null, FileCategorizer.CategoryFor(file.Name)));
        }
        return results.OrderBy(f => f.ModifiedLocal).Take(max).ToList();
    }

    /// <summary>Proposes moving the folder's loose files into category subfolders. Nothing is changed.</summary>
    public IReadOnlyList<FileMove> PlanOrganise(string root)
    {
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Folder '{root}' does not exist.");
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var moves = new List<FileMove>();
        foreach (var file in new DirectoryInfo(root).EnumerateFiles("*", Options(false)).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (FileCategorizer.IsTransient(file.Name) || (file.Attributes & FileAttributes.Hidden) != 0) continue;
            var target = UniquePath(Path.Combine(root, FileCategorizer.CategoryFor(file.Name), file.Name), reserved);
            reserved.Add(target);
            moves.Add(new FileMove(file.FullName, target));
        }
        return moves;
    }

    /// <summary>Executes moves without ever overwriting; every successful move is journalled for undo.</summary>
    public MoveOutcome ApplyMoves(IReadOnlyList<FileMove> moves, CancellationToken ct)
    {
        var batchId = Guid.NewGuid().ToString("N");
        var errors = new List<string>();
        var moved = 0;
        foreach (var move in moves)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (!File.Exists(move.Source)) { errors.Add($"{Path.GetFileName(move.Source)}: no longer exists."); continue; }
                var target = UniquePath(move.Destination, null);
                EnsureParent(target);
                File.Move(move.Source, target, overwrite: false);
                _journal.Record(batchId, move.Source, target);
                moved++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{Path.GetFileName(move.Source)}: {ex.Message}");
            }
        }
        return new MoveOutcome(batchId, moved, errors);
    }

    /// <summary>Moves every file of a journalled batch back to where it came from.</summary>
    public MoveOutcome UndoBatch(string batchId)
    {
        var errors = new List<string>();
        var restored = 0;
        foreach (var move in _journal.GetBatch(batchId).Reverse())
        {
            try
            {
                if (!File.Exists(move.Destination)) { errors.Add($"{Path.GetFileName(move.Destination)}: no longer at its new location."); continue; }
                if (File.Exists(move.Source)) { errors.Add($"{Path.GetFileName(move.Source)}: something else now exists at the original location."); continue; }
                EnsureParent(move.Source);
                File.Move(move.Destination, move.Source, overwrite: false);
                restored++;
                var folder = Path.GetDirectoryName(move.Destination);
                if (folder is not null && Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                    Directory.Delete(folder, recursive: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{Path.GetFileName(move.Source)}: {ex.Message}");
            }
        }
        _journal.MarkUndone(batchId);
        return new MoveOutcome(batchId, restored, errors);
    }

    public string ReadText(string path, int maxChars)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"'{path}' does not exist.");
        using var reader = new StreamReader(path, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[maxChars];
        var read = reader.ReadBlock(buffer, 0, maxChars);
        return new string(buffer, 0, read);
    }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string UniquePath(string desired, HashSet<string>? reserved)
    {
        bool Taken(string p) => File.Exists(p) || Directory.Exists(p) || (reserved is not null && reserved.Contains(p));
        if (!Taken(desired)) return desired;
        var dir = Path.GetDirectoryName(desired) ?? "";
        var name = Path.GetFileNameWithoutExtension(desired);
        var ext = Path.GetExtension(desired);
        for (var i = 2; i < 10000; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!Taken(candidate)) return candidate;
        }
        throw new IOException($"Could not find a free name for '{desired}'.");
    }

    private static void CopyDirectory(string source, string target, bool overwrite)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite);
        foreach (var dir in Directory.EnumerateDirectories(source))
            CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)), overwrite);
    }

    private static void EnsureParent(string path)
    {
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
    }

    private static StringComparison PathComparison
        => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static bool PathsEqual(string a, string b)
        => string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar), PathComparison);

    private static bool IsSameOrChild(string candidate, string parent)
    {
        var c = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar);
        var p = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar);
        return c.Equals(p, PathComparison) || c.StartsWith(p + Path.DirectorySeparatorChar, PathComparison);
    }

    public static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }
}
