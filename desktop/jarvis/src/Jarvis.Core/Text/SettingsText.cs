using System.Text;
using Jarvis.Core.Models;

namespace Jarvis.Core.Text;

/// <summary>Plain-text editing formats used by the settings screens (one entry per line).</summary>
public static class SettingsText
{
    public static List<string> Lines(string? text)
        => (text ?? "").Split('\n').Select(l => l.Trim().Trim('"')).Where(l => l.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public static string Join(IEnumerable<string> lines) => string.Join(Environment.NewLine, lines);

    /// <summary>"name = target" per line.</summary>
    public static Dictionary<string, string> ParseAliases(string? text)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in (text ?? "").Split('\n'))
        {
            var index = line.IndexOf('=');
            if (index <= 0) continue;
            var name = line.Substring(0, index).Trim();
            var target = line.Substring(index + 1).Trim().Trim('"');
            if (name.Length > 0 && target.Length > 0) map[name] = target;
        }
        return map;
    }

    public static string FormatAliases(IDictionary<string, string> aliases)
        => string.Join(Environment.NewLine, aliases.OrderBy(a => a.Key, StringComparer.OrdinalIgnoreCase).Select(a => $"{a.Key} = {a.Value}"));

    /// <summary>
    /// One approved command per line: <c>name | executable | arguments | working folder | trusted | dev-setup</c>.
    /// Only the first two fields are required; "trusted" and "dev-setup" are yes/no.
    /// </summary>
    public static List<ApprovedCommand> ParseCommands(string? text, out List<string> errors)
    {
        errors = new List<string>();
        var commands = new List<ApprovedCommand>();
        var lineNumber = 0;
        foreach (var raw in (text ?? "").Split('\n'))
        {
            lineNumber++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var parts = line.Split('|').Select(p => p.Trim()).ToArray();
            if (parts.Length < 2 || parts[0].Length == 0 || parts[1].Length == 0)
            {
                errors.Add($"Line {lineNumber}: expected at least 'name | executable'.");
                continue;
            }
            if (commands.Any(c => string.Equals(c.Name, parts[0], StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add($"Line {lineNumber}: the name '{parts[0]}' is used twice.");
                continue;
            }
            commands.Add(new ApprovedCommand
            {
                Name = parts[0],
                Executable = parts[1].Trim('"'),
                Arguments = parts.Length > 2 ? SplitArguments(parts[2]) : new List<string>(),
                WorkingDirectory = parts.Length > 3 ? parts[3].Trim('"') : "",
                Trusted = parts.Length > 4 && IsYes(parts[4]),
                RunOnDevSetup = parts.Length > 5 && IsYes(parts[5])
            });
        }
        return commands;
    }

    public static string FormatCommands(IEnumerable<ApprovedCommand> commands)
        => string.Join(Environment.NewLine, commands.Select(c =>
            $"{c.Name} | {c.Executable} | {JoinArguments(c.Arguments)} | {c.WorkingDirectory} | {(c.Trusted ? "yes" : "no")} | {(c.RunOnDevSetup ? "yes" : "no")}"));

    private static bool IsYes(string value)
        => value.Equals("yes", StringComparison.OrdinalIgnoreCase) || value.Equals("true", StringComparison.OrdinalIgnoreCase)
           || value.Equals("y", StringComparison.OrdinalIgnoreCase);

    /// <summary>Splits on spaces, keeping double-quoted segments together. No shell semantics are applied.</summary>
    public static List<string> SplitArguments(string text)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var hasToken = false;
        foreach (var c in text)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                hasToken = true;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (hasToken) result.Add(current.ToString());
                current.Clear();
                hasToken = false;
            }
            else
            {
                current.Append(c);
                hasToken = true;
            }
        }
        if (hasToken) result.Add(current.ToString());
        return result;
    }

    public static string JoinArguments(IEnumerable<string> arguments)
        => string.Join(" ", arguments.Select(a => a.Length == 0 || a.Any(char.IsWhiteSpace) ? "\"" + a + "\"" : a));
}
