using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Iris.Extensions;
using Iris.Tui;
using Iris.WebAccess.Curator;
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
            "Searches return without the interactive curator by default; set workflow to \"summary-review\" to let the user pick results and approve a summary, or \"auto-summary\" to summarize without the curator. " +
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
        var workflow = ResolveWorkflow(args.Workflow, ctx);
        if (workflow == SearchWorkflow.None) return BuildSearchReturn(new SearchReturn(queries, results, urls, includeContent, inline.Count > 0 ? inline : null));

        var summaryModels = SummaryGenerator.Candidates(ctx.ModelRegistry, ctx.Model, null);
        if (summaryModels.Count == 0 && workflow == SearchWorkflow.AutoSummary)
        {
            return BuildSearchReturn(new SearchReturn(queries, results, urls, includeContent, inline.Count > 0 ? inline : null));
        }

        call.Update(ToolResult.Text("Generating summary...", new JsonObject { ["phase"] = "generating-summary", ["progress"] = 1 }));
        var draft = await SummaryGenerator.GenerateAsync(results, ctx.ModelRegistry, ctx.Model, null, null, call.CancellationToken);
        if (workflow == SearchWorkflow.AutoSummary)
        {
            return BuildSearchReturn(new SearchReturn(queries, results, urls, includeContent, inline.Count > 0 ? inline : null)
            {
                Workflow = "auto-summary",
                ApprovedSummary = draft.Summary,
                SummaryMeta = draft.Meta,
            });
        }
        return await CurateAsync(call, ctx, queries, results, inline, includeContent, draft);
    }

    private async Task<ToolResult> CurateAsync(
        ToolCallContext call,
        ExtensionContext ctx,
        List<string> queries,
        List<QueryResultData> results,
        List<ExtractedContent> inline,
        bool includeContent,
        SummaryDraft draft)
    {
        var state = new CuratorState(results) { Summary = draft.Summary, Meta = draft.Meta };
        if (draft.Meta.FallbackUsed) state.Status = $"Summary model unavailable ({draft.Meta.FallbackReason}); showing a generated outline.";

        while (true)
        {
            call.CancellationToken.ThrowIfCancellationRequested();
            var action = await ctx.UI.CustomAsync<CuratorAction>(
                context => new CuratorModal(context, state),
                new CustomUIOptions
                {
                    Overlay = true,
                    OverlayOptions = new OverlayOptions { Width = SizeValue.Percent(85), MinWidth = 50, MaxHeight = SizeValue.Percent(90) },
                });

            // A closed overlay yields the default action, Cancel.
            switch (action)
            {
                case CuratorAction.Cancel:
                    var message = "Search curation cancelled (user).";
                    return Error(message, message, new JsonObject { ["cancelled"] = true, ["cancelReason"] = "user", ["queryCount"] = queries.Count });

                case CuratorAction.Submit or CuratorAction.SubmitAndAutoSummarize:
                    if (action == CuratorAction.SubmitAndAutoSummarize) _autoSummarizeRemaining = true;
                    var selected = state.Selected();
                    var summary = state.Summary.Trim();
                    var meta = state.Meta;
                    if (summary.Length == 0)
                    {
                        var deterministic = SummaryGenerator.Deterministic(selected);
                        (summary, meta) = (deterministic.Summary, deterministic.Meta);
                    }
                    var selectedUrls = new List<string>();
                    foreach (var url in selected.SelectMany(r => r.Results.Select(source => source.Url)))
                    {
                        if (!selectedUrls.Contains(url)) selectedUrls.Add(url);
                    }
                    var selectedInline = inline.Where(c => selectedUrls.Contains(c.Url)).ToList();
                    return BuildSearchReturn(new SearchReturn([.. selected.Select(r => r.Query)], selected, selectedUrls, includeContent, selectedInline.Count > 0 ? selectedInline : null)
                    {
                        Curated = true,
                        CuratedFrom = queries.Count,
                        Workflow = "summary-review",
                        ApprovedSummary = summary,
                        SummaryMeta = meta,
                    });

                case CuratorAction.Feedback:
                    var feedback = await ctx.UI.InputAsync("Feedback for the summary", cancellationToken: call.CancellationToken);
                    if (string.IsNullOrWhiteSpace(feedback)) break;
                    state.Feedback = feedback.Trim();
                    await RegenerateAsync(call, ctx, state);
                    break;

                case CuratorAction.Regenerate:
                    await RegenerateAsync(call, ctx, state);
                    break;

                case CuratorAction.Edit:
                    var edited = await ctx.UI.EditorAsync("Edit the summary", state.Summary);
                    if (edited is null) break;
                    state.Summary = edited.Trim();
                    state.Meta = state.Meta with { Edited = true, TokenEstimate = SummaryGenerator.EstimateTokens(state.Summary) };
                    state.Status = "Summary edited.";
                    state.SummaryScroll = 0;
                    break;
            }
        }
    }

    private static async Task RegenerateAsync(ToolCallContext call, ExtensionContext ctx, CuratorState state)
    {
        var selected = state.Selected();
        if (selected.Count == 0)
        {
            state.Status = "Select at least one query before generating a summary.";
            return;
        }
        call.Update(ToolResult.Text("Generating summary...", new JsonObject { ["phase"] = "generating-summary", ["progress"] = 1 }));
        var draft = await SummaryGenerator.GenerateAsync(selected, ctx.ModelRegistry, ctx.Model, null, state.Feedback, call.CancellationToken);
        state.Summary = draft.Summary;
        state.Meta = draft.Meta;
        state.SummaryScroll = 0;
        state.Status = draft.Meta.FallbackUsed
            ? $"Summary model unavailable ({draft.Meta.FallbackReason}); showing a generated outline."
            : state.Feedback is not null ? "Summary regenerated with your feedback." : "Summary regenerated.";
    }

    private sealed record SearchReturn(List<string> Queries, List<QueryResultData> Results, List<string> Urls, bool IncludeContent, List<ExtractedContent>? Inline)
    {
        public bool Curated { get; init; }

        public int CuratedFrom { get; init; }

        public string? Workflow { get; init; }

        public string? ApprovedSummary { get; init; }

        public SummaryMeta? SummaryMeta { get; init; }
    }

    private ToolResult BuildSearchReturn(SearchReturn options)
    {
        var (queries, results, urls, includeContent, inline) = options;
        var approvedSummary = options.ApprovedSummary?.Trim();
        var hasApprovedSummary = approvedSummary is { Length: > 0 };
        var output = new StringBuilder();
        if (hasApprovedSummary)
        {
            output.Append(approvedSummary);
        }
        else
        {
            if (options.Curated) output.Append("[These results were manually curated by the user. Use them as-is — do not re-search or discard.]\n\n");
            foreach (var result in results)
            {
                if (queries.Count > 1) output.Append($"## Query: \"{result.Query}\"\n\n");
                output.Append(result.Error is not null ? $"Error: {result.Error}\n\n" : $"{FormatSearchSummary(result.Results, result.Answer)}\n\n");
            }
        }

        var coveredUrls = inline?.Select(c => c.Url).ToHashSet();
        var hasInlineReady = coveredUrls is { Count: > 0 } && urls.All(coveredUrls.Contains);
        string? fetchId = null;
        if (hasInlineReady)
        {
            fetchId = ResultStore.GenerateId();
            _iris.AppendEntry(ResultStore.EntryType, _store.StoreFetch(fetchId, inline!));
            if (!hasApprovedSummary) output.Append($"---\nFull content for {inline!.Count} sources available [{fetchId}].");
        }
        else if (includeContent)
        {
            fetchId = StartBackgroundFetch(urls);
            if (fetchId is not null && !hasApprovedSummary) output.Append($"---\nContent fetching in background [{fetchId}]. Will notify when ready.");
        }

        var searchId = ResultStore.GenerateId();
        _iris.AppendEntry(ResultStore.EntryType, _store.StoreSearch(searchId, results));
        if (!hasApprovedSummary) output.Append($"\n---\nResults stored as responseId \"{searchId}\". Use {GetSearchContentTool}({{ responseId: \"{searchId}\", queryIndex: 0 }}) to retrieve them.");

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
        if (options.Curated)
        {
            details["curated"] = true;
            details["curatedFrom"] = options.CuratedFrom;
            details["curatedQueries"] = new JsonArray([.. results.Select(r => (JsonNode)new JsonObject
            {
                ["query"] = r.Query,
                ["provider"] = r.Provider,
                ["answer"] = r.Answer.Length > 0 ? r.Answer : null,
                ["error"] = r.Error,
                ["sources"] = new JsonArray([.. r.Results.Select(source => (JsonNode)new JsonObject { ["title"] = source.Title, ["url"] = source.Url })]),
            })]);
        }
        if (hasApprovedSummary && options.Workflow is { } workflow)
        {
            var meta = options.SummaryMeta;
            details["summary"] = new JsonObject
            {
                ["text"] = approvedSummary,
                ["workflow"] = workflow,
                ["model"] = meta?.Model,
                ["durationMs"] = meta?.DurationMs ?? 0,
                ["tokenEstimate"] = meta?.TokenEstimate ?? SummaryGenerator.EstimateTokens(approvedSummary!),
                ["fallbackUsed"] = meta?.FallbackUsed ?? false,
                ["fallbackReason"] = meta?.FallbackReason,
                ["phase"] = meta?.Phase,
                ["edited"] = meta?.Edited ?? false,
            };
        }
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
