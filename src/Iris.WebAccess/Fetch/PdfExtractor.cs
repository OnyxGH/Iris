using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Iris.WebAccess.Fetch;

internal sealed record PdfConfig(bool Enabled, double MaxSizeMB, int MaxPages)
{
    public const double DefaultMaxSizeMB = 20;
    public const double MaxMaxSizeMB = 50;
    public const int DefaultMaxPages = 100;

    public static PdfConfig Load()
    {
        var pdf = WebConfig.GetObject("pdf");
        var enabled = WebConfig.GetBool(pdf, "enabled") != false;
        var maxSize = pdf?["maxSizeMB"] is System.Text.Json.Nodes.JsonValue size && size.TryGetValue<double>(out var mb) && double.IsFinite(mb) && mb > 0
            ? Math.Min(mb, MaxMaxSizeMB)
            : DefaultMaxSizeMB;
        var maxPages = WebConfig.GetInt(pdf, "maxPages") is { } pages && pages > 0 ? pages : DefaultMaxPages;
        return new PdfConfig(enabled, maxSize, maxPages);
    }
}

internal sealed record PdfExtractResult(string Title, string Content, int Pages, int Chars, string OutputPath);

/// <summary>Extracts PDF text page by page and saves it as Markdown in a temporary directory.</summary>
internal static partial class PdfExtractor
{
    private static string OutputDir => Path.Combine(Path.GetTempPath(), "iris-web-pdf");

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"/(?:pdf|abs)/(\d+\.\d+)")]
    private static partial Regex ArxivId();

    [GeneratedRegex(@"[_-]+")]
    private static partial Regex Separators();

    public static bool IsPdf(string url, string? contentType)
    {
        if (contentType?.Contains("application/pdf", StringComparison.OrdinalIgnoreCase) == true) return true;
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.AbsolutePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<PdfExtractResult> ExtractToMarkdownAsync(byte[] data, string url, PdfConfig config, CancellationToken cancellationToken)
    {
        var urlTitle = TitleFromUrl(url);
        var (title, author, pageCount, body) = await Task.Run(() =>
        {
            using var document = PdfDocument.Open(data);
            var pages = new StringBuilder();
            var toExtract = Math.Min(document.NumberOfPages, config.MaxPages);
            var first = true;
            for (var number = 1; number <= toExtract; number++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var text = Whitespace().Replace(ContentOrderTextExtractor.GetText(document.GetPage(number)), " ").Trim();
                if (text.Length == 0) continue;
                if (!first) pages.Append($"\n\n<!-- Page {number} -->\n\n");
                pages.Append(text);
                first = false;
            }
            var metaTitle = document.Information.Title?.Trim();
            return (string.IsNullOrEmpty(metaTitle) ? urlTitle : metaTitle, document.Information.Author, document.NumberOfPages, pages.ToString());
        }, cancellationToken);

        var truncated = pageCount > config.MaxPages;
        var lines = new List<string> { $"# {title}", "", $"> Source: {url}", $"> Pages: {pageCount}{(truncated ? $" (extracted first {config.MaxPages})" : "")}" };
        if (!string.IsNullOrWhiteSpace(author)) lines.Add($"> Author: {author}");
        lines.AddRange(["", "---", ""]);
        if (body.Length > 0) lines.Add(body);
        if (truncated) lines.AddRange(["", "---", "", $"*[Truncated: Only first {config.MaxPages} of {pageCount} pages extracted]*"]);
        var content = string.Join("\n", lines);

        Directory.CreateDirectory(OutputDir);
        var outputPath = Path.Combine(OutputDir, $"{SanitizeFilename(title)}.md");
        await File.WriteAllTextAsync(outputPath, content, cancellationToken);
        return new PdfExtractResult(title, content, pageCount, content.Length, outputPath);
    }

    private static string TitleFromUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "document";
        var filename = Path.GetFileName(uri.AbsolutePath);
        if (filename.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) filename = filename[..^4];
        if (uri.Host.Contains("arxiv.org", StringComparison.OrdinalIgnoreCase) && ArxivId().Match(uri.AbsolutePath) is { Success: true } match) filename = $"arxiv-{match.Groups[1].Value}";
        filename = Whitespace().Replace(Separators().Replace(Uri.UnescapeDataString(filename), " "), " ").Trim();
        return filename.Length > 0 ? filename : "document";
    }

    private static string SanitizeFilename(string name)
    {
        var builder = new StringBuilder();
        foreach (var c in name.ToLowerInvariant())
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-') builder.Append(c);
            else if (char.IsWhiteSpace(c)) builder.Append('-');
        }
        var sanitized = Regex.Replace(builder.ToString(), "-+", "-");
        if (sanitized.Length > 100) sanitized = sanitized[..100];
        sanitized = sanitized.Trim('-');
        return sanitized.Length > 0 ? sanitized : "document";
    }
}
