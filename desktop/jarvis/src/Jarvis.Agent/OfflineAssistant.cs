using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Jarvis.Core.Abstractions;
using Jarvis.Core.Modes;
using Jarvis.Core.Planning;
using Jarvis.Core.Tools;

namespace Jarvis.Agent;

/// <summary>
/// Basic functionality without Gemini: a local command interpreter for the things that do not need a
/// language model (reminders, tasks, apps, files, search, system status, modes, workflows). Commands run
/// through the same <see cref="ToolExecutor"/> pipeline, so permissions and auditing are identical.
/// </summary>
public sealed class OfflineAssistant
{
    public const string CapabilitiesHint =
        "Without Gemini I can still: set and list reminders, manage tasks and your daily plan, open and close apps, " +
        "open folders and websites, search your indexed files, report system status, change volume and brightness, " +
        "switch modes, run built-in workflows and manage background tasks. Try \"remind me to call the bank tomorrow at 10\" or \"open VS Code\".";

    private const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static readonly string[] KnownFolders = { "downloads", "documents", "desktop", "pictures", "music", "videos" };

    private readonly ToolExecutor _executor;
    private readonly ModeService _modes;
    private readonly IClock _clock;

    public OfflineAssistant(ToolExecutor executor, ModeService modes, IClock clock)
    {
        _executor = executor;
        _modes = modes;
        _clock = clock;
    }

    /// <summary>What a local command maps to, or null when the text needs a language model.</summary>
    public sealed record Intent(string Tool, JsonObject Args);

    public async Task<string?> TryHandleAsync(string text, CancellationToken ct)
    {
        var trimmed = text.Trim();
        if (Regex.IsMatch(trimmed, @"^(help|what can you do( offline)?\??)$", Opt)) return CapabilitiesHint;

        var intent = Interpret(trimmed, _clock.LocalNow, out var problem);
        if (intent is null) return problem;

        var context = new ToolContext { Mode = _modes.Current, BypassModeFilter = true, UserRequest = trimmed };
        var result = await _executor.ExecuteAsync(intent.Tool, intent.Args, context, ct).ConfigureAwait(false);
        if (result.Success && !result.Verified) return result.Message + " (requested; I could not confirm the outcome.)";
        return result.Message;
    }

    private static JsonObject Args(params (string Key, JsonNode? Value)[] pairs)
    {
        var o = new JsonObject();
        foreach (var (key, value) in pairs) o[key] = value;
        return o;
    }

    private static string Clean(string value) => value.Trim().TrimEnd('.', '!', '?').Trim().Trim('"', '\'');

    /// <summary>Pure text → intent mapping (no side effects), so it can be unit-tested.</summary>
    public static Intent? Interpret(string input, DateTime nowLocal, out string? problem)
    {
        problem = null;
        var text = Regex.Replace(input.Trim(), @"^(hey\s+|ok\s+|okay\s+)?jarvis[,:]?\s+", "", Opt);
        text = Regex.Replace(text, @"^(please|can you|could you|would you)\s+", "", Opt).Trim();
        var lower = text.ToLowerInvariant().TrimEnd('.', '!', '?');
        Match m;

        // "Remember ..." is checked first: the sentence that follows can mention anything (RAM, tasks, reminders).
        if ((m = Regex.Match(text, @"^remember\s+(?:that\s+)?(.+)$", Opt)).Success)
            return new Intent("remember", Args(("content", Clean(m.Groups[1].Value)), ("category", "other"), ("source", "user_provided")));

        // Reminders
        if (Regex.IsMatch(lower, @"^(remind me|set (a|an) (reminder|alarm)|add (a )?reminder|create (a )?reminder)\b", Opt))
        {
            var parsed = ScheduleParser.Parse(text, nowLocal);
            if (parsed is null || string.IsNullOrWhiteSpace(parsed.Text))
            {
                problem = "I could not work out what to remind you about and when. Try \"remind me to submit the report tomorrow at 5 pm\".";
                return null;
            }
            return new Intent("add_reminder", Args(
                ("text", parsed.Text),
                ("due_local", parsed.DueLocal.ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture)),
                ("recurrence", parsed.Recurrence.ToString().ToLowerInvariant())));
        }
        if (Regex.IsMatch(lower, @"\b(list|show|what are|what's|my|any|upcoming)\b.*\breminders?\b", Opt)) return new Intent("list_reminders", Args());
        if ((m = Regex.Match(lower, @"^(delete|remove|cancel) reminder #?(\d+)$", Opt)).Success)
            return new Intent("delete_reminder", Args(("id", long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture))));

        // Tasks
        if ((m = Regex.Match(text, @"^(?:add|create|new)\s+(?:an?\s+)?(?:urgent\s+|high[- ]priority\s+)?(?:task|todo|to-do)\s*(?:to\s+|:|-)?\s*(.+)$", Opt)).Success)
        {
            var title = Clean(m.Groups[1].Value);
            if (title.Length > 0) title = char.ToUpperInvariant(title[0]) + title.Substring(1);
            var args = Args(("title", title));
            if (Regex.IsMatch(lower, @"\burgent\b", Opt)) args["priority"] = "urgent";
            else if (Regex.IsMatch(lower, @"\bhigh[- ]priority\b", Opt)) args["priority"] = "high";
            var schedule = ScheduleParser.Parse(title, nowLocal);
            if (schedule is not null && !string.IsNullOrWhiteSpace(schedule.Text))
            {
                args["title"] = schedule.Text;
                args["deadline_local"] = schedule.DueLocal.ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture);
            }
            return new Intent("add_task", args);
        }
        if ((m = Regex.Match(lower, @"^(?:complete|finish|done with|mark|tick off)\s+(?:task\s+)?#?(\d+)(?:\s+(?:as\s+)?(?:done|complete|completed))?$", Opt)).Success)
            return new Intent("complete_task", Args(("id", long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))));
        if ((m = Regex.Match(lower, @"^(delete|remove) task #?(\d+)$", Opt)).Success)
            return new Intent("delete_task", Args(("id", long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture))));
        if (Regex.IsMatch(lower, @"\b(background|running) tasks?\b", Opt)) return new Intent("list_background_tasks", Args());
        if (Regex.IsMatch(lower, @"\b(list|show|what are|what's|my|pending|open)\b.*\b(tasks?|todos?|to-dos?|to do list)\b", Opt)) return new Intent("list_tasks", Args());

        // Plans
        if (Regex.IsMatch(lower, @"\b(plan my day|daily plan|plan for today|today's plan|my agenda|my schedule|morning briefing|what should i (do|work on))\b", Opt))
            return new Intent("get_daily_plan", Args());
        if (Regex.IsMatch(lower, @"\b(end[- ]of[- ]day|day summary|summari[sz]e my day|what did i (do|finish|complete))\b", Opt))
            return new Intent("get_day_summary", Args());

        // Workflows
        if (Regex.IsMatch(lower, @"\b(prepare|set ?up|start)\b.*\b(dev|development|coding)\b.*\b(environment|workspace|setup)\b", Opt))
            return new Intent("run_workflow", Args(("name", "prepare_dev_environment")));
        if (Regex.IsMatch(lower, @"\bstart\b.*\bstudy session\b", Opt)) return new Intent("run_workflow", Args(("name", "start_study_session")));
        if (Regex.IsMatch(lower, @"\b(system|health) check-?up\b", Opt)) return new Intent("run_workflow", Args(("name", "system_checkup")));

        // Modes
        if ((m = Regex.Match(lower, @"^(?:switch|change|go|set)\s+(?:to\s+|into\s+)?(?:the\s+)?(.+?)\s+mode$", Opt)).Success
            || (m = Regex.Match(lower, @"^(?:start|begin|enter|activate)\s+(.+?)\s+mode$", Opt)).Success
            || (m = Regex.Match(lower, @"^(.+?)\s+mode$", Opt)).Success)
        {
            if (ModeCatalog.TryParse(m.Groups[1].Value, out var mode))
                return new Intent("switch_mode", Args(("mode", mode.ToString().ToLowerInvariant())));
        }
        if (Regex.IsMatch(lower, @"^open (my |the )?workspace$", Opt)) return new Intent("open_workspace", Args());

        // Focus
        if ((m = Regex.Match(lower, @"^(?:start|begin)\s+(?:a\s+)?focus(?: session)?(?:\s+(?:for|of)\s+(\d{1,3})\s*(?:min|minutes|m))?(?:\s+(?:on|for)\s+(.+))?$", Opt)).Success)
        {
            var args = Args(("goal", m.Groups[2].Success ? Clean(m.Groups[2].Value) : "Focused work"));
            if (m.Groups[1].Success) args["minutes"] = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            return new Intent("start_focus_session", args);
        }
        if (Regex.IsMatch(lower, @"^(end|stop|finish) (the |my )?focus( session)?$", Opt)) return new Intent("end_focus_session", Args());
        if (Regex.IsMatch(lower, @"\bfocus (status|time)\b", Opt)) return new Intent("get_focus_status", Args());

        // System
        if ((m = Regex.Match(lower, @"\bvolume\s+(?:to\s+|at\s+)?(\d{1,3})\b", Opt)).Success)
            return new Intent("set_volume", Args(("percent", Math.Clamp(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), 0, 100))));
        if (Regex.IsMatch(lower, @"^unmute\b", Opt)) return new Intent("set_mute", Args(("muted", false)));
        if (Regex.IsMatch(lower, @"^mute\b", Opt)) return new Intent("set_mute", Args(("muted", true)));
        if ((m = Regex.Match(lower, @"\bbrightness\s+(?:to\s+|at\s+)?(\d{1,3})\b", Opt)).Success)
            return new Intent("set_brightness", Args(("percent", Math.Clamp(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), 0, 100))));
        if (Regex.IsMatch(lower, @"^lock (the |my )?(computer|pc|screen|laptop)$", Opt)) return new Intent("lock_computer", Args());
        if (Regex.IsMatch(lower, @"\b(heaviest|top|what's using|what is using)\b.*\b(process|processes|memory|ram|cpu)\b", Opt))
            return new Intent("get_top_processes", Args());
        if (Regex.IsMatch(lower, @"\b(system (status|health|info)|cpu|ram|memory usage|battery|disk space|storage|how is my (pc|laptop|computer))\b", Opt))
            return new Intent("get_system_status", Args());
        if (Regex.IsMatch(lower, @"\b(what time is it|what's the time|what is the time|current time|what day is it|today's date|what's the date|what is the date)\b", Opt))
            return new Intent("get_current_time", Args());

        // Memory
        if (Regex.IsMatch(lower, @"\b(what do you (remember|know) about me|list (my )?memories|show (my )?memories)\b", Opt)) return new Intent("list_memories", Args());

        // Files and search
        if ((m = Regex.Match(lower, @"\bduplicates?(?:\s+files?)?\s+in\s+(?:my\s+|the\s+)?(.+?)(?:\s+folder)?$", Opt)).Success)
            return new Intent("find_duplicates", Args(("folder", Clean(m.Groups[1].Value))));
        if ((m = Regex.Match(lower, @"^(?:organi[sz]e|tidy|clean up|sort)\s+(?:my\s+|the\s+)?(.+?)(?:\s+folder)?$", Opt)).Success)
            return new Intent("organise_folder", Args(("folder", Clean(m.Groups[1].Value))));
        if (Regex.IsMatch(lower, @"^undo (the )?(last )?organi[sz](e|ation|ing)$", Opt)) return new Intent("undo_last_organise", Args());
        if (Regex.IsMatch(lower, @"^(rebuild|refresh|update) (the |my )?(search )?index$", Opt)) return new Intent("rebuild_search_index", Args());
        if ((m = Regex.Match(text, @"^(?:find|search|look)\s+(?:for\s+)?(?:my\s+)?(?:files?\s+|documents?\s+|notes?\s+)?(?:named\s+|called\s+|about\s+|containing\s+|on\s+|for\s+)?(.+)$", Opt)).Success)
            return new Intent("search_files", Args(("query", Clean(m.Groups[1].Value))));

        // Apps
        if ((m = Regex.Match(text, @"^(?:close|quit|exit)\s+(.+)$", Opt)).Success)
            return new Intent("close_app", Args(("name", Clean(m.Groups[1].Value))));
        if ((m = Regex.Match(text, @"^(?:switch to|go to|bring up|focus on)\s+(.+)$", Opt)).Success)
            return new Intent("switch_to_app", Args(("name", Clean(m.Groups[1].Value))));
        if ((m = Regex.Match(text, @"^(?:open|launch|start|run)\s+(?:my\s+|the\s+)?(.+)$", Opt)).Success)
        {
            var target = Clean(m.Groups[1].Value);
            var bare = Regex.Replace(target, @"\s+folder$", "", Opt);
            if (Regex.IsMatch(target, @"^(https?://|www\.)\S+$", Opt) || Regex.IsMatch(target, @"^[\w-]+(\.[\w-]+)*\.(com|org|net|io|dev|in|edu|gov|co|ai|app)(/\S*)?$", Opt))
                return new Intent("open_url", Args(("url", target)));
            if (KnownFolders.Contains(bare.ToLowerInvariant()) || target.Contains('\\') || Regex.IsMatch(target, @"^[a-zA-Z]:"))
                return new Intent("open_path", Args(("path", bare)));
            return new Intent("launch_app", Args(("name", target)));
        }

        return null;
    }
}
