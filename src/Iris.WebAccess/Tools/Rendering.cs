using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Iris.Agent;
using Iris.CodingAgent.Modes.Interactive;
using Iris.CodingAgent.Modes.Interactive.Components;
using Iris.Tui;
using Iris.Tui.Components;

namespace Iris.WebAccess;

/// <summary>Transcript rendering for the web access tools.</summary>
public sealed partial class WebAccessExtension
{
    [GeneratedRegex(@"^https?://")]
    private static partial Regex SchemePrefix();

    private static string Ellipsize(string text, int max) => text.Length > max ? $"{text[..(max - 3)]}..." : text;

    private static string? DetailString(JsonNode? details, string key) => details?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static int? DetailInt(JsonNode? details, string key) => Number(details?[key]) is { } d ? (int)d : null;

    /// <summary>A numeric value, whether the node was built from an int, a double or parsed JSON.</summary>
    private static double? Number(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == System.Text.Json.JsonValueKind.Number
        && double.TryParse(value.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number)
            ? number
            : null;

    private static bool DetailBool(JsonNode? details, string key) => details?[key] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    private static string ProgressBar(double progress)
    {
        var filled = Math.Clamp((int)Math.Floor(progress * 10), 0, 10);
        return new string('█', filled) + new string('░', 10 - filled);
    }

    private static IComponent RenderPartial(AgentToolResult result, Theme theme, string fallbackPhase)
    {
        var details = result.Details;
        var progress = Number(details?["progress"]) ?? 0;
        var label = DetailString(details, "currentQuery") is { Length: > 0 } query ? Ellipsize(query, 40) : DetailString(details, "phase") ?? fallbackPhase;
        return new Text(theme.Fg("accent", $"[{ProgressBar(progress)}] {label}"), 0, 0);
    }

    private static IComponent RenderError(AgentToolResult result, ToolRenderResultOptions options, Theme theme, IEnumerable<string>? extraLines = null)
    {
        var error = DetailString(result.Details, "error") ?? "unknown error";
        var text = FirstText(result);
        var lines = new List<string> { theme.Fg("error", $"Error: {error}") };
        if (options.Expanded)
        {
            lines.AddRange((extraLines ?? []).Select(l => theme.Fg("muted", l)));
            if (text.Length > 0 && !text.Equals($"Error: {error}", StringComparison.Ordinal)) lines.Add(theme.Fg("dim", Ellipsize(text, 1000)));
        }
        return new Text(string.Join("\n", lines), 0, 0);
    }

    // ----- web_search -----

    private static IComponent RenderSearchCall(JsonObject args, Theme theme)
    {
        var queries = args["queries"] is JsonArray array
            ? NormalizeQueries(array.Select(q => q is JsonValue v && v.TryGetValue<string>(out var s) ? s : ""))
            : args["query"] is JsonValue query && query.TryGetValue<string>(out var single) ? ExpandQueryString(single) : [];
        var title = theme.Fg("toolTitle", theme.Bold("search "));
        if (queries.Count == 0) return new Text(title + theme.Fg("error", "(no query)"), 0, 0);
        if (queries.Count == 1) return new Text(title + theme.Fg("accent", $"\"{Ellipsize(queries[0], 60)}\""), 0, 0);
        var lines = new List<string> { title + theme.Fg("accent", $"{queries.Count} queries") };
        lines.AddRange(queries.Take(5).Select(q => theme.Fg("muted", $"  \"{Ellipsize(q, 50)}\"")));
        if (queries.Count > 5) lines.Add(theme.Fg("muted", $"  ... and {queries.Count - 5} more"));
        return new Text(string.Join("\n", lines), 0, 0);
    }

    private static IComponent RenderSearchResult(AgentToolResult result, ToolRenderResultOptions options, Theme theme)
    {
        if (options.IsPartial) return RenderPartial(result, theme, "searching");
        var details = result.Details;
        if (DetailString(details, "error") is not null) return RenderError(result, options, theme);

        var queryCount = DetailInt(details, "queryCount") ?? 1;
        var queryInfo = queryCount == 1 ? "" : $"{DetailInt(details, "successfulQueries") ?? 0}/{queryCount} queries, ";
        var statusLine = theme.Fg("success", $"{queryInfo}{DetailInt(details, "totalResults") ?? 0} sources");
        if (DetailBool(details, "curated") && DetailInt(details, "curatedFrom") is { } curatedFrom)
        {
            statusLine += theme.Fg("muted", $" ({queryCount}/{curatedFrom} queries curated)");
        }
        var fetchUrls = details?["fetchUrls"] as JsonArray;
        if (DetailString(details, "fetchId") is not null)
        {
            statusLine += theme.Fg("muted", fetchUrls is not null ? $" (fetching {fetchUrls.Count} URLs)" : " (content ready)");
        }

        var text = FirstText(result);
        var summary = details?["summary"];
        var summaryText = DetailString(summary, "text")?.Trim() ?? "";
        var curatedQueries = details?["curatedQueries"] as JsonArray;
        if (!options.Expanded)
        {
            var box = new Box(1, 0);
            box.AddChild(new Text(statusLine, 0, 0));
            if (summaryText.Length > 0)
            {
                box.AddChild(new Text(theme.Fg("dim", Ellipsize(summaryText.Split('\n')[0], 120)), 0, 0));
            }
            else if (curatedQueries is { Count: > 0 })
            {
                foreach (var query in curatedQueries.Take(3))
                {
                    var sources = query?["sources"] as JsonArray;
                    var suffix = DetailString(query, "error") is not null ? theme.Fg("error", " (error)") : theme.Fg("dim", $" · {sources?.Count ?? 0} sources");
                    box.AddChild(new Text(theme.Fg("accent", $"  \"{Ellipsize(DetailString(query, "query") ?? "", 55)}\"") + suffix, 0, 0));
                }
                if (curatedQueries.Count > 3) box.AddChild(new Text(theme.Fg("dim", $"  ... and {curatedQueries.Count - 3} more"), 0, 0));
            }
            else
            {
                var firstLine = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith('[') && !l.StartsWith('#') && !l.StartsWith("---", StringComparison.Ordinal));
                if (firstLine is not null) box.AddChild(new Text(theme.Fg("dim", Ellipsize(firstLine.Replace("**", "", StringComparison.Ordinal), 120)), 0, 0));
            }
            box.AddChild(new Text(theme.Fg("muted", $"... ({KeyHints.KeyText("app.tools.expand")} to expand)"), 0, 0));
            return box;
        }

        var lines = new List<string> { statusLine };
        if (summaryText.Length > 0)
        {
            lines.Add("");
            lines.Add(theme.Fg("accent", $"── Summary ({DetailString(summary, "workflow") ?? "summary-review"}) " + new string('─', 32)));
            lines.Add("");
            lines.AddRange(summaryText.Split('\n').Select(line => $"  {line}"));
            lines.Add("");
            var meta = new List<string>
            {
                DetailString(summary, "model") is { } model ? $"model={model}" : "model=deterministic",
                $"duration={DetailInt(summary, "durationMs") ?? 0}ms",
                $"tokens~{DetailInt(summary, "tokenEstimate") ?? 0}",
                $"fallback={(DetailBool(summary, "fallbackUsed") ? "true" : "false")}",
                $"edited={(DetailBool(summary, "edited") ? "true" : "false")}",
            };
            if (DetailString(summary, "fallbackReason") is { } reason) meta.Add($"reason={reason}");
            lines.Add(theme.Fg("dim", "  " + string.Join(" · ", meta)));
        }
        if (curatedQueries is { Count: > 0 })
        {
            lines.Add("");
            lines.Add(theme.Fg("accent", $"── Curated results ({curatedQueries.Count} of {DetailInt(details, "curatedFrom") ?? curatedQueries.Count} queries kept) " + new string('─', 20)));
            foreach (var query in curatedQueries)
            {
                lines.Add("");
                var provider = DetailString(query, "provider") is { } name ? $" ({name})" : "";
                lines.Add(theme.Fg("accent", $"  \"{Ellipsize(DetailString(query, "query") ?? "", 65)}\"{provider}"));
                if (DetailString(query, "error") is { } error) lines.Add(theme.Fg("error", $"  {error}"));
                foreach (var source in (query?["sources"] as JsonArray) ?? [])
                {
                    var url = DetailString(source, "url") ?? "";
                    var domain = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;
                    lines.Add(theme.Fg("muted", $"  ▸ {Ellipsize(DetailString(source, "title") ?? "", 50)}") + theme.Fg("dim", $" · {domain}"));
                }
            }
        }
        else
        {
            lines.AddRange(Ellipsize(text, 500).Split('\n').Select(l => theme.Fg("dim", l)));
        }
        if (fetchUrls is { Count: > 0 })
        {
            lines.Add(theme.Fg("muted", "Fetching:"));
            lines.AddRange(fetchUrls.Take(5).Select(u => theme.Fg("dim", $"  {Ellipsize(u?.GetValue<string>() ?? "", 60)}")));
            if (fetchUrls.Count > 5) lines.Add(theme.Fg("dim", $"  ... and {fetchUrls.Count - 5} more"));
        }
        return new Text(string.Join("\n", lines), 0, 0);
    }

    // ----- fetch_content -----

    private static IComponent RenderFetchCall(JsonObject args, Theme theme)
    {
        var urls = args["urls"] is JsonArray array
            ? NormalizeQueries(array.Select(u => u is JsonValue v && v.TryGetValue<string>(out var s) ? s : ""))
            : args["url"] is JsonValue url && url.TryGetValue<string>(out var single) ? NormalizeQueries([single]) : [];
        var title = theme.Fg("toolTitle", theme.Bold("fetch "));
        if (urls.Count == 0) return new Text(title + theme.Fg("error", "(no URL)"), 0, 0);
        var lines = new List<string>();
        if (urls.Count == 1)
        {
            lines.Add(title + theme.Fg("accent", Ellipsize(urls[0], 60)));
        }
        else
        {
            lines.Add(title + theme.Fg("accent", $"{urls.Count} URLs"));
            lines.AddRange(urls.Take(5).Select(u => theme.Fg("muted", $"  {Ellipsize(u, 60)}")));
            if (urls.Count > 5) lines.Add(theme.Fg("muted", $"  ... and {urls.Count - 5} more"));
        }
        if (args["mode"] is JsonValue mode && mode.TryGetValue<string>(out var modeText) && modeText != "readable")
        {
            lines.Add(theme.Fg("dim", "  mode: ") + theme.Fg("warning", modeText));
        }
        return new Text(string.Join("\n", lines), 0, 0);
    }

    private static IComponent RenderFetchResult(AgentToolResult result, ToolRenderResultOptions options, Theme theme)
    {
        if (options.IsPartial) return RenderPartial(result, theme, "fetching");
        var details = result.Details;
        if (DetailString(details, "error") is not null)
        {
            var extras = new List<string> { $"urls: {DetailInt(details, "successful") ?? 0}/{DetailInt(details, "urlCount") ?? 0} succeeded" };
            if (DetailString(details, "responseId") is { } id) extras.Add($"response id: {id}");
            return RenderError(result, options, theme, extras);
        }

        var text = FirstText(result);
        if (DetailInt(details, "urlCount") == 1)
        {
            var title = DetailString(details, "title") is { Length: > 0 } t ? t : "Untitled";
            var statusLine = theme.Fg("success", title) + theme.Fg("muted", $" ({DetailInt(details, "totalChars") ?? 0} chars)");
            if (DetailBool(details, "truncated")) statusLine += theme.Fg("warning", " [truncated]");
            var preview = options.Expanded ? Ellipsize(text, 500) : Ellipsize(text, 200);
            return new Text($"{statusLine}\n{theme.Fg("dim", preview)}", 0, 0);
        }

        var successful = DetailInt(details, "successful") ?? 0;
        var summary = theme.Fg(successful > 0 ? "success" : "error", $"{successful}/{DetailInt(details, "urlCount") ?? 0} URLs") + theme.Fg("muted", " (content stored)");
        return new Text(options.Expanded ? $"{summary}\n{theme.Fg("dim", Ellipsize(text, 500))}" : summary, 0, 0);
    }

    // ----- get_search_content -----

    private static IComponent RenderGetContentCall(JsonObject args, Theme theme)
    {
        string? Str(string key) => args[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        int? Int(string key) => Number(args[key]) is { } d ? (int)d : null;
        var target = Str("query") is { Length: > 0 } query ? $"query=\"{query}\""
            : Int("queryIndex") is { } queryIndex ? $"queryIndex={queryIndex}"
            : Str("url") is { Length: > 0 } url ? Ellipsize(SchemePrefix().Replace(url, ""), 30)
            : Int("urlIndex") is { } urlIndex ? $"urlIndex={urlIndex}"
            : "";
        if (Int("offset") is { } offset) target += target.Length > 0 ? $" @ {offset}" : $"offset={offset}";
        if (args["findText"] is { } findText) target += $"{(target.Length > 0 ? " · " : "")}find {(findText is JsonArray list ? list.Count : 1)}";
        var responseId = Str("responseId") ?? "";
        return new Text(theme.Fg("toolTitle", theme.Bold("get_content ")) + theme.Fg("accent", target.Length > 0 ? target : responseId[..Math.Min(8, responseId.Length)]), 0, 0);
    }

    private static IComponent RenderGetContentResult(AgentToolResult result, ToolRenderResultOptions options, Theme theme)
    {
        var details = result.Details;
        if (DetailString(details, "error") is not null)
        {
            var extras = new List<string>();
            if (DetailString(details, "query") is { } query) extras.Add($"query: {query}");
            if (DetailString(details, "url") is { } url) extras.Add($"url: {url}");
            return RenderError(result, options, theme, extras);
        }

        string statusLine;
        var name = DetailString(details, "title") is { Length: > 0 } title ? title : null;
        if (DetailInt(details, "matchCount") is { } matches)
        {
            statusLine = theme.Fg("success", name ?? DetailString(details, "query") ?? "Content") + theme.Fg("muted", $" ({matches} matches, {DetailInt(details, "returnedMatches") ?? 0} shown)");
        }
        else if (DetailString(details, "query") is { } query)
        {
            statusLine = theme.Fg("success", $"\"{query}\"") + theme.Fg("muted", $" ({DetailInt(details, "resultCount") ?? 0} results)");
        }
        else
        {
            var start = DetailInt(details, "offset") ?? 0;
            var returned = DetailInt(details, "returnedChars") ?? DetailInt(details, "contentLength") ?? 0;
            var slice = details?["nextOffset"] is not null || start > 0 || DetailBool(details, "truncated") ? $", showing {start}-{start + returned}" : "";
            statusLine = theme.Fg("success", name ?? "Content") + theme.Fg("muted", $" ({DetailInt(details, "contentLength") ?? 0} chars{slice})");
        }
        return new Text(options.Expanded ? $"{statusLine}\n{theme.Fg("dim", Ellipsize(FirstText(result), 500))}" : statusLine, 0, 0);
    }
}
