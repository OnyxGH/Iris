using System.Text.RegularExpressions;

namespace PiSharp.Tui.Markdown;

public sealed class MdTableCell
{
    public string Text { get; set; } = "";
    public List<MdToken> Tokens { get; set; } = [];
    public bool Header { get; set; }
    public string? Align { get; set; }
}

/// <summary>A marked token. Only the fields relevant to each <see cref="Type"/> are populated.</summary>
public sealed class MdToken
{
    public string Type { get; set; } = "";
    public string Raw { get; set; } = "";
    public string Text { get; set; } = "";
    public List<MdToken>? Tokens { get; set; }

    public int Depth { get; set; }
    public string? Lang { get; set; }
    public string? CodeBlockStyle { get; set; }

    public bool Ordered { get; set; }
    public int? Start { get; set; }
    public bool Loose { get; set; }
    public List<MdToken> Items { get; set; } = [];
    public bool Task { get; set; }
    public bool? Checked { get; set; }

    public string? Href { get; set; }
    public string? Title { get; set; }
    public string? Tag { get; set; }

    public List<MdTableCell> Header { get; set; } = [];
    public List<List<MdTableCell>> Rows { get; set; } = [];
    public List<string?> Align { get; set; } = [];

    public bool Pending { get; set; }
    public bool Escaped { get; set; }
    public bool Block { get; set; }
    public bool Pre { get; set; }
    public bool InLink { get; set; }
    public bool InRawBlock { get; set; }
}

/// <summary>A tokenizer extension (marked TokenizerExtension).</summary>
public sealed record MarkedExtension(string Name, bool Block, Func<string, int?> Start, Func<string, MdToken?> Tokenizer);

/// <summary>
/// Port of the marked 18 lexer and tokenizer with GFM rules (non-pedantic, no breaks), including tokenizer extensions
/// and an overridable strikethrough tokenizer.
/// </summary>
public sealed partial class MarkedLexer
{
    private sealed class InlineQueueItem(string src, List<MdToken> tokens)
    {
        public string Src { get; set; } = src;
        public List<MdToken> Tokens { get; } = tokens;
    }

    private readonly Dictionary<string, (string Href, string? Title)> _links = [];
    private readonly List<InlineQueueItem> _inlineQueue = [];
    private readonly IReadOnlyList<MarkedExtension> _blockExtensions;
    private readonly IReadOnlyList<MarkedExtension> _inlineExtensions;
    private readonly Func<string, string, string, MdToken?>? _delOverride;
    private bool _inLink;
    private bool _inRawBlock;
    private bool _top = true;

    public MarkedLexer(IEnumerable<MarkedExtension>? extensions = null, Func<string, Func<string, List<MdToken>>, MdToken?>? strictDel = null)
    {
        var list = extensions?.ToList() ?? [];
        _blockExtensions = list.Where(e => e.Block).ToList();
        _inlineExtensions = list.Where(e => !e.Block).ToList();
        if (strictDel is not null) _delOverride = (src, _, _) => strictDel(src, text => InlineTokens(text));
    }

    // ----- rules.other -----
    [GeneratedRegex(@"^(?: {1,4}| {0,3}\t)", RegexOptions.Multiline)]
    private static partial Regex CodeRemoveIndent();

    [GeneratedRegex(@"\\([\[\]])")]
    private static partial Regex OutputLinkReplace();

    [GeneratedRegex(@"^(\s+)(?:```)")]
    private static partial Regex IndentCodeCompensation();

    [GeneratedRegex(@"^\s+")]
    private static partial Regex BeginningSpace();

    [GeneratedRegex("^[ \t]*$")]
    private static partial Regex BlankLine();

    [GeneratedRegex(@"\n[ \t]*\n[ \t]*\z")]
    private static partial Regex DoubleBlankLine();

    [GeneratedRegex("^ {0,3}>")]
    private static partial Regex BlockquoteStart();

    [GeneratedRegex(@"\n {0,3}((?:=+|-+) *)(?=\n|\z)")]
    private static partial Regex BlockquoteSetextReplace();

    [GeneratedRegex("^ {0,3}>[ \t]?", RegexOptions.Multiline)]
    private static partial Regex BlockquoteSetextReplace2();

    [GeneratedRegex(@"^\[[ xX]\] +[^\t\n\x0B\f\r \u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000\uFEFF]")]
    private static partial Regex ListIsTask();

    [GeneratedRegex(@"^\[[ xX]\] +")]
    private static partial Regex ListReplaceTask();

    [GeneratedRegex(@"\[[ xX]\]")]
    private static partial Regex ListTaskCheckbox();

    [GeneratedRegex(@"\n[^\n\r\u2028\u2029]*\n")]
    private static partial Regex AnyLine();

    [GeneratedRegex("^<(.*)>$", RegexOptions.Singleline)]
    private static partial Regex HrefBrackets();

    [GeneratedRegex("[:|]")]
    private static partial Regex TableDelimiter();

    [GeneratedRegex(@"^\||\| *\z")]
    private static partial Regex TableAlignChars();

    [GeneratedRegex(@"\n[ \t]*\z")]
    private static partial Regex TableRowBlankLine();

    [GeneratedRegex(@"^ *-+: *\z")]
    private static partial Regex TableAlignRight();

    [GeneratedRegex(@"^ *:-+: *\z")]
    private static partial Regex TableAlignCenter();

    [GeneratedRegex(@"^ *:-+ *\z")]
    private static partial Regex TableAlignLeft();

    [GeneratedRegex("^<a ", RegexOptions.IgnoreCase)]
    private static partial Regex StartATag();

    [GeneratedRegex("^</a>", RegexOptions.IgnoreCase)]
    private static partial Regex EndATag();

    [GeneratedRegex(@"^<(pre|code|kbd|script)(\s|>)", RegexOptions.IgnoreCase)]
    private static partial Regex StartPreScriptTag();

    [GeneratedRegex(@"^</(pre|code|kbd|script)(\s|>)", RegexOptions.IgnoreCase)]
    private static partial Regex EndPreScriptTag();

    [GeneratedRegex(@"[\p{L}\p{N}]")]
    private static partial Regex UnicodeAlphaNumeric();

    [GeneratedRegex(@"[\t\n\x0B\f\r \u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000\uFEFF]+")]
    private static partial Regex MultipleSpaceGlobal();

    [GeneratedRegex(@"\\\|")]
    private static partial Regex SlashPipe();

    [GeneratedRegex(@"\r\n|\r")]
    private static partial Regex CarriageReturn();

    private static readonly Regex[] NextBulletRegexes = BuildIndent(i => $@"^ {{0,{i}}}(?:[*+-]|\d{{1,9}}[.)])((?:[ \t][^\n]*)?(?:\n|$))");
    private static readonly Regex[] HrRegexes = BuildIndent(i => $@"^ {{0,{i}}}((?:- *){{3,}}|(?:_ *){{3,}}|(?:\* *){{3,}})(?:\n+|$)");
    private static readonly Regex[] FencesBeginRegexes = BuildIndent(i => $"^ {{0,{i}}}(?:```|~~~)");
    private static readonly Regex[] HeadingBeginRegexes = BuildIndent(i => $"^ {{0,{i}}}#");
    private static readonly Regex[] HtmlBeginRegexes = BuildIndent(i => $"^ {{0,{i}}}<(?:[a-z].*>|!--)", "i");
    private static readonly Regex[] BlockquoteBeginRegexes = BuildIndent(i => $"^ {{0,{i}}}>");

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Regex> ListItemRegexes = new();

    private static Regex[] BuildIndent(Func<int, string> source, string flags = "") =>
        Enumerable.Range(0, 4).Select(i => JsRegex.Create(source(i), flags)).ToArray();

    private static Regex IndentRegex(Regex[] regexes, int indent) => regexes[Math.Max(0, Math.Min(3, indent - 1))];

    private static bool Truthy(Group g) => g.Success && g.Value.Length > 0;

    // ----- helpers.ts -----

    private static string RTrim(string str, char c)
    {
        var l = str.Length;
        var suffLen = 0;
        while (suffLen < l && str[l - suffLen - 1] == c) suffLen++;
        return str[..(l - suffLen)];
    }

    private static string TrimTrailingBlankLines(string str)
    {
        var lines = str.Split('\n');
        var end = lines.Length - 1;
        while (end >= 0 && BlankLine().IsMatch(lines[end])) end--;
        if (lines.Length - end <= 2) return str;
        return string.Join("\n", lines.Take(end + 1));
    }

    private static int FindClosingBracket(string str, string b)
    {
        if (str.IndexOf(b[1]) == -1) return -1;
        var level = 0;
        for (var i = 0; i < str.Length; i++)
        {
            if (str[i] == '\\') i++;
            else if (str[i] == b[0]) level++;
            else if (str[i] == b[1])
            {
                level--;
                if (level < 0) return i;
            }
        }
        return level > 0 ? -2 : -1;
    }

    private static string ExpandTabs(string line, int indent = 0)
    {
        var col = indent;
        var expanded = new System.Text.StringBuilder();
        foreach (var ch in line)
        {
            if (ch == '\t')
            {
                var added = 4 - (col % 4);
                expanded.Append(' ', added);
                col += added;
            }
            else
            {
                expanded.Append(ch);
                // JS iterates code points; a surrogate pair advances the column once.
                if (!char.IsLowSurrogate(ch)) col++;
            }
        }
        return expanded.ToString();
    }

    private static List<string> SplitCells(string tableRow, int? count = null)
    {
        var sb = new System.Text.StringBuilder();
        for (var offset = 0; offset < tableRow.Length; offset++)
        {
            if (tableRow[offset] != '|')
            {
                sb.Append(tableRow[offset]);
                continue;
            }
            var escaped = false;
            var curr = offset;
            while (--curr >= 0 && tableRow[curr] == '\\') escaped = !escaped;
            sb.Append(escaped ? "|" : " |");
        }
        var cells = sb.ToString().Split(" |").ToList();
        if (cells[0].Trim().Length == 0) cells.RemoveAt(0);
        if (cells.Count > 0 && cells[^1].Trim().Length == 0) cells.RemoveAt(cells.Count - 1);
        if (count is { } c && c > 0)
        {
            if (cells.Count > c) cells.RemoveRange(c, cells.Count - c);
            else while (cells.Count < c) cells.Add("");
        }
        for (var i = 0; i < cells.Count; i++) cells[i] = SlashPipe().Replace(cells[i].Trim(), "|");
        return cells;
    }

    private static int SearchNonSpace(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] != ' ') return i;
        }
        return -1;
    }

    // ----- Lexer -----

    public List<MdToken> Lex(string src)
    {
        src = CarriageReturn().Replace(src, "\n");
        var tokens = BlockTokens(src, []);
        for (var i = 0; i < _inlineQueue.Count; i++)
        {
            InlineTokens(_inlineQueue[i].Src, _inlineQueue[i].Tokens);
        }
        _inlineQueue.Clear();
        return tokens;
    }

    private List<MdToken> BlockTokens(string src, List<MdToken> tokens, bool lastParagraphClipped = false)
    {
        var srcLength = int.MaxValue;
        while (src.Length > 0)
        {
            if (src.Length < srcLength) srcLength = src.Length;
            else throw new InvalidOperationException("Infinite loop on byte: " + (int)src[0]);

            MdToken? token = null;
            var handled = false;
            foreach (var ext in _blockExtensions)
            {
                token = ext.Tokenizer(src);
                if (token is null) continue;
                src = src[token.Raw.Length..];
                tokens.Add(token);
                handled = true;
                break;
            }
            if (handled) continue;

            if ((token = Space(src)) is not null)
            {
                src = src[token.Raw.Length..];
                if (token.Raw.Length == 1 && tokens.Count > 0) tokens[^1].Raw += "\n";
                else tokens.Add(token);
                continue;
            }

            if ((token = Code(src)) is not null)
            {
                src = src[token.Raw.Length..];
                var last = tokens.Count > 0 ? tokens[^1] : null;
                if (last?.Type is "paragraph" or "text")
                {
                    last.Raw += (last.Raw.EndsWith('\n') ? "" : "\n") + token.Raw;
                    last.Text += "\n" + token.Text;
                    _inlineQueue[^1].Src = last.Text;
                }
                else
                {
                    tokens.Add(token);
                }
                continue;
            }

            if ((token = Fences(src)) is not null || (token = Heading(src)) is not null || (token = Hr(src)) is not null
                || (token = Blockquote(src)) is not null || (token = List(src)) is not null || (token = Html(src)) is not null)
            {
                src = src[token.Raw.Length..];
                tokens.Add(token);
                continue;
            }

            if ((token = Def(src)) is not null)
            {
                src = src[token.Raw.Length..];
                var last = tokens.Count > 0 ? tokens[^1] : null;
                if (last?.Type is "paragraph" or "text")
                {
                    last.Raw += (last.Raw.EndsWith('\n') ? "" : "\n") + token.Raw;
                    last.Text += "\n" + token.Raw;
                    _inlineQueue[^1].Src = last.Text;
                }
                else if (!_links.ContainsKey(token.Tag!))
                {
                    _links[token.Tag!] = (token.Href ?? "", token.Title);
                    tokens.Add(token);
                }
                continue;
            }

            if ((token = Table(src)) is not null || (token = LHeading(src)) is not null)
            {
                src = src[token.Raw.Length..];
                tokens.Add(token);
                continue;
            }

            var cutSrc = src;
            if (_blockExtensions.Count > 0)
            {
                var startIndex = int.MaxValue;
                var tempSrc = src[1..];
                foreach (var ext in _blockExtensions)
                {
                    if (ext.Start(tempSrc) is { } s && s >= 0) startIndex = Math.Min(startIndex, s);
                }
                if (startIndex < int.MaxValue) cutSrc = src[..Math.Min(src.Length, startIndex + 1)];
            }
            if (_top && (token = Paragraph(cutSrc)) is not null)
            {
                var last = tokens.Count > 0 ? tokens[^1] : null;
                if (lastParagraphClipped && last?.Type == "paragraph")
                {
                    last.Raw += (last.Raw.EndsWith('\n') ? "" : "\n") + token.Raw;
                    last.Text += "\n" + token.Text;
                    _inlineQueue.RemoveAt(_inlineQueue.Count - 1);
                    _inlineQueue[^1].Src = last.Text;
                }
                else
                {
                    tokens.Add(token);
                }
                lastParagraphClipped = cutSrc.Length != src.Length;
                src = src[token.Raw.Length..];
                continue;
            }

            if ((token = TextBlock(src)) is not null)
            {
                src = src[token.Raw.Length..];
                var last = tokens.Count > 0 ? tokens[^1] : null;
                if (last?.Type == "text")
                {
                    last.Raw += (last.Raw.EndsWith('\n') ? "" : "\n") + token.Raw;
                    last.Text += "\n" + token.Text;
                    _inlineQueue.RemoveAt(_inlineQueue.Count - 1);
                    _inlineQueue[^1].Src = last.Text;
                }
                else
                {
                    tokens.Add(token);
                }
                continue;
            }

            if (src.Length > 0) throw new InvalidOperationException("Infinite loop on byte: " + (int)src[0]);
        }
        _top = true;
        return tokens;
    }

    private List<MdToken> Inline(string src, List<MdToken>? tokens = null)
    {
        tokens ??= [];
        _inlineQueue.Add(new InlineQueueItem(src, tokens));
        return tokens;
    }

    public List<MdToken> InlineTokens(string src, List<MdToken>? tokens = null)
    {
        tokens ??= [];
        var maskedSrc = src;
        Match? match;

        if (_links.Count > 0)
        {
            var lastIndex = 0;
            while ((match = JsRegex.ExecFrom(MarkedRules.InlineReflinkSearch, maskedSrc, ref lastIndex)) is not null)
            {
                var m0 = match.Value;
                var label = m0[(m0.LastIndexOf('[') + 1)..^1];
                if (_links.ContainsKey(label))
                {
                    maskedSrc = maskedSrc[..match.Index] + "[" + new string('a', m0.Length - 2) + "]" + maskedSrc[lastIndex..];
                }
            }
        }

        {
            var lastIndex = 0;
            while ((match = JsRegex.ExecFrom(MarkedRules.InlineAnyPunctuation, maskedSrc, ref lastIndex)) is not null)
            {
                maskedSrc = maskedSrc[..match.Index] + "++" + maskedSrc[lastIndex..];
            }
        }

        {
            var lastIndex = 0;
            while ((match = JsRegex.ExecFrom(MarkedRules.InlineBlockSkip, maskedSrc, ref lastIndex)) is not null)
            {
                // The empty group after (?<!`) always captures "" when lookbehind is supported, so the offset is 0.
                maskedSrc = maskedSrc[..match.Index] + "[" + new string('a', Math.Max(0, match.Length - 2)) + "]" + maskedSrc[lastIndex..];
            }
        }

        var keepPrevChar = false;
        var prevChar = "";
        var srcLength = int.MaxValue;
        while (src.Length > 0)
        {
            if (src.Length < srcLength) srcLength = src.Length;
            else throw new InvalidOperationException("Infinite loop on byte: " + (int)src[0]);

            if (!keepPrevChar) prevChar = "";
            keepPrevChar = false;

            MdToken? token = null;
            var handled = false;
            foreach (var ext in _inlineExtensions)
            {
                token = ext.Tokenizer(src);
                if (token is null) continue;
                src = src[token.Raw.Length..];
                tokens.Add(token);
                handled = true;
                break;
            }
            if (handled) continue;

            if ((token = Escape(src)) is not null || (token = Tag(src)) is not null || (token = Link(src)) is not null)
            {
                src = src[token.Raw.Length..];
                tokens.Add(token);
                continue;
            }

            if ((token = RefLink(src)) is not null)
            {
                src = src[token.Raw.Length..];
                var last = tokens.Count > 0 ? tokens[^1] : null;
                if (token.Type == "text" && last?.Type == "text")
                {
                    last.Raw += token.Raw;
                    last.Text += token.Text;
                }
                else
                {
                    tokens.Add(token);
                }
                continue;
            }

            if ((token = EmStrong(src, maskedSrc, prevChar)) is not null || (token = CodeSpan(src)) is not null || (token = Br(src)) is not null
                || (token = (_delOverride ?? Del)(src, maskedSrc, prevChar)) is not null || (token = AutoLink(src)) is not null
                || (!_inLink && (token = Url(src)) is not null))
            {
                src = src[token.Raw.Length..];
                tokens.Add(token);
                continue;
            }

            var cutSrc = src;
            if (_inlineExtensions.Count > 0)
            {
                var startIndex = int.MaxValue;
                var tempSrc = src[1..];
                foreach (var ext in _inlineExtensions)
                {
                    if (ext.Start(tempSrc) is { } s && s >= 0) startIndex = Math.Min(startIndex, s);
                }
                if (startIndex < int.MaxValue) cutSrc = src[..Math.Min(src.Length, startIndex + 1)];
            }
            if ((token = InlineText(cutSrc)) is not null)
            {
                src = src[token.Raw.Length..];
                if (!token.Raw.EndsWith('_')) prevChar = token.Raw.Length > 0 ? token.Raw[^1].ToString() : "";
                keepPrevChar = true;
                var last = tokens.Count > 0 ? tokens[^1] : null;
                if (last?.Type == "text")
                {
                    last.Raw += token.Raw;
                    last.Text += token.Text;
                }
                else
                {
                    tokens.Add(token);
                }
                continue;
            }

            if (src.Length > 0) throw new InvalidOperationException("Infinite loop on byte: " + (int)src[0]);
        }
        return tokens;
    }

    // ----- Tokenizer -----

    private MdToken? Space(string src)
    {
        var cap = MarkedRules.BlockNewline.Match(src);
        return cap.Success && cap.Length > 0 ? new MdToken { Type = "space", Raw = cap.Value } : null;
    }

    private MdToken? Code(string src)
    {
        var cap = MarkedRules.BlockCode.Match(src);
        if (!cap.Success) return null;
        var raw = TrimTrailingBlankLines(cap.Value);
        return new MdToken { Type = "code", Raw = raw, CodeBlockStyle = "indented", Text = CodeRemoveIndent().Replace(raw, "") };
    }

    private static string IndentCodeCompensate(string raw, string text)
    {
        var m = IndentCodeCompensation().Match(raw);
        if (!m.Success) return text;
        var indentToCode = m.Groups[1].Value;
        return string.Join("\n", text.Split('\n').Select(node =>
        {
            var mi = BeginningSpace().Match(node);
            if (!mi.Success) return node;
            return mi.Value.Length >= indentToCode.Length ? node[indentToCode.Length..] : node;
        }));
    }

    private MdToken? Fences(string src)
    {
        var cap = MarkedRules.BlockFences.Match(src);
        if (!cap.Success) return null;
        var raw = cap.Value;
        var text = IndentCodeCompensate(raw, cap.Groups[3].Success ? cap.Groups[3].Value : "");
        string? lang = cap.Groups[2].Success ? cap.Groups[2].Value : null;
        if (!string.IsNullOrEmpty(lang)) lang = MarkedRules.InlineAnyPunctuation.Replace(lang.Trim(), "$1");
        return new MdToken { Type = "code", Raw = raw, Lang = lang, Text = text };
    }

    private MdToken? Heading(string src)
    {
        var cap = MarkedRules.BlockHeading.Match(src);
        if (!cap.Success) return null;
        var text = cap.Groups[2].Value.Trim();
        if (text.EndsWith('#'))
        {
            var trimmed = RTrim(text, '#');
            if (trimmed.Length == 0 || trimmed.EndsWith(' ')) text = trimmed.Trim();
        }
        return new MdToken { Type = "heading", Raw = RTrim(cap.Value, '\n'), Depth = cap.Groups[1].Length, Text = text, Tokens = Inline(text) };
    }

    private MdToken? Hr(string src)
    {
        var cap = MarkedRules.BlockHr.Match(src);
        return cap.Success ? new MdToken { Type = "hr", Raw = RTrim(cap.Value, '\n') } : null;
    }

    private MdToken? Blockquote(string src)
    {
        var cap = MarkedRules.BlockBlockquote.Match(src);
        if (!cap.Success) return null;

        var lines = RTrim(cap.Value, '\n').Split('\n').ToList();
        var raw = "";
        var text = "";
        var tokens = new List<MdToken>();

        while (lines.Count > 0)
        {
            var inBlockquote = false;
            var currentLines = new List<string>();
            int i;
            for (i = 0; i < lines.Count; i++)
            {
                if (BlockquoteStart().IsMatch(lines[i]))
                {
                    currentLines.Add(lines[i]);
                    inBlockquote = true;
                }
                else if (!inBlockquote)
                {
                    currentLines.Add(lines[i]);
                }
                else
                {
                    break;
                }
            }
            lines = lines.Skip(i).ToList();

            var currentRaw = string.Join("\n", currentLines);
            var currentText = BlockquoteSetextReplace2().Replace(BlockquoteSetextReplace().Replace(currentRaw, "\n    $1"), "");
            raw = raw.Length > 0 ? $"{raw}\n{currentRaw}" : currentRaw;
            text = text.Length > 0 ? $"{text}\n{currentText}" : currentText;

            var top = _top;
            _top = true;
            BlockTokens(currentText, tokens, true);
            _top = top;

            if (lines.Count == 0) break;

            var lastToken = tokens.Count > 0 ? tokens[^1] : null;
            if (lastToken?.Type == "code")
            {
                break;
            }
            if (lastToken?.Type == "blockquote")
            {
                var newText = lastToken.Raw + "\n" + string.Join("\n", lines);
                var newToken = Blockquote(newText)!;
                tokens[^1] = newToken;
                raw = raw[..(raw.Length - lastToken.Raw.Length)] + newToken.Raw;
                text = text[..(text.Length - lastToken.Text.Length)] + newToken.Text;
                break;
            }
            if (lastToken?.Type == "list")
            {
                var newText = lastToken.Raw + "\n" + string.Join("\n", lines);
                var newToken = List(newText)!;
                tokens[^1] = newToken;
                raw = raw[..(raw.Length - lastToken.Raw.Length)] + newToken.Raw;
                text = text[..(text.Length - lastToken.Raw.Length)] + newToken.Raw;
                lines = newText[tokens[^1].Raw.Length..].Split('\n').ToList();
            }
        }

        return new MdToken { Type = "blockquote", Raw = raw, Tokens = tokens, Text = text };
    }

    private MdToken? List(string src)
    {
        var cap = MarkedRules.BlockList.Match(src);
        if (!cap.Success) return null;

        var bull = cap.Groups[1].Value.Trim();
        var isOrdered = bull.Length > 1;
        var list = new MdToken { Type = "list", Raw = "", Ordered = isOrdered, Start = isOrdered ? int.Parse(bull[..^1]) : null, Loose = false };

        bull = isOrdered ? $@"\d{{1,9}}\{bull[^1]}" : $@"\{bull}";
        var itemRegex = ListItemRegexes.GetOrAdd(bull, b => JsRegex.Create($@"^( {{0,3}}{b})((?:[\t ][^\n]*)?(?:\n|$))"));
        var endsWithBlankLine = false;

        while (src.Length > 0)
        {
            var endEarly = false;
            var itemContents = "";
            cap = itemRegex.Match(src);
            if (!cap.Success) break;
            if (MarkedRules.BlockHr.IsMatch(src)) break;

            var raw = cap.Value;
            src = src[raw.Length..];

            var line = ExpandTabs(cap.Groups[2].Value.Split('\n')[0], cap.Groups[1].Length);
            var nextLine = src.Split('\n')[0];
            var blankLine = line.Trim().Length == 0;

            int indent;
            if (blankLine)
            {
                indent = cap.Groups[1].Length + 1;
            }
            else
            {
                indent = SearchNonSpace(line);
                indent = indent > 4 ? 1 : indent;
                itemContents = line[indent..];
                indent += cap.Groups[1].Length;
            }

            if (blankLine && BlankLine().IsMatch(nextLine))
            {
                raw += nextLine + "\n";
                src = src[Math.Min(src.Length, nextLine.Length + 1)..];
                endEarly = true;
            }

            if (!endEarly)
            {
                var nextBulletRegex = IndentRegex(NextBulletRegexes, indent);
                var hrRegex = IndentRegex(HrRegexes, indent);
                var fencesBeginRegex = IndentRegex(FencesBeginRegexes, indent);
                var headingBeginRegex = IndentRegex(HeadingBeginRegexes, indent);
                var htmlBeginRegex = IndentRegex(HtmlBeginRegexes, indent);
                var blockquoteBeginRegex = IndentRegex(BlockquoteBeginRegexes, indent);

                while (src.Length > 0)
                {
                    var rawLine = src.Split('\n')[0];
                    nextLine = rawLine;
                    var nextLineWithoutTabs = nextLine.Replace("\t", "    ");

                    if (fencesBeginRegex.IsMatch(nextLine) || headingBeginRegex.IsMatch(nextLine) || htmlBeginRegex.IsMatch(nextLine)
                        || blockquoteBeginRegex.IsMatch(nextLine) || nextBulletRegex.IsMatch(nextLine) || hrRegex.IsMatch(nextLine))
                    {
                        break;
                    }

                    if (SearchNonSpace(nextLineWithoutTabs) >= indent || nextLine.Trim().Length == 0)
                    {
                        itemContents += "\n" + (indent <= nextLineWithoutTabs.Length ? nextLineWithoutTabs[indent..] : "");
                    }
                    else
                    {
                        if (blankLine) break;
                        if (SearchNonSpace(line.Replace("\t", "    ")) >= 4) break;
                        if (fencesBeginRegex.IsMatch(line) || headingBeginRegex.IsMatch(line) || hrRegex.IsMatch(line)) break;
                        itemContents += "\n" + nextLine;
                    }

                    blankLine = nextLine.Trim().Length == 0;
                    raw += rawLine + "\n";
                    src = src[Math.Min(src.Length, rawLine.Length + 1)..];
                    line = indent <= nextLineWithoutTabs.Length ? nextLineWithoutTabs[indent..] : "";
                }
            }

            if (!list.Loose)
            {
                if (endsWithBlankLine) list.Loose = true;
                else if (DoubleBlankLine().IsMatch(raw)) endsWithBlankLine = true;
            }

            list.Items.Add(new MdToken
            {
                Type = "list_item",
                Raw = raw,
                Task = ListIsTask().IsMatch(itemContents),
                Loose = false,
                Text = itemContents,
                Tokens = [],
            });
            list.Raw += raw;
        }

        if (list.Items.Count == 0) return null;
        var lastItem = list.Items[^1];
        lastItem.Raw = lastItem.Raw.TrimEnd();
        lastItem.Text = lastItem.Text.TrimEnd();
        list.Raw = list.Raw.TrimEnd();

        foreach (var item in list.Items)
        {
            _top = false;
            item.Tokens = BlockTokens(item.Text, []);
            var itemToken = item.Tokens.Count > 0 ? item.Tokens[0] : null;
            if (item.Task && itemToken?.Type is "text" or "paragraph")
            {
                item.Text = ListReplaceTask().Replace(item.Text, "", 1);
                itemToken.Raw = ListReplaceTask().Replace(itemToken.Raw, "", 1);
                itemToken.Text = ListReplaceTask().Replace(itemToken.Text, "", 1);
                for (var i = _inlineQueue.Count - 1; i >= 0; i--)
                {
                    if (ListIsTask().IsMatch(_inlineQueue[i].Src))
                    {
                        _inlineQueue[i].Src = ListReplaceTask().Replace(_inlineQueue[i].Src, "", 1);
                        break;
                    }
                }

                var taskRaw = ListTaskCheckbox().Match(item.Raw);
                if (taskRaw.Success)
                {
                    var checkbox = new MdToken { Type = "checkbox", Raw = taskRaw.Value + " ", Checked = taskRaw.Value != "[ ]" };
                    item.Checked = checkbox.Checked;
                    if (list.Loose)
                    {
                        if (item.Tokens.Count > 0 && item.Tokens[0].Type is "paragraph" or "text" && item.Tokens[0].Tokens is { } first)
                        {
                            item.Tokens[0].Raw = checkbox.Raw + item.Tokens[0].Raw;
                            item.Tokens[0].Text = checkbox.Raw + item.Tokens[0].Text;
                            first.Insert(0, checkbox);
                        }
                        else
                        {
                            item.Tokens.Insert(0, new MdToken { Type = "paragraph", Raw = checkbox.Raw, Text = checkbox.Raw, Tokens = [checkbox] });
                        }
                    }
                    else
                    {
                        item.Tokens.Insert(0, checkbox);
                    }
                }
            }
            else if (item.Task)
            {
                item.Task = false;
            }

            if (!list.Loose)
            {
                var spacers = item.Tokens.Where(t => t.Type == "space").ToList();
                list.Loose = spacers.Count > 0 && spacers.Any(t => AnyLine().IsMatch(t.Raw));
            }
        }

        if (list.Loose)
        {
            foreach (var item in list.Items)
            {
                item.Loose = true;
                foreach (var token in item.Tokens!)
                {
                    if (token.Type == "text") token.Type = "paragraph";
                }
            }
        }

        return list;
    }

    private MdToken? Html(string src)
    {
        var cap = MarkedRules.BlockHtml.Match(src);
        if (!cap.Success) return null;
        var raw = TrimTrailingBlankLines(cap.Value);
        var tag = cap.Groups[1].Value;
        return new MdToken { Type = "html", Block = true, Raw = raw, Pre = tag is "pre" or "script" or "style", Text = raw };
    }

    private MdToken? Def(string src)
    {
        var cap = MarkedRules.BlockDef.Match(src);
        if (!cap.Success) return null;
        var tag = MultipleSpaceGlobal().Replace(cap.Groups[1].Value.ToLowerInvariant(), " ");
        var href = Truthy(cap.Groups[2]) ? MarkedRules.InlineAnyPunctuation.Replace(HrefBrackets().Replace(cap.Groups[2].Value, "$1", 1), "$1") : "";
        string? title = Truthy(cap.Groups[3])
            ? MarkedRules.InlineAnyPunctuation.Replace(cap.Groups[3].Value[1..^1], "$1")
            : cap.Groups[3].Success ? cap.Groups[3].Value : null;
        return new MdToken { Type = "def", Tag = tag, Raw = RTrim(cap.Value, '\n'), Href = href, Title = title };
    }

    private MdToken? Table(string src)
    {
        var cap = MarkedRules.BlockTable.Match(src);
        if (!cap.Success) return null;
        if (!TableDelimiter().IsMatch(cap.Groups[2].Value)) return null;

        var headers = SplitCells(cap.Groups[1].Value);
        var aligns = TableAlignChars().Replace(cap.Groups[2].Value, "").Split('|');
        var rows = cap.Groups[3].Success && cap.Groups[3].Value.Trim().Length > 0
            ? TableRowBlankLine().Replace(cap.Groups[3].Value, "", 1).Split('\n')
            : [];

        var item = new MdToken { Type = "table", Raw = RTrim(cap.Value, '\n') };
        if (headers.Count != aligns.Length) return null;

        foreach (var align in aligns)
        {
            if (TableAlignRight().IsMatch(align)) item.Align.Add("right");
            else if (TableAlignCenter().IsMatch(align)) item.Align.Add("center");
            else if (TableAlignLeft().IsMatch(align)) item.Align.Add("left");
            else item.Align.Add(null);
        }

        for (var i = 0; i < headers.Count; i++)
        {
            item.Header.Add(new MdTableCell { Text = headers[i], Tokens = Inline(headers[i]), Header = true, Align = item.Align[i] });
        }

        foreach (var row in rows)
        {
            item.Rows.Add(SplitCells(row, item.Header.Count)
                .Select((cell, i) => new MdTableCell { Text = cell, Tokens = Inline(cell), Header = false, Align = i < item.Align.Count ? item.Align[i] : null })
                .ToList());
        }

        return item;
    }

    private MdToken? LHeading(string src)
    {
        var cap = MarkedRules.BlockLheading.Match(src);
        if (!cap.Success) return null;
        var text = cap.Groups[1].Value.Trim();
        return new MdToken { Type = "heading", Raw = RTrim(cap.Value, '\n'), Depth = cap.Groups[2].Value[0] == '=' ? 1 : 2, Text = text, Tokens = Inline(text) };
    }

    private MdToken? Paragraph(string src)
    {
        var cap = MarkedRules.BlockParagraph.Match(src);
        if (!cap.Success) return null;
        var g1 = cap.Groups[1].Value;
        var text = g1.EndsWith('\n') ? g1[..^1] : g1;
        return new MdToken { Type = "paragraph", Raw = cap.Value, Text = text, Tokens = Inline(text) };
    }

    private MdToken? TextBlock(string src)
    {
        var cap = MarkedRules.BlockText.Match(src);
        return cap.Success ? new MdToken { Type = "text", Raw = cap.Value, Text = cap.Value, Tokens = Inline(cap.Value) } : null;
    }

    private MdToken? Escape(string src)
    {
        var cap = MarkedRules.InlineEscape.Match(src);
        return cap.Success ? new MdToken { Type = "escape", Raw = cap.Value, Text = cap.Groups[1].Value } : null;
    }

    private MdToken? Tag(string src)
    {
        var cap = MarkedRules.InlineTag.Match(src);
        if (!cap.Success) return null;
        if (!_inLink && StartATag().IsMatch(cap.Value)) _inLink = true;
        else if (_inLink && EndATag().IsMatch(cap.Value)) _inLink = false;
        if (!_inRawBlock && StartPreScriptTag().IsMatch(cap.Value)) _inRawBlock = true;
        else if (_inRawBlock && EndPreScriptTag().IsMatch(cap.Value)) _inRawBlock = false;
        return new MdToken { Type = "html", Raw = cap.Value, InLink = _inLink, InRawBlock = _inRawBlock, Block = false, Text = cap.Value };
    }

    private MdToken OutputLink(string cap0, string cap1, string href, string? title, string raw)
    {
        var text = OutputLinkReplace().Replace(cap1, "$1");
        _inLink = true;
        var token = new MdToken
        {
            Type = cap0.StartsWith('!') ? "image" : "link",
            Raw = raw,
            Href = href,
            Title = string.IsNullOrEmpty(title) ? null : title,
            Text = text,
            Tokens = InlineTokens(text),
        };
        _inLink = false;
        return token;
    }

    private MdToken? Link(string src)
    {
        var cap = MarkedRules.InlineLink.Match(src);
        if (!cap.Success) return null;
        var cap0 = cap.Value;
        var cap1 = cap.Groups[1].Value;
        var cap2 = cap.Groups[2].Value;
        var cap3 = cap.Groups[3].Success ? cap.Groups[3].Value : null;

        var trimmedUrl = cap2.Trim();
        if (trimmedUrl.StartsWith('<'))
        {
            if (!trimmedUrl.EndsWith('>')) return null;
            var rtrimSlash = RTrim(trimmedUrl[..^1], '\\');
            if ((trimmedUrl.Length - rtrimSlash.Length) % 2 == 0) return null;
        }
        else
        {
            var lastParenIndex = FindClosingBracket(cap2, "()");
            if (lastParenIndex == -2) return null;
            if (lastParenIndex > -1)
            {
                var start = cap0.IndexOf('!') == 0 ? 5 : 4;
                var linkLen = start + cap1.Length + lastParenIndex;
                cap2 = cap2[..lastParenIndex];
                cap0 = cap0[..linkLen].Trim();
                cap3 = "";
            }
        }

        var title = !string.IsNullOrEmpty(cap3) ? cap3[1..^1] : "";
        var href = cap2.Trim();
        if (href.StartsWith('<')) href = href.Length >= 2 ? href[1..^1] : "";
        return OutputLink(
            cap0,
            cap1,
            href.Length > 0 ? MarkedRules.InlineAnyPunctuation.Replace(href, "$1") : href,
            title.Length > 0 ? MarkedRules.InlineAnyPunctuation.Replace(title, "$1") : title,
            cap0);
    }

    private MdToken? RefLink(string src)
    {
        var cap = MarkedRules.InlineReflink.Match(src);
        if (!cap.Success) cap = MarkedRules.InlineNolink.Match(src);
        if (!cap.Success) return null;
        var linkString = MultipleSpaceGlobal().Replace(Truthy(cap.Groups[2]) ? cap.Groups[2].Value : cap.Groups[1].Value, " ");
        if (!_links.TryGetValue(linkString.ToLowerInvariant(), out var link))
        {
            var text = cap.Value[0].ToString();
            return new MdToken { Type = "text", Raw = text, Text = text };
        }
        return OutputLink(cap.Value, cap.Groups[1].Value, link.Href, link.Title, cap.Value);
    }

    private MdToken? EmStrong(string src, string maskedSrc, string prevChar)
    {
        var match = MarkedRules.InlineEmStrongLDelim.Match(src);
        if (!match.Success) return null;
        if (!Truthy(match.Groups[1]) && !Truthy(match.Groups[2]) && !Truthy(match.Groups[3]) && !Truthy(match.Groups[4])) return null;
        if (Truthy(match.Groups[4]) && UnicodeAlphaNumeric().IsMatch(prevChar)) return null;

        var nextChar = Truthy(match.Groups[1]) ? match.Groups[1].Value : Truthy(match.Groups[3]) ? match.Groups[3].Value : "";
        if (nextChar.Length > 0 && prevChar.Length > 0 && !MarkedRules.InlinePunctuation.IsMatch(prevChar)) return null;

        var lLength = JsRegex.CodePointCount(match.Value) - 1;
        var delimTotal = lLength;
        var midDelimTotal = 0;
        var endReg = match.Value[0] == '*' ? MarkedRules.InlineEmStrongRDelimAst : MarkedRules.InlineEmStrongRDelimUnd;
        var sliceStart = maskedSrc.Length - src.Length + lLength;
        maskedSrc = sliceStart >= 0 && sliceStart <= maskedSrc.Length ? maskedSrc[sliceStart..] : maskedSrc;

        var lastIndex = 0;
        while ((match = JsRegex.ExecFrom(endReg, maskedSrc, ref lastIndex)) is not null)
        {
            var rDelim = FirstTruthy(match, 6);
            if (rDelim is null) continue;
            var rLength = JsRegex.CodePointCount(rDelim);

            if (Truthy(match.Groups[3]) || Truthy(match.Groups[4]))
            {
                delimTotal += rLength;
                continue;
            }
            if (Truthy(match.Groups[5]) || Truthy(match.Groups[6]))
            {
                if (lLength % 3 != 0 && (lLength + rLength) % 3 == 0)
                {
                    midDelimTotal += rLength;
                    continue;
                }
            }

            delimTotal -= rLength;
            if (delimTotal > 0) continue;

            rLength = Math.Min(rLength, rLength + delimTotal + midDelimTotal);
            var lastCharLength = JsRegex.FirstCodePointLength(match.Value);
            var raw = src[..Math.Min(src.Length, lLength + match.Index + lastCharLength + rLength)];

            if (Math.Min(lLength, rLength) % 2 == 1)
            {
                var emText = raw.Length >= 2 ? raw[1..^1] : "";
                return new MdToken { Type = "em", Raw = raw, Text = emText, Tokens = InlineTokens(emText) };
            }
            var strongText = raw.Length >= 4 ? raw[2..^2] : "";
            return new MdToken { Type = "strong", Raw = raw, Text = strongText, Tokens = InlineTokens(strongText) };
        }
        return null;
    }

    private static string? FirstTruthy(Match match, int groups)
    {
        for (var i = 1; i <= groups; i++)
        {
            if (Truthy(match.Groups[i])) return match.Groups[i].Value;
        }
        return null;
    }

    private MdToken? CodeSpan(string src)
    {
        var cap = MarkedRules.InlineCode.Match(src);
        if (!cap.Success) return null;
        var text = cap.Groups[2].Value.Replace('\n', ' ');
        var hasNonSpaceChars = text.Any(c => c != ' ');
        var hasSpaceCharsOnBothEnds = text.StartsWith(' ') && text.EndsWith(' ');
        if (hasNonSpaceChars && hasSpaceCharsOnBothEnds) text = text[1..^1];
        return new MdToken { Type = "codespan", Raw = cap.Value, Text = text };
    }

    private MdToken? Br(string src)
    {
        var cap = MarkedRules.InlineBr.Match(src);
        return cap.Success ? new MdToken { Type = "br", Raw = cap.Value } : null;
    }

    private MdToken? Del(string src, string maskedSrc, string prevChar)
    {
        var match = MarkedRules.InlineDelLDelim.Match(src);
        if (!match.Success) return null;
        var nextChar = Truthy(match.Groups[1]) ? match.Groups[1].Value : "";
        if (nextChar.Length > 0 && prevChar.Length > 0 && !MarkedRules.InlinePunctuation.IsMatch(prevChar)) return null;

        var lLength = JsRegex.CodePointCount(match.Value) - 1;
        var delimTotal = lLength;
        var sliceStart = maskedSrc.Length - src.Length + lLength;
        maskedSrc = sliceStart >= 0 && sliceStart <= maskedSrc.Length ? maskedSrc[sliceStart..] : maskedSrc;

        var lastIndex = 0;
        while ((match = JsRegex.ExecFrom(MarkedRules.InlineDelRDelim, maskedSrc, ref lastIndex)) is not null)
        {
            var rDelim = FirstTruthy(match, 6);
            if (rDelim is null) continue;
            var rLength = JsRegex.CodePointCount(rDelim);
            if (rLength != lLength) continue;
            if (Truthy(match.Groups[3]) || Truthy(match.Groups[4]))
            {
                delimTotal += rLength;
                continue;
            }
            delimTotal -= rLength;
            if (delimTotal > 0) continue;
            rLength = Math.Min(rLength, rLength + delimTotal);
            var lastCharLength = JsRegex.FirstCodePointLength(match.Value);
            var raw = src[..Math.Min(src.Length, lLength + match.Index + lastCharLength + rLength)];
            var text = raw.Length >= 2 * lLength ? raw[lLength..^lLength] : "";
            return new MdToken { Type = "del", Raw = raw, Text = text, Tokens = InlineTokens(text) };
        }
        return null;
    }

    private MdToken? AutoLink(string src)
    {
        var cap = MarkedRules.InlineAutolink.Match(src);
        if (!cap.Success) return null;
        var text = cap.Groups[1].Value;
        var href = cap.Groups[2].Value == "@" ? "mailto:" + text : text;
        return new MdToken { Type = "link", Raw = cap.Value, Text = text, Href = href, Tokens = [new MdToken { Type = "text", Raw = text, Text = text }] };
    }

    private MdToken? Url(string src)
    {
        var cap = MarkedRules.InlineUrl.Match(src);
        if (!cap.Success) return null;
        string text;
        string href;
        var cap0 = cap.Value;
        if (cap.Groups[2].Value == "@")
        {
            text = cap0;
            href = "mailto:" + text;
        }
        else
        {
            string prev;
            do
            {
                prev = cap0;
                var bp = MarkedRules.InlineBackpedal.Match(cap0);
                cap0 = bp.Success ? bp.Value : "";
            } while (prev != cap0);
            text = cap0;
            href = cap.Groups[1].Value == "www." ? "http://" + cap0 : cap0;
        }
        return new MdToken { Type = "link", Raw = cap0, Text = text, Href = href, Tokens = [new MdToken { Type = "text", Raw = text, Text = text }] };
    }

    private MdToken? InlineText(string src)
    {
        var cap = MarkedRules.InlineText.Match(src);
        return cap.Success ? new MdToken { Type = "text", Raw = cap.Value, Text = cap.Value, Escaped = _inRawBlock } : null;
    }
}
