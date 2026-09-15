using System.Text.RegularExpressions;
using Iris.Tui.Markdown;

namespace Iris.Tui.Components;

/// <summary>Default text styling for markdown content.</summary>
public sealed class DefaultTextStyle
{
    public Func<string, string>? Color { get; init; }
    public Func<string, string>? BgColor { get; init; }
    public bool Bold { get; init; }
    public bool Italic { get; init; }
    public bool Strikethrough { get; init; }
    public bool Underline { get; init; }
}

public sealed class MarkdownTheme
{
    public required Func<string, string> Heading { get; init; }
    public required Func<string, string> Link { get; init; }
    public required Func<string, string> LinkUrl { get; init; }
    public required Func<string, string> Code { get; init; }
    public required Func<string, string> CodeBlock { get; init; }
    public required Func<string, string> CodeBlockBorder { get; init; }
    public required Func<string, string> Quote { get; init; }
    public required Func<string, string> QuoteBorder { get; init; }
    public required Func<string, string> Hr { get; init; }
    public required Func<string, string> ListBullet { get; init; }
    public required Func<string, string> Bold { get; init; }
    public required Func<string, string> Italic { get; init; }
    public required Func<string, string> Strikethrough { get; init; }
    public required Func<string, string> Underline { get; init; }
    public Func<string, string?, List<string>>? HighlightCode { get; init; }
    public string? CodeBlockIndent { get; init; }
}

public sealed class MarkdownOptions
{
    public bool PreserveOrderedListMarkers { get; init; }
    public bool PreserveBackslashEscapes { get; init; }
    public Func<string, int, string>? Transform { get; init; }
    public bool RenderLatex { get; init; } = true;
}

/// <summary>Markdown renderer for terminal output. Port of pi-tui Markdown.</summary>
public sealed partial class MarkdownComponent : IComponent
{
    private sealed record InlineStyleContext(Func<string, string> ApplyText, string StylePrefix);

    private string _text;
    private readonly int _paddingX;
    private readonly int _paddingY;
    private readonly DefaultTextStyle? _defaultTextStyle;
    private readonly MarkdownTheme _theme;
    private readonly MarkdownOptions _options;
    private string? _defaultStylePrefix;
    private string? _cachedText;
    private int? _cachedWidth;
    private List<string>? _cachedLines;

    public MarkdownComponent(string text, int paddingX, int paddingY, MarkdownTheme theme, DefaultTextStyle? defaultTextStyle = null, MarkdownOptions? options = null)
    {
        _text = text;
        _paddingX = paddingX;
        _paddingY = paddingY;
        _theme = theme;
        _defaultTextStyle = defaultTextStyle;
        _options = options ?? new MarkdownOptions();
    }

    // ----- Parser configuration (strict strikethrough + LaTeX extensions) -----

    private static readonly Regex StrictStrikethrough = JsRegex.Create(@"^(~~)(?=[^\s~])((?:\\.|[^\\])*?(?:\\.|[^\s~\\]))\1(?=[^~]|$)");
    private static readonly Regex PendingDollarMath = JsRegex.Create(@"\\[A-Za-z]+|[_^=+*/<>()[\]|±≤≥≠≈∈→⇒∞∫∑√-]");
    private static readonly Regex DollarWhitespaceStart = JsRegex.Create(@"^\$\s");
    private static readonly Regex TrailingWhitespace = JsRegex.Create(@"\s$");
    private static readonly Regex LeadingDigit = JsRegex.Create(@"^\d");
    private static readonly Regex EnvLikeName = JsRegex.Create(@"^[A-Z_][A-Z0-9_]*(?:[^A-Za-z0-9_\s])?$");
    private static readonly Regex IdentifierStart = JsRegex.Create("^[A-Za-z_][A-Za-z0-9_]*");
    private static readonly Regex BlockDollar = JsRegex.Create(@"^ {0,3}\$\$[ \t]*(?:\n)?([\s\S]*?)\$\$[ \t]*(?:\n|$)");
    private static readonly Regex BlockBracket = JsRegex.Create(@"^ {0,3}\\\[[ \t]*(?:\n)?([\s\S]*?)\\\][ \t]*(?:\n|$)");
    private static readonly Regex PendingBracket = JsRegex.Create(@"^ {0,3}\\\[[ \t]*(?:\n)?([\s\S]*)$");
    private static readonly Regex PendingDollar = JsRegex.Create(@"^ {0,3}\$\$[ \t]*(?:\n)?([\s\S]*)$");
    private static readonly Regex BlockLatexStart = JsRegex.Create(@"(?:^|\n) {0,3}(?:\$\$|\\\[)");
    private static readonly Regex FenceMarker = JsRegex.Create("^(`{3,}|~{3,})");

    private static bool IsEscaped(string source, int index)
    {
        var backslashes = 0;
        for (var p = index - 1; p >= 0 && source[p] == '\\'; p--) backslashes++;
        return backslashes % 2 == 1;
    }

    private static int FindClosingDelimiter(string source, string closing, int start)
    {
        var index = start <= source.Length ? source.IndexOf(closing, start, StringComparison.Ordinal) : -1;
        while (index >= 0 && IsEscaped(source, index))
        {
            var next = index + closing.Length;
            index = next <= source.Length ? source.IndexOf(closing, next, StringComparison.Ordinal) : -1;
        }
        return index;
    }

    private static MdToken? TokenizeInlineLatex(string source)
    {
        string opening;
        string closing;
        if (source.StartsWith("$$", StringComparison.Ordinal)) (opening, closing) = ("$$", "$$");
        else if (source.StartsWith("\\(", StringComparison.Ordinal)) (opening, closing) = ("\\(", "\\)");
        else if (source.StartsWith("\\[", StringComparison.Ordinal)) (opening, closing) = ("\\[", "\\]");
        else if (source.StartsWith('$') && !DollarWhitespaceStart.IsMatch(source)) (opening, closing) = ("$", "$");
        else return null;

        var closingIndex = FindClosingDelimiter(source, closing, opening.Length);
        if (closingIndex >= 0 && opening == "$")
        {
            var inner = source[opening.Length..closingIndex];
            var after = source[(closingIndex + 1)..];
            if (TrailingWhitespace.IsMatch(inner) || LeadingDigit.IsMatch(after) || (EnvLikeName.IsMatch(inner) && IdentifierStart.IsMatch(after)) || inner.Contains('`'))
            {
                return null;
            }
        }

        if (closingIndex < 0)
        {
            var pendingSource = source[opening.Length..];
            if (opening.StartsWith('\\') || PendingDollarMath.IsMatch(pendingSource))
            {
                return new MdToken { Type = "latex", Raw = source, Text = pendingSource, Pending = true };
            }
            return null;
        }

        var text = source[opening.Length..closingIndex];
        if (text.Length == 0 || text.Contains('\n')) return null;
        return new MdToken { Type = "latex", Raw = source[..(closingIndex + closing.Length)], Text = text };
    }

    private static MdToken? TokenizeBlockLatex(string source)
    {
        var dollar = BlockDollar.Match(source);
        if (dollar.Success && dollar.Groups[1].Value.Length > 0) return new MdToken { Type = "latexBlock", Raw = dollar.Value, Text = dollar.Groups[1].Value.Trim() };
        var bracket = BlockBracket.Match(source);
        if (bracket.Success && bracket.Groups[1].Value.Length > 0) return new MdToken { Type = "latexBlock", Raw = bracket.Value, Text = bracket.Groups[1].Value.Trim() };
        var pendingBracket = PendingBracket.Match(source);
        if (pendingBracket.Success) return new MdToken { Type = "latexBlock", Raw = pendingBracket.Value, Text = pendingBracket.Groups[1].Value, Pending = true };
        var pendingDollar = PendingDollar.Match(source);
        if (pendingDollar.Success && pendingDollar.Groups[1].Value.Length > 0 && PendingDollarMath.IsMatch(pendingDollar.Groups[1].Value))
        {
            return new MdToken { Type = "latexBlock", Raw = pendingDollar.Value, Text = pendingDollar.Groups[1].Value, Pending = true };
        }
        return null;
    }

    private static readonly MarkedExtension[] LatexExtensions =
    [
        new("latexBlock", true, source =>
        {
            var match = BlockLatexStart.Match(source);
            return match.Success ? match.Index + (match.Value.StartsWith('\n') ? 1 : 0) : null;
        }, TokenizeBlockLatex),
        new("latex", false, source =>
        {
            var indices = new[] { source.IndexOf('$'), source.IndexOf("\\(", StringComparison.Ordinal), source.IndexOf("\\[", StringComparison.Ordinal) }.Where(i => i >= 0).ToList();
            return indices.Count > 0 ? indices.Min() : null;
        }, TokenizeInlineLatex),
    ];

    private static MdToken? StrictDel(string src, Func<string, List<MdToken>> inlineTokens)
    {
        var match = StrictStrikethrough.Match(src);
        if (!match.Success) return null;
        var text = match.Groups[2].Value;
        return new MdToken { Type = "del", Raw = match.Value, Text = text, Tokens = inlineTokens(text) };
    }

    public static List<MdToken> Lex(string markdown) => new MarkedLexer(LatexExtensions, StrictDel).Lex(markdown);

    private static void TrimPartialClosingFences(IReadOnlyList<MdToken> tokens)
    {
        if (tokens.Count == 0) return;
        var token = tokens[^1];
        if (token.Type == "list")
        {
            if (token.Items.Count > 0) TrimPartialClosingFences(token.Items[^1].Tokens ?? []);
            return;
        }
        if (token.Type == "blockquote")
        {
            TrimPartialClosingFences(token.Tokens ?? []);
            return;
        }
        if (token.Type != "code") return;

        var markerMatch = FenceMarker.Match(token.Raw);
        if (!markerMatch.Success) return;
        var marker = markerMatch.Groups[1].Value;
        var lastLine = token.Raw.Split('\n')[^1];
        if (lastLine.Length == 0 || lastLine.Length >= marker.Length || lastLine != new string(marker[0], lastLine.Length)) return;
        var text = token.Text.Length >= lastLine.Length ? token.Text[..^lastLine.Length] : "";
        token.Text = text.EndsWith('\n') ? text[..^1] : text;
    }

    // ----- Component -----

    public void SetText(string text)
    {
        _text = text;
        Invalidate();
    }

    public void Invalidate()
    {
        _cachedText = null;
        _cachedWidth = null;
        _cachedLines = null;
    }

    public List<string> Render(int width)
    {
        if (_cachedLines is not null && _cachedText == _text && _cachedWidth == width) return _cachedLines;

        var contentWidth = Math.Max(1, width - _paddingX * 2);
        var text = _options.Transform?.Invoke(_text, contentWidth) ?? _text;
        if (string.IsNullOrEmpty(text) || text.Trim().Length == 0)
        {
            _cachedText = _text;
            _cachedWidth = width;
            _cachedLines = [];
            return _cachedLines;
        }

        var normalized = text.Replace("\t", "   ");
        var tokens = Lex(normalized);
        TrimPartialClosingFences(tokens);

        var renderedLines = new List<string>();
        for (var i = 0; i < tokens.Count; i++)
        {
            renderedLines.AddRange(RenderToken(tokens[i], contentWidth, i + 1 < tokens.Count ? tokens[i + 1].Type : null));
        }

        var wrappedLines = new List<string>();
        foreach (var line in renderedLines)
        {
            if (TerminalImage.IsImageLine(line)) wrappedLines.Add(line);
            else wrappedLines.AddRange(TextUtils.WrapTextWithAnsi(line, contentWidth));
        }

        var margin = new string(' ', _paddingX);
        var bgFn = _defaultTextStyle?.BgColor;
        var contentLines = new List<string>();
        foreach (var line in wrappedLines)
        {
            if (TerminalImage.IsImageLine(line))
            {
                contentLines.Add(line);
                continue;
            }
            var withMargins = margin + line + margin;
            contentLines.Add(bgFn is not null
                ? TextUtils.ApplyBackgroundToLine(withMargins, width, bgFn)
                : withMargins + new string(' ', Math.Max(0, width - TextUtils.VisibleWidth(withMargins))));
        }

        var emptyLine = new string(' ', Math.Max(0, width));
        var emptyLines = Enumerable.Range(0, _paddingY).Select(_ => bgFn is not null ? TextUtils.ApplyBackgroundToLine(emptyLine, width, bgFn) : emptyLine).ToList();
        var result = new List<string>(emptyLines);
        result.AddRange(contentLines);
        result.AddRange(emptyLines);

        _cachedText = _text;
        _cachedWidth = width;
        _cachedLines = result;
        return result.Count > 0 ? result : [""];
    }

    private string ApplyDefaultStyle(string text)
    {
        if (_defaultTextStyle is null) return text;
        var styled = text;
        if (_defaultTextStyle.Color is { } color) styled = color(styled);
        if (_defaultTextStyle.Bold) styled = _theme.Bold(styled);
        if (_defaultTextStyle.Italic) styled = _theme.Italic(styled);
        if (_defaultTextStyle.Strikethrough) styled = _theme.Strikethrough(styled);
        if (_defaultTextStyle.Underline) styled = _theme.Underline(styled);
        return styled;
    }

    private string GetDefaultStylePrefix()
    {
        if (_defaultTextStyle is null) return "";
        return _defaultStylePrefix ??= GetStylePrefix(ApplyDefaultStyle);
    }

    private static string GetStylePrefix(Func<string, string> styleFn)
    {
        const string sentinel = "\0";
        var styled = styleFn(sentinel);
        var index = styled.IndexOf(sentinel, StringComparison.Ordinal);
        return index >= 0 ? styled[..index] : "";
    }

    private InlineStyleContext DefaultInlineStyleContext() => new(ApplyDefaultStyle, GetDefaultStylePrefix());

    private List<string> RenderToken(MdToken token, int width, string? nextTokenType, InlineStyleContext? styleContext = null)
    {
        var lines = new List<string>();
        switch (token.Type)
        {
            case "heading":
            {
                var level = token.Depth;
                var prefix = new string('#', level) + " ";
                Func<string, string> headingStyle = level == 1
                    ? t => _theme.Heading(_theme.Bold(_theme.Underline(t)))
                    : t => _theme.Heading(_theme.Bold(t));
                var headingContext = new InlineStyleContext(headingStyle, GetStylePrefix(headingStyle));
                var headingText = RenderInlineTokens(token.Tokens ?? [], headingContext);
                lines.Add(level >= 3 ? headingStyle(prefix) + headingText : headingText);
                if (nextTokenType is not null && nextTokenType != "space") lines.Add("");
                break;
            }
            case "paragraph":
                lines.Add(RenderInlineTokens(token.Tokens ?? [], styleContext));
                if (nextTokenType is not null && nextTokenType is not ("list" or "space")) lines.Add("");
                break;
            case "text":
                lines.Add(RenderInlineTokens([token], styleContext));
                break;
            case "latexBlock":
            {
                var rendered = !token.Pending && _options.RenderLatex ? Latex.Render(token.Text, true) ?? token.Raw.Trim() : token.Raw.Trim();
                foreach (var line in rendered.Split('\n')) lines.Add(ApplyDefaultStyle(line));
                if (nextTokenType is not null && nextTokenType != "space") lines.Add("");
                break;
            }
            case "code":
            {
                var indent = _theme.CodeBlockIndent ?? "  ";
                lines.Add(_theme.CodeBlockBorder("```" + (token.Lang ?? "")));
                if (_theme.HighlightCode is { } highlight)
                {
                    foreach (var hl in highlight(token.Text, string.IsNullOrEmpty(token.Lang) ? null : token.Lang)) lines.Add(indent + hl);
                }
                else
                {
                    foreach (var codeLine in token.Text.Split('\n')) lines.Add(indent + _theme.CodeBlock(codeLine));
                }
                lines.Add(_theme.CodeBlockBorder("```"));
                if (nextTokenType is not null && nextTokenType != "space") lines.Add("");
                break;
            }
            case "list":
                lines.AddRange(RenderList(token, 0, width, styleContext));
                break;
            case "table":
                lines.AddRange(RenderTable(token, width, nextTokenType, styleContext));
                break;
            case "blockquote":
            {
                Func<string, string> quoteStyle = t => _theme.Quote(_theme.Italic(t));
                var quoteStylePrefix = GetStylePrefix(quoteStyle);
                string ApplyQuoteStyle(string line) =>
                    quoteStylePrefix.Length == 0 ? quoteStyle(line) : quoteStyle(line.Replace("\e[0m", "\e[0m" + quoteStylePrefix));

                var quoteContentWidth = Math.Max(1, width - 2);
                var quoteContext = new InlineStyleContext(t => t, quoteStylePrefix);
                var quoteTokens = token.Tokens ?? [];
                var renderedQuote = new List<string>();
                for (var i = 0; i < quoteTokens.Count; i++)
                {
                    renderedQuote.AddRange(RenderToken(quoteTokens[i], quoteContentWidth, i + 1 < quoteTokens.Count ? quoteTokens[i + 1].Type : null, quoteContext));
                }
                while (renderedQuote.Count > 0 && renderedQuote[^1] == "") renderedQuote.RemoveAt(renderedQuote.Count - 1);
                foreach (var quoteLine in renderedQuote)
                {
                    foreach (var wrapped in TextUtils.WrapTextWithAnsi(ApplyQuoteStyle(quoteLine), quoteContentWidth))
                    {
                        lines.Add(_theme.QuoteBorder("│ ") + wrapped);
                    }
                }
                if (nextTokenType is not null && nextTokenType != "space") lines.Add("");
                break;
            }
            case "hr":
                lines.Add(_theme.Hr(new string('─', Math.Max(0, Math.Min(width, 80)))));
                if (nextTokenType is not null && nextTokenType != "space") lines.Add("");
                break;
            case "html":
                lines.Add(ApplyDefaultStyle(token.Raw.Trim()));
                break;
            case "space":
                lines.Add("");
                break;
            case "def":
            case "checkbox":
            case "br":
                // No "text" field on these token types; nothing is rendered.
                break;
            default:
                lines.Add(token.Text);
                break;
        }
        return lines;
    }

    private string RenderInlineTokens(IReadOnlyList<MdToken> tokens, InlineStyleContext? styleContext = null)
    {
        var result = "";
        var context = styleContext ?? DefaultInlineStyleContext();
        var applyText = context.ApplyText;
        var stylePrefix = context.StylePrefix;
        string ApplyTextWithNewlines(string text) => string.Join("\n", text.Split('\n').Select(applyText));

        foreach (var token in tokens)
        {
            switch (token.Type)
            {
                case "latex":
                {
                    var rendered = !token.Pending && _options.RenderLatex ? Latex.Render(token.Text) ?? token.Raw : token.Raw;
                    result += ApplyTextWithNewlines(rendered);
                    break;
                }
                case "escape":
                    result += ApplyTextWithNewlines(_options.PreserveBackslashEscapes ? token.Raw : token.Text);
                    break;
                case "text":
                    result += token.Tokens is { Count: > 0 } nested ? RenderInlineTokens(nested, context) : ApplyTextWithNewlines(token.Text);
                    break;
                case "paragraph":
                    result += RenderInlineTokens(token.Tokens ?? [], context);
                    break;
                case "strong":
                    result += _theme.Bold(RenderInlineTokens(token.Tokens ?? [], context)) + stylePrefix;
                    break;
                case "em":
                    result += _theme.Italic(RenderInlineTokens(token.Tokens ?? [], context)) + stylePrefix;
                    break;
                case "codespan":
                    result += _theme.Code(token.Text) + stylePrefix;
                    break;
                case "link":
                {
                    var linkText = RenderInlineTokens(token.Tokens ?? [], context);
                    var styledLink = _theme.Link(_theme.Underline(linkText));
                    var href = token.Href ?? "";
                    if (TerminalImage.GetCapabilities().Hyperlinks)
                    {
                        result += TerminalImage.Hyperlink(styledLink, href) + stylePrefix;
                    }
                    else
                    {
                        var hrefForComparison = href.StartsWith("mailto:", StringComparison.Ordinal) ? href[7..] : href;
                        result += token.Text == href || token.Text == hrefForComparison
                            ? styledLink + stylePrefix
                            : styledLink + _theme.LinkUrl($" ({href})") + stylePrefix;
                    }
                    break;
                }
                case "br":
                    result += "\n";
                    break;
                case "del":
                    result += _theme.Strikethrough(RenderInlineTokens(token.Tokens ?? [], context)) + stylePrefix;
                    break;
                case "html":
                    result += ApplyTextWithNewlines(token.Raw);
                    break;
                case "checkbox":
                    // Checkbox tokens have no text field in marked; the list renderer draws the marker.
                    break;
                default:
                    result += ApplyTextWithNewlines(token.Text);
                    break;
            }
        }

        while (stylePrefix.Length > 0 && result.EndsWith(stylePrefix, StringComparison.Ordinal)) result = result[..^stylePrefix.Length];
        return result;
    }

    [GeneratedRegex(@"^(?: {0,3})(\d{1,9}[.)])[ \t]+")]
    private static partial Regex OrderedListMarker();

    [GeneratedRegex(@"^(?: {0,3})([-+*])(?:[ \t]+|(?=\r?\n|$))")]
    private static partial Regex UnorderedListMarker();

    private List<string> RenderList(MdToken token, int depth, int width, InlineStyleContext? styleContext)
    {
        var lines = new List<string>();
        var indent = new string(' ', depth * 4);
        var startNumber = token.Start ?? 1;

        for (var i = 0; i < token.Items.Count; i++)
        {
            var item = token.Items[i];
            var isLastItem = i == token.Items.Count - 1;
            string bullet;
            if (token.Ordered)
            {
                var m = _options.PreserveOrderedListMarkers ? OrderedListMarker().Match(item.Raw) : null;
                bullet = m is { Success: true } ? m.Groups[1].Value + " " : $"{startNumber + i}. ";
            }
            else
            {
                var m = _options.PreserveOrderedListMarkers ? UnorderedListMarker().Match(item.Raw) : null;
                bullet = m is { Success: true } ? m.Groups[1].Value + " " : "- ";
            }
            var taskMarker = item.Task ? $"[{(item.Checked == true ? "x" : " ")}] " : "";
            var marker = bullet + taskMarker;
            var firstPrefix = indent + _theme.ListBullet(marker);
            var continuationPrefix = indent + new string(' ', TextUtils.VisibleWidth(marker));
            var itemWidth = Math.Max(1, width - TextUtils.VisibleWidth(firstPrefix));
            var renderedAnyLine = false;

            foreach (var itemToken in item.Tokens ?? [])
            {
                if (itemToken.Type == "list")
                {
                    lines.AddRange(RenderList(itemToken, depth + 1, width, styleContext));
                    renderedAnyLine = true;
                    continue;
                }
                foreach (var line in RenderToken(itemToken, itemWidth, null, styleContext))
                {
                    foreach (var wrapped in TextUtils.WrapTextWithAnsi(line, itemWidth))
                    {
                        lines.Add((renderedAnyLine ? continuationPrefix : firstPrefix) + wrapped);
                        renderedAnyLine = true;
                    }
                }
            }

            if (!renderedAnyLine) lines.Add(firstPrefix);
            if (token.Loose && !isLastItem) lines.Add("");
        }
        return lines;
    }

    [GeneratedRegex(@"[\t\n\x0B\f\r \u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000\uFEFF]+")]
    private static partial Regex JsWhitespaceRun();

    private static int GetLongestWordWidth(string text, int? maxWidth = null)
    {
        var longest = JsWhitespaceRun().Split(text).Where(w => w.Length > 0).Select(TextUtils.VisibleWidth).DefaultIfEmpty(0).Max();
        return maxWidth is { } max ? Math.Min(longest, max) : longest;
    }

    private static List<string> WrapCellText(string text, int maxWidth, string stylePrefix = "")
    {
        var lines = TextUtils.WrapTextWithAnsi(text, Math.Max(1, maxWidth));
        return lines.Select((line, index) => line + (index < lines.Count - 1 ? "\e[22;23;24;25;27;28;29;39m" : "") + stylePrefix).ToList();
    }

    private List<string> RenderTable(MdToken token, int availableWidth, string? nextTokenType, InlineStyleContext? styleContext)
    {
        var lines = new List<string>();
        var numCols = token.Header.Count;
        if (numCols == 0) return lines;

        var borderOverhead = 3 * numCols + 1;
        var availableForCells = availableWidth - borderOverhead;
        if (availableForCells < numCols)
        {
            var fallback = token.Raw.Length > 0 ? TextUtils.WrapTextWithAnsi(token.Raw, availableWidth) : [];
            if (nextTokenType is not null && nextTokenType != "space") fallback.Add("");
            return fallback;
        }

        const int maxUnbrokenWordWidth = 30;
        var naturalWidths = new int[numCols];
        var minWordWidths = new int[numCols];
        for (var i = 0; i < numCols; i++)
        {
            var headerText = RenderInlineTokens(token.Header[i].Tokens, styleContext);
            naturalWidths[i] = TextUtils.VisibleWidth(headerText);
            minWordWidths[i] = Math.Max(1, GetLongestWordWidth(headerText, maxUnbrokenWordWidth));
        }
        foreach (var row in token.Rows)
        {
            for (var i = 0; i < row.Count && i < numCols; i++)
            {
                var cellText = RenderInlineTokens(row[i].Tokens, styleContext);
                naturalWidths[i] = Math.Max(naturalWidths[i], TextUtils.VisibleWidth(cellText));
                minWordWidths[i] = Math.Max(minWordWidths[i], GetLongestWordWidth(cellText, maxUnbrokenWordWidth));
            }
        }

        var minColumnWidths = minWordWidths.ToArray();
        var minCellsWidth = minColumnWidths.Sum();
        if (minCellsWidth > availableForCells)
        {
            minColumnWidths = Enumerable.Repeat(1, numCols).ToArray();
            var remaining = availableForCells - numCols;
            if (remaining > 0)
            {
                var totalWeight = minWordWidths.Sum(w => Math.Max(0, w - 1));
                var growth = minWordWidths.Select(w => totalWeight > 0 ? (int)Math.Floor((double)Math.Max(0, w - 1) / totalWeight * remaining) : 0).ToArray();
                for (var i = 0; i < numCols; i++) minColumnWidths[i] += growth[i];
                var leftover = remaining - growth.Sum();
                for (var i = 0; leftover > 0 && i < numCols; i++)
                {
                    minColumnWidths[i]++;
                    leftover--;
                }
            }
            minCellsWidth = minColumnWidths.Sum();
        }

        int[] columnWidths;
        var totalNaturalWidth = naturalWidths.Sum() + borderOverhead;
        if (totalNaturalWidth <= availableWidth)
        {
            columnWidths = naturalWidths.Select((w, i) => Math.Max(w, minColumnWidths[i])).ToArray();
        }
        else
        {
            var totalGrowPotential = naturalWidths.Select((w, i) => Math.Max(0, w - minColumnWidths[i])).Sum();
            var extraWidth = Math.Max(0, availableForCells - minCellsWidth);
            columnWidths = minColumnWidths.Select((minWidth, i) =>
            {
                var delta = Math.Max(0, naturalWidths[i] - minWidth);
                var grow = totalGrowPotential > 0 ? (int)Math.Floor((double)delta / totalGrowPotential * extraWidth) : 0;
                return minWidth + grow;
            }).ToArray();
            var remaining = availableForCells - columnWidths.Sum();
            while (remaining > 0)
            {
                var grew = false;
                for (var i = 0; i < numCols && remaining > 0; i++)
                {
                    if (columnWidths[i] < naturalWidths[i])
                    {
                        columnWidths[i]++;
                        remaining--;
                        grew = true;
                    }
                }
                if (!grew) break;
            }
        }

        lines.Add($"┌─{string.Join("─┬─", columnWidths.Select(w => new string('─', w)))}─┐");

        var headerCellLines = token.Header.Select((cell, i) => WrapCellText(RenderInlineTokens(cell.Tokens, styleContext), columnWidths[i], styleContext?.StylePrefix ?? "")).ToList();
        var headerLineCount = headerCellLines.Max(c => c.Count);
        for (var lineIdx = 0; lineIdx < headerLineCount; lineIdx++)
        {
            var parts = headerCellLines.Select((cellLines, col) =>
            {
                var text = lineIdx < cellLines.Count ? cellLines[lineIdx] : "";
                return _theme.Bold(text + new string(' ', Math.Max(0, columnWidths[col] - TextUtils.VisibleWidth(text))));
            });
            lines.Add($"│ {string.Join(" │ ", parts)} │");
        }

        var separator = $"├─{string.Join("─┼─", columnWidths.Select(w => new string('─', w)))}─┤";
        lines.Add(separator);

        for (var rowIndex = 0; rowIndex < token.Rows.Count; rowIndex++)
        {
            var row = token.Rows[rowIndex];
            var rowCellLines = row.Select((cell, i) => WrapCellText(RenderInlineTokens(cell.Tokens, styleContext), i < numCols ? columnWidths[i] : 1, styleContext?.StylePrefix ?? "")).ToList();
            var rowLineCount = rowCellLines.Count == 0 ? 0 : rowCellLines.Max(c => c.Count);
            for (var lineIdx = 0; lineIdx < rowLineCount; lineIdx++)
            {
                var parts = rowCellLines.Select((cellLines, col) =>
                {
                    var text = lineIdx < cellLines.Count ? cellLines[lineIdx] : "";
                    return text + new string(' ', Math.Max(0, (col < numCols ? columnWidths[col] : 0) - TextUtils.VisibleWidth(text)));
                });
                lines.Add($"│ {string.Join(" │ ", parts)} │");
            }
            if (rowIndex < token.Rows.Count - 1) lines.Add(separator);
        }

        lines.Add($"└─{string.Join("─┴─", columnWidths.Select(w => new string('─', w)))}─┘");
        if (nextTokenType is not null && nextTokenType != "space") lines.Add("");
        return lines;
    }
}

public sealed class ImageTheme
{
    public required Func<string, string> FallbackColor { get; init; }
}

public sealed class ImageOptions
{
    public int? MaxWidthCells { get; init; }
    public int? MaxHeightCells { get; init; }
    public string? Filename { get; init; }
    public int? ImageId { get; init; }
}

/// <summary>Inline terminal image with text fallback. Port of pi-tui Image.</summary>
public sealed class ImageComponent : IComponent
{
    private readonly string _base64Data;
    private readonly string _mimeType;
    private readonly ImageDimensions _dimensions;
    private readonly ImageTheme _theme;
    private readonly ImageOptions _options;
    private int? _imageId;
    private List<string>? _cachedLines;
    private int? _cachedWidth;

    public ImageComponent(string base64Data, string mimeType, ImageTheme theme, ImageOptions? options = null, ImageDimensions? dimensions = null)
    {
        _base64Data = base64Data;
        _mimeType = mimeType;
        _theme = theme;
        _options = options ?? new ImageOptions();
        _dimensions = dimensions ?? TerminalImage.GetImageDimensions(base64Data, mimeType) ?? new ImageDimensions(800, 600);
        _imageId = _options.ImageId;
    }

    public int? GetImageId() => _imageId;

    public void Invalidate()
    {
        _cachedLines = null;
        _cachedWidth = null;
    }

    public List<string> Render(int width)
    {
        if (_cachedLines is not null && _cachedWidth == width) return _cachedLines;

        var maxWidth = Math.Max(1, Math.Min(width - 2, _options.MaxWidthCells ?? 60));
        var cell = TerminalImage.GetCellDimensions();
        var defaultMaxHeight = Math.Max(1, (int)Math.Ceiling((double)maxWidth * cell.WidthPx / cell.HeightPx));
        var maxHeight = _options.MaxHeightCells ?? defaultMaxHeight;
        var caps = TerminalImage.GetCapabilities();
        List<string> lines;

        RenderedImage? result = null;
        if (caps.Images is not null)
        {
            if (caps.Images == "kitty" && _imageId is null) _imageId = TerminalImage.AllocateImageId();
            result = TerminalImage.RenderImage(_base64Data, _dimensions, maxWidth, maxHeight, imageId: _imageId, moveCursor: false);
        }

        if (result is not null)
        {
            if (result.ImageId is { } id) _imageId = id;
            if (caps.Images == "kitty")
            {
                lines = [result.Sequence];
                for (var i = 0; i < result.Rows - 1; i++) lines.Add("");
            }
            else
            {
                lines = [];
                for (var i = 0; i < result.Rows - 1; i++) lines.Add("");
                var rowOffset = result.Rows - 1;
                lines.Add((rowOffset > 0 ? $"\e[{rowOffset}A" : "") + result.Sequence);
            }
        }
        else
        {
            lines = [TextUtils.TruncateToWidth(_theme.FallbackColor(TerminalImage.ImageFallback(_mimeType, _dimensions, _options.Filename)), width)];
        }

        _cachedLines = lines;
        _cachedWidth = width;
        return lines;
    }
}
