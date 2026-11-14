using System.ComponentModel;
using System.Diagnostics;
using Jarvis.Core.Abstractions;

namespace Jarvis.Windows;

/// <summary>
/// Finds, launches, focuses and gracefully closes applications. Launch targets come only from the user's
/// aliases, a built-in table of well-known apps, or Start Menu shortcuts — never from a raw path or command
/// line supplied by the model, and never with arguments.
/// </summary>
public sealed class AppController : IAppController
{
    private sealed record KnownApp(string[] Names, string Target, string[] ProcessNames);

    private static readonly KnownApp[] Known =
    {
        new(new[] { "vs code", "vscode", "visual studio code", "code" }, "vscode", new[] { "Code" }),
        new(new[] { "chrome", "google chrome" }, "chrome", new[] { "chrome" }),
        new(new[] { "edge", "microsoft edge" }, "msedge", new[] { "msedge" }),
        new(new[] { "firefox", "mozilla firefox" }, "firefox", new[] { "firefox" }),
        new(new[] { "brave", "brave browser" }, "brave", new[] { "brave" }),
        new(new[] { "terminal", "windows terminal" }, "wt", new[] { "WindowsTerminal" }),
        new(new[] { "notepad" }, "notepad", new[] { "notepad", "Notepad" }),
        new(new[] { "file explorer", "explorer", "files" }, "explorer", new[] { "explorer" }),
        new(new[] { "calculator", "calc" }, "calc", new[] { "CalculatorApp", "Calculator" }),
        new(new[] { "paint" }, "mspaint", new[] { "mspaint" }),
        new(new[] { "task manager" }, "taskmgr", new[] { "Taskmgr" }),
        new(new[] { "word", "microsoft word" }, "winword", new[] { "WINWORD" }),
        new(new[] { "excel", "microsoft excel" }, "excel", new[] { "EXCEL" }),
        new(new[] { "powerpoint", "microsoft powerpoint" }, "powerpnt", new[] { "POWERPNT" }),
        new(new[] { "outlook" }, "outlook", new[] { "OUTLOOK", "olk" }),
        new(new[] { "onenote" }, "onenote", new[] { "ONENOTE" }),
        new(new[] { "spotify" }, "spotify:", new[] { "Spotify" }),
        new(new[] { "teams", "microsoft teams" }, "msteams:", new[] { "ms-teams", "Teams" }),
        new(new[] { "settings", "windows settings" }, "ms-settings:", new[] { "SystemSettings" }),
        new(new[] { "store", "microsoft store" }, "ms-windows-store:", new[] { "WinStore.App" }),
        new(new[] { "photos" }, "ms-photos:", new[] { "Photos", "Microsoft.Photos" }),
        new(new[] { "clock", "alarms" }, "ms-clock:", new[] { "Time" }),
        new(new[] { "snipping tool" }, "ms-screenclip:", new[] { "SnippingTool" })
    };

    private static readonly HashSet<string> ProtectedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "dwm", "winlogon", "csrss", "lsass", "services", "smss", "system", "svchost", "wininit",
        "fontdrvhost", "sihost", "ctfmon", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost"
    };

    private readonly ISettingsStore _settings;

    public AppController(ISettingsStore settings) => _settings = settings;

    private static string Normalize(string value)
    {
        var chars = value.Trim().ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray();
        var text = new string(chars);
        return text.EndsWith("exe", StringComparison.Ordinal) && value.Trim().EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? text.Substring(0, text.Length - 3)
            : text;
    }

    private static KnownApp? FindKnown(string name)
    {
        var key = name.Trim().ToLowerInvariant();
        return Known.FirstOrDefault(k => k.Names.Contains(key));
    }

    private string? FindAlias(string name)
    {
        foreach (var kv in _settings.Current.AppAliases)
        {
            if (string.Equals(kv.Key.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(kv.Value))
                return Environment.ExpandEnvironmentVariables(kv.Value.Trim());
        }
        return null;
    }

    /// <summary>Best Start Menu shortcut for a name: exact match, then prefix, then substring.</summary>
    public static string? FindStartMenuShortcut(string name, IEnumerable<string> roots)
    {
        var wanted = Normalize(name);
        if (wanted.Length < 2) return null;
        string? best = null;
        var bestScore = 0;
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> shortcuts;
            try
            {
                shortcuts = Directory.EnumerateFiles(root, "*.lnk", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var shortcut in shortcuts)
            {
                var title = Normalize(Path.GetFileNameWithoutExtension(shortcut));
                if (title.Contains("uninstall", StringComparison.Ordinal)) continue;
                var score = title == wanted ? 3 : title.StartsWith(wanted, StringComparison.Ordinal) ? 2 : (wanted.Length >= 4 && title.Contains(wanted, StringComparison.Ordinal)) ? 1 : 0;
                if (score > bestScore || (score == bestScore && score > 0 && best is not null && shortcut.Length < best.Length))
                {
                    best = shortcut;
                    bestScore = score;
                }
            }
        }
        return best;
    }

    private static IEnumerable<string> StartMenuRoots()
    {
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs");
    }

    private string? ResolveTarget(string name)
    {
        var alias = FindAlias(name);
        if (alias is not null) return alias;

        var known = FindKnown(name);
        if (known is not null)
        {
            if (known.Target == "vscode") return IdeLocator.FindVsCode() ?? FindStartMenuShortcut("Visual Studio Code", StartMenuRoots());
            return known.Target;
        }
        return FindStartMenuShortcut(name, StartMenuRoots());
    }

    public ActionOutcome Launch(string nameOrPath)
    {
        var name = nameOrPath.Trim().Trim('"');
        if (name.Length == 0) return new ActionOutcome(false, "No application name was given.");
        if (name.Contains('\\') || name.Contains('/'))
            return new ActionOutcome(false, "Applications are opened by name, not by path. Add an alias for it under Settings › Modes › App aliases.");

        var target = ResolveTarget(name);
        if (target is null)
            return new ActionOutcome(false, $"I could not find an application called '{name}'. If it is installed, add an alias for it under Settings › Modes › App aliases.");

        Process? started;
        try
        {
            started = Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            return new ActionOutcome(false, $"'{name}' could not be opened: {ex.Message}");
        }

        // Verify: either the process we started is alive, or a matching process is now visible.
        for (var i = 0; i < 16; i++)
        {
            Thread.Sleep(250);
            try
            {
                if (started is not null && !started.HasExited && i >= 1)
                {
                    started.Dispose();
                    return new ActionOutcome(true, $"{name} is open.", true);
                }
            }
            catch (InvalidOperationException)
            {
                // No process associated (handed off to an existing instance).
            }
            if (IsRunning(name)) return new ActionOutcome(true, $"{name} is open.", true);
        }
        started?.Dispose();
        return new ActionOutcome(true, $"I asked Windows to open {name}, but could not confirm that it started.", false);
    }

    private List<Process> FindProcesses(string name)
    {
        var matches = new List<Process>();
        var known = FindKnown(name);
        var alias = FindAlias(name);
        var aliasProcess = alias is not null && alias.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? Path.GetFileNameWithoutExtension(alias) : null;
        var wanted = Normalize(name);
        var own = Environment.ProcessId;

        foreach (var process in Process.GetProcesses())
        {
            var keep = false;
            try
            {
                if (process.Id != own)
                {
                    var processName = process.ProcessName;
                    if (known is not null) keep = known.ProcessNames.Contains(processName, StringComparer.OrdinalIgnoreCase);
                    else if (aliasProcess is not null) keep = string.Equals(processName, aliasProcess, StringComparison.OrdinalIgnoreCase);
                    else
                    {
                        var normalized = Normalize(processName);
                        keep = normalized == wanted || (wanted.Length >= 4 && normalized.Contains(wanted, StringComparison.Ordinal));
                        if (!keep && wanted.Length >= 4 && process.MainWindowHandle != IntPtr.Zero)
                            keep = Normalize(process.MainWindowTitle).Contains(wanted, StringComparison.Ordinal);
                    }
                }
            }
            catch (Exception)
            {
                keep = false;
            }
            if (keep) matches.Add(process);
            else process.Dispose();
        }
        return matches;
    }

    private static void DisposeAll(IEnumerable<Process> processes)
    {
        foreach (var process in processes) process.Dispose();
    }

    public bool IsRunning(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var found = FindProcesses(name);
        var any = found.Count > 0;
        DisposeAll(found);
        return any;
    }

    public async Task<ActionOutcome> CloseAsync(string name, CancellationToken ct)
    {
        var found = FindProcesses(name);
        try
        {
            if (found.Count == 0) return new ActionOutcome(false, $"{name} is not running.");
            if (found.Any(p => ProtectedProcesses.Contains(p.ProcessName)))
                return new ActionOutcome(false, $"{name} is part of Windows and will not be closed.");

            var windows = found.Where(p => SafeHandle(p) != IntPtr.Zero).ToList();
            if (windows.Count == 0)
                return new ActionOutcome(false, $"{name} is running without a window. JARVIS only closes applications gracefully and never force-kills them.");

            foreach (var process in windows)
            {
                try { process.CloseMainWindow(); }
                catch (InvalidOperationException) { }
            }

            for (var i = 0; i < 24; i++)
            {
                await Task.Delay(250, ct).ConfigureAwait(false);
                if (windows.All(HasExited)) break;
            }

            if (!IsRunning(name)) return new ActionOutcome(true, $"{name} is closed.", true);
            return new ActionOutcome(false, $"I asked {name} to close, but it is still open — it may be waiting for you to save your work.");
        }
        finally
        {
            DisposeAll(found);
        }
    }

    private static IntPtr SafeHandle(Process process)
    {
        try { return process.MainWindowHandle; }
        catch (Exception) { return IntPtr.Zero; }
    }

    private static bool HasExited(Process process)
    {
        try { return process.HasExited; }
        catch (Exception) { return true; }
    }

    public ActionOutcome SwitchTo(string name)
    {
        var found = FindProcesses(name);
        try
        {
            var target = found.FirstOrDefault(p => SafeHandle(p) != IntPtr.Zero);
            if (target is null) return new ActionOutcome(false, found.Count == 0 ? $"{name} is not running." : $"{name} has no window to switch to.");
            var handle = target.MainWindowHandle;
            if (Native.IsIconic(handle)) Native.ShowWindow(handle, Native.SW_RESTORE);
            Native.SetForegroundWindow(handle);
            Thread.Sleep(150);
            return Native.GetForegroundWindow() == handle
                ? new ActionOutcome(true, $"Switched to {name}.", true)
                : new ActionOutcome(true, $"I asked Windows to bring {name} to the front, but it did not take focus (Windows sometimes blocks this). Its taskbar button should be flashing.", false);
        }
        finally
        {
            DisposeAll(found);
        }
    }

    public IReadOnlyList<RunningApp> ListRunning()
    {
        var apps = new List<RunningApp>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.MainWindowHandle != IntPtr.Zero && !string.IsNullOrWhiteSpace(process.MainWindowTitle))
                    apps.Add(new RunningApp(process.Id, process.ProcessName, process.MainWindowTitle));
            }
            catch (Exception)
            {
                // Process exited or is protected.
            }
            finally
            {
                process.Dispose();
            }
        }
        return apps.OrderBy(a => a.ProcessName, StringComparer.OrdinalIgnoreCase).ToList();
    }
}

internal static class IdeLocator
{
    /// <summary>Code.exe from the standard per-user or machine-wide install locations.</summary>
    public static string? FindVsCode()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Microsoft VS Code", "Code.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft VS Code", "Code.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft VS Code", "Code.exe")
        };
        return candidates.FirstOrDefault(File.Exists);
    }
}
