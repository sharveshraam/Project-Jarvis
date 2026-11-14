using System.Text.RegularExpressions;

namespace Jarvis.Core.Security;

/// <summary>Keeps credentials out of ordinary memory and out of logs.</summary>
public static class SecretDetector
{
    private static readonly Regex[] TokenPatterns =
    {
        new(@"AIza[0-9A-Za-z_\-]{30,}", RegexOptions.Compiled),
        new(@"\bsk-[A-Za-z0-9_\-]{20,}", RegexOptions.Compiled),
        new(@"\bgh[pousr]_[A-Za-z0-9]{30,}", RegexOptions.Compiled),
        new(@"-----BEGIN [A-Z ]*PRIVATE KEY-----", RegexOptions.Compiled),
        new(@"\beyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{5,}", RegexOptions.Compiled),
        new(@"\b(?=[A-Za-z0-9_\-]*[0-9])(?=[A-Za-z0-9_\-]*[A-Za-z])[A-Za-z0-9_\-]{40,}\b", RegexOptions.Compiled)
    };

    private static readonly Regex LabelledSecret = new(
        @"\b(password|passcode|passwd|pwd|api[ _\-]?key|secret([ _\-]?key)?|access[ _\-]?token|private[ _\-]?key|cvv|pin(\s+code)?)\b\s*(is|was|=|:)\s*\S+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex KeyValueSecret = new(
        @"(?i)\b(key|api_key|apikey|token|password|secret|x-goog-api-key)\b(\s*[=:]\s*)(""?)([^\s""&,;]{6,})\3",
        RegexOptions.Compiled);

    private static readonly Regex BearerToken = new(@"(?i)\bBearer\s+[A-Za-z0-9._\-]+", RegexOptions.Compiled);

    public static bool LooksSensitive(string text, out string reason)
    {
        if (LabelledSecret.IsMatch(text)) { reason = "it appears to contain a password, PIN or key"; return true; }
        foreach (var rx in TokenPatterns)
        {
            if (rx.IsMatch(text)) { reason = "it appears to contain an access token or API key"; return true; }
        }
        reason = "";
        return false;
    }

    /// <summary>Masks anything credential-like. Applied to every log line.</summary>
    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var result = text;
        foreach (var rx in TokenPatterns) result = rx.Replace(result, "[REDACTED]");
        result = BearerToken.Replace(result, "Bearer [REDACTED]");
        result = KeyValueSecret.Replace(result, m => $"{m.Groups[1].Value}{m.Groups[2].Value}[REDACTED]");
        return result;
    }
}
