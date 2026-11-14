using System.Text.Json;
using Jarvis.Core.Models;
using System.Text.Json.Serialization;

namespace Jarvis.Core.Config;

/// <summary>User settings. Persisted as JSON under %LocalAppData%\JARVIS\settings.json.
/// The Gemini API key is NEVER stored here — see ISecretVault (DPAPI).</summary>
public sealed class JarvisSettings
{
    public string GeminiModel { get; set; } = "gemini-2.5-flash";
    public string? GeminiApiKeyHint { get; set; } // last 4 chars only, for UI display
    public bool HasApiKey { get; set; }
    public bool StartWithWindows { get; set; } = true;
    public string PushToTalkShortcut { get; set; } = "Ctrl+Alt+J";
    public string ToggleWindowShortcut { get; set; } = "Ctrl+Alt+Space";
    public OperatingMode ActiveMode { get; set; } = OperatingMode.Personal;
    public bool SpeakResponses { get; set; } = true;
    public string SpeechCulture { get; set; } = "en-GB";
    public bool VoiceHistoryEnabled { get; set; } = false;
    public List<string> TrustedActions { get; set; } = new()
    {
        "open_app", "switch_window", "open_folder", "open_file", "get_system_info",
        "search_files", "list_tasks", "create_reminder", "read_clipboard_summary"
    };
    public List<string> IndexedFolders { get; set; } = new();
    public Dictionary<string, string> ModeWorkspaces { get; set; } = new();
    public Dictionary<string, string> ModeApps { get; set; } = new();
    public int MaxConversationTurnsSentToGemini { get; set; } = 12;
    public int RequestTimeoutSeconds { get; set; } = 60;
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public static JarvisSettings Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new JarvisSettings();
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<JarvisSettings>(json, Options) ?? new JarvisSettings();
        }
        catch { return new JarvisSettings(); } // corrupt settings must not brick startup
    }

    public static void Save(string path, JarvisSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Options));
        File.Move(tmp, path, overwrite: true); // crash-safe replace
    }
}
