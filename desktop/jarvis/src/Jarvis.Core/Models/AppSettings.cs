using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jarvis.Core.Models;

public enum PermissionLevel { Allow, Ask, Deny }

/// <summary>User-configured workspace for one operating mode.</summary>
public sealed class ModeConfig
{
    public List<string> Apps { get; set; } = new();
    public List<string> Folders { get; set; } = new();
    public List<string> Files { get; set; } = new();
    /// <summary>Development mode only: the project folder the IDE/terminal should open.</summary>
    public string ProjectPath { get; set; } = "";
}

/// <summary>
/// A command the user has explicitly approved for automation. Gemini can only reference it by
/// <see cref="Name"/>; it can never supply the executable or its arguments.
/// </summary>
public sealed class ApprovedCommand
{
    public string Name { get; set; } = "";
    public string Executable { get; set; } = "";
    public List<string> Arguments { get; set; } = new();
    public string WorkingDirectory { get; set; } = "";
    /// <summary>Trusted commands run without a prompt; untrusted ones ask every time.</summary>
    public bool Trusted { get; set; }
    /// <summary>Include in the "prepare my development environment" workflow.</summary>
    public bool RunOnDevSetup { get; set; }
}

public sealed class AppSettings
{
    // Name-keyed tables stay case-insensitive even after a JSON round trip (which would otherwise
    // replace them with ordinary, case-sensitive dictionaries).
    private Dictionary<string, ModeConfig> _modes = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, PermissionLevel> _toolPermissions = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _appAliases = new(StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, T> IgnoreCase<T>(Dictionary<string, T>? source)
    {
        var result = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        if (source is null) return result;
        foreach (var pair in source) result[pair.Key] = pair.Value;
        return result;
    }

    public string UserName { get; set; } = "";
    public string GeminiModel { get; set; } = "gemini-4-hydrogen";
    public string GeminiApiVersion { get; set; } = "v1beta";

    public string ToggleHotkey { get; set; } = "Ctrl+Alt+J";
    public string PushToTalkHotkey { get; set; } = "Ctrl+Alt+Space";
    public bool LaunchAtStartup { get; set; } = true;
    /// <summary>Keep the assistant panel above other windows while it is visible.</summary>
    public bool KeepOnTop { get; set; } = true;

    /// <summary>Speak replies to requests made by voice.</summary>
    public bool SpeakResponses { get; set; } = true;
    /// <summary>Also speak replies to typed requests.</summary>
    public bool SpeakTypedReplies { get; set; }
    public string VoiceName { get; set; } = "";
    public int SpeechRate { get; set; }

    public JarvisMode CurrentMode { get; set; } = JarvisMode.Personal;
    public Dictionary<string, ModeConfig> Modes { get => _modes; set => _modes = IgnoreCase(value); }

    /// <summary>Folders covered by universal search.</summary>
    public List<string> IndexedFolders { get; set; } = new();
    /// <summary>Extra folders (outside the user profile) that file tools may touch.</summary>
    public List<string> AllowedFolders { get; set; } = new();

    /// <summary>Master switch for "trusted routine actions": reversible actions run without a prompt.</summary>
    public bool TrustRoutineActions { get; set; } = true;
    /// <summary>Per-tool overrides. Consequential actions always ask regardless of this table.</summary>
    public Dictionary<string, PermissionLevel> ToolPermissions { get => _toolPermissions; set => _toolPermissions = IgnoreCase(value); }

    public List<string> DistractingApps { get; set; } = new();
    public List<ApprovedCommand> ApprovedCommands { get; set; } = new();
    /// <summary>Friendly app name → executable path or URI.</summary>
    public Dictionary<string, string> AppAliases { get => _appAliases; set => _appAliases = IgnoreCase(value); }

    /// <summary>"HH:mm" local time, or empty to disable.</summary>
    public string MorningBriefingTime { get; set; } = "";
    public string EndOfDaySummaryTime { get; set; } = "";

    public bool TasksPaused { get; set; }

    public ModeConfig GetMode(JarvisMode mode)
    {
        var key = mode.ToString();
        if (!Modes.TryGetValue(key, out var config))
        {
            config = new ModeConfig();
            Modes[key] = config;
        }
        return config;
    }

    public AppSettings Clone() => JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, JsonDefaults.Options), JsonDefaults.Options)!;
}

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions { WriteIndented = false, PropertyNameCaseInsensitive = true };
        o.Converters.Add(new JsonStringEnumConverter());
        return o;
    }
}
