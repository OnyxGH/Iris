using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Iris.WebAccess.Http;

namespace Iris.WebAccess.Search;

/// <summary>Brave Search API (braveApiKey / BRAVE_API_KEY).</summary>
internal sealed class BraveProvider : ISearchProvider
{
    private const string DefaultBaseUrl = "https://api.search.brave.com/res/v1";

    public string Id => "brave";

    public string Label => "Brave";

    public bool IsAvailable() => Credentials.HasSource("braveApiKey", "BRAVE_API_KEY");

    public async Task<SearchResponse?> SearchAsync(string query, SearchOptions options)
    {
        var apiUrl = $"{WebConfig.ResolveBaseUrl("braveBaseUrl", "BRAVE_BASE_URL", DefaultBaseUrl)}/web/search";
        var apiKey = await Credentials.ResolveAsync("Brave", "braveApiKey", "BRAVE_API_KEY", options.CancellationToken)
            ?? throw new InvalidOperationException(
                $"Brave Search API key not found. Either:\n  1. Create {WebConfig.Path} with {{ \"braveApiKey\": \"your-key\" }}\n  2. Set BRAVE_API_KEY environment variable\nGet a key at https://brave.com/search/api/");
        var numResults = SearchText.ResultCount(options.NumResults);
        var filters = DomainFilters.From(options.DomainFilter);
        var searchQuery = filters.ApplyToQuery(query, "NOT site:");
        var parameters = new List<string> { $"q={Uri.EscapeDataString(searchQuery)}", $"count={(options.DomainFilter is { Count: > 0 } ? 20 : numResults)}" };
        var freshness = options.RecencyFilter switch { "day" => "pd", "week" => "pw", "month" => "pm", "year" => "py", _ => null };
        if (freshness is not null) parameters.Add($"freshness={freshness}");

        try
        {
            using var response = await SafeHttp.SendApiAsync(target =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, target);
                request.Headers.Add("X-Subscription-Token", apiKey);
                request.Headers.Accept.ParseAdd("application/json");
                return request;
            }, new Uri($"{apiUrl}?{string.Join("&", parameters)}"), ["X-Subscription-Token"], TimeSpan.FromSeconds(30), options.CancellationToken);
            var text = await response.Content.ReadAsStringAsync(options.CancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Brave Search API error {(int)response.StatusCode}: {SearchText.Truncate(Credentials.Redact(text, apiKey), 300)}");
            }
            var results = new List<SearchResult>();
            foreach (var item in JsonNode.Parse(text)?["web"]?["results"] as JsonArray ?? [])
            {
                var url = item?["url"]?.GetValue<string>();
                if (string.IsNullOrEmpty(url) || !filters.Matches(url)) continue;
                var title = item?["title"]?.GetValue<string>();
                results.Add(new SearchResult(string.IsNullOrEmpty(title) ? url : title, url, item?["description"]?.GetValue<string>() ?? ""));
                if (results.Count >= numResults) break;
            }
            return new SearchResponse { Answer = SearchText.AnswerFromResults(results), Results = results };
        }
        catch (Exception ex) when (ex is not OperationCanceledException && Credentials.Redact(ex.Message, apiKey) != ex.Message)
        {
            throw new InvalidOperationException(Credentials.Redact(ex.Message, apiKey));
        }
    }
}

/// <summary>Tavily (tavilyApiKey / TAVILY_API_KEY). Returns raw page markdown inline when content is requested.</summary>
internal sealed partial class TavilyProvider : ISearchProvider
{
    private const string DefaultBaseUrl = "https://api.tavily.com";

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    public string Id => "tavily";

    public string Label => "Tavily";

    public bool IsAvailable() => Credentials.HasSource("tavilyApiKey", "TAVILY_API_KEY");

    public async Task<SearchResponse?> SearchAsync(string query, SearchOptions options)
    {
        var apiUrl = $"{WebConfig.ResolveBaseUrl("tavilyBaseUrl", "TAVILY_BASE_URL", DefaultBaseUrl)}/search";
        var apiKey = await Credentials.ResolveAsync("Tavily", "tavilyApiKey", "TAVILY_API_KEY", options.CancellationToken)
            ?? throw new InvalidOperationException(
                $"Tavily API key not found. Either:\n  1. Create {WebConfig.Path} with {{ \"tavilyApiKey\": \"your-key\" }}\n  2. Set TAVILY_API_KEY environment variable\nGet a key at https://app.tavily.com/");
        var numResults = SearchText.ResultCount(options.NumResults);
        var body = new JsonObject
        {
            ["query"] = query,
            ["search_depth"] = "basic",
            ["max_results"] = numResults,
            ["include_answer"] = "basic",
            ["include_raw_content"] = options.IncludeContent ? "markdown" : false,
        };
        if (options.RecencyFilter is { } recency) body["time_range"] = recency;
        var filters = DomainFilters.From(options.DomainFilter);
        if (filters.Allowed.Count > 0) body["include_domains"] = new JsonArray([.. filters.Allowed.Select(d => (JsonNode)d)]);
        if (filters.Blocked.Count > 0) body["exclude_domains"] = new JsonArray([.. filters.Blocked.Select(d => (JsonNode)d)]);

        try
        {
            using var response = await SafeHttp.SendApiAsync(target =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, target) { Content = SafeHttp.JsonBody(body) };
                request.Headers.Add("Authorization", $"Bearer {apiKey}");
                return request;
            }, new Uri(apiUrl), ["Authorization"], TimeSpan.FromSeconds(60), options.CancellationToken);
            var text = await response.Content.ReadAsStringAsync(options.CancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Tavily API error {(int)response.StatusCode}: {SearchText.Truncate(Credentials.Redact(text, apiKey), 300)}");
            }
            JsonNode? data;
            try
            {
                data = JsonNode.Parse(text);
            }
            catch (System.Text.Json.JsonException ex)
            {
                throw new InvalidOperationException($"Tavily API returned invalid JSON: {ex.Message}");
            }
            var items = data?["results"] as JsonArray ?? [];
            var results = new List<SearchResult>();
            var inline = new List<ExtractedContent>();
            foreach (var item in items)
            {
                var url = item?["url"]?.GetValue<string>();
                if (string.IsNullOrEmpty(url)) continue;
                var title = item?["title"]?.GetValue<string>();
                if (results.Count < numResults)
                {
                    var snippet = item?["content"] is JsonValue c && c.TryGetValue<string>(out var content) ? Whitespace().Replace(content, " ").Trim() : "";
                    results.Add(new SearchResult(string.IsNullOrEmpty(title) ? $"Source {results.Count + 1}" : title, url, snippet));
                }
                if (options.IncludeContent && item?["raw_content"] is JsonValue raw && raw.TryGetValue<string>(out var rawContent) && rawContent.Trim().Length > 0)
                {
                    inline.Add(new ExtractedContent { Url = url, Title = title ?? "", Content = rawContent });
                }
            }
            return new SearchResponse
            {
                Answer = data?["answer"] is JsonValue a && a.TryGetValue<string>(out var answer) ? answer : "",
                Results = results,
                InlineContent = inline.Count > 0 ? inline : null,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException && Credentials.Redact(ex.Message, apiKey) != ex.Message)
        {
            throw new InvalidOperationException(Credentials.Redact(ex.Message, apiKey));
        }
    }
}

/// <summary>Perplexity Sonar (perplexityApiKey / PERPLEXITY_API_KEY), rate limited to 10 requests a minute.</summary>
internal sealed partial class PerplexityProvider : ISearchProvider
{
    private const string ApiUrl = "https://api.perplexity.ai/chat/completions";
    private const int MaxRequests = 10;
    private const int MaxCitations = 20;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private static readonly Queue<DateTimeOffset> RequestTimes = new();

    [GeneratedRegex(@"\[(\d{1,3})\]")]
    private static partial Regex CitationMarker();

    [GeneratedRegex(@"^[a-zA-Z0-9][a-zA-Z0-9-_.]*\.[a-zA-Z]{2,}$")]
    private static partial Regex DomainPattern();

    public string Id => "perplexity";

    public string Label => "Perplexity";

    public bool IsAvailable() => Credentials.HasSource("perplexityApiKey", "PERPLEXITY_API_KEY");

    public async Task<SearchResponse?> SearchAsync(string query, SearchOptions options)
    {
        CheckRateLimit();
        var apiKey = await Credentials.ResolveAsync("Perplexity", "perplexityApiKey", "PERPLEXITY_API_KEY", options.CancellationToken)
            ?? throw new InvalidOperationException(
                $"Perplexity API key not found. Either:\n  1. Create {WebConfig.Path} with {{ \"perplexityApiKey\": \"your-key\" }}\n  2. Set PERPLEXITY_API_KEY environment variable\nGet a key at https://perplexity.ai/settings/api");
        var numResults = SearchText.ResultCount(options.NumResults);
        var body = new JsonObject
        {
            ["model"] = "sonar",
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = query }),
            ["max_tokens"] = 1024,
            ["return_related_questions"] = false,
        };
        if (options.RecencyFilter is { } recency) body["search_recency_filter"] = recency;
        var domains = (options.DomainFilter ?? []).Where(d => DomainPattern().IsMatch(d.StartsWith('-') ? d[1..] : d)).ToList();
        if (domains.Count > 0) body["search_domain_filter"] = new JsonArray([.. domains.Select(d => (JsonNode)d)]);

        try
        {
            using var response = await SafeHttp.SendApiAsync(target =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, target) { Content = SafeHttp.JsonBody(body) };
                request.Headers.Add("Authorization", $"Bearer {apiKey}");
                return request;
            }, new Uri(ApiUrl), ["Authorization"], TimeSpan.FromSeconds(60), options.CancellationToken);
            var text = await response.Content.ReadAsStringAsync(options.CancellationToken);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Perplexity API error {(int)response.StatusCode}: {Credentials.Redact(text, apiKey)}");
            JsonNode? data;
            try
            {
                data = JsonNode.Parse(text);
            }
            catch (System.Text.Json.JsonException ex)
            {
                throw new InvalidOperationException($"Perplexity API returned invalid JSON: {ex.Message}");
            }
            var answer = data?["choices"]?[0]?["message"]?["content"] is JsonValue content && content.TryGetValue<string>(out var answerText) ? answerText : "";
            var citations = data?["citations"] as JsonArray ?? [];
            var highestCited = CitationMarker().Matches(answer).Select(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).DefaultIfEmpty(0).Max();
            var keep = Math.Min(citations.Count, Math.Min(MaxCitations, Math.Max(numResults, highestCited)));
            var results = new List<SearchResult>();
            for (var i = 0; i < keep; i++)
            {
                if (citations[i] is JsonValue value && value.TryGetValue<string>(out var url)) results.Add(new SearchResult($"Source {i + 1}", url, ""));
                else if (citations[i] is JsonObject obj && obj["url"]?.GetValue<string>() is { } objectUrl)
                {
                    var title = obj["title"]?.GetValue<string>();
                    results.Add(new SearchResult(string.IsNullOrEmpty(title) ? $"Source {i + 1}" : title, objectUrl, ""));
                }
            }
            return new SearchResponse { Answer = answer, Results = results };
        }
        catch (Exception ex) when (ex is not OperationCanceledException && Credentials.Redact(ex.Message, apiKey) != ex.Message)
        {
            throw new InvalidOperationException(Credentials.Redact(ex.Message, apiKey));
        }
    }

    private static void CheckRateLimit()
    {
        lock (RequestTimes)
        {
            var now = DateTimeOffset.UtcNow;
            while (RequestTimes.Count > 0 && RequestTimes.Peek() < now - Window) RequestTimes.Dequeue();
            if (RequestTimes.Count >= MaxRequests)
            {
                var wait = RequestTimes.Peek() + Window - now;
                throw new InvalidOperationException($"Rate limited. Try again in {(int)Math.Ceiling(wait.TotalSeconds)}s");
            }
            RequestTimes.Enqueue(now);
        }
    }
}
