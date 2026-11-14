using System.IO.Compression;
using System.Text;
using System.Xml;
using UglyToad.PdfPig;

namespace Jarvis.Infrastructure.Search;

/// <summary>Extracts plain text from the formats covered by universal search. Everything stays on this machine.</summary>
public static class TextExtractor
{
    public static readonly IReadOnlySet<string> CodeExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".py", ".js", ".ts", ".tsx", ".jsx", ".java", ".kt", ".kts", ".c", ".cpp", ".cc", ".h", ".hpp", ".go", ".rs",
        ".rb", ".php", ".swift", ".html", ".htm", ".css", ".scss", ".sql", ".sh", ".ps1", ".bat", ".cmd", ".gradle",
        ".xaml", ".csproj", ".sln", ".vue", ".dart", ".lua", ".r", ".m"
    };

    private static readonly HashSet<string> PlainExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".rst", ".log", ".csv", ".tsv", ".json", ".xml", ".yml", ".yaml", ".toml", ".ini",
        ".cfg", ".conf", ".tex", ".properties", ".env.example"
    };

    public static bool Supports(string extension)
        => PlainExtensions.Contains(extension) || CodeExtensions.Contains(extension)
           || extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase)
           || extension.Equals(".docx", StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns the document's text, or null when it cannot be read. Never throws for bad input files.</summary>
    public static string? Extract(string path, int maxChars)
    {
        try
        {
            var ext = Path.GetExtension(path);
            if (ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase)) return ExtractPdf(path, maxChars);
            if (ext.Equals(".docx", StringComparison.OrdinalIgnoreCase)) return ExtractDocx(path, maxChars);
            if (PlainExtensions.Contains(ext) || CodeExtensions.Contains(ext)) return ExtractPlain(path, maxChars);
            return null;
        }
        catch (Exception)
        {
            // Corrupt, encrypted or locked documents are simply indexed by name only.
            return null;
        }
    }

    private static string? ExtractPlain(string path, int maxChars)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[Math.Min(maxChars, 1 << 20)];
        var sb = new StringBuilder();
        int read;
        while (sb.Length < maxChars && (read = reader.Read(buffer, 0, Math.Min(buffer.Length, maxChars - sb.Length))) > 0)
            sb.Append(buffer, 0, read);
        var text = sb.ToString();
        // Binary files masquerading as text contain NULs; skip them.
        return text.IndexOf('\0') >= 0 ? null : text;
    }

    private static string ExtractPdf(string path, int maxChars)
    {
        var sb = new StringBuilder();
        using var document = PdfDocument.Open(path);
        foreach (var page in document.GetPages())
        {
            sb.AppendLine(page.Text);
            if (sb.Length >= maxChars) break;
        }
        return sb.Length > maxChars ? sb.ToString(0, maxChars) : sb.ToString();
    }

    private static string? ExtractDocx(string path, int maxChars)
    {
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry("word/document.xml");
        if (entry is null) return null;

        var sb = new StringBuilder();
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var inText = false;
        while (reader.Read() && sb.Length < maxChars)
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                    if (reader.LocalName == "t") inText = !reader.IsEmptyElement;
                    else if (reader.LocalName == "tab") sb.Append('\t');
                    else if (reader.LocalName == "br") sb.Append('\n');
                    break;
                case XmlNodeType.Text:
                case XmlNodeType.SignificantWhitespace:
                case XmlNodeType.Whitespace:
                    if (inText) sb.Append(reader.Value);
                    break;
                case XmlNodeType.EndElement:
                    if (reader.LocalName == "t") inText = false;
                    else if (reader.LocalName == "p") sb.Append('\n');
                    break;
            }
        }
        return sb.ToString();
    }
}
