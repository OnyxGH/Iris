using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using Iris.WebAccess.Http;

namespace Iris.WebAccess.Search;

/// <summary>A self-hosted SearXNG instance (searxngBaseUrl / SEARXNG_BASE_URL), queried through the SSRF guard.</summary>
internal sealed partial class SearxngProvider : ISearchProvider
{
    [GeneratedRegex(@"^[!#$%&'*+.^_`|~0-9A-Za-z-]+$")]
    private static partial Regex HeaderName();

    public string Id => "searxng";

    public string Label => "SearXNG";

    public bool IsAvailable()
    {
        if (BaseUrl() is null) return false;
        SsrfSettings.Load();
        return true;
    }

    private static string? BaseUrl()
    {
        var configured = Environment.GetEnvironmentVariable("SEARXNG_BASE_URL") ?? WebConfig.GetString("searxngBaseUrl");
        if (string.IsNullOrWhiteSpace(configured) || !Uri.TryCreate(configured.Trim(), UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0) return null;
        return new UriBuilder(uri) { Query = "", Fragment = "" }.Uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    private static Dictionary<string, string> ConfiguredHeaders()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Accept"] = "application/json" };
        if (WebConfig.GetObject("searxngHeaders") is not { } configured) return headers;
        foreach (var (key, value) in configured)
        {
            if (value is not JsonValue text || !text.TryGetValue<string>(out var headerValue)) continue;
            var name = key.Trim();
            if (name.Length == 0 || !HeaderName().IsMatch(name) || headerValue.Any(c => c is '\r' or '\n' or '\0')) continue;
            headers[name] = headerValue;
        }
        return headers;
    }

    public async Task<SearchResponse?> SearchAsync(string query, SearchOptions options)
    {
        var baseUrl = BaseUrl() ?? throw new InvalidOperationException(
            $"SearXNG base URL is invalid or missing. Either:\n  1. Create {WebConfig.Path} with {{ \"searxngBaseUrl\": \"https://search.example.com\" }}\n  2. Set SEARXNG_BASE_URL to an HTTP(S) URL");
        var numResults = SearchText.ResultCount(options.NumResults);
        var filters = DomainFilters.From(options.DomainFilter);
        var searchQuery = filters.ApplyToQuery(query, "-site:");
        var url = $"{baseUrl}/search?q={Uri.EscapeDataString(searchQuery)}&format=json";
        if (options.RecencyFilter is "day" or "week" or "month" or "year") url += $"&time_range={options.RecencyFilter}";

        var headers = ConfiguredHeaders();
        var origin = new Uri(url).GetLeftPart(UriPartial.Authority);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(options.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await SafeHttp.SendRemoteAsync(new Uri(url), target =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, target);
            // Configured headers (often credentials) only go to the configured origin.
            foreach (var (name, value) in target.GetLeftPart(UriPartial.Authority) == origin ? headers : new() { ["Accept"] = "application/json" })
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }
            return request;
        }, new RemoteFetchPolicy(SsrfSettings.Load()), timeout.Token);
        var text = await response.Content.ReadAsStringAsync(timeout.Token);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"SearXNG search error {(int)response.StatusCode}: {SearchText.Truncate(text, 300)}");
        JsonNode? data;
        try
        {
            data = JsonNode.Parse(text);
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new InvalidOperationException($"SearXNG returned invalid JSON: {ex.Message}");
        }

        var results = new List<SearchResult>();
        foreach (var item in data?["results"] as JsonArray ?? [])
        {
            var resultUrl = item?["url"]?.GetValue<string>();
            if (string.IsNullOrEmpty(resultUrl) || !filters.Matches(resultUrl)) continue;
            var title = item?["title"]?.GetValue<string>();
            results.Add(new SearchResult(string.IsNullOrEmpty(title) ? resultUrl : title, resultUrl, item?["content"]?.GetValue<string>() ?? ""));
            if (results.Count >= numResults) break;
        }
        var answers = (data?["answers"] as JsonArray ?? [])
            .Select(a => a is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : "")
            .Where(a => a.Length > 0)
            .ToList();
        var resultAnswer = SearchText.AnswerFromResults(results);
        if (resultAnswer.Length > 0) answers.Add(resultAnswer);
        return new SearchResponse { Answer = string.Join("\n\n", answers), Results = results };
    }
}

/// <summary>DuckDuckGo's HTML endpoint. Needs no key; used only when selected explicitly.</summary>
internal sealed class DuckDuckGoProvider : ISearchProvider
{
    private const string SearchUrl = "https://html.duckduckgo.com/html/";

    public string Id => "duckduckgo";

    public string Label => "DuckDuckGo";

    public bool IsAvailable() => true;

    public async Task<SearchResponse?> SearchAsync(string query, SearchOptions options)
    {
        using var response = await SafeHttp.SendApiAsync(target =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, target);
            request.Headers.Accept.ParseAdd("text/html");
            request.Headers.UserAgent.ParseAdd(SafeHttp.UserAgent);
            return request;
        }, new Uri($"{SearchUrl}?q={Uri.EscapeDataString(query)}"), [], TimeSpan.FromSeconds(30), options.CancellationToken);
        var html = await response.Content.ReadAsStringAsync(options.CancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"DuckDuckGo search error {(int)response.StatusCode}: {SearchText.Truncate(html, 300)}");

        var document = await new HtmlParser().ParseDocumentAsync(html, options.CancellationToken);
        var filters = DomainFilters.From(options.DomainFilter);
        var limit = SearchText.ResultCount(options.NumResults);
        var results = new List<SearchResult>();
        var parseable = 0;
        foreach (var container in document.QuerySelectorAll(".result"))
        {
            if (container.ClassList.Contains("result--ad")) continue;
            var anchor = container.QuerySelector(".result__a");
            var title = anchor?.TextContent.Trim() ?? "";
            var href = anchor?.GetAttribute("href")?.Trim() ?? "";
            if (title.Length == 0 || href.Length == 0 || DecodeResultUrl(href) is not { } url) continue;
            parseable++;
            if (!filters.Matches(url)) continue;
            results.Add(new SearchResult(title, url, container.QuerySelector(".result__snippet")?.TextContent.Trim() ?? ""));
            if (results.Count >= limit) break;
        }
        if (parseable == 0) throw new InvalidOperationException("DuckDuckGo returned no parseable results (invalid response)");
        return new SearchResponse { Answer = SearchText.AnswerFromResults(results), Results = results };
    }

    private static string? DecodeResultUrl(string href)
    {
        if (!Uri.TryCreate(new Uri(SearchUrl), href, out var link)) return null;
        var destination = System.Web.HttpUtility.ParseQueryString(link.Query)["uddg"] ?? link.ToString();
        return Uri.TryCreate(destination, UriKind.Absolute, out var url) && url.Scheme is "http" or "https" ? url.ToString() : null;
    }
}
