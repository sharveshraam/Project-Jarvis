using Jarvis.Core.Models;
using Jarvis.Core.Tools;

namespace Jarvis.Core.Modes;

/// <summary>
/// A mode is more than a greeting: it selects which tool categories Gemini may use, the behaviour
/// instructions it receives, and (with the user's ModeConfig) the workspace that gets opened.
/// </summary>
public sealed record ModeDefinition(
    JarvisMode Mode, string DisplayName, string Purpose, string Behaviour, IReadOnlyCollection<ToolCategory> Categories)
{
    public bool Allows(ToolCategory category) => category == ToolCategory.General || Categories.Contains(category);
}

public static class ModeCatalog
{
    public static readonly IReadOnlyList<ModeDefinition> All = new[]
    {
        new ModeDefinition(JarvisMode.Personal, "Personal",
            "Reminders, tasks, daily plans and everyday assistance.",
            "Act as a personal secretary. Keep track of the user's reminders, tasks and deadlines, prepare daily plans, summarise pending work and pick up previous conversations. Save durable personal context to memory.",
            new[] { ToolCategory.Planner, ToolCategory.Memory, ToolCategory.Apps, ToolCategory.Search, ToolCategory.Clipboard,
                    ToolCategory.Screen, ToolCategory.Web, ToolCategory.System, ToolCategory.Files, ToolCategory.Agent }),
        new ModeDefinition(JarvisMode.Study, "Study",
            "Notes, PDFs, study materials and study tasks.",
            "Act as a study partner. Search the user's study materials before answering from general knowledge, explain academic content step by step, cite which file an excerpt came from, and turn study goals into tasks with deadlines.",
            new[] { ToolCategory.Planner, ToolCategory.Memory, ToolCategory.Apps, ToolCategory.Files, ToolCategory.Search,
                    ToolCategory.Clipboard, ToolCategory.Screen, ToolCategory.Web }),
        new ModeDefinition(JarvisMode.Development, "Development",
            "IDE, project, terminal, Git and source code.",
            "Act as a senior engineering assistant. Work against the configured project folder: inspect it with the project and Git tools, search the source index, and help debug errors from the clipboard or screen. Only run commands the user has pre-approved by name.",
            new[] { ToolCategory.Development, ToolCategory.Apps, ToolCategory.Files, ToolCategory.Search, ToolCategory.Clipboard,
                    ToolCategory.Screen, ToolCategory.Memory, ToolCategory.Web, ToolCategory.Agent }),
        new ModeDefinition(JarvisMode.Research, "Research",
            "Web research, source comparison and summaries.",
            "Act as a research analyst. Use the web research tool for anything current or factual, compare sources, state disagreements between them, and always list source links with the summary. Offer to open the most relevant pages.",
            new[] { ToolCategory.Web, ToolCategory.Search, ToolCategory.Clipboard, ToolCategory.Screen, ToolCategory.Memory,
                    ToolCategory.Apps, ToolCategory.Files }),
        new ModeDefinition(JarvisMode.Focus, "Focus",
            "Work sessions, priorities and distraction control.",
            "Act as a focus coach. Keep replies short. Help plan a work session, show the top priorities, start and end focus sessions, and manage only the distractions the user configured. Closing distractions is reversible: offer to reopen them when the session ends.",
            new[] { ToolCategory.Focus, ToolCategory.Planner, ToolCategory.Apps, ToolCategory.Memory }),
        new ModeDefinition(JarvisMode.System, "System",
            "Laptop health, resource usage and Windows settings.",
            "Act as a systems technician. Check system health only when asked, identify resource-heavy applications, explain Windows errors in plain language, and open the relevant Windows settings page rather than describing where it is.",
            new[] { ToolCategory.System, ToolCategory.Apps, ToolCategory.Screen, ToolCategory.Clipboard, ToolCategory.Files,
                    ToolCategory.Agent })
    };

    public static ModeDefinition Get(JarvisMode mode) => All.First(m => m.Mode == mode);

    public static bool TryParse(string? text, out JarvisMode mode)
    {
        mode = JarvisMode.Personal;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim().ToLowerInvariant();
        if (t.EndsWith(" mode", StringComparison.Ordinal)) t = t.Substring(0, t.Length - 5).Trim();
        switch (t)
        {
            case "personal": case "secretary": case "default": mode = JarvisMode.Personal; return true;
            case "study": case "studying": mode = JarvisMode.Study; return true;
            case "development": case "dev": case "developer": case "coding": mode = JarvisMode.Development; return true;
            case "research": mode = JarvisMode.Research; return true;
            case "focus": case "deep work": mode = JarvisMode.Focus; return true;
            case "system": case "maintenance": mode = JarvisMode.System; return true;
            default: return false;
        }
    }
}
