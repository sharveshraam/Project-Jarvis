using System.Text;
using System.Text.Json.Nodes;
using Jarvis.Core.Abstractions;
using Jarvis.Core.Tools;

namespace Jarvis.Agent.Tools;

/// <summary>Application control and browser hand-off.</summary>
public sealed class AppTools : ToolModule
{
    private readonly IAppController _apps;
    private readonly IShellService _shell;

    public AppTools(IAppController apps, IShellService shell)
    {
        _apps = apps;
        _shell = shell;
    }

    public static bool TryNormalizeUrl(string? text, out string url)
    {
        url = "";
        if (string.IsNullOrWhiteSpace(text)) return false;
        var candidate = text.Trim();
        if (!candidate.Contains("://", StringComparison.Ordinal)) candidate = "https://" + candidate;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
        if (string.IsNullOrWhiteSpace(uri.Host) || !uri.Host.Contains('.')) return false;
        url = uri.AbsoluteUri;
        return true;
    }

    public override IEnumerable<ITool> Build()
    {
        yield return Sync(
            Def("launch_app", "Open an installed application by name (e.g. 'VS Code', 'Chrome', 'Spotify', 'Windows Terminal').", ToolCategory.Apps, ToolRisk.Routine)
                .Str("name", "Application name.", required: true),
            (args, context) => From(_apps.Launch(args.Required("name"))),
            (args, context) => ToolAssessment.Of(ToolRisk.Routine, "Open " + args.Str("name")),
            args => _apps.IsRunning(args.Str("name") ?? ""));

        yield return Tool(
            Def("close_app", "Ask a running application to close. Closing is graceful: the app can still prompt to save work; nothing is force-killed.", ToolCategory.Apps, ToolRisk.Routine)
                .Str("name", "Application name.", required: true),
            async (args, context, ct) => From(await _apps.CloseAsync(args.Required("name"), ct).ConfigureAwait(false)),
            (args, context) => ToolAssessment.Of(ToolRisk.Routine, "Close " + args.Str("name")),
            (args, ct) => Task.FromResult(!_apps.IsRunning(args.Str("name") ?? "")));

        yield return Sync(
            Def("switch_to_app", "Bring a running application's window to the front.", ToolCategory.Apps, ToolRisk.Routine)
                .Str("name", "Application name or window title.", required: true).Retryable(),
            (args, context) => From(_apps.SwitchTo(args.Required("name"))),
            (args, context) => ToolAssessment.Of(ToolRisk.Routine, "Switch to " + args.Str("name")));

        yield return Sync(
            Def("list_running_apps", "List applications that currently have a window open.", ToolCategory.Apps, ToolRisk.Safe).Retryable(),
            (args, context) =>
            {
                var running = _apps.ListRunning();
                if (running.Count == 0) return ToolResult.Ok("No applications with windows are open.");
                var sb = new StringBuilder();
                sb.Append(running.Count).Append(" open applications:");
                foreach (var app in running.Take(40))
                    sb.Append("\n• ").Append(app.ProcessName).Append(string.IsNullOrWhiteSpace(app.WindowTitle) ? "" : " — " + app.WindowTitle);
                return ToolResult.Ok(sb.ToString(), true, new JsonObject
                {
                    ["apps"] = ToJsonArray(running.Take(40).Select(a => new JsonObject { ["name"] = a.ProcessName, ["title"] = a.WindowTitle }))
                });
            });

        yield return Sync(
            Def("open_url", "Open a web address in the user's default browser.", ToolCategory.Apps, ToolRisk.Routine)
                .Str("url", "http or https address.", required: true),
            (args, context) =>
            {
                if (!TryNormalizeUrl(args.Str("url"), out var url)) return ToolResult.Fail("That is not a valid web address.");
                return From(_shell.OpenUrl(url));
            },
            (args, context) =>
            {
                if (!TryNormalizeUrl(args.Str("url"), out var url)) throw new ToolArgumentException("Only http and https addresses can be opened.");
                return ToolAssessment.Of(ToolRisk.Routine, "Open " + url + " in the browser");
            });

        yield return Sync(
            Def("search_in_browser", "Open the default browser on a web search for the given query, for the user to read.", ToolCategory.Apps, ToolRisk.Routine)
                .Str("query", "Search terms.", required: true),
            (args, context) => From(_shell.OpenUrl("https://www.google.com/search?q=" + Uri.EscapeDataString(args.Required("query")))),
            (args, context) => ToolAssessment.Of(ToolRisk.Routine, "Search the web for '" + args.Str("query") + "' in the browser"));
    }
}
