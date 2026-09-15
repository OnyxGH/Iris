using System.Text.RegularExpressions;
using Iris.WebAccess.Http;
using ReverseMarkdown;

namespace Iris.WebAccess.Fetch;

internal sealed class ExtractOptions
{
    public bool ForceClone { get; init; }

    /// <summary>"readable" (default) converts pages to Markdown; "raw" returns text responses unchanged.</summary>
    public string Mode { get; init; } = "readable";

    public TimeSpan? Timeout { get; init; }

    /// <summary>Tool names for guidance in error messages.</summary>
    public string? WebSearchTool { get; init; }

    public string? FetchContentTool { get; init; }
}

/// <summary>Fetches URLs: GitHub repositories are cloned, PDFs converted to Markdown, HTML reduced to its readable article.</summary>
internal static partial class ContentExtractor
{
    private const int ConcurrentLimit = 3;
    private const int MinUsefulContent = 500;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private static readonly SemaphoreSlim FetchLimit = new(ConcurrentLimit);

    private static readonly Converter Markdown = new(new Config
    {
        GithubFlavored = true,
        RemoveComments = true,
        SmartHrefHandling = true,
        UnknownTags = Config.UnknownTagsOption.Bypass,
    });

    [GeneratedRegex(@"<body[^>]*>([\s\S]*?)</body>", RegexOptions.IgnoreCase)]
    private static partial Regex BodyPattern();

    [GeneratedRegex(@"<script[\s\S]*?</script>", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptPattern();

    [GeneratedRegex(@"<style[\s\S]*?</style>", RegexOptions.IgnoreCase)]
    private static partial Regex StylePattern();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"<script", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptOpen();

    [GeneratedRegex(@"^#{1,2}\s+(.+)", RegexOptions.Multiline)]
    private static partial Regex HeadingPattern();

    [GeneratedRegex(@"!\[([^\]]*)\]\(data:[^)]*\)")]
    private static partial Regex MarkdownDataImage();

    [GeneratedRegex(@"data:[a-zA-Z0-9.+/-]+;base64,[A-Za-z0-9+/=]{256,}")]
    private static partial Regex InlineDataUri();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ExtraBlankLines();

    public static async Task<List<ExtractedContent>> FetchAllAsync(IReadOnlyList<string> urls, ExtractOptions options, CancellationToken cancellationToken)
    {
        var tasks = urls.Select(async url =>
        {
            await FetchLimit.WaitAsync(cancellationToken);
            try
            {
                return await ExtractAsync(url, options, cancellationToken);
            }
            finally
            {
                FetchLimit.Release();
            }
        });
        var results = await Task.WhenAll(tasks);
        if (options.Mode == "raw") return [.. results];
        // Inline base64 data would otherwise flow into tool results as opaque text.
        return [.. results.Select(r => r.Content.Length == 0 ? r : new ExtractedContent { Url = r.Url, Title = r.Title, Content = SanitizeDataUris(r.Content), Error = r.Error, MimeType = r.MimeType, Status = r.Status })];
    }

    public static async Task<ExtractedContent> ExtractAsync(string url, ExtractOptions options, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return ExtractedContent.Failed(url, "Aborted");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return ExtractedContent.Failed(url, Uri.TryCreate(url, UriKind.Absolute, out _) ? "Only HTTP and HTTPS URLs can be fetched remotely" : $"Invalid URL: {url}");
        }

        RemoteFetchPolicy policy;
        try
        {
            policy = new RemoteFetchPolicy(SsrfSettings.Load(), DomainPolicy.Load());
            await SafeHttp.ValidateRemoteUrlAsync(uri, policy, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return ExtractedContent.Failed(url, "Aborted");
        }
        catch (Exception ex)
        {
            return ExtractedContent.Failed(url, ex.Message);
        }

        if (options.Mode != "raw")
        {
            try
            {
                if (await GitHubExtractor.ExtractAsync(url, options.ForceClone, cancellationToken) is { } github) return github;
            }
            catch (OperationCanceledException)
            {
                return ExtractedContent.Failed(url, "Aborted");
            }
            catch (ConfigParseException ex)
            {
                return ExtractedContent.Failed(url, ex.Message);
            }
            catch (Exception)
            {
                // Fall back to fetching the page.
            }
        }

        var result = await ExtractViaHttpAsync(url, uri, policy, options, cancellationToken);
        if (result.Status is 404 or 410 && result.Error is not null)
        {
            return new ExtractedContent { Url = url, Title = result.Title, Error = NotFoundGuidance(result, options), Status = result.Status };
        }
        return result;
    }

    private static string NotFoundGuidance(ExtractedContent result, ExtractOptions options)
    {
        var lines = new List<string> { result.Error ?? $"HTTP {result.Status}", "", $"The origin server says this page does not exist (HTTP {result.Status}), so extraction providers cannot retrieve it." };
        if (options.WebSearchTool is { } search && options.FetchContentTool is { } fetch)
        {
            lines.Add($"The page may have moved or been renamed. Use {search} to find the current URL, then retry {fetch} with it.");
        }
        else if (options.WebSearchTool is { } searchOnly)
        {
            lines.Add($"The page may have moved or been renamed. Use {searchOnly} to find the current URL.");
        }
        else
        {
            lines.Add("The page may have moved or been renamed. Find the current URL, then retry the fetch with it.");
        }
        return string.Join("\n", lines);
    }

    private static async Task<ExtractedContent> ExtractViaHttpAsync(string url, Uri uri, RemoteFetchPolicy policy, ExtractOptions options, CancellationToken cancellationToken)
    {
        var timeout = options.Timeout ?? LoadTimeout();
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var token = timeoutSource.Token;
        try
        {
            using var response = await SafeHttp.SendRemoteAsync(uri, target =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, target);
                request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0 Safari/537.36");
                request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
                request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
                request.Headers.TryAddWithoutValidation("Cache-Control", "no-cache");
                return request;
            }, policy, token);

            var status = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode && options.Mode != "raw")
            {
                return new ExtractedContent { Url = url, Error = $"HTTP {status}: {response.ReasonPhrase}", Status = status };
            }

            var contentType = response.Content.Headers.ContentType?.ToString() ?? "";
            var mimeType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "";
            var isPdf = PdfExtractor.IsPdf(url, contentType);
            var pdfConfig = isPdf ? PdfConfig.Load() : null;
            if (pdfConfig is { Enabled: false }) return new ExtractedContent { Url = url, Error = "PDF extraction is disabled by pdf.enabled", MimeType = mimeType, Status = status };

            var maxBytes = (long)((pdfConfig?.MaxSizeMB ?? 5) * 1024 * 1024);
            if (response.Content.Headers.ContentLength is { } length && length > maxBytes)
            {
                return ExtractedContent.Failed(url, pdfConfig is not null ? PdfSizeError(pdfConfig) : $"Response too large ({Math.Round(length / 1024.0 / 1024)}MB)");
            }
            Func<Exception> tooLarge = () => new InvalidOperationException(pdfConfig is not null ? PdfSizeError(pdfConfig) : $"Response too large ({Math.Round(maxBytes / 1024.0 / 1024)}MB)");

            if (options.Mode == "raw")
            {
                if (!IsTextContentType(mimeType)) return new ExtractedContent { Url = url, Error = $"Unsupported content type in raw mode: {(mimeType.Length > 0 ? mimeType : "missing")}", MimeType = mimeType, Status = status };
                var raw = SafeHttp.DecodeText(await SafeHttp.ReadBodyAsync(response, maxBytes, tooLarge, token), response.Content.Headers);
                return new ExtractedContent { Url = url, Title = TextTitle(raw, url), Content = raw, MimeType = mimeType, Status = status };
            }

            if (isPdf && pdfConfig is not null)
            {
                try
                {
                    var data = await SafeHttp.ReadBodyAsync(response, maxBytes, tooLarge, token);
                    var pdf = await PdfExtractor.ExtractToMarkdownAsync(data, url, pdfConfig, token);
                    return new ExtractedContent { Url = url, Title = pdf.Title, Content = $"PDF extracted and saved to: {pdf.OutputPath}\n\nPages: {pdf.Pages}\nCharacters: {pdf.Chars}" };
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return ExtractedContent.Failed(url, ex.Message.StartsWith("PDF exceeds", StringComparison.Ordinal) ? ex.Message : $"PDF extraction failed: {ex.Message}");
                }
            }

            if (mimeType.StartsWith("image/", StringComparison.Ordinal) || mimeType.StartsWith("audio/", StringComparison.Ordinal) || mimeType.StartsWith("video/", StringComparison.Ordinal)
                || mimeType is "application/octet-stream" or "application/zip")
            {
                return ExtractedContent.Failed(url, $"Unsupported content type: {mimeType}");
            }

            var text = SafeHttp.DecodeText(await SafeHttp.ReadBodyAsync(response, maxBytes, tooLarge, token), response.Content.Headers);
            var isHtml = mimeType is "text/html" or "application/xhtml+xml";
            if (!isHtml) return new ExtractedContent { Url = url, Title = TextTitle(text, url), Content = text };

            var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? url;
            return await ExtractHtmlAsync(url, finalUrl, text, token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ExtractedContent.Failed(url, "Aborted");
        }
        catch (OperationCanceledException)
        {
            return ExtractedContent.Failed(url, "The operation was aborted.");
        }
        catch (Exception ex)
        {
            return ExtractedContent.Failed(url, ex is HttpRequestException { InnerException: { } inner } ? $"{ex.Message} ({inner.Message})" : ex.Message);
        }
    }

    internal static async Task<ExtractedContent> ExtractHtmlAsync(string url, string finalUrl, string html, CancellationToken cancellationToken)
    {
        var (title, articleHtml) = await Task.Run(() =>
        {
            var reader = new SmartReader.Reader(finalUrl, html);
            var article = reader.GetArticle();
            return (article.Title?.Trim() ?? "", article.IsReadable ? article.Content : null);
        }, cancellationToken);

        if (articleHtml is null)
        {
            return new ExtractedContent
            {
                Url = url,
                Title = title,
                Error = IsLikelyJsRendered(html) ? "Page appears to be JavaScript-rendered (content loads dynamically)" : "Could not extract readable content from HTML structure",
            };
        }

        var markdown = ExtraBlankLines().Replace(Markdown.Convert(articleHtml), "\n\n").Trim();
        if (markdown.Length < MinUsefulContent)
        {
            return new ExtractedContent
            {
                Url = url,
                Title = title,
                Content = markdown,
                Error = IsLikelyJsRendered(html) ? "Page appears to be JavaScript-rendered (content loads dynamically)" : "Extracted content appears incomplete",
            };
        }
        return new ExtractedContent { Url = url, Title = title, Content = markdown };
    }

    private static TimeSpan LoadTimeout()
    {
        var fetch = WebConfig.GetObject("fetch");
        return WebConfig.GetInt(fetch, "timeoutMs") is { } ms && ms > 0 ? TimeSpan.FromMilliseconds(ms) : DefaultTimeout;
    }

    private static string PdfSizeError(PdfConfig config) => $"PDF exceeds configured pdf.maxSizeMB limit ({config.MaxSizeMB} MB)";

    private static bool IsTextContentType(string mimeType) =>
        mimeType.StartsWith("text/", StringComparison.Ordinal)
        || mimeType is "application/json" or "application/ld+json" or "application/xml" or "application/xhtml+xml" or "application/javascript" or "application/x-javascript"
        || mimeType.EndsWith("+json", StringComparison.Ordinal) || mimeType.EndsWith("+xml", StringComparison.Ordinal);

    private static bool IsLikelyJsRendered(string html)
    {
        if (BodyPattern().Match(html) is not { Success: true } body) return false;
        var text = TagPattern().Replace(StylePattern().Replace(ScriptPattern().Replace(body.Groups[1].Value, ""), ""), "");
        var textLength = Regex.Replace(text, @"\s+", " ").Trim().Length;
        return textLength < 500 && ScriptOpen().Matches(html).Count > 3;
    }

    private static string TextTitle(string text, string url)
    {
        if (HeadingPattern().Match(text) is { Success: true } heading && heading.Groups[1].Value.Replace("*", "").Trim() is { Length: > 0 } cleaned) return cleaned;
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && Path.GetFileName(uri.AbsolutePath) is { Length: > 0 } name ? name : url;
    }

    private static string SanitizeDataUris(string content)
    {
        var withoutImages = MarkdownDataImage().Replace(content, m => $"![{m.Groups[1].Value}](data URI omitted)");
        return InlineDataUri().Replace(withoutImages, "[data URI omitted]");
    }
}
