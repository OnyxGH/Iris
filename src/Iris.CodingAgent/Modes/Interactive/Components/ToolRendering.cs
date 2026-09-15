using System.Text.Json;
using System.Text.Json.Nodes;
using Iris.Agent;
using Iris.Ai;
using Iris.Ai.Json;
using Iris.CodingAgent.Config;
using Iris.CodingAgent.Core.Tools;
using Iris.CodingAgent.Utils;
using Iris.Tui;
using Iris.Tui.Components;

namespace Iris.CodingAgent.Modes.Interactive.Components;

/// <summary>Mutable per-tool-call renderer state (pi's context.state).</summary>
public sealed class ToolRenderState
{
    public long? StartedAt { get; set; }
    public long? EndedAt { get; set; }
    public IDisposable? Interval { get; set; }
    public object? CallComponent { get; set; }
    public Dictionary<string, object?> Values { get; } = [];
}

public sealed class ToolRenderContext
{
    public required JsonObject Args { get; init; }
    public required string ToolCallId { get; init; }
    public required Action Invalidate { get; init; }
    public IComponent? LastComponent { get; init; }
    public required ToolRenderState State { get; init; }
    public required string Cwd { get; init; }
    public bool ExecutionStarted { get; init; }
    public bool ArgsComplete { get; init; }
    public bool IsPartial { get; init; }
    public bool Expanded { get; init; }
    public bool ShowImages { get; init; }
    public bool IsError { get; init; }
}

public readonly record struct ToolRenderResultOptions(bool Expanded, bool IsPartial);

public delegate IComponent ToolRenderCall(JsonObject args, Theme theme, ToolRenderContext context);

public delegate IComponent ToolRenderResult(AgentToolResult result, ToolRenderResultOptions options, Theme theme, ToolRenderContext context);

/// <summary>How a tool draws its call and result (renderShell / renderCall / renderResult).</summary>
public sealed class ToolRenderers
{
    public string? RenderShell { get; init; }
    public ToolRenderCall? RenderCall { get; init; }
    public ToolRenderResult? RenderResult { get; init; }
}

/// <summary>Shared tool rendering helpers. Port of core/tools/render-utils.ts.</summary>
public static class RenderUtils
{
    public static string ShortenPath(string? path)
    {
        if (path is null) return "";
        var home = AppConfig.HomeDir;
        return path.StartsWith(home, StringComparison.Ordinal) ? "~" + path[home.Length..] : path;
    }

    public static string LinkPath(string styledText, string rawPath, string cwd)
    {
        if (!TerminalImage.GetCapabilities().Hyperlinks) return styledText;
        var absolute = PathUtils.ResolvePath(rawPath, cwd);
        return TerminalImage.Hyperlink(styledText, new Uri(absolute).AbsoluteUri);
    }

    /// <summary>String arg: the string, "" for null/undefined, null for other types (invalid).</summary>
    public static string? Str(JsonNode? value)
    {
        if (value is null) return "";
        return value is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    }

    public static string ReplaceTabs(string text) => text.Replace("\t", "   ");

    public static string NormalizeDisplayText(string text) => text.Replace("\r", "");

    public static string GetTextOutput(IReadOnlyList<ContentBlock>? content, bool showImages)
    {
        if (content is null) return "";
        var output = string.Join("\n", content.OfType<TextContent>().Select(c => ShellUtils.SanitizeBinaryOutput(AnsiUtils.StripAnsi(c.Text)).Replace("\r", "")));
        var images = content.OfType<ImageContent>().ToList();
        var caps = TerminalImage.GetCapabilities();
        if (images.Count > 0 && (caps.Images is null || !showImages))
        {
            var indicators = string.Join("\n", images.Select(img =>
            {
                var mime = string.IsNullOrEmpty(img.MimeType) ? "image/unknown" : img.MimeType;
                var dims = !string.IsNullOrEmpty(img.Data) && !string.IsNullOrEmpty(img.MimeType) ? TerminalImage.GetImageDimensions(img.Data, img.MimeType) : null;
                return TerminalImage.ImageFallback(mime, dims);
            }));
            output = output.Length > 0 ? $"{output}\n{indicators}" : indicators;
        }
        return output;
    }

    public static string InvalidArgText(Theme theme) => theme.Fg("error", "[invalid arg]");

    public static string RenderToolPath(string? rawPath, Theme theme, string cwd, string? emptyFallback = null)
    {
        if (rawPath is null) return InvalidArgText(theme);
        var value = rawPath.Length > 0 ? rawPath : emptyFallback;
        if (string.IsNullOrEmpty(value)) return theme.Fg("toolOutput", "...");
        return LinkPath(theme.Fg("accent", ShortenPath(value)), value, cwd);
    }

    internal static long? GetLong(JsonNode? node) => PiJson.GetLong(node);

    internal static bool IsNumber(JsonNode? node) => node is JsonValue v && v.TryGetValue<double>(out _);

    internal static string NumberText(JsonNode? node) => node is JsonValue v && v.TryGetValue<double>(out var d) ? NodeCompat.FormatNumber(d) : "";

    internal static List<string> TrimTrailingEmptyLines(List<string> lines)
    {
        var end = lines.Count;
        while (end > 0 && lines[end - 1] == "") end--;
        return lines.Take(end).ToList();
    }

    internal static string MoreLinesHint(int remaining, Theme theme, string? totalSuffix = null) =>
        $"{theme.Fg("muted", $"\n... ({remaining} more lines{totalSuffix},")} {KeyHints.KeyHint("app.tools.expand", "to expand")}{theme.Fg("muted", ")")}";
}

/// <summary>Built-in tool renderers. Port of core/tools/renderers/*.ts.</summary>
public static class BuiltInToolRenderers
{
    private static Text TextFor(ToolRenderContext context) => context.LastComponent as Text ?? new Text("", 0, 0);

    public static ToolRenderers? Get(string toolName) => toolName switch
    {
        "read" => Read,
        "bash" => CreateShell("$"),
        "powershell" => CreateShell("PS>"),
        "edit" => Edit,
        "write" => Write,
        "grep" => Grep,
        "find" => Find,
        "ls" => Ls,
        _ => null,
    };

    public static ToolRenderers? WithBuiltInRenderers(string toolName, ToolRenderers? definition)
    {
        var builtIn = Get(toolName);
        if (definition is null) return builtIn;
        if (builtIn is null) return definition;
        return new ToolRenderers
        {
            RenderShell = definition.RenderShell,
            RenderCall = definition.RenderCall ?? builtIn.RenderCall,
            RenderResult = definition.RenderResult ?? builtIn.RenderResult,
        };
    }

    // ----- read -----

    private static readonly HashSet<string> CompactResourceFileNames = ["AGENTS.override.md", "AGENTS.md", "AGENTS.MD", "CLAUDE.md", "CLAUDE.MD"];

    private static string FormatReadLineRange(JsonObject args, Theme theme)
    {
        var offset = args["offset"];
        var limit = args["limit"];
        if (offset is null && limit is null) return "";
        var startLine = offset is not null ? RenderUtils.NumberText(offset) : "1";
        var endLine = limit is not null && double.TryParse(startLine, System.Globalization.CultureInfo.InvariantCulture, out var s) && limit is JsonValue lv && lv.TryGetValue<double>(out var l)
            ? NodeCompat.FormatNumber(s + l - 1)
            : "";
        return theme.Fg("warning", $":{startLine}{(endLine.Length > 0 ? $"-{endLine}" : "")}");
    }

    private static (string Kind, string Label)? GetCompactReadClassification(JsonObject args, string cwd)
    {
        var rawPath = RenderUtils.Str(args["file_path"] ?? args["path"]);
        if (string.IsNullOrEmpty(rawPath)) return null;
        var absolute = ToolPaths.ResolveToCwd(rawPath, cwd);
        var fileName = Path.GetFileName(absolute);
        if (fileName == "SKILL.md")
        {
            var dirName = Path.GetFileName(Path.GetDirectoryName(absolute) ?? "");
            return ("skill", dirName.Length > 0 ? dirName : fileName);
        }
        var packageRoot = Path.GetDirectoryName(AppConfig.ReadmePath)!;
        var relative = Path.GetRelativePath(Path.GetFullPath(packageRoot), Path.GetFullPath(absolute));
        if (relative != "." && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative))
        {
            var label = relative.Replace(Path.DirectorySeparatorChar, '/');
            if (label == "README.md" || label.StartsWith("docs/", StringComparison.Ordinal) || label.StartsWith("examples/", StringComparison.Ordinal)) return ("docs", label);
        }
        if (CompactResourceFileNames.Contains(fileName)) return ("resource", PathUtils.FormatPathRelativeToCwdOrAbsolute(absolute, cwd));
        return null;
    }

    public static readonly ToolRenderers Read = new()
    {
        RenderCall = (args, theme, context) =>
        {
            var text = TextFor(context);
            var classification = !context.Expanded ? GetCompactReadClassification(args, context.Cwd) : null;
            if (classification is { } c)
            {
                var expandHint = theme.Fg("dim", $" ({KeyHints.KeyText("app.tools.expand")} to expand)");
                text.SetText(c.Kind == "skill"
                    ? theme.Fg("customMessageLabel", "\e[1m[skill]\e[22m ") + theme.Fg("customMessageText", c.Label) + FormatReadLineRange(args, theme) + expandHint
                    : theme.Fg("toolTitle", theme.Bold($"read {c.Kind}")) + " " + theme.Fg("accent", c.Label) + FormatReadLineRange(args, theme) + expandHint);
            }
            else
            {
                var pathDisplay = RenderUtils.RenderToolPath(RenderUtils.Str(args["file_path"] ?? args["path"]), theme, context.Cwd);
                text.SetText($"{theme.Fg("toolTitle", theme.Bold("read"))} {pathDisplay}{FormatReadLineRange(args, theme)}");
            }
            return text;
        },
        RenderResult = (result, options, theme, context) =>
        {
            var text = TextFor(context);
            if (!options.Expanded && !context.IsError)
            {
                text.SetText("");
                return text;
            }
            var rawPath = RenderUtils.Str(context.Args["file_path"] ?? context.Args["path"]);
            var output = RenderUtils.GetTextOutput(result.Content, context.ShowImages);
            var lang = !context.IsError && !string.IsNullOrEmpty(rawPath) ? ThemeManager.GetLanguageFromPath(rawPath) : null;
            var rendered = lang is not null ? ThemeManager.HighlightCode(RenderUtils.ReplaceTabs(output), lang) : output.Split('\n').ToList();
            var lines = RenderUtils.TrimTrailingEmptyLines(rendered);
            var maxLines = options.Expanded ? lines.Count : 10;
            var display = lines.Take(maxLines);
            var remaining = lines.Count - maxLines;
            var body = "\n" + string.Join("\n", display.Select(line => lang is not null ? RenderUtils.ReplaceTabs(line) : theme.Fg("toolOutput", RenderUtils.ReplaceTabs(line))));
            if (remaining > 0) body += RenderUtils.MoreLinesHint(remaining, theme);

            if (result.Details?["truncation"] is JsonObject truncation && PiJson.GetBool(truncation["truncated"]) == true)
            {
                var maxBytes = PiJson.GetLong(truncation["maxBytes"]) ?? Truncate.DefaultMaxBytes;
                if (PiJson.GetBool(truncation["firstLineExceedsLimit"]) == true)
                {
                    body += "\n" + theme.Fg("warning", $"[First line exceeds {Truncate.FormatSize(maxBytes)} limit]");
                }
                else if (PiJson.GetString(truncation["truncatedBy"]) == "lines")
                {
                    body += "\n" + theme.Fg("warning", $"[Truncated: showing {RenderUtils.NumberText(truncation["outputLines"])} of {RenderUtils.NumberText(truncation["totalLines"])} lines ({(truncation["maxLines"] is { } ml ? RenderUtils.NumberText(ml) : Truncate.DefaultMaxLines.ToString())} line limit)]");
                }
                else
                {
                    body += "\n" + theme.Fg("warning", $"[Truncated: {RenderUtils.NumberText(truncation["outputLines"])} lines shown ({Truncate.FormatSize(maxBytes)} limit)]");
                }
            }
            text.SetText(body);
            return text;
        },
    };

    // ----- shell -----

    private sealed class BashResultRenderComponent : Container
    {
        public int? CachedWidth;
        public List<string>? CachedLines;
        public int? CachedSkipped;
    }

    private static string FormatDuration(long ms) => NodeCompat.ToFixed(ms / 1000.0, 1) + "s";

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public static ToolRenderers CreateShell(string prompt) => new()
    {
        RenderCall = (args, theme, context) =>
        {
            var state = context.State;
            if (context.ExecutionStarted && state.StartedAt is null)
            {
                state.StartedAt = NowMs();
                state.EndedAt = null;
            }
            var command = RenderUtils.Str(args["command"]);
            var timeout = args["timeout"];
            var timeoutSuffix = timeout is not null && RenderUtils.IsNumber(timeout) && timeout.GetValue<double>() != 0 ? theme.Fg("muted", $" (timeout {RenderUtils.NumberText(timeout)}s)") : "";
            var commandDisplay = command is null ? RenderUtils.InvalidArgText(theme) : command.Length > 0 ? command : theme.Fg("toolOutput", "...");
            var text = TextFor(context);
            text.SetText(theme.Fg("toolTitle", theme.Bold($"{prompt} {commandDisplay}")) + timeoutSuffix);
            return text;
        },
        RenderResult = (result, options, theme, context) =>
        {
            var state = context.State;
            if (state.StartedAt is not null && options.IsPartial && state.Interval is null)
            {
                state.Interval = (UiDispatcher.Current)?.SetInterval(context.Invalidate, 1000);
            }
            if (!options.IsPartial || context.IsError)
            {
                state.EndedAt ??= NowMs();
                state.Interval?.Dispose();
                state.Interval = null;
            }
            var component = context.LastComponent as BashResultRenderComponent ?? new BashResultRenderComponent();
            RebuildBashResult(component, result, options, context.ShowImages, state.StartedAt, state.EndedAt, theme);
            component.Invalidate();
            return component;
        },
    };

    private static void RebuildBashResult(BashResultRenderComponent component, AgentToolResult result, ToolRenderResultOptions options, bool showImages, long? startedAt, long? endedAt, Theme theme)
    {
        component.Clear();
        var output = RenderUtils.GetTextOutput(result.Content, showImages).Trim();
        var truncation = result.Details?["truncation"] as JsonObject;
        var truncated = PiJson.GetBool(truncation?["truncated"]) == true;
        var fullOutputPath = PiJson.GetString(result.Details?["fullOutputPath"]);
        if (!options.IsPartial && truncated && fullOutputPath is not null && output.EndsWith(']'))
        {
            var footerStart = output.LastIndexOf("\n\n[", StringComparison.Ordinal);
            if (footerStart != -1 && output[footerStart..].Contains(fullOutputPath)) output = output[..footerStart].TrimEnd();
        }

        if (output.Length > 0)
        {
            var styled = string.Join("\n", output.Split('\n').Select(line => theme.Fg("toolOutput", line)));
            if (options.Expanded)
            {
                component.AddChild(new Text("\n" + styled, 0, 0));
            }
            else
            {
                component.AddChild(new DelegateComponent(width =>
                {
                    if (component.CachedLines is null || component.CachedWidth != width)
                    {
                        var preview = VisualTruncate.TruncateToVisualLines(styled, 5, width);
                        component.CachedLines = preview.VisualLines;
                        component.CachedSkipped = preview.SkippedCount;
                        component.CachedWidth = width;
                    }
                    if (component.CachedSkipped is > 0)
                    {
                        var hint = ThemeManager.Current.Fg("muted", $"... ({component.CachedSkipped} earlier lines,") + $" {KeyHints.KeyHint("app.tools.expand", "to expand")}{ThemeManager.Current.Fg("muted", ")")}";
                        return ["", TextUtils.TruncateToWidth(hint, width, "..."), .. component.CachedLines];
                    }
                    return ["", .. component.CachedLines];
                }, () =>
                {
                    component.CachedWidth = null;
                    component.CachedLines = null;
                    component.CachedSkipped = null;
                }));
            }
        }

        if (truncated || fullOutputPath is not null)
        {
            var warnings = new List<string>();
            if (fullOutputPath is not null) warnings.Add($"Full output: {fullOutputPath}");
            if (truncated)
            {
                warnings.Add(PiJson.GetString(truncation?["truncatedBy"]) == "lines"
                    ? $"Truncated: showing {RenderUtils.NumberText(truncation?["outputLines"])} of {RenderUtils.NumberText(truncation?["totalLines"])} lines"
                    : $"Truncated: {RenderUtils.NumberText(truncation?["outputLines"])} lines shown ({Truncate.FormatSize(PiJson.GetLong(truncation?["maxBytes"]) ?? Truncate.DefaultMaxBytes)} limit)");
            }
            component.AddChild(new Text("\n" + theme.Fg("warning", $"[{string.Join(". ", warnings)}]"), 0, 0));
        }

        if (startedAt is { } started)
        {
            var label = options.IsPartial ? "Elapsed" : "Took";
            component.AddChild(new Text("\n" + theme.Fg("muted", $"{label} {FormatDuration((endedAt ?? NowMs()) - started)}"), 0, 0));
        }
    }

    // ----- edit -----

    private sealed class EditCallRenderComponent() : Box(1, 1, t => t)
    {
        public (string? Diff, int? FirstChangedLine, string? Error)? Preview;
        public string? PreviewArgsKey;
        public bool PreviewPending;
        public bool SettledError;
    }

    private static EditCallRenderComponent GetEditCallComponent(ToolRenderState state, IComponent? lastComponent)
    {
        if (lastComponent is EditCallRenderComponent last)
        {
            state.CallComponent = last;
            return last;
        }
        if (state.CallComponent is EditCallRenderComponent existing) return existing;
        var created = new EditCallRenderComponent();
        state.CallComponent = created;
        return created;
    }

    private static (string Path, List<EditReplacement> Edits)? GetRenderablePreviewInput(JsonObject args)
    {
        var path = args["path"] is JsonValue pv && pv.TryGetValue<string>(out var p) ? p : args["file_path"] is JsonValue fv && fv.TryGetValue<string>(out var fp) ? fp : null;
        if (string.IsNullOrEmpty(path)) return null;
        if (args["edits"] is JsonArray edits && edits.Count > 0 && edits.All(e => e is JsonObject o && o["oldText"] is JsonValue ov && ov.TryGetValue<string>(out _) && o["newText"] is JsonValue nv && nv.TryGetValue<string>(out _)))
        {
            return (path, edits.Select(e => new EditReplacement(e!["oldText"]!.GetValue<string>(), e["newText"]!.GetValue<string>())).ToList());
        }
        if (args["oldText"] is JsonValue o2 && o2.TryGetValue<string>(out var oldText) && args["newText"] is JsonValue n2 && n2.TryGetValue<string>(out var newText))
        {
            return (path, [new EditReplacement(oldText, newText)]);
        }
        return null;
    }

    private static string? ArgsKey((string Path, List<EditReplacement> Edits)? input)
    {
        if (input is not { } i) return null;
        var obj = new JsonObject
        {
            ["path"] = i.Path,
            ["edits"] = new JsonArray(i.Edits.Select(e => (JsonNode)new JsonObject { ["oldText"] = e.OldText, ["newText"] = e.NewText }).ToArray()),
        };
        return obj.ToJsonString();
    }

    private static Func<string, string> EditHeaderBg(EditCallRenderComponent component, Theme theme)
    {
        if (component.Preview is { } preview) return preview.Error is not null ? t => theme.Bg("toolErrorBg", t) : t => theme.Bg("toolSuccessBg", t);
        return component.SettledError ? t => theme.Bg("toolErrorBg", t) : t => theme.Bg("toolPendingBg", t);
    }

    private static EditCallRenderComponent BuildEditCall(EditCallRenderComponent component, JsonObject args, Theme theme, string cwd)
    {
        component.SetBg(EditHeaderBg(component, theme));
        component.Clear();
        var pathDisplay = RenderUtils.RenderToolPath(RenderUtils.Str(args["file_path"] ?? args["path"]), theme, cwd);
        component.AddChild(new Text($"{theme.Fg("toolTitle", theme.Bold("edit"))} {pathDisplay}", 0, 0));
        if (component.Preview is not { } preview) return component;
        var body = preview.Error is not null ? theme.Fg("error", preview.Error) : DiffRenderer.RenderDiff(preview.Diff ?? "");
        component.AddChild(new Spacer(1));
        component.AddChild(new Text(body, 0, 0));
        return component;
    }

    private static bool SetEditPreview(EditCallRenderComponent component, (string? Diff, int? FirstChangedLine, string? Error) preview, string? argsKey)
    {
        var current = component.Preview;
        var changed = current is null
            || (current.Value.Error is not null && preview.Error is not null ? current.Value.Error != preview.Error : (current.Value.Error is not null) != (preview.Error is not null))
            || (current.Value.Error is null && preview.Error is null && (current.Value.Diff != preview.Diff || current.Value.FirstChangedLine != preview.FirstChangedLine));
        component.Preview = preview;
        component.PreviewArgsKey = argsKey;
        component.PreviewPending = false;
        return changed;
    }

    public static readonly ToolRenderers Edit = new()
    {
        RenderCall = (args, theme, context) =>
        {
            var component = GetEditCallComponent(context.State, context.LastComponent);
            var previewInput = GetRenderablePreviewInput(args);
            var argsKey = ArgsKey(previewInput);
            if (component.PreviewArgsKey != argsKey)
            {
                component.Preview = null;
                component.PreviewArgsKey = argsKey;
                component.PreviewPending = false;
                component.SettledError = false;
            }
            if (context.ArgsComplete && previewInput is { } input && component.Preview is null && !component.PreviewPending)
            {
                component.PreviewPending = true;
                var requestKey = argsKey;
                _ = RunPreviewAsync();

                async Task RunPreviewAsync()
                {
                    var (result, error) = await EditDiff.ComputeEditsDiffAsync(input.Path, input.Edits, context.Cwd);
                    if (component.PreviewArgsKey != requestKey) return;
                    SetEditPreview(component, error is not null ? (null, null, error) : (result!.Diff, result.FirstChangedLine, null), requestKey);
                    context.Invalidate();
                }
            }
            return BuildEditCall(component, args, theme, context.Cwd);
        },
        RenderResult = (result, _, theme, context) =>
        {
            var callComponent = context.State.CallComponent as EditCallRenderComponent;
            var argsKey = ArgsKey(GetRenderablePreviewInput(context.Args));
            var resultDiff = !context.IsError ? PiJson.GetString(result.Details?["diff"]) : null;
            var changed = false;
            if (callComponent is not null)
            {
                if (resultDiff is not null)
                {
                    var firstChanged = PiJson.GetLong(result.Details?["firstChangedLine"]) is { } fcl ? (int?)fcl : null;
                    changed = SetEditPreview(callComponent, (resultDiff, firstChanged, null), argsKey) || changed;
                }
                if (callComponent.SettledError != context.IsError)
                {
                    callComponent.SettledError = context.IsError;
                    changed = true;
                }
                if (changed) BuildEditCall(callComponent, context.Args, theme, context.Cwd);
            }

            string? output = null;
            var previewDiff = callComponent?.Preview is { Error: null } p ? p.Diff : null;
            var previewError = callComponent?.Preview?.Error;
            if (context.IsError)
            {
                var errorText = string.Join("\n", result.Content.OfType<TextContent>().Select(c => c.Text));
                if (errorText.Length > 0 && errorText != previewError) output = theme.Fg("error", errorText);
            }
            else if (PiJson.GetString(result.Details?["diff"]) is { } diff && diff != previewDiff)
            {
                output = DiffRenderer.RenderDiff(diff);
            }

            var component = context.LastComponent as Container ?? new Container();
            component.Clear();
            if (output is null) return component;
            component.AddChild(new Spacer(1));
            component.AddChild(new Text(output, 1, 0));
            return component;
        },
    };

    // ----- write -----

    private sealed class WriteHighlightCache
    {
        public string? RawPath;
        public string Lang = "";
        public string RawContent = "";
        public List<string> NormalizedLines = [];
        public List<string> HighlightedLines = [];
    }

    private sealed class WriteCallRenderComponent() : Text("", 0, 0)
    {
        public WriteHighlightCache? Cache;
    }

    private const int WritePartialFullHighlightLines = 50;

    private static string HighlightSingleLine(string line, string lang) => ThemeManager.HighlightCode(line, lang).FirstOrDefault() ?? "";

    private static WriteHighlightCache? RebuildWriteCacheFull(string? rawPath, string content)
    {
        var lang = !string.IsNullOrEmpty(rawPath) ? ThemeManager.GetLanguageFromPath(rawPath) : null;
        if (lang is null) return null;
        var normalized = RenderUtils.ReplaceTabs(RenderUtils.NormalizeDisplayText(content));
        return new WriteHighlightCache { RawPath = rawPath, Lang = lang, RawContent = content, NormalizedLines = normalized.Split('\n').ToList(), HighlightedLines = ThemeManager.HighlightCode(normalized, lang) };
    }

    private static WriteHighlightCache? UpdateWriteCacheIncremental(WriteHighlightCache? cache, string? rawPath, string content)
    {
        var lang = !string.IsNullOrEmpty(rawPath) ? ThemeManager.GetLanguageFromPath(rawPath) : null;
        if (lang is null) return null;
        if (cache is null || cache.Lang != lang || cache.RawPath != rawPath || !content.StartsWith(cache.RawContent, StringComparison.Ordinal)) return RebuildWriteCacheFull(rawPath, content);
        if (content.Length == cache.RawContent.Length) return cache;

        var delta = RenderUtils.ReplaceTabs(RenderUtils.NormalizeDisplayText(content[cache.RawContent.Length..]));
        cache.RawContent = content;
        if (cache.NormalizedLines.Count == 0)
        {
            cache.NormalizedLines.Add("");
            cache.HighlightedLines.Add("");
        }
        var segments = delta.Split('\n');
        var lastIndex = cache.NormalizedLines.Count - 1;
        cache.NormalizedLines[lastIndex] += segments[0];
        while (cache.HighlightedLines.Count <= lastIndex) cache.HighlightedLines.Add("");
        cache.HighlightedLines[lastIndex] = HighlightSingleLine(cache.NormalizedLines[lastIndex], cache.Lang);
        for (var i = 1; i < segments.Length; i++)
        {
            cache.NormalizedLines.Add(segments[i]);
            cache.HighlightedLines.Add(HighlightSingleLine(segments[i], cache.Lang));
        }
        var prefixCount = Math.Min(WritePartialFullHighlightLines, cache.NormalizedLines.Count);
        if (prefixCount > 0)
        {
            var prefixHighlighted = ThemeManager.HighlightCode(string.Join("\n", cache.NormalizedLines.Take(prefixCount)), cache.Lang);
            for (var i = 0; i < prefixCount; i++)
            {
                cache.HighlightedLines[i] = i < prefixHighlighted.Count ? prefixHighlighted[i] : HighlightSingleLine(cache.NormalizedLines[i], cache.Lang);
            }
        }
        return cache;
    }

    public static readonly ToolRenderers Write = new()
    {
        RenderCall = (args, theme, context) =>
        {
            var rawPath = RenderUtils.Str(args["file_path"] ?? args["path"]);
            var fileContent = RenderUtils.Str(args["content"]);
            var component = context.LastComponent as WriteCallRenderComponent ?? new WriteCallRenderComponent();
            component.Cache = fileContent is not null
                ? context.ArgsComplete ? RebuildWriteCacheFull(rawPath, fileContent) : UpdateWriteCacheIncremental(component.Cache, rawPath, fileContent)
                : null;

            var text = $"{theme.Fg("toolTitle", theme.Bold("write"))} {RenderUtils.RenderToolPath(rawPath, theme, context.Cwd)}";
            if (fileContent is null)
            {
                text += "\n\n" + theme.Fg("error", "[invalid content arg - expected string]");
            }
            else if (fileContent.Length > 0)
            {
                var lang = !string.IsNullOrEmpty(rawPath) ? ThemeManager.GetLanguageFromPath(rawPath) : null;
                var rendered = lang is not null
                    ? component.Cache?.HighlightedLines ?? ThemeManager.HighlightCode(RenderUtils.ReplaceTabs(RenderUtils.NormalizeDisplayText(fileContent)), lang)
                    : RenderUtils.NormalizeDisplayText(fileContent).Split('\n').ToList();
                var lines = RenderUtils.TrimTrailingEmptyLines(rendered);
                var maxLines = context.Expanded ? lines.Count : 10;
                var remaining = lines.Count - maxLines;
                text += "\n\n" + string.Join("\n", lines.Take(maxLines).Select(line => lang is not null ? line : theme.Fg("toolOutput", RenderUtils.ReplaceTabs(line))));
                if (remaining > 0) text += RenderUtils.MoreLinesHint(remaining, theme, $", {lines.Count} total");
            }
            component.SetText(text);
            return component;
        },
        RenderResult = (result, _, theme, context) =>
        {
            string? output = null;
            if (context.IsError)
            {
                var errorText = string.Join("\n", result.Content.OfType<TextContent>().Select(c => c.Text));
                if (errorText.Length > 0) output = "\n" + theme.Fg("error", errorText);
            }
            if (output is null)
            {
                var container = context.LastComponent as Container ?? new Container();
                container.Clear();
                return container;
            }
            var text = TextFor(context);
            text.SetText(output);
            return text;
        },
    };

    // ----- grep / find / ls -----

    private static string ListResult(AgentToolResult result, ToolRenderResultOptions options, Theme theme, bool showImages, int collapsedLines)
    {
        var output = RenderUtils.GetTextOutput(result.Content, showImages).Trim();
        var text = "";
        if (output.Length > 0)
        {
            var lines = output.Split('\n');
            var maxLines = options.Expanded ? lines.Length : collapsedLines;
            var remaining = lines.Length - maxLines;
            text += "\n" + string.Join("\n", lines.Take(maxLines).Select(line => theme.Fg("toolOutput", line)));
            if (remaining > 0) text += RenderUtils.MoreLinesHint(remaining, theme);
        }
        return text;
    }

    private static string TruncationWarnings(Theme theme, IEnumerable<string> warnings)
    {
        var list = warnings.ToList();
        return list.Count == 0 ? "" : "\n" + theme.Fg("warning", $"[Truncated: {string.Join(", ", list)}]");
    }

    private static string SizeLimit(JsonNode? truncation) => $"{Truncate.FormatSize(PiJson.GetLong(truncation?["maxBytes"]) ?? Truncate.DefaultMaxBytes)} limit";

    public static readonly ToolRenderers Grep = new()
    {
        RenderCall = (args, theme, context) =>
        {
            var pattern = RenderUtils.Str(args["pattern"]);
            var rawPath = RenderUtils.Str(args["path"]);
            var path = rawPath is not null ? RenderUtils.ShortenPath(rawPath.Length > 0 ? rawPath : ".") : null;
            var glob = RenderUtils.Str(args["glob"]);
            var invalid = RenderUtils.InvalidArgText(theme);
            var text = theme.Fg("toolTitle", theme.Bold("grep")) + " " + (pattern is null ? invalid : theme.Fg("accent", $"/{pattern}/")) + theme.Fg("toolOutput", $" in {path ?? invalid}");
            if (!string.IsNullOrEmpty(glob)) text += theme.Fg("toolOutput", $" ({glob})");
            if (args["limit"] is { } limit) text += theme.Fg("toolOutput", $" limit {limit.ToJsonString()}");
            var component = TextFor(context);
            component.SetText(text);
            return component;
        },
        RenderResult = (result, options, theme, context) =>
        {
            var text = ListResult(result, options, theme, context.ShowImages, 15);
            var details = result.Details;
            var warnings = new List<string>();
            if (details?["matchLimitReached"] is { } matchLimit && IsTruthy(matchLimit)) warnings.Add($"{JsText(matchLimit)} matches limit");
            if (PiJson.GetBool(details?["truncation"]?["truncated"]) == true) warnings.Add(SizeLimit(details?["truncation"]));
            if (details?["linesTruncated"] is { } lt && IsTruthy(lt)) warnings.Add("some lines truncated");
            text += TruncationWarnings(theme, warnings);
            var component = TextFor(context);
            component.SetText(text);
            return component;
        },
    };

    public static readonly ToolRenderers Find = new()
    {
        RenderCall = (args, theme, context) =>
        {
            var pattern = RenderUtils.Str(args["pattern"]);
            var rawPath = RenderUtils.Str(args["path"]);
            var path = rawPath is not null ? RenderUtils.ShortenPath(rawPath.Length > 0 ? rawPath : ".") : null;
            var invalid = RenderUtils.InvalidArgText(theme);
            var text = theme.Fg("toolTitle", theme.Bold("find")) + " " + (pattern is null ? invalid : theme.Fg("accent", pattern)) + theme.Fg("toolOutput", $" in {path ?? invalid}");
            if (args["limit"] is { } limit) text += theme.Fg("toolOutput", $" (limit {limit.ToJsonString()})");
            var component = TextFor(context);
            component.SetText(text);
            return component;
        },
        RenderResult = (result, options, theme, context) =>
        {
            var text = ListResult(result, options, theme, context.ShowImages, 20);
            var details = result.Details;
            var warnings = new List<string>();
            if (details?["resultLimitReached"] is { } limitReached && IsTruthy(limitReached)) warnings.Add($"{JsText(limitReached)} results limit");
            if (PiJson.GetBool(details?["truncation"]?["truncated"]) == true) warnings.Add(SizeLimit(details?["truncation"]));
            text += TruncationWarnings(theme, warnings);
            var component = TextFor(context);
            component.SetText(text);
            return component;
        },
    };

    public static readonly ToolRenderers Ls = new()
    {
        RenderCall = (args, theme, context) =>
        {
            var text = $"{theme.Fg("toolTitle", theme.Bold("ls"))} {RenderUtils.RenderToolPath(RenderUtils.Str(args["path"]), theme, context.Cwd, ".")}";
            if (args["limit"] is { } limit) text += theme.Fg("toolOutput", $" (limit {limit.ToJsonString()})");
            var component = TextFor(context);
            component.SetText(text);
            return component;
        },
        RenderResult = (result, options, theme, context) =>
        {
            var text = ListResult(result, options, theme, context.ShowImages, 20);
            var details = result.Details;
            var warnings = new List<string>();
            if (details?["entryLimitReached"] is { } limitReached && IsTruthy(limitReached)) warnings.Add($"{JsText(limitReached)} entries limit");
            if (PiJson.GetBool(details?["truncation"]?["truncated"]) == true) warnings.Add(SizeLimit(details?["truncation"]));
            text += TruncationWarnings(theme, warnings);
            var component = TextFor(context);
            component.SetText(text);
            return component;
        },
    };

    private static bool IsTruthy(JsonNode node) => node switch
    {
        JsonValue v when v.TryGetValue<bool>(out var b) => b,
        JsonValue v when v.TryGetValue<double>(out var d) => d != 0 && !double.IsNaN(d),
        JsonValue v when v.TryGetValue<string>(out var s) => s.Length > 0,
        _ => true,
    };

    private static string JsText(JsonNode node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : node is JsonValue n && n.TryGetValue<double>(out var d) ? NodeCompat.FormatNumber(d) : node.ToJsonString();
}

/// <summary>Component with render and invalidate callbacks (object literal components in pi).</summary>
public sealed class DelegateComponent(Func<int, List<string>> render, Action? invalidate = null) : IComponent
{
    public List<string> Render(int width) => render(width);

    public void Invalidate() => invalidate?.Invoke();
}

/// <summary>Renders a tool call and its result. Port of tool-execution.ts.</summary>
public sealed class ToolExecutionComponent : Container, IExpandable
{
    private const int FallbackPreviewLines = 10;
    private readonly Box _contentBox;
    private readonly Text _contentText;
    private readonly Container _selfRenderContainer = new();
    private IComponent? _callRendererComponent;
    private IComponent? _resultRendererComponent;
    private readonly ToolRenderState _rendererState = new();
    private readonly List<ImageComponent> _imageComponents = [];
    private readonly Dictionary<int, (string Data, string MimeType)> _convertedImages = [];
    private readonly List<Spacer> _imageSpacers = [];
    private readonly string _toolName;
    private readonly string _toolCallId;
    private JsonObject _args;
    private bool _expanded;
    private bool _showImages;
    private int _imageWidthCells;
    private bool _isPartial = true;
    private readonly ToolRenderers? _renderers;
    private readonly TuiBase _ui;
    private readonly string _cwd;
    private bool _executionStarted;
    private bool _argsComplete;
    private AgentToolResult? _result;
    private bool _resultIsError;
    private bool _hideComponent;

    public ToolExecutionComponent(string toolName, string toolCallId, JsonObject args, bool showImages, int imageWidthCells, ToolRenderers? renderers, TuiBase ui, string cwd)
    {
        _toolName = toolName;
        _toolCallId = toolCallId;
        _args = args;
        _renderers = renderers;
        _showImages = showImages;
        _imageWidthCells = imageWidthCells;
        _ui = ui;
        _cwd = cwd;

        AddChild(new Spacer(1));
        _contentBox = new Box(1, 1, t => ThemeManager.Current.Bg("toolPendingBg", t));
        _contentText = new Text("", 1, 1, t => ThemeManager.Current.Bg("toolPendingBg", t));
        if (_renderers is not null) AddChild(RenderShell == "self" ? _selfRenderContainer : _contentBox);
        else AddChild(_contentText);
        UpdateDisplay();
    }

    private string RenderShell => _renderers?.RenderShell ?? "default";

    private ToolRenderContext Context(IComponent? lastComponent) => new()
    {
        Args = _args,
        ToolCallId = _toolCallId,
        Invalidate = () =>
        {
            Invalidate();
            _ui.RequestRender();
        },
        LastComponent = lastComponent,
        State = _rendererState,
        Cwd = _cwd,
        ExecutionStarted = _executionStarted,
        ArgsComplete = _argsComplete,
        IsPartial = _isPartial,
        Expanded = _expanded,
        ShowImages = _showImages,
        IsError = _resultIsError,
    };

    private IComponent CreateCallFallback() => new Text(ThemeManager.Current.Fg("toolTitle", ThemeManager.Current.Bold(_toolName)), 0, 0);

    private IComponent? CreateResultFallback()
    {
        var output = RenderUtils.GetTextOutput(_result?.Content, _showImages);
        if (output.Length == 0) return null;
        var theme = ThemeManager.Current;
        var lines = output.Split('\n');
        var display = _expanded ? lines : lines.Take(FallbackPreviewLines).ToArray();
        var remaining = lines.Length - display.Length;
        var text = string.Join("\n", display.Select(l => theme.Fg("toolOutput", l)));
        if (remaining > 0) text += RenderUtils.MoreLinesHint(remaining, theme);
        return new Text(text, 0, 0);
    }

    public void UpdateArgs(JsonObject args)
    {
        _args = args;
        UpdateDisplay();
    }

    public void MarkExecutionStarted()
    {
        _executionStarted = true;
        UpdateDisplay();
        _ui.RequestRender();
    }

    public void SetArgsComplete()
    {
        _argsComplete = true;
        UpdateDisplay();
        _ui.RequestRender();
    }

    public void UpdateResult(AgentToolResult result, bool isError, bool isPartial = false)
    {
        _result = result;
        _resultIsError = isError;
        _isPartial = isPartial;
        UpdateDisplay();
        MaybeConvertImagesForKitty();
    }

    /// <summary>Kitty graphics needs PNG, so convert other formats in the background. Port of maybeConvertImagesForKitty.</summary>
    private void MaybeConvertImagesForKitty()
    {
        if (TerminalImage.GetCapabilities().Images != "kitty" || _result is null) return;
        var images = _result.Content.OfType<ImageContent>().ToList();
        for (var i = 0; i < images.Count; i++)
        {
            var img = images[i];
            if (string.IsNullOrEmpty(img.Data) || string.IsNullOrEmpty(img.MimeType) || img.MimeType == "image/png" || _convertedImages.ContainsKey(i)) continue;
            var index = i;
            _ = ConvertAsync();

            async Task ConvertAsync()
            {
                byte[]? png;
                try
                {
                    png = await ImageProcessor.Codec.ConvertToPngAsync(Convert.FromBase64String(img.Data));
                }
                catch
                {
                    png = null;
                }
                if (png is null) return;
                _convertedImages[index] = (Convert.ToBase64String(png), "image/png");
                UpdateDisplay();
                _ui.RequestRender();
            }
        }
    }

    public void SetExpanded(bool expanded)
    {
        _expanded = expanded;
        UpdateDisplay();
    }

    public void SetShowImages(bool show)
    {
        _showImages = show;
        UpdateDisplay();
    }

    public void SetImageWidthCells(int width)
    {
        _imageWidthCells = Math.Max(1, width);
        UpdateDisplay();
    }

    public override void Invalidate()
    {
        base.Invalidate();
        UpdateDisplay();
    }

    public override List<string> Render(int width)
    {
        if (_hideComponent) return [];
        if (_renderers is not null && RenderShell == "self")
        {
            var contentLines = _selfRenderContainer.Render(width);
            if (contentLines.Count == 0 && _imageComponents.Count == 0) return [];
            var lines = new List<string>();
            if (contentLines.Count > 0)
            {
                lines.Add("");
                lines.AddRange(contentLines);
            }
            for (var i = 0; i < _imageComponents.Count; i++)
            {
                if (i < _imageSpacers.Count) lines.AddRange(_imageSpacers[i].Render(width));
                lines.AddRange(_imageComponents[i].Render(width));
            }
            return lines;
        }
        return base.Render(width);
    }

    private void UpdateDisplay()
    {
        var theme = ThemeManager.Current;
        Func<string, string> bgFn = _isPartial
            ? t => ThemeManager.Current.Bg("toolPendingBg", t)
            : _resultIsError ? t => ThemeManager.Current.Bg("toolErrorBg", t) : t => ThemeManager.Current.Bg("toolSuccessBg", t);

        var hasContent = false;
        _hideComponent = false;
        if (_renderers is not null)
        {
            var self = RenderShell == "self";
            if (!self) _contentBox.SetBg(bgFn);
            void AddToContainer(IComponent c)
            {
                if (self) _selfRenderContainer.AddChild(c);
                else _contentBox.AddChild(c);
            }
            if (self) _selfRenderContainer.Clear();
            else _contentBox.Clear();

            if (_renderers.RenderCall is not { } callRenderer)
            {
                AddToContainer(CreateCallFallback());
                hasContent = true;
            }
            else
            {
                try
                {
                    var component = callRenderer(_args, theme, Context(_callRendererComponent));
                    _callRendererComponent = component;
                    AddToContainer(component);
                }
                catch
                {
                    _callRendererComponent = null;
                    AddToContainer(CreateCallFallback());
                }
                hasContent = true;
            }

            if (_result is not null)
            {
                if (_renderers.RenderResult is not { } resultRenderer)
                {
                    if (CreateResultFallback() is { } fallback)
                    {
                        AddToContainer(fallback);
                        hasContent = true;
                    }
                }
                else
                {
                    try
                    {
                        var component = resultRenderer(new AgentToolResult { Content = _result.Content, Details = _result.Details }, new ToolRenderResultOptions(_expanded, _isPartial), theme, Context(_resultRendererComponent));
                        _resultRendererComponent = component;
                        AddToContainer(component);
                        hasContent = true;
                    }
                    catch
                    {
                        _resultRendererComponent = null;
                        if (CreateResultFallback() is { } fallback)
                        {
                            AddToContainer(fallback);
                            hasContent = true;
                        }
                    }
                }
            }
        }
        else
        {
            _contentText.SetCustomBg(bgFn);
            _contentText.SetText(FormatToolExecution());
            hasContent = true;
        }

        foreach (var img in _imageComponents) RemoveChild(img);
        _imageComponents.Clear();
        foreach (var spacer in _imageSpacers) RemoveChild(spacer);
        _imageSpacers.Clear();

        if (_result is not null)
        {
            var caps = TerminalImage.GetCapabilities();
            var imageBlocks = _result.Content.OfType<ImageContent>().ToList();
            for (var i = 0; i < imageBlocks.Count; i++)
            {
                var original = imageBlocks[i];
                if (caps.Images is null || !_showImages || string.IsNullOrEmpty(original.Data) || string.IsNullOrEmpty(original.MimeType)) continue;
                var img = _convertedImages.TryGetValue(i, out var converted) ? new ImageContent(converted.Data, converted.MimeType) : original;
                if (caps.Images == "kitty" && img.MimeType != "image/png") continue;
                var spacer = new Spacer(1);
                AddChild(spacer);
                _imageSpacers.Add(spacer);
                var imageComponent = new ImageComponent(img.Data, img.MimeType, new ImageTheme { FallbackColor = s => ThemeManager.Current.Fg("toolOutput", s) }, new ImageOptions { MaxWidthCells = _imageWidthCells });
                _imageComponents.Add(imageComponent);
                AddChild(imageComponent);
            }
        }

        if (_renderers is not null && !hasContent && _imageComponents.Count == 0) _hideComponent = true;
    }

    private string FormatToolExecution()
    {
        var theme = ThemeManager.Current;
        var text = theme.Fg("toolTitle", theme.Bold(_toolName));
        var content = _args.ToJsonString(new JsonSerializerOptions { WriteIndented = true, IndentSize = 2, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        if (content.Length > 0) text += $"\n\n{content}";
        var output = RenderUtils.GetTextOutput(_result?.Content, _showImages);
        if (output.Length > 0) text += $"\n{output}";
        return text;
    }
}
