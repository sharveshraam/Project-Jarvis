using System.Text;
using Jarvis.Core.Abstractions;
using Jarvis.Core.Models;
using Jarvis.Core.Modes;
using Jarvis.Core.Security;
using Microsoft.Extensions.Logging;

namespace Jarvis.Agent;

/// <summary>Wakes the scheduler when reminders or briefing times change.</summary>
public sealed class PlannerSignal
{
    public event Action? Changed;
    public void Raise() => Changed?.Invoke();
}

/// <summary>
/// Operating modes. A mode changes the tools Gemini may use, the behaviour instructions it gets and the
/// workspace (apps, folders, files, project) that "open workspace" brings up.
/// </summary>
public sealed class ModeService
{
    private readonly ISettingsStore _settings;
    private readonly IAppController _apps;
    private readonly IShellService _shell;
    private readonly PathPolicy _paths;
    private readonly ILogger<ModeService> _log;

    public ModeService(ISettingsStore settings, IAppController apps, IShellService shell, PathPolicy paths, ILogger<ModeService> log)
    {
        _settings = settings;
        _apps = apps;
        _shell = shell;
        _paths = paths;
        _log = log;
    }

    public JarvisMode Current => _settings.Current.CurrentMode;

    public event Action<JarvisMode>? Changed;

    public void Switch(JarvisMode mode)
    {
        if (_settings.Current.CurrentMode != mode)
        {
            var updated = _settings.Current.Clone();
            updated.CurrentMode = mode;
            _settings.Save(updated);
            _log.LogInformation("Mode switched to {Mode}.", mode);
        }
        Changed?.Invoke(mode);
    }

    /// <summary>What "open workspace" would do for a mode, without doing it.</summary>
    public IReadOnlyList<string> PreviewWorkspace(JarvisMode mode)
    {
        var config = _settings.Current.GetMode(mode);
        var lines = new List<string>();
        if (mode == JarvisMode.Development && !string.IsNullOrWhiteSpace(config.ProjectPath))
            lines.Add("Open project in the IDE: " + config.ProjectPath);
        lines.AddRange(config.Apps.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => "Launch " + a));
        lines.AddRange(config.Folders.Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => "Open folder " + f));
        lines.AddRange(config.Files.Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => "Open file " + f));
        return lines;
    }

    /// <summary>Opens the configured workspace and reports what actually opened and what did not.</summary>
    public (int Opened, int Failed, string Report) OpenWorkspace(JarvisMode mode)
    {
        var config = _settings.Current.GetMode(mode);
        var done = new List<string>();
        var failed = new List<string>();

        void Track(string label, ActionOutcome outcome)
        {
            if (outcome.Success) done.Add(label);
            else failed.Add($"{label} ({outcome.Message})");
        }

        if (mode == JarvisMode.Development && !string.IsNullOrWhiteSpace(config.ProjectPath))
        {
            if (TryPath(config.ProjectPath, out var project, out var reason)) Track("project " + project, _shell.OpenInIde(project));
            else failed.Add($"project {config.ProjectPath} ({reason})");
        }
        foreach (var app in config.Apps.Where(a => !string.IsNullOrWhiteSpace(a)))
        {
            if (_apps.IsRunning(app)) done.Add(app + " (already running)");
            else Track(app, _apps.Launch(app));
        }
        foreach (var entry in config.Folders.Concat(config.Files).Where(f => !string.IsNullOrWhiteSpace(f)))
        {
            if (TryPath(entry, out var full, out var reason)) Track(full, _shell.OpenPath(full));
            else failed.Add($"{entry} ({reason})");
        }

        var name = ModeCatalog.Get(mode).DisplayName;
        if (done.Count == 0 && failed.Count == 0)
            return (0, 0, $"No workspace is configured for {name} mode yet. Add apps and folders under Settings › Modes.");

        var sb = new StringBuilder();
        if (done.Count > 0) sb.Append("Opened: ").Append(string.Join(", ", done)).Append('.');
        if (failed.Count > 0) sb.Append(sb.Length > 0 ? " " : "").Append("Could not open: ").Append(string.Join("; ", failed)).Append('.');
        return (done.Count, failed.Count, sb.ToString());
    }

    private bool TryPath(string input, out string full, out string reason)
    {
        if (!_paths.TryResolve(input, out full, out reason)) return false;
        if (!_paths.IsAllowed(full, out reason)) return false;
        if (!File.Exists(full) && !Directory.Exists(full)) { reason = "it does not exist"; return false; }
        return true;
    }

    /// <summary>One or two lines describing the mode's configured workspace, for the system prompt.</summary>
    public string Describe(JarvisMode mode)
    {
        var config = _settings.Current.GetMode(mode);
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(config.ProjectPath)) parts.Add("project folder: " + config.ProjectPath);
        if (config.Apps.Count > 0) parts.Add("apps: " + string.Join(", ", config.Apps));
        if (config.Folders.Count > 0) parts.Add("folders: " + string.Join(", ", config.Folders));
        if (config.Files.Count > 0) parts.Add("files: " + string.Join(", ", config.Files));
        return parts.Count == 0
            ? "No workspace is configured for this mode."
            : "Configured workspace for this mode — " + string.Join("; ", parts) + ".";
    }
}
