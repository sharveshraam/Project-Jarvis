using System.Text.RegularExpressions;

namespace Jarvis.Core.Text;

/// <summary>Small keyword-overlap scorer used to pick relevant memories without sending them anywhere.</summary>
public static class Relevance
{
    private static readonly Regex Word = new(@"[\p{L}\p{N}_]{3,}", RegexOptions.Compiled);
    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "that", "this", "with", "you", "your", "are", "was", "what", "when", "where", "which",
        "have", "has", "from", "about", "can", "could", "would", "should", "please", "will", "want", "need", "tell",
        "show", "give", "how", "why", "who", "does", "did", "not", "but", "any", "all", "our", "mine", "its", "into"
    };

    public static HashSet<string> Tokens(string text)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Word.Matches(text ?? ""))
        {
            if (!Stop.Contains(m.Value)) set.Add(m.Value.ToLowerInvariant());
        }
        return set;
    }

    public static int Score(HashSet<string> queryTokens, string candidate)
    {
        if (queryTokens.Count == 0) return 0;
        var score = 0;
        foreach (var token in Tokens(candidate))
        {
            if (queryTokens.Contains(token)) score += 2;
            else if (token.Length >= 5 && queryTokens.Any(q => q.Length >= 5 && (q.StartsWith(token, StringComparison.Ordinal) || token.StartsWith(q, StringComparison.Ordinal)))) score += 1;
        }
        return score;
    }

    public static string Truncate(string text, int max)
        => text.Length <= max ? text : text.Substring(0, Math.Max(0, max - 1)).TrimEnd() + "…";
}
