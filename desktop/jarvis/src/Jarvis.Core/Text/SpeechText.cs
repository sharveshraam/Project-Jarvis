using System.Text.RegularExpressions;

namespace Jarvis.Core.Text;

/// <summary>Turns a written reply into something that sounds natural when spoken.</summary>
public static class SpeechText
{
    private static readonly Regex CodeBlock = new(@"```.*?```", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex Link = new(@"\[([^\]]+)\]\((https?://[^)]+)\)", RegexOptions.Compiled);
    private static readonly Regex Url = new(@"https?://\S+", RegexOptions.Compiled);
    private static readonly Regex WindowsPath = new(@"[A-Za-z]:\\[^\s""]+", RegexOptions.Compiled);
    private static readonly Regex Markup = new(@"[*_`#>|]+", RegexOptions.Compiled);
    private static readonly Regex Bullet = new(@"^\s*(?:[•\-–]|\d+\.)\s+", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex Ids = new(@"\s*\[#?[0-9a-f]{1,32}\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"[ \t]{2,}", RegexOptions.Compiled);

    public static string ForSpeaking(string text, int maxChars = 600)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var s = CodeBlock.Replace(text, " (code omitted) ");
        s = Link.Replace(s, "$1");
        s = Url.Replace(s, "a link");
        s = WindowsPath.Replace(s, m => System.IO.Path.GetFileName(m.Value.TrimEnd('\\', '.', ',', ';', ':')));
        s = Ids.Replace(s, "");
        s = Bullet.Replace(s, "");
        s = Markup.Replace(s, "");
        s = s.Replace("→", " to ").Replace("—", ", ").Replace("·", ", ");
        s = Regex.Replace(s, @"\s*\n+\s*", ". ");
        s = Regex.Replace(s, @"([.!?:;,])\s*\.", "$1");
        s = Spaces.Replace(s, " ").Trim();
        if (s.Length <= maxChars) return s;

        // Cut at a sentence boundary so speech does not stop mid-word.
        var cut = s.LastIndexOfAny(new[] { '.', '!', '?' }, maxChars - 1);
        if (cut < maxChars / 2) cut = maxChars - 1;
        return s.Substring(0, cut + 1).TrimEnd() + " The rest is on screen.";
    }
}
