using System.Text;
using System.Text.Json.Nodes;
using Jarvis.Core.Abstractions;
using Jarvis.Core.Planning;
using Jarvis.Core.Tools;

namespace Jarvis.Agent.Tools;

/// <summary>Focus sessions: a goal, a planned length, and reversible distraction control.</summary>
public sealed class FocusTools : ToolModule
{
    private readonly IFocusStore _focus;
    private readonly IAppController _apps;
    private readonly ISettingsStore _settings;
    private readonly IPlannerStore _planner;
    private readonly IClock _clock;
    private readonly PlannerSignal _signal;

    public FocusTools(IFocusStore focus, IAppController apps, ISettingsStore settings, IPlannerStore planner, IClock clock, PlannerSignal signal)
    {
        _focus = focus;
        _apps = apps;
        _settings = settings;
        _planner = planner;
        _clock = clock;
        _signal = signal;
    }

    private List<string> RunningDistractions()
        => _settings.Current.DistractingApps.Where(a => !string.IsNullOrWhiteSpace(a) && _apps.IsRunning(a)).ToList();

    public override IEnumerable<ITool> Build()
    {
        yield return Tool(
            Def("start_focus_session", "Start a focus session with a goal and length. Can close the distracting apps the user configured (gracefully; they can be reopened when the session ends).", ToolCategory.Focus, ToolRisk.Routine)
                .Str("goal", "What the user wants to get done.", required: true)
                .Int("minutes", "Planned length in minutes (default 50).", false, 5, 480)
                .Bool("close_distractions", "Close the configured distracting apps that are currently open."),
            async (args, context, ct) =>
            {
                var active = _focus.Active();
                if (active is not null) return ToolResult.Fail($"A focus session is already running: {active.Goal}. End it first.");

                var minutes = Math.Clamp(args.Int("minutes", 50), 5, 480);
                var closed = new List<string>();
                var stillOpen = new List<string>();
                if (args.Bool("close_distractions"))
                {
                    foreach (var app in RunningDistractions())
                    {
                        var outcome = await _apps.CloseAsync(app, ct).ConfigureAwait(false);
                        if (outcome.Success && !_apps.IsRunning(app)) closed.Add(app);
                        else stillOpen.Add(app);
                    }
                }

                var session = _focus.Start(args.Required("goal"), minutes, closed);
                _signal.Raise();
                var end = _clock.LocalNow.AddMinutes(minutes);
                var sb = new StringBuilder();
                sb.Append("Focus session started: ").Append(session.Goal).Append(". ").Append(minutes).Append(" minutes, until ")
                    .Append(end.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture)).Append('.');
                if (closed.Count > 0) sb.Append(" Closed: ").Append(string.Join(", ", closed)).Append('.');
                if (stillOpen.Count > 0) sb.Append(" Still open (they may be waiting for you to save work): ").Append(string.Join(", ", stillOpen)).Append('.');
                var top = BriefingBuilder.Prioritised(_planner.ListTodos(false)).Take(3).Select(t => t.Title).ToList();
                if (top.Count > 0) sb.Append(" Top priorities: ").Append(string.Join("; ", top)).Append('.');
                return ToolResult.Ok(sb.ToString(), _focus.Active() is not null);
            },
            (args, context) =>
            {
                var details = new List<string>();
                if (args.Bool("close_distractions"))
                {
                    var running = RunningDistractions();
                    details.Add(running.Count == 0 ? "No configured distracting apps are open." : "Will ask these apps to close: " + string.Join(", ", running));
                }
                return new ToolAssessment(ToolRisk.Routine, $"Start a {args.Int("minutes", 50)}-minute focus session: {args.Str("goal")}", details);
            },
            (args, ct) => Task.FromResult(_focus.Active() is not null));

        yield return Sync(
            Def("end_focus_session", "End the running focus session and optionally reopen the apps that were closed for it.", ToolCategory.Focus, ToolRisk.Routine)
                .Bool("reopen_apps", "Reopen the apps that were closed when the session started."),
            (args, context) =>
            {
                var active = _focus.Active();
                if (active is null) return ToolResult.Ok("No focus session is running.");
                _focus.End(active.Id, _clock.UtcNow);
                var minutes = (int)Math.Round((_clock.UtcNow - active.StartUtc).TotalMinutes);
                var sb = new StringBuilder();
                sb.Append("Focus session ended after ").Append(minutes).Append(" minutes: ").Append(active.Goal).Append('.');
                if (args.Bool("reopen_apps") && active.ClosedApps.Count > 0)
                {
                    var reopened = new List<string>();
                    var failed = new List<string>();
                    foreach (var app in active.ClosedApps)
                    {
                        if (_apps.Launch(app).Success) reopened.Add(app);
                        else failed.Add(app);
                    }
                    if (reopened.Count > 0) sb.Append(" Reopened: ").Append(string.Join(", ", reopened)).Append('.');
                    if (failed.Count > 0) sb.Append(" Could not reopen: ").Append(string.Join(", ", failed)).Append('.');
                }
                else if (active.ClosedApps.Count > 0)
                {
                    sb.Append(" Apps closed for the session: ").Append(string.Join(", ", active.ClosedApps)).Append('.');
                }
                return ToolResult.Ok(sb.ToString(), _focus.Active() is null);
            },
            (args, context) => ToolAssessment.Of(ToolRisk.Routine, "End the focus session"),
            args => _focus.Active() is null);

        yield return Sync(
            Def("get_focus_status", "Report the running focus session (goal, time left) and today's focus time.", ToolCategory.Focus, ToolRisk.Safe).Retryable(),
            (args, context) =>
            {
                var now = _clock.UtcNow;
                var today = _clock.LocalNow.Date;
                var recent = _focus.Recent(50);
                var minutesToday = recent
                    .Where(s => TimeText.ToLocal(s.StartUtc).Date == today)
                    .Sum(s => ((s.EndUtc ?? now) - s.StartUtc).TotalMinutes);
                var active = _focus.Active();
                var data = new JsonObject { ["active"] = active is not null, ["minutes_today"] = (int)Math.Round(minutesToday) };
                if (active is null) return ToolResult.Ok($"No focus session is running. Focus time today: {(int)Math.Round(minutesToday)} minutes.", true, data);
                var left = active.PlannedMinutes - (now - active.StartUtc).TotalMinutes;
                var leftText = left > 0 ? $"{(int)Math.Ceiling(left)} minutes left" : $"{(int)Math.Round(-left)} minutes over the planned time";
                return ToolResult.Ok($"Focusing on: {active.Goal} — {leftText}. Focus time today: {(int)Math.Round(minutesToday)} minutes.", true, data);
            });
    }
}
