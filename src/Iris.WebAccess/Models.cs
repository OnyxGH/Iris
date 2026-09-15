using System.Text.Json;
using System.Text.Json.Serialization;

namespace Iris.WebAccess;

public sealed record SearchResult(string Title, string Url, string Snippet);

/// <summary>Page content extracted from a URL (or supplied inline by a search provider).</summary>
public sealed class ExtractedContent
{
    public required string Url { get; init; }

    public string Title { get; init; } = "";

    public string Content { get; init; } = "";

    public string? Error { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MimeType { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Status { get; init; }

    public static ExtractedContent Failed(string url, string error, string title = "") => new() { Url = url, Title = title, Error = error };
}

public sealed class SearchResponse
{
    public string Answer { get; init; } = "";

    public List<SearchResult> Results { get; init; } = [];

    public List<ExtractedContent>? InlineContent { get; init; }

    /// <summary>The provider that answered ("all" for a multi-provider search).</summary>
    public string? Provider { get; init; }
}

public sealed class SearchOptions
{
    public int? NumResults { get; init; }

    /// <summary>"day" | "week" | "month" | "year".</summary>
    public string? RecencyFilter { get; init; }

    public IReadOnlyList<string>? DomainFilter { get; init; }

    public bool IncludeContent { get; init; }

    public CancellationToken CancellationToken { get; init; }
}

/// <summary>One query of a web_search call, as stored for get_search_content.</summary>
public sealed class QueryResultData
{
    public required string Query { get; init; }

    public string Answer { get; init; } = "";

    public List<SearchResult> Results { get; init; } = [];

    public string? Error { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Provider { get; init; }
}

internal static class WebJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
}
