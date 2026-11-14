using Jarvis.Core.Models;

namespace Jarvis.Core.Security;

/// <summary>
/// Confines file tools to the user's own folders plus folders they configured.
/// Gemini never gets filesystem-wide access: system, program and JARVIS data folders are always off limits.
/// </summary>
public sealed class PathPolicy
{
    private readonly Func<AppSettings> _settings;
    private readonly string _userProfile;
    private readonly List<string> _protectedRoots;
    private readonly StringComparison _cmp;

    public PathPolicy(Func<AppSettings> settings, string userProfile, IEnumerable<string> protectedRoots)
    {
        _settings = settings;
        _userProfile = Normalize(userProfile);
        _protectedRoots = protectedRoots.Where(p => !string.IsNullOrWhiteSpace(p)).Select(Normalize).ToList();
        _cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    }

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["downloads"] = "Downloads", ["download"] = "Downloads", ["documents"] = "Documents", ["docs"] = "Documents",
        ["desktop"] = "Desktop", ["pictures"] = "Pictures", ["photos"] = "Pictures", ["music"] = "Music",
        ["videos"] = "Videos", ["home"] = ""
    };

    /// <summary>Expands "~", environment variables and friendly names ("Downloads") into a full path.</summary>
    public bool TryResolve(string? input, out string fullPath, out string error)
    {
        fullPath = "";
        error = "";
        if (string.IsNullOrWhiteSpace(input)) { error = "No path was given."; return false; }

        var text = input.Trim().Trim('"');
        var aliasKey = text.EndsWith(" folder", StringComparison.OrdinalIgnoreCase) ? text.Substring(0, text.Length - 7).Trim() : text;
        if (aliasKey.StartsWith("my ", StringComparison.OrdinalIgnoreCase)) aliasKey = aliasKey.Substring(3).Trim();
        if (Aliases.TryGetValue(aliasKey, out var sub))
        {
            fullPath = Normalize(Path.Combine(_userProfile, sub));
            return true;
        }

        if (text == "~") text = _userProfile;
        else if (text.StartsWith("~/", StringComparison.Ordinal) || text.StartsWith("~\\", StringComparison.Ordinal))
            text = Path.Combine(_userProfile, text.Substring(2));

        try
        {
            text = Environment.ExpandEnvironmentVariables(text);
            if (!Path.IsPathRooted(text)) { error = $"'{input}' is not a full path."; return false; }
            fullPath = Normalize(text);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = $"'{input}' is not a valid path.";
            return false;
        }
    }

    public bool IsAllowed(string fullPath, out string reason)
    {
        var path = Normalize(fullPath);
        foreach (var root in _protectedRoots)
        {
            if (IsUnder(path, root)) { reason = "That location is a protected system or application folder."; return false; }
        }

        if (IsUnder(path, Path.Combine(_userProfile, "AppData")))
        {
            reason = "Application data folders are off limits.";
            return false;
        }

        if (IsUnder(path, _userProfile)) { reason = ""; return true; }

        var s = _settings();
        var extra = s.AllowedFolders.Concat(s.IndexedFolders)
            .Concat(s.Modes.Values.SelectMany(m => m.Folders))
            .Concat(s.Modes.Values.Select(m => m.ProjectPath));
        foreach (var folder in extra)
        {
            if (!string.IsNullOrWhiteSpace(folder) && IsUnder(path, Normalize(folder))) { reason = ""; return true; }
        }

        reason = "That location is outside your user folders. Add it under Settings › Files & search to permit access.";
        return false;
    }

    /// <summary>Resolves and authorises in one step; throws a user-presentable error otherwise.</summary>
    public string Require(string? input)
    {
        if (!TryResolve(input, out var full, out var error)) throw new Tools.ToolArgumentException(error);
        if (!IsAllowed(full, out var reason)) throw new Tools.ToolArgumentException(reason);
        return full;
    }

    private bool IsUnder(string path, string root)
    {
        if (root.Length == 0) return false;
        if (path.Equals(root, _cmp)) return true;
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, _cmp);
    }

    private static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        return full.Length > 3 ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : full;
    }
}
