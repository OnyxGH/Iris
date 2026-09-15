using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Iris.Extensions;
using Iris.WebAccess.Search;
using Iris.WebAccess.Tools;

namespace Iris.WebAccess;

public sealed partial class WebAccessExtension
{
    private Tool<WebSearchParams> CreateWebSearchTool() => new()
    {
        Name = WebSearchTool,
        Label = "Web Search",
        Description =
            "Search the web with SearXNG, Exa, Brave, Tavily, Perplexity, or DuckDuckGo. Provider lists run simultaneously; \"all\" searches every configured provider except DuckDuckGo. " +
            "Returns source-linked search results or provider answers. For comprehensive research, prefer queries (plural) with 2-4 varied angles over a single query. " +
            "When includeContent is true, full page content is fetched in the background. The configured provider is used when provider is omitted or set to auto; omit provider unless explicitly overriding it.",
        PromptSnippet = "Use for web research questions. Prefer {queries:[...]} with 2-4 varied angles over a single query for broader coverage. Omit provider unless explicitly overriding the configured default.",
        ExecuteAsync = ExecuteWebSearchAsync,
        RenderCall = (args, theme, _) => RenderSearchCall(args, theme),
        RenderResult = (result, options, theme, _) => RenderSearchResult(result, options, theme),
    };

    private async Task<ToolResult> ExecuteWebSearchAsync(ToolCallContext call, WebSearchParams args, ExtensionContext ctx)
    {
        var queries = NormalizeQueries(args.Queries is not null ? args.Queries : args.Query is not null ? ExpandQueryString(args.Query) : []);
        if (queries.Count == 0) return Error("Error: No query provided. Use 'query' or 'queries' parameter.", "No query provided");

        ProviderSelection provider;
        try
        {
            provider = SearchDispatcher.Parse(args.Provider, "provider");
        }
        catch (Exception ex) when (ex is InvalidOperationException or ConfigParseException)
        {
            return Error($"Error: {ex.Message}", ex.Message);
        }

        var includeContent = args.IncludeContent ?? false;
        var recency = args.RecencyFilter?.ToString().ToLowerInvariant();
        var completed = 0;
        var gate = new SemaphoreSlim(SearchQueryConcurrency);
        var responses = await Task.WhenAll(queries.Select(async query =>
        {
            await gate.WaitAsync(call.CancellationToken);
            try
            {
                call.CancellationToken.ThrowIfCancellationRequested();
                call.Update(ToolResult.Text($"Searching \"{query}\" ({completed}/{queries.Count} complete)...", new JsonObject
                {
                    ["phase"] = "search", ["progress"] = (double)completed / queries.Count, ["currentQuery"] = query,
                }));
                var response = await _dispatcher.SearchAsync(query, provider, new SearchOptions
                {
                    NumResults = args.NumResults,
                    RecencyFilter = recency,
                    DomainFilter = args.DomainFilter,
                    IncludeContent = includeContent,
                    CancellationToken = call.CancellationToken,
                });
                return (Result: new QueryResultData { Query = query, Answer = response.Answer, Results = response.Results, Provider = response.Provider }, Inline: response.InlineContent);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !call.CancellationToken.IsCancellationRequested)
            {
                return (Result: new QueryResultData { Query = query, Error = SearchText.ErrorMessage(ex), Provider = provider.Mode == "single" ? provider.Providers[0] : null }, Inline: (List<ExtractedContent>?)null);
            }
            finally
            {
                Interlocked.Increment(ref completed);
                gate.Release();
                if (!call.CancellationToken.IsCancellationRequested)
                {
                    call.Update(ToolResult.Text($"Completed {completed}/{queries.Count} searches.", new JsonObject
                    {
                        ["phase"] = "search", ["progress"] = (double)completed / queries.Count, ["currentQuery"] = query,
                    }));
                }
            }
        }));

        var results = responses.Select(r => r.Result).ToList();
        var urls = new List<string>();
        var inline = new List<ExtractedContent>();
        foreach (var (result, content) in responses)
        {
            foreach (var source in result.Results) if (!urls.Contains(source.Url)) urls.Add(source.Url);
            if (content is not null) inline.AddRange(content);
        }
        return BuildSearchReturn(queries, results, urls, includeContent, inline.Count > 0 ? inline : null);
    }

    private ToolResult BuildSearchReturn(List<string> queries, List<QueryResultData> results, List<string> urls, bool includeContent, List<ExtractedContent>? inline)
    {
        var output = new StringBuilder();
        foreach (var result in results)
        {
            if (queries.Count > 1) output.Append($"## Query: \"{result.Query}\"\n\n");
            output.Append(result.Error is not null ? $"Error: {result.Error}\n\n" : $"{FormatSearchSummary(result.Results, result.Answer)}\n\n");
        }

        var coveredUrls = inline?.Select(c => c.Url).ToHashSet();
        var hasInlineReady = coveredUrls is { Count: > 0 } && urls.All(coveredUrls.Contains);
        string? fetchId = null;
        if (hasInlineReady)
        {
            fetchId = ResultStore.GenerateId();
            _iris.AppendEntry(ResultStore.EntryType, _store.StoreFetch(fetchId, inline!));
            output.Append($"---\nFull content for {inline!.Count} sources available [{fetchId}].");
        }
        else if (includeContent)
        {
            fetchId = StartBackgroundFetch(urls);
            if (fetchId is not null) output.Append($"---\nContent fetching in background [{fetchId}]. Will notify when ready.");
        }

        var searchId = ResultStore.GenerateId();
        _iris.AppendEntry(ResultStore.EntryType, _store.StoreSearch(searchId, results));
        output.Append($"\n---\nResults stored as responseId \"{searchId}\". Use {GetSearchContentTool}({{ responseId: \"{searchId}\", queryIndex: 0 }}) to retrieve them.");

        var details = new JsonObject
        {
            ["queries"] = new JsonArray([.. queries.Select(q => (JsonNode)q)]),
            ["queryCount"] = queries.Count,
            ["successfulQueries"] = results.Count(r => r.Error is null),
            ["totalResults"] = results.Sum(r => r.Results.Count),
            ["includeContent"] = includeContent,
            ["fetchId"] = fetchId,
            ["searchId"] = searchId,
        };
        if (fetchId is not null && !hasInlineReady) details["fetchUrls"] = new JsonArray([.. urls.Select(u => (JsonNode)u)]);
        return ToolResult.Text(output.ToString().Trim(), details);
    }

    internal static string FormatSearchSummary(List<SearchResult> results, string answer)
    {
        if (results.Count == 0) return answer.Length > 0 ? $"{answer}\n\n---\n\n**Sources:**\nNo sources returned." : "No results found.";
        var output = answer.Length > 0 ? $"{answer}\n\n---\n\n**Sources:**\n" : "";
        return output + string.Join("\n\n", results.Select((r, i) => $"{i + 1}. {r.Title}\n   {r.Url}"));
    }

    internal static List<string> NormalizeQueries(IEnumerable<string> queries) => [.. queries.Select(q => q.Trim()).Where(q => q.Length > 0)];

    /// <summary>
    /// Some models serialize a query list into the single query string as a JSON array; search each element instead of the
    /// literal array text.
    /// </summary>
    internal static List<string> ExpandQueryString(string query)
    {
        var trimmed = query.Trim();
        if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
        {
            try
            {
                if (JsonNode.Parse(trimmed) is JsonArray array && array.All(e => e is JsonValue v && v.GetValueKind() == JsonValueKind.String))
                {
                    return NormalizeQueries(array.Select(e => e!.GetValue<string>()));
                }
            }
            catch (JsonException)
            {
                // A literal query.
            }
        }
        return [query];
    }

    private static ToolResult Error(string text, string error, JsonObject? extra = null)
    {
        var details = extra ?? [];
        details["error"] = error;
        return ToolResult.Text(text, details);
    }
}
