using System.Text;
using System.Text.Json.Nodes;
using Iris.Ai;
using Iris.Extensions;
using Iris.WebAccess.Fetch;
using Iris.WebAccess.Tools;

namespace Iris.WebAccess;

public sealed partial class WebAccessExtension
{
    private Tool<FetchContentParams> CreateFetchContentTool() => new()
    {
        Name = FetchContentTool,
        Label = "Fetch Content",
        Description =
            "Fetch URL(s). Available modes: readable (default): extract the page's readable article as Markdown; raw: return text responses (HTML source, JSON, plain text) unchanged. " +
            $"Supports GitHub repositories (cloned locally) and PDFs. Full original content is stored for retrieval with {GetSearchContentTool}.",
        PromptSnippet = "Use to fetch URL content, GitHub repos, and PDFs.",
        ExecuteAsync = ExecuteFetchContentAsync,
        RenderCall = (args, theme, _) => RenderFetchCall(args, theme),
        RenderResult = (result, options, theme, _) => RenderFetchResult(result, options, theme),
    };

    private async Task<ToolResult> ExecuteFetchContentAsync(ToolCallContext call, FetchContentParams args, ExtensionContext ctx)
    {
        var urls = NormalizeQueries(args.Urls ?? (args.Url is not null ? [args.Url] : []));
        if (urls.Count == 0) return Error("Error: No URL provided.", "No URL provided");
        var mode = args.Mode == FetchMode.Raw ? "raw" : "readable";
        if (mode == "raw" && args.ForceClone == true) return Error("Error: mode raw cannot be combined with forceClone.", "Incompatible raw mode options");

        call.Update(ToolResult.Text($"Fetching {urls.Count} URL(s)...", new JsonObject { ["phase"] = "fetch", ["progress"] = 0 }));
        var results = await ContentExtractor.FetchAllAsync(urls, new ExtractOptions
        {
            ForceClone = args.ForceClone ?? false,
            Mode = mode,
            WebSearchTool = WebSearchTool,
            FetchContentTool = FetchContentTool,
        }, call.CancellationToken);

        var responseId = ResultStore.GenerateId();
        _iris.AppendEntry(ResultStore.EntryType, _store.StoreFetch(responseId, results));

        if (urls.Count == 1)
        {
            var result = results[0];
            if (result.Error is not null)
            {
                return Error($"Error: {result.Error}", result.Error, new JsonObject
                {
                    ["urls"] = new JsonArray((JsonNode)urls[0]), ["urlCount"] = 1, ["successful"] = 0, ["responseId"] = responseId,
                });
            }

            var slice = InitialSlice(result.Content, MaxInlineChars());
            var output = new StringBuilder(slice.Text);
            var truncated = slice.EndOffset < result.Content.Length;
            if (truncated)
            {
                output.Append($"\n\n---\nShowing {slice.EndOffset} of {result.Content.Length} chars, {slice.ShownBytes} of {slice.TotalBytes} bytes, and {slice.ShownLines} of {slice.TotalLines} lines. ");
                output.Append($"Use {GetSearchContentTool}({{ responseId: \"{responseId}\", urlIndex: 0, offset: {slice.EndOffset} }}) for the next slice.");
            }
            return ToolResult.Text(output.ToString(), new JsonObject
            {
                ["urls"] = new JsonArray((JsonNode)urls[0]),
                ["urlCount"] = 1,
                ["successful"] = 1,
                ["totalChars"] = result.Content.Length,
                ["title"] = result.Title,
                ["responseId"] = responseId,
                ["truncated"] = truncated,
                ["mode"] = mode,
                ["mimeType"] = result.MimeType,
                ["status"] = result.Status,
                ["totalBytes"] = slice.TotalBytes,
                ["totalLines"] = slice.TotalLines,
                ["shownBytes"] = slice.ShownBytes,
                ["shownLines"] = slice.ShownLines,
            });
        }

        var list = new StringBuilder("## Fetched URLs\n\n");
        foreach (var result in results)
        {
            list.Append(result.Error is not null ? $"- {result.Url}: Error - {result.Error}\n" : $"- {(result.Title.Length > 0 ? result.Title : result.Url)} ({result.Content.Length} chars)\n");
        }
        list.Append($"\n---\nUse {GetSearchContentTool}({{ responseId: \"{responseId}\", urlIndex: 0 }}) to retrieve bounded content slices.");
        return ToolResult.Text(list.ToString(), new JsonObject
        {
            ["urls"] = new JsonArray([.. urls.Select(u => (JsonNode)u)]),
            ["urlCount"] = urls.Count,
            ["successful"] = results.Count(r => r.Error is null),
            ["totalChars"] = results.Sum(r => r.Content.Length),
            ["responseId"] = responseId,
        });
    }

    internal sealed record ContentSlice(string Text, int EndOffset, int TotalBytes, int TotalLines, int ShownBytes, int ShownLines);

    /// <summary>The first maxChars characters, ending at a line break when one is near the limit.</summary>
    internal static ContentSlice InitialSlice(string content, int maxChars)
    {
        var endOffset = Math.Min(content.Length, maxChars);
        if (endOffset < content.Length)
        {
            var lineBreak = content.LastIndexOf('\n', endOffset);
            if (lineBreak >= (int)Math.Floor(maxChars * 0.8)) endOffset = lineBreak + 1;
        }
        var text = content[..endOffset];
        static int Lines(string value) => value.Length == 0 ? 0 : value.Count(c => c == '\n') + 1;
        return new ContentSlice(text, endOffset, Encoding.UTF8.GetByteCount(content), Lines(content), Encoding.UTF8.GetByteCount(text), Lines(text));
    }

    private static string FirstText(Iris.Agent.AgentToolResult result) => result.Content.OfType<TextContent>().FirstOrDefault()?.Text ?? "";
}
