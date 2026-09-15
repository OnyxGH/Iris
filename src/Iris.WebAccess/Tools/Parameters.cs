using System.ComponentModel;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Iris.WebAccess.Tools;

public enum RecencyFilter
{
    Day,
    Week,
    Month,
    Year,
}

public enum SearchWorkflow
{
    None,
    [JsonStringEnumMemberName("summary-review")]
    SummaryReview,
    [JsonStringEnumMemberName("auto-summary")]
    AutoSummary,
}

public enum FetchMode
{
    Readable,
    Raw,
}

public enum FindMode
{
    Exact,
    [JsonStringEnumMemberName("case-insensitive")]
    CaseInsensitive,
    Fuzzy,
}

public sealed record WebSearchParams(
    [property: Description("Single search query. For research tasks, prefer 'queries' with multiple varied angles instead.")]
    string? Query = null,
    [property: Description("Multiple queries searched concurrently (up to three at a time), each returning source-linked search results or a provider answer. Prefer this for research — vary phrasing, scope, and angle across 2-4 queries to maximize coverage. Good: ['React vs Vue performance benchmarks 2026', 'React vs Vue developer experience comparison', 'React ecosystem size vs Vue ecosystem']. Bad: ['React vs Vue', 'React vs Vue comparison', 'React vs Vue review'] (too similar, redundant results).")]
    List<string>? Queries = null,
    [property: Description("Results per query (default: 5, max: 20)")]
    int? NumResults = null,
    [property: Description("Fetch full page content (async)")]
    bool? IncludeContent = null,
    [property: Description("Filter by recency")]
    RecencyFilter? RecencyFilter = null,
    [property: Description("Limit to domains (prefix with - to exclude)")]
    List<string>? DomainFilter = null,
    [property: Description("Search provider (auto, all, exa, brave, tavily, perplexity, searxng, duckduckgo) or a non-empty list of providers to search simultaneously. Omit this field to use the configured provider.")]
    JsonNode? Provider = null,
    [property: Description("Search workflow: none = return the results (default); summary-review = open the curator so the user can pick results and approve a summary; auto-summary = generate a summary without the curator.")]
    SearchWorkflow? Workflow = null);

public sealed record FetchContentParams(
    [property: Description("Single URL to fetch")]
    string? Url = null,
    [property: Description("Multiple URLs (parallel)")]
    List<string>? Urls = null,
    [property: Description("Force cloning large GitHub repositories that exceed the size threshold")]
    bool? ForceClone = null,
    [property: Description("Fetch mode. readable (default): extract the readable article as Markdown; raw: return text responses unchanged.")]
    FetchMode? Mode = null);

public sealed record GetSearchContentParams(
    [property: Description("The responseId from web_search or fetch_content")]
    string ResponseId,
    [property: Description("Get content for this query (web_search)")]
    string? Query = null,
    [property: Description("Get content for query at index")]
    int? QueryIndex = null,
    [property: Description("Get content for this URL")]
    string? Url = null,
    [property: Description("Get content for URL at index")]
    int? UrlIndex = null,
    [property: Description("Character offset for fetched URL content slices (default 0). Ignored when findText is supplied.")]
    int? Offset = null,
    [property: Description("Maximum characters to return for fetched URL content slices (default and max are set by maxInlineContentChars). Ignored when findText is supplied.")]
    int? Limit = null,
    [property: Description("Text or list of texts (up to 10) to find in the selected stored content. When supplied, offset and limit are ignored.")]
    JsonNode? FindText = null,
    [property: Description("Matching mode for findText (default: case-insensitive). Requires findText.")]
    FindMode? FindMode = null);
