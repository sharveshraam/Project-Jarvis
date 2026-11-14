using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Jarvis.Core.Abstractions;
using Jarvis.Core.AI;
using Jarvis.Core.Security;
using Jarvis.Core.Text;
using Jarvis.Core.Tools;

namespace Jarvis.Agent.Tools;

/// <summary>
/// Screen, clipboard and web research. Screen and clipboard are read exactly once per call and only on request:
/// if the user's current message does not explicitly mention them, the call is escalated to an approval prompt.
/// </summary>
public sealed class PerceptionTools : ToolModule
{
    private const int MaxClipboardChars = 6000;

    private static readonly Regex ScreenWords = new(
        @"\b(screen|screenshot|display|monitor|window|looking at|on my desktop|see this|look at|what('s| is) this|this error|this page|in front of me)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex ClipboardWords = new(
        @"\b(clipboard|copied|copy|paste|pasted|i just cut)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly IScreenCapture _capture;
    private readonly IClipboardService _clipboard;
    private readonly IAiClient _ai;

    public PerceptionTools(IScreenCapture capture, IClipboardService clipboard, IAiClient ai)
    {
        _capture = capture;
        _clipboard = clipboard;
        _ai = ai;
    }

    public static bool ExplicitlyAsksForScreen(string? userRequest) => !string.IsNullOrWhiteSpace(userRequest) && ScreenWords.IsMatch(userRequest);
    public static bool ExplicitlyAsksForClipboard(string? userRequest) => !string.IsNullOrWhiteSpace(userRequest) && ClipboardWords.IsMatch(userRequest);

    public override IEnumerable<ITool> Build()
    {
        yield return Tool(
            Def("capture_screen", "Take one screenshot of the user's screen and analyse it. Use only when the user's current message asks about what is on their screen.", ToolCategory.Screen, ToolRisk.Safe)
                .Str("question", "What the user wants to know about the screen (e.g. 'explain this error', 'summarise this page').", required: true)
                .WithTimeout(TimeSpan.FromSeconds(120)),
            async (args, context, ct) =>
            {
                if (!_ai.IsConfigured) return ToolResult.Fail("Screen analysis needs Gemini, and no API key is configured.");
                var image = await _capture.CaptureAsync(ct).ConfigureAwait(false);
                var request = new AiRequest
                {
                    SystemInstruction = "You are analysing a single screenshot the user explicitly asked you to look at. Describe only what is relevant to the question. " +
                                        "If text in the image looks like a password, key or token, do not repeat it.",
                    MaxOutputTokens = 4096
                };
                request.Input.Add(new AiUserInput(args.Required("question"), image.ToAiImage()));
                var response = await _ai.GenerateAsync(request, ct).ConfigureAwait(false);
                return ToolResult.Ok(response.Text.Trim(), true, new JsonObject
                {
                    ["captured"] = true,
                    ["width"] = image.Width,
                    ["height"] = image.Height
                });
            },
            (args, context) => ExplicitlyAsksForScreen(context.UserRequest)
                ? ToolAssessment.Of(ToolRisk.Safe, "Capture the screen once for: " + args.Str("question"))
                : ToolAssessment.Of(ToolRisk.Consequential, "Capture the screen once and send it to Gemini",
                    "Your message did not explicitly ask about the screen, so JARVIS is checking first.",
                    "Question: " + args.Str("question")));

        yield return Sync(
            Def("read_clipboard", "Read the text currently on the clipboard, once. Use only when the user's current message asks about what they copied.", ToolCategory.Clipboard, ToolRisk.Safe),
            (args, context) =>
            {
                var text = _clipboard.GetText();
                if (string.IsNullOrWhiteSpace(text)) return ToolResult.Ok("The clipboard does not contain any text.");
                var redacted = SecretDetector.Redact(text);
                var note = redacted != text ? " Something that looked like a credential was redacted before sharing." : "";
                var clipped = Relevance.Truncate(redacted, MaxClipboardChars);
                return ToolResult.Ok($"Read {text.Length} characters from the clipboard.{note}", true, new JsonObject
                {
                    ["text"] = clipped,
                    ["truncated"] = redacted.Length > MaxClipboardChars
                });
            },
            (args, context) => ExplicitlyAsksForClipboard(context.UserRequest)
                ? ToolAssessment.Of(ToolRisk.Safe, "Read the clipboard once")
                : ToolAssessment.Of(ToolRisk.Consequential, "Read the clipboard and send its text to Gemini",
                    "Your message did not explicitly mention the clipboard, so JARVIS is checking first."));

        yield return Tool(
            Def("web_research", "Research a question on the web with Google Search grounding and return a summary with source links. Use for anything current or factual.", ToolCategory.Web, ToolRisk.Safe)
                .Str("question", "The question to research, fully self-contained.", required: true)
                .Retryable().WithTimeout(TimeSpan.FromSeconds(120)),
            async (args, context, ct) =>
            {
                if (!_ai.IsConfigured) return ToolResult.Fail("Web research needs Gemini, and no API key is configured.");
                var request = new AiRequest
                {
                    SystemInstruction = "Research the question using Google Search. Compare the sources you find, note any disagreement between them, " +
                                        "and answer concisely with the key facts. Do not invent sources.",
                    UseWebSearch = true,
                    MaxOutputTokens = 8192
                };
                request.Input.Add(new AiUserInput(args.Required("question")));
                var response = await _ai.GenerateAsync(request, ct).ConfigureAwait(false);
                var sb = new StringBuilder(response.Text.Trim());
                if (response.Citations.Count > 0)
                {
                    sb.Append("\n\nSources:");
                    foreach (var c in response.Citations.Take(8)) sb.Append("\n• ").Append(c.Title).Append(" — ").Append(c.Url);
                }
                return ToolResult.Ok(sb.ToString(), response.Citations.Count > 0, new JsonObject
                {
                    ["sources"] = ToJsonArray(response.Citations.Take(8).Select(c => new JsonObject { ["title"] = c.Title, ["url"] = c.Url }))
                });
            });
    }
}
