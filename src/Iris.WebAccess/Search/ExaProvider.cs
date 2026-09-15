using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Iris.WebAccess.Http;

namespace Iris.WebAccess.Search;

/// <summary>Exa: the direct API when an exaApiKey is configured, otherwise Exa's public MCP endpoint (no key needed).</summary>
internal sealed partial class ExaProvider : ISearchProvider
{
    private const string DefaultBaseUrl = "https://api.exa.ai";
    private const string McpUrl = "https://mcp.exa.ai/mcp";
    private const string McpAdvancedTool = "web_search_advanced_exa";
    private const string McpBasicTool = "web_search_exa";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    public string Id => "exa";

    public string Label => "Exa";

    public bool IsAvailable() => true;

    public static bool HasApiKey() => Credentials.HasSource("exaApiKey", "EXA_API_KEY");

    [GeneratedRegex(@"(?=^Title: )", RegexOptions.Multiline)]
    private static partial Regex McpBlockSplit();

    [GeneratedRegex(@"^Title: (.+)", RegexOptions.Multiline)]
    private static partial Regex McpTitle();

    [GeneratedRegex(@"^URL: (.+)", RegexOptions.Multiline)]
    private static partial Regex McpUrlLine();

    [GeneratedRegex(@"\nHighlights:\s*\n")]
    private static partial Regex McpHighlights();

    [GeneratedRegex(@"\n---\s*$")]
    private static partial Regex TrailingRule();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    public async Task<SearchResponse?> SearchAsync(string query, SearchOptions options)
    {
        var apiKey = await Credentials.ResolveAsync("Exa", "exaApiKey", "EXA_API_KEY", options.CancellationToken);
        if (apiKey is null) return await SearchWithMcpAsync(query, options);

        var baseUrl = WebConfig.ResolveBaseUrl("exaBaseUrl", "EXA_BASE_URL", DefaultBaseUrl);
        var useSearch = options.IncludeContent || options.RecencyFilter is not null || options.DomainFilter is { Count: > 0 } || options.NumResults is { } n && n != 5;
        try
        {
            if (!useSearch)
            {
                var answer = await PostAsync(new Uri($"{baseUrl}/answer"), apiKey, new JsonObject { ["query"] = query }, options.CancellationToken);
                return new SearchResponse { Answer = answer["answer"]?.GetValue<string>() ?? "", Results = MapResults(answer["citations"] as JsonArray) };
            }
            var body = SearchArgs(query, options);
            body["contents"] = options.IncludeContent ? new JsonObject { ["text"] = true, ["highlights"] = true } : new JsonObject { ["highlights"] = true };
            var data = await PostAsync(new Uri($"{baseUrl}/search"), apiKey, body, options.CancellationToken);
            var results = data["results"] as JsonArray;
            return ToResponse(AnswerFromSearchResults(results), MapResults(results), options.IncludeContent ? InlineContent(results) : null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && Credentials.Redact(ex.Message, apiKey) != ex.Message)
        {
            throw new InvalidOperationException(Credentials.Redact(ex.Message, apiKey));
        }
    }

    private static async Task<JsonObject> PostAsync(Uri url, string apiKey, JsonObject body, CancellationToken cancellationToken)
    {
        using var response = await SafeHttp.SendApiAsync(target =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, target) { Content = SafeHttp.JsonBody(body) };
            request.Headers.Add("x-api-key", apiKey);
            request.Headers.Add("x-exa-integration", "iris-web-access");
            return request;
        }, url, ["x-api-key"], Timeout, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Exa API error {(int)response.StatusCode}: {SearchText.Truncate(Credentials.Redact(text, apiKey), 300)}");
        }
        return JsonNode.Parse(text) as JsonObject ?? throw new InvalidOperationException("Exa API returned invalid JSON");
    }

    private static JsonObject SearchArgs(string query, SearchOptions options)
    {
        var args = new JsonObject { ["query"] = query, ["type"] = "auto", ["numResults"] = options.NumResults ?? 5 };
        var include = (options.DomainFilter ?? []).Where(d => !d.StartsWith('-') && d.Trim().Length > 0).Select(d => d.Trim()).ToList();
        var exclude = (options.DomainFilter ?? []).Where(d => d.StartsWith('-')).Select(d => d[1..].Trim()).Where(d => d.Length > 0).ToList();
        if (include.Count > 0) args["includeDomains"] = new JsonArray([.. include.Select(d => (JsonNode)d)]);
        if (exclude.Count > 0) args["excludeDomains"] = new JsonArray([.. exclude.Select(d => (JsonNode)d)]);
        if (options.RecencyFilter is { } recency)
        {
            var days = recency switch { "day" => 1, "week" => 7, "month" => 30, "year" => 365, _ => 0 };
            args["startPublishedDate"] = DateTimeOffset.UtcNow.AddDays(-days).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        }
        return args;
    }

    private static List<string> Highlights(JsonNode? value) =>
        value is JsonArray array ? [.. array.Select(h => h is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!)] : [];

    private static string? Str(JsonNode? node, string key) => node?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static string AnswerFromSearchResults(JsonArray? results)
    {
        if (results is null) return "";
        var parts = new List<string>();
        for (var i = 0; i < results.Count; i++)
        {
            var item = results[i];
            if (Str(item, "url") is not { } url) continue;
            var highlights = Highlights(item?["highlights"]);
            var content = highlights.Count > 0 ? string.Join(" ", highlights) : SearchText.Truncate(Str(item, "text")?.Trim() ?? "", 1000);
            if (content.Length == 0) continue;
            parts.Add($"{content}\nSource: {(string.IsNullOrEmpty(Str(item, "title")) ? $"Source {i + 1}" : Str(item, "title"))} ({url})");
        }
        return string.Join("\n\n", parts);
    }

    private static List<SearchResult> MapResults(JsonArray? results)
    {
        var mapped = new List<SearchResult>();
        if (results is null) return mapped;
        for (var i = 0; i < results.Count; i++)
        {
            if (Str(results[i], "url") is not { } url) continue;
            var title = Str(results[i], "title");
            mapped.Add(new SearchResult(string.IsNullOrEmpty(title) ? $"Source {i + 1}" : title, url, ""));
        }
        return mapped;
    }

    private static List<ExtractedContent> InlineContent(JsonArray? results) =>
        results is null
            ? []
            : [.. results.Where(r => Str(r, "url") is not null && Str(r, "text") is { Length: > 0 }).Select(r => new ExtractedContent { Url = Str(r, "url")!, Title = Str(r, "title") ?? "", Content = Str(r, "text")! })];

    private static SearchResponse ToResponse(string answer, List<SearchResult> results, List<ExtractedContent>? inline) =>
        new() { Answer = answer, Results = results, InlineContent = inline is { Count: > 0 } ? inline : null };

    // ----- MCP (no API key) -----

    private static async Task<SearchResponse?> SearchWithMcpAsync(string query, SearchOptions options)
    {
        var basicArgs = new JsonObject { ["query"] = McpQuery(query, options), ["numResults"] = options.NumResults ?? 5 };
        var filtered = options.IncludeContent || options.RecencyFilter is not null || options.DomainFilter is { Count: > 0 };
        if (!filtered) return await SearchWithMcpToolAsync(McpBasicTool, basicArgs, options);
        try
        {
            var advanced = SearchArgs(query, options);
            advanced["enableHighlights"] = true;
            advanced["textMaxCharacters"] = options.IncludeContent ? 50000 : 3000;
            return await SearchWithMcpToolAsync(McpAdvancedTool, advanced, options);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Not every deployment exposes the advanced tool; the basic tool takes the filters as query text.
            return await SearchWithMcpToolAsync(McpBasicTool, basicArgs, options);
        }
    }

    private static async Task<SearchResponse?> SearchWithMcpToolAsync(string tool, JsonObject args, SearchOptions options)
    {
        var text = await CallMcpAsync(tool, args, options.CancellationToken);
        if (ParseJsonResults(text) is { } jsonResults)
        {
            return ToResponse(AnswerFromSearchResults(jsonResults), MapResults(jsonResults), options.IncludeContent ? InlineContent(jsonResults) : null);
        }
        if (ParseTextResults(text) is not { } textResults) return null;
        var answer = string.Join("\n\n", textResults.Select((r, i) =>
        {
            var snippet = SearchText.Truncate(Whitespace().Replace(r.Content, " ").Trim(), 500);
            return snippet.Length == 0 ? null : $"{snippet}\nSource: {(r.Title.Length > 0 ? r.Title : $"Source {i + 1}")} ({r.Url})";
        }).Where(p => p is not null));
        var results = textResults.Select((r, i) => new SearchResult(r.Title.Length > 0 ? r.Title : $"Source {i + 1}", r.Url, "")).ToList();
        var inline = options.IncludeContent ? textResults.Where(r => r.Content.Length > 0).Select(r => new ExtractedContent { Url = r.Url, Title = r.Title, Content = r.Content }).ToList() : null;
        return ToResponse(answer, results, inline);
    }

    internal static async Task<string> CallMcpAsync(string tool, JsonObject args, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = args },
        };
        using var response = await SafeHttp.SendApiAsync(target =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, target) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
            request.Headers.Accept.ParseAdd("application/json, text/event-stream");
            request.Headers.Add("x-exa-source", "iris-web-access");
            return request;
        }, new Uri($"{McpUrl}?tools={tool}"), [], Timeout, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throw new InvalidOperationException($"Exa MCP rate limit reached (429). Add \"exaApiKey\" to {WebConfig.Path} for unthrottled Exa search: {SearchText.Truncate(text, 200)}");
            }
            throw new InvalidOperationException($"Exa MCP error {(int)response.StatusCode}: {SearchText.Truncate(text, 300)}");
        }

        JsonObject? parsed = null;
        foreach (var line in text.Split('\n').Where(l => l.StartsWith("data:", StringComparison.Ordinal)))
        {
            var payload = line[5..].Trim();
            if (payload.Length == 0) continue;
            if (TryParseRpc(payload) is { } candidate)
            {
                parsed = candidate;
                break;
            }
        }
        parsed ??= TryParseRpc(text);
        if (parsed is null) throw new InvalidOperationException("Exa MCP returned an empty response");
        if (parsed["error"] is JsonObject error)
        {
            var code = error["code"] is JsonValue c && c.TryGetValue<int>(out var number) ? $" {number}" : "";
            throw new InvalidOperationException($"Exa MCP error{code}: {Str(error, "message") ?? "Unknown error"}");
        }
        var content = parsed["result"]?["content"] as JsonArray;
        if (parsed["result"]?["isError"] is JsonValue isError && isError.TryGetValue<bool>(out var failed) && failed)
        {
            throw new InvalidOperationException(content?.Select(item => Str(item, "text")?.Trim()).FirstOrDefault(t => !string.IsNullOrEmpty(t)) ?? "Exa MCP returned an error");
        }
        return content?.Where(item => Str(item, "type") == "text").Select(item => Str(item, "text")).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t))
            ?? throw new InvalidOperationException("Exa MCP returned empty content");
    }

    private static JsonObject? TryParseRpc(string payload)
    {
        try
        {
            return JsonNode.Parse(payload) is JsonObject obj && (obj["result"] is not null || obj["error"] is not null) ? obj : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonArray? ParseJsonResults(string text)
    {
        try
        {
            return JsonNode.Parse(text)?["results"] is JsonArray { Count: > 0 } results ? results : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static List<(string Title, string Url, string Content)>? ParseTextResults(string text)
    {
        var parsed = McpBlockSplit().Split(text).Where(block => block.Trim().Length > 0).Select(block =>
        {
            var title = McpTitle().Match(block) is { Success: true } t ? t.Groups[1].Value.Trim() : "";
            var url = McpUrlLine().Match(block) is { Success: true } u ? u.Groups[1].Value.Trim() : "";
            var content = "";
            var textStart = block.IndexOf("\nText: ", StringComparison.Ordinal);
            if (textStart >= 0) content = block[(textStart + 7)..].Trim();
            else if (McpHighlights().Match(block) is { Success: true } h) content = block[(h.Index + h.Length)..].Trim();
            return (Title: title, Url: url, Content: TrailingRule().Replace(content, "").Trim());
        }).Where(r => r.Url.Length > 0).ToList();
        return parsed.Count > 0 ? parsed : null;
    }

    private static string McpQuery(string query, SearchOptions options)
    {
        var parts = new List<string> { query };
        foreach (var domain in options.DomainFilter ?? []) parts.Add(domain.StartsWith('-') ? $"-site:{domain[1..]}" : $"site:{domain}");
        var now = DateTime.Now;
        switch (options.RecencyFilter)
        {
            case "day": parts.Add("past 24 hours"); break;
            case "week": parts.Add("past week"); break;
            case "month": parts.Add($"{now.ToString("MMMM", CultureInfo.InvariantCulture)} {now.Year}"); break;
            case "year": parts.Add(now.Year.ToString(CultureInfo.InvariantCulture)); break;
        }
        return string.Join(" ", parts);
    }
}
