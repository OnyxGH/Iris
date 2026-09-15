using System.Text.Json.Nodes;

namespace Iris.WebAccess.Search;

/// <summary>A provider selection: "auto", "all", one provider, or a list of providers searched simultaneously.</summary>
internal sealed record ProviderSelection(string Mode, IReadOnlyList<string> Providers)
{
    public static ProviderSelection Auto { get; } = new("auto", []);

    public override string ToString() => Mode == "list" ? string.Join(",", Providers) : Mode == "single" ? Providers[0] : Mode;
}

/// <summary>
/// Routes a query to search providers. In auto mode it tries a configured SearXNG endpoint, then Exa (direct API or the
/// keyless MCP endpoint), Brave, Tavily and Perplexity, returning the first success. "all" or a provider list runs the
/// providers in parallel and merges their results.
/// </summary>
internal sealed class SearchDispatcher
{
    private readonly IReadOnlyDictionary<string, ISearchProvider> _providers;

    /// <summary>Providers tried in auto mode, in order.</summary>
    private static readonly string[] AutoOrder = ["searxng", "exa", "brave", "tavily", "perplexity"];

    /// <summary>Providers included in "all"; the rest must be selected explicitly.</summary>
    private static readonly string[] AllEligible = ["searxng", "exa", "brave", "tavily", "perplexity"];

    public SearchDispatcher(IEnumerable<ISearchProvider>? providers = null)
    {
        _providers = (providers ?? [new ExaProvider(), new BraveProvider(), new TavilyProvider(), new PerplexityProvider(), new SearxngProvider(), new DuckDuckGoProvider()])
            .ToDictionary(p => p.Id, StringComparer.Ordinal);
    }

    public static IReadOnlyList<string> AllProviderIds { get; } = ["exa", "brave", "tavily", "perplexity", "searxng", "duckduckgo"];

    public string Label(string providerId) => _providers.TryGetValue(providerId, out var provider) ? provider.Label : providerId;

    /// <summary>Providers allowed by webSearch.allowedProviders (all when unset).</summary>
    public static IReadOnlyList<string> AllowedProviders()
    {
        if (WebConfig.GetObject("webSearch") is not { } webSearch || !webSearch.ContainsKey("allowedProviders")) return AllProviderIds;
        return ParseProviderList(webSearch["allowedProviders"], $"webSearch.allowedProviders in {WebConfig.Path}");
    }

    public static ProviderSelection Parse(JsonNode? value, string label)
    {
        if (value is null) return ProviderSelection.Auto;
        if (value is JsonValue text && text.TryGetValue<string>(out var name))
        {
            var normalized = name.Trim().ToLowerInvariant();
            if (normalized is "auto" or "all") return new ProviderSelection(normalized, []);
            if (normalized.Length == 0) return ProviderSelection.Auto;
            return new ProviderSelection("single", [ValidateProvider(normalized, label)]);
        }
        var providers = ParseProviderList(value, label);
        return providers.Count == 1 ? new ProviderSelection("single", providers) : new ProviderSelection("list", providers);
    }

    private static List<string> ParseProviderList(JsonNode? value, string label)
    {
        if (value is not JsonArray array || array.Count == 0) throw new InvalidOperationException($"{label} must be \"auto\", \"all\", a provider name, or a non-empty list of providers");
        var providers = new List<string>();
        foreach (var entry in array)
        {
            if (entry is not JsonValue text || !text.TryGetValue<string>(out var name)) throw new InvalidOperationException($"{label} must contain only provider names");
            var normalized = ValidateProvider(name.Trim().ToLowerInvariant(), label);
            if (!providers.Contains(normalized)) providers.Add(normalized);
        }
        return providers;
    }

    private static string ValidateProvider(string name, string label) =>
        AllProviderIds.Contains(name)
            ? name
            : throw new InvalidOperationException($"{label}: unsupported search provider \"{name}\". Supported providers: auto, all, {string.Join(", ", AllProviderIds)}");

    /// <summary>The provider configured in web-search.json (searchProvider, or provider).</summary>
    public static ProviderSelection ConfiguredSelection()
    {
        var root = WebConfig.Root;
        var key = root.ContainsKey("searchProvider") ? "searchProvider" : root.ContainsKey("provider") ? "provider" : null;
        return key is null ? ProviderSelection.Auto : Parse(root[key], $"{key} in {WebConfig.Path}");
    }

    public async Task<SearchResponse> SearchAsync(string query, ProviderSelection requested, SearchOptions options)
    {
        var selection = requested.Mode == "auto" ? ConfiguredSelection() : requested;
        var allowed = AllowedProviders();
        var disallowed = selection.Providers.Where(p => !allowed.Contains(p)).ToList();
        if (disallowed.Count > 0) throw new InvalidOperationException($"Requested provider is not allowed by webSearch.allowedProviders: {string.Join(", ", disallowed)}");

        switch (selection.Mode)
        {
            case "single":
                return await SearchWithProviderAsync(selection.Providers[0], query, options);
            case "list":
                return await SearchWithProvidersAsync(query, options, selection.Providers, "Selected-provider");
            case "all":
                var eligible = AllEligible.Where(p => allowed.Contains(p) && _providers[p].IsAvailable()).ToList();
                if (eligible.Count == 0) throw new InvalidOperationException("No configured search provider available for provider \"all\". DuckDuckGo is excluded.");
                return await SearchWithProvidersAsync(query, options, eligible, "All-provider");
        }

        var errors = new List<string>();
        foreach (var id in AutoOrder)
        {
            if (!allowed.Contains(id) || !_providers.TryGetValue(id, out var provider) || !provider.IsAvailable()) continue;
            try
            {
                if (await provider.SearchAsync(query, options) is { } response) return WithProvider(response, id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not CredentialResolutionException and not ConfigParseException)
            {
                errors.Add($"{provider.Label}: {SearchText.ErrorMessage(ex)}");
            }
        }
        if (errors.Count > 0) throw new InvalidOperationException($"Auto provider search failed:\n  - {string.Join("\n  - ", errors)}");
        throw new InvalidOperationException(
            "No search provider available. Either:\n" +
            $"  1. Set exaApiKey, braveApiKey, tavilyApiKey, perplexityApiKey or searxngBaseUrl in {WebConfig.Path}\n" +
            "  2. Set EXA_API_KEY, BRAVE_API_KEY, TAVILY_API_KEY, PERPLEXITY_API_KEY or SEARXNG_BASE_URL env vars\n" +
            "  3. Explicitly select provider: \"duckduckgo\" for keyless DuckDuckGo search");
    }

    private async Task<SearchResponse> SearchWithProviderAsync(string id, string query, SearchOptions options)
    {
        var provider = _providers[id];
        var response = await provider.SearchAsync(query, options) ?? throw new InvalidOperationException($"{provider.Label} search returned no results.");
        return WithProvider(response, id);
    }

    private async Task<SearchResponse> SearchWithProvidersAsync(string query, SearchOptions options, IReadOnlyList<string> providers, string label)
    {
        var tasks = providers.Select(id => SearchWithProviderAsync(id, query, options)).ToList();
        try
        {
            await Task.WhenAll(tasks);
        }
        catch
        {
            // Inspected per task below.
        }
        options.CancellationToken.ThrowIfCancellationRequested();

        var successes = new List<SearchResponse>();
        var failures = new List<(string Provider, string Error)>();
        for (var i = 0; i < tasks.Count; i++)
        {
            if (tasks[i].IsCompletedSuccessfully) successes.Add(tasks[i].Result);
            else failures.Add((providers[i], tasks[i].Exception is { } ex ? SearchText.ErrorMessage(ex) : "cancelled"));
        }
        if (successes.Count == 0) throw new InvalidOperationException($"{label} search failed:\n  - {string.Join("\n  - ", failures.Select(f => $"{Label(f.Provider)}: {f.Error}"))}");

        var results = new List<SearchResult>();
        var seenResults = new HashSet<string>();
        var inline = new List<ExtractedContent>();
        var seenInline = new HashSet<string>();
        foreach (var response in successes)
        {
            foreach (var result in response.Results.Where(r => seenResults.Add(r.Url))) results.Add(result);
            foreach (var content in (response.InlineContent ?? []).Where(c => seenInline.Add(c.Url))) inline.Add(content);
        }
        var sections = successes.Select(r => $"## {Label(r.Provider!)}\n\n{(r.Answer.Length > 0 ? r.Answer : "(No answer text returned.)")}").ToList();
        if (failures.Count > 0) sections.Add($"## Provider errors\n\n{string.Join("\n", failures.Select(f => $"- **{Label(f.Provider)}:** {f.Error}"))}");
        return new SearchResponse { Provider = "all", Answer = string.Join("\n\n", sections), Results = results, InlineContent = inline.Count > 0 ? inline : null };
    }

    private static SearchResponse WithProvider(SearchResponse response, string provider) =>
        new() { Answer = response.Answer, Results = response.Results, InlineContent = response.InlineContent, Provider = provider };
}
