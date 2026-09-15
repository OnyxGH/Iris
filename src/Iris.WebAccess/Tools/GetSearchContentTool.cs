using System.Text.Json;
using System.Text.Json.Nodes;
using Iris.Extensions;
using Iris.WebAccess.Tools;

namespace Iris.WebAccess;

public sealed partial class WebAccessExtension
{
    private const string StoredContentSources = "web_search or fetch_content";

    private Tool<GetSearchContentParams> CreateGetSearchContentTool() => new()
    {
        Name = GetSearchContentTool,
        Label = "Get Search Content",
        Description = $"Retrieve bounded content slices or find matching passages in a previous {StoredContentSources} call.",
        PromptSnippet = $"Use after {StoredContentSources} to retrieve stored content via responseId. Use findText to locate passages without paging through the full content.",
        ExecuteAsync = (call, args, ctx) => Task.FromResult(ExecuteGetSearchContent(args)),
        RenderCall = (args, theme, _) => RenderGetContentCall(args, theme),
        RenderResult = (result, options, theme, _) => RenderGetContentResult(result, options, theme),
    };

    private static string Quote(string? value) => JsonSerializer.Serialize(value);

    private ToolResult ExecuteGetSearchContent(GetSearchContentParams args)
    {
        var maxChars = MaxInlineChars();
        var query = string.IsNullOrWhiteSpace(args.Query) ? null : args.Query;
        var url = string.IsNullOrWhiteSpace(args.Url) ? null : args.Url;
        var findMode = args.FindMode switch { FindMode.Exact => "exact", FindMode.Fuzzy => "fuzzy", _ => "case-insensitive" };

        List<string>? findQueries = null;
        if (args.FindText is not null)
        {
            findQueries = args.FindText switch
            {
                JsonValue text when text.TryGetValue<string>(out var single) => [single],
                JsonArray array when array.All(e => e is JsonValue v && v.GetValueKind() == JsonValueKind.String) => [.. array.Select(e => e!.GetValue<string>())],
                _ => null,
            };
            findQueries = findQueries?.Select(q => q.Trim()).Where(q => q.Length > 0).ToList();
            if (findQueries is not { Count: > 0 }) return Error("findText must contain at least one non-empty string", "findText must contain at least one non-empty string");
            if (findQueries.Count > 10 || findQueries.Any(q => q.Length > 500)) return Error("findText accepts up to 10 strings of at most 500 characters", "Invalid findText");
        }
        else if (args.FindMode is not null)
        {
            return Error($"findMode {Quote(findMode)} requires findText; provide findText or omit findMode.", "findMode requires findText");
        }

        if (_store.Get(args.ResponseId) is not { } data)
        {
            return Error($"Error: No stored results for responseId {Quote(args.ResponseId)}. Use a responseId returned by {StoredContentSources}.", "Not found",
                new JsonObject { ["responseId"] = args.ResponseId });
        }

        if (data is { Type: "search", Queries: { } queries })
        {
            QueryResultData? queryData;
            var available = string.Join(", ", queries.Select((q, i) => $"{i}: \"{q.Query}\""));
            if (query is not null)
            {
                queryData = queries.FirstOrDefault(q => q.Query == query);
                if (queryData is null)
                {
                    return Error($"Query {Quote(query)} was not found for responseId {Quote(args.ResponseId)}. Available queries: {(queries.Count > 0 ? string.Join(", ", queries.Select(q => $"\"{q.Query}\"")) : "none")}. Use one of the available queries or queryIndex.", "Query not found");
                }
            }
            else if (args.QueryIndex is { } index)
            {
                queryData = index >= 0 && index < queries.Count ? queries[index] : null;
                if (queryData is null)
                {
                    return Error($"Query index {index} is out of range for responseId {Quote(args.ResponseId)}; valid indexes are 0-{queries.Count - 1}. Available queries: {(available.Length > 0 ? available : "none")}. Use one of the available indexes.", "Index out of range");
                }
            }
            else
            {
                return Error($"Specify query or queryIndex for responseId {Quote(args.ResponseId)}. Available queries: {(available.Length > 0 ? available : "none")}.", "No query specified");
            }

            if (queryData.Error is not null)
            {
                return Error($"Error retrieving query {Quote(queryData.Query)} from responseId {Quote(args.ResponseId)}: {queryData.Error}. Check the stored search result and retry with another query or queryIndex if needed.",
                    queryData.Error, new JsonObject { ["query"] = queryData.Query });
            }

            var full = $"## Results for: \"{queryData.Query}\"\n\n" + (queryData.Answer.Length > 0 ? $"{queryData.Answer}\n\n---\n\n" : "")
                + string.Concat(queryData.Results.Select(r => $"### {r.Title}\n{r.Url}\n\n"));
            if (findQueries is not null)
            {
                var found = ContentFind.Find(full, findQueries, findMode);
                return ToolResult.Text(found.Text, FindDetails(found, findMode, new JsonObject { ["query"] = queryData.Query, ["resultCount"] = queryData.Results.Count }));
            }
            return ToolResult.Text(full, new JsonObject { ["query"] = queryData.Query, ["resultCount"] = queryData.Results.Count });
        }

        if (data is { Type: "fetch", Urls: { } urls })
        {
            var availableUrls = string.Join("\n  ", urls.Select((u, i) => $"{i}: {u.Url}"));
            int selected;
            if (url is not null)
            {
                selected = urls.FindIndex(u => u.Url == url);
                if (selected < 0)
                {
                    return Error($"URL {Quote(url)} was not found for responseId {Quote(args.ResponseId)}. Available URLs:\n  {(urls.Count > 0 ? string.Join("\n  ", urls.Select(u => u.Url)) : "none")}\nUse one of the available URLs or urlIndex.", "URL not found");
                }
            }
            else if (args.UrlIndex is { } index)
            {
                selected = index;
                if (index < 0 || index >= urls.Count)
                {
                    return Error($"URL index {index} is out of range for responseId {Quote(args.ResponseId)}; valid indexes are 0-{urls.Count - 1}. Available URLs:\n  {(availableUrls.Length > 0 ? availableUrls : "none")}\nUse one of the available indexes.", "Index out of range");
                }
            }
            else
            {
                return Error($"Specify url or urlIndex for responseId {Quote(args.ResponseId)}. Available URLs:\n  {(availableUrls.Length > 0 ? availableUrls : "none")}", "No URL specified");
            }

            var urlData = urls[selected];
            var heading = $"# {(urlData.Title.Length > 0 ? urlData.Title : urlData.Url)}";
            if (urlData.Error is not null)
            {
                return Error($"Error retrieving URL {Quote(urlData.Url)} from responseId {Quote(args.ResponseId)}: {urlData.Error}. Check the stored fetch result and retry with another URL or urlIndex if needed.",
                    urlData.Error, new JsonObject { ["url"] = urlData.Url });
            }
            if (findQueries is not null)
            {
                var found = ContentFind.Find(urlData.Content, findQueries, findMode);
                return ToolResult.Text($"{heading}\n\n{found.Text}", FindDetails(found, findMode, new JsonObject
                {
                    ["url"] = urlData.Url, ["title"] = urlData.Title, ["contentLength"] = urlData.Content.Length,
                }));
            }

            var offset = args.Offset ?? 0;
            var limit = args.Limit ?? maxChars;
            if (offset < 0)
            {
                return Error($"Invalid offset: received {offset} for URL {Quote(urlData.Url)}; offset must be a non-negative integer. Use 0 or a larger integer.", "Invalid offset", new JsonObject { ["offset"] = offset });
            }
            if (limit <= 0 || limit > maxChars)
            {
                return Error($"Invalid limit: received {limit} for URL {Quote(urlData.Url)}; limit must be an integer from 1 to {maxChars}. Use a value in that range.", "Invalid limit",
                    new JsonObject { ["limit"] = limit, ["maxLimit"] = maxChars });
            }
            if (offset > urlData.Content.Length)
            {
                return Error($"Offset {offset} is out of range for URL {Quote(urlData.Url)} in responseId {Quote(args.ResponseId)}; valid range is 0-{urlData.Content.Length}. Use an offset within that range.", "Offset out of range",
                    new JsonObject { ["offset"] = offset, ["contentLength"] = urlData.Content.Length });
            }

            var end = Math.Min(offset + limit, urlData.Content.Length);
            var slice = urlData.Content[offset..end];
            var hasMore = end < urlData.Content.Length;
            var text = $"{heading}\n\n{slice}";
            if (hasMore || offset > 0)
            {
                text += $"\n\n---\nShowing chars {offset}-{end} of {urlData.Content.Length}.";
                if (hasMore) text += $" Use {GetSearchContentTool}({{ responseId: \"{args.ResponseId}\", urlIndex: {selected}, offset: {end}, limit: {limit} }}) for the next slice.";
            }
            return ToolResult.Text(text, new JsonObject
            {
                ["url"] = urlData.Url,
                ["title"] = urlData.Title,
                ["contentLength"] = urlData.Content.Length,
                ["offset"] = offset,
                ["limit"] = limit,
                ["returnedChars"] = slice.Length,
                ["nextOffset"] = hasMore ? end : null,
                ["truncated"] = hasMore,
            });
        }

        return Error($"Invalid stored data for responseId {Quote(args.ResponseId)}: received type {Quote(data.Type)}. Use a responseId returned by {StoredContentSources}.", "Invalid data");
    }

    private static JsonObject FindDetails(FindResult found, string mode, JsonObject details)
    {
        details["findMode"] = mode;
        details["matchCount"] = found.MatchCount;
        details["returnedMatches"] = found.ReturnedMatches;
        details["queryResults"] = new JsonArray([.. found.QueryResults.Select(r => (JsonNode)new JsonObject { ["query"] = r.Query, ["matchCount"] = r.MatchCount })]);
        return details;
    }
}
