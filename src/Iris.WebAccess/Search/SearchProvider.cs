using System.Text.RegularExpressions;

namespace Iris.WebAccess.Search;

/// <summary>A web search backend.</summary>
internal interface ISearchProvider
{
    /// <summary>Id used in the provider parameter and config ("exa", "brave", ...).</summary>
    string Id { get; }

    string Label { get; }

    /// <summary>Whether the provider is configured (API key, endpoint) without making a request.</summary>
    bool IsAvailable();

    /// <summary>Search; null when the provider returned nothing usable.</summary>
    Task<SearchResponse?> SearchAsync(string query, SearchOptions options);
}

internal sealed record DomainFilters(IReadOnlyList<string> Allowed, IReadOnlyList<string> Blocked)
{
    public bool IsEmpty => Allowed.Count == 0 && Blocked.Count == 0;

    public static DomainFilters From(IReadOnlyList<string>? domainFilter)
    {
        var allowed = new List<string>();
        var blocked = new List<string>();
        foreach (var raw in domainFilter ?? [])
        {
            if (SearchText.NormalizeDomain(raw) is not { } domain) continue;
            var target = raw.Trim().StartsWith('-') ? blocked : allowed;
            if (!target.Contains(domain)) target.Add(domain);
        }
        return new DomainFilters(allowed, blocked);
    }

    public bool Matches(string url)
    {
        if (IsEmpty) return true;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        var hostname = uri.Host.ToLowerInvariant();
        if (Allowed.Count > 0 && !Allowed.Any(domain => HostMatches(hostname, domain))) return false;
        return !Blocked.Any(domain => HostMatches(hostname, domain));
    }

    /// <summary>site: operators appended to the query; excluded domains use the given prefix ("-site:" or "NOT site:").</summary>
    public string ApplyToQuery(string query, string excludePrefix)
    {
        var parts = new List<string> { query };
        if (Allowed.Count == 1) parts.Add($"site:{Allowed[0]}");
        else if (Allowed.Count > 1) parts.Add(string.Join(" OR ", Allowed.Select(domain => $"site:{domain}")));
        parts.AddRange(Blocked.Select(domain => $"{excludePrefix}{domain}"));
        return string.Join(" ", parts);
    }

    private static bool HostMatches(string hostname, string domain) => hostname == domain || hostname.EndsWith($".{domain}", StringComparison.Ordinal);
}

internal static partial class SearchText
{
    [GeneratedRegex(@"^[a-z0-9][a-z0-9.-]*\.[a-z]{2,}$", RegexOptions.IgnoreCase)]
    private static partial Regex DomainPattern();

    public static int ResultCount(int? value) => value is { } n ? Math.Clamp(n, 1, 20) : 5;

    public static string? NormalizeDomain(string value)
    {
        var input = value.Trim().ToLowerInvariant();
        if (input.StartsWith('-')) input = input[1..].Trim();
        if (input.Length == 0) return null;
        input = Uri.TryCreate(input.Contains("://", StringComparison.Ordinal) ? input : $"https://{input}", UriKind.Absolute, out var uri)
            ? uri.Host
            : input.Split('/')[0].Split(':')[0];
        input = input.Trim('.');
        return DomainPattern().IsMatch(input) ? input : null;
    }

    /// <summary>"snippet\nSource: title (url)" blocks, the answer format of result-list providers.</summary>
    public static string AnswerFromResults(IEnumerable<SearchResult> results) =>
        string.Join("\n\n", results.Select(r => r.Snippet.Length > 0 ? $"{r.Snippet}\nSource: {r.Title} ({r.Url})" : $"Source: {r.Title} ({r.Url})"));

    public static string Truncate(string text, int length) => text.Length > length ? text[..length] : text;

    public static string ErrorMessage(Exception ex) => ex is AggregateException { InnerException: { } inner } ? inner.Message : ex.Message;
}
