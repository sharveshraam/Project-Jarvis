using Jarvis.Core.Models;

namespace Jarvis.Core.Modes;

/// <summary>The six operating modes. Each has real functional differences:
/// allowed tools, workspace folder and preferred app — not just a label.</summary>
public static class OperatingModes
{
    public static IReadOnlyList<ModeProfile> All { get; } =
    [
        new(OperatingMode.Study, "Study", "Academic work and learning.",
            ["open_folder", "open_file", "search_files", "open_app", "get_system_info", "list_tasks", "create_reminder"], null, null),
        new(OperatingMode.Development, "Development", "Software development.",
            ["open_app", "open_folder", "open_file", "search_files", "get_system_info", "list_tasks", "create_reminder", "switch_window"], null, null),
        new(OperatingMode.Research, "Research", "Information gathering and research.",
            ["search_files", "open_file", "open_app", "get_system_info", "list_tasks"], null, null),
        new(OperatingMode.Focus, "Focus", "Organising focused work sessions.",
            ["list_tasks", "create_reminder", "get_system_info", "search_files"], null, null),
        new(OperatingMode.System, "System", "Laptop and Windows awareness.",
            ["get_system_info", "open_app", "switch_window", "open_folder", "list_tasks"], null, null),
        new(OperatingMode.Personal, "Personal", "Everyday assistance.",
            ["open_app", "switch_window", "open_folder", "open_file", "search_files", "get_system_info", "list_tasks", "create_reminder", "read_clipboard_summary"], null, null),
    ];

    public static ModeProfile Get(OperatingMode mode) => All.First(m => m.Mode == mode);

    public static bool ToolAllowedInMode(OperatingMode mode, string toolName) =>
        Get(mode).AllowedTools.Contains(toolName, StringComparer.OrdinalIgnoreCase);

    public static OperatingMode Parse(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "study" => OperatingMode.Study,
        "development" or "dev" or "coding" => OperatingMode.Development,
        "research" => OperatingMode.Research,
        "focus" => OperatingMode.Focus,
        "system" => OperatingMode.System,
        "personal" => OperatingMode.Personal,
        _ => OperatingMode.Personal
    };
}
