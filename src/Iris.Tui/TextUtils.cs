using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Iris.Tui;

public readonly record struct AnsiCode(string Code, int Length);

public readonly record struct TextSlice(string Text, int Width);

public readonly record struct LineSegments(string Before, int BeforeWidth, string After, int AfterWidth);

/// <summary>Terminal text measurement, wrapping, truncation and slicing with ANSI awareness. Port of pi-tui utils.ts.</summary>
public static partial class TextUtils
{
    private const int WidthCacheSize = 512;
    private static readonly Dictionary<string, int> WidthCache = new(StringComparer.Ordinal);
    private static readonly LinkedList<string> WidthCacheOrder = [];
    private static readonly object WidthCacheLock = new();

    /// <summary>Enumerate extended grapheme clusters.</summary>
    public static IEnumerable<string> Graphemes(string text)
    {
        var index = 0;
        while (index < text.Length)
        {
            var length = StringInfo.GetNextTextElementLength(text, index);
            if (length <= 0) length = 1;
            yield return text.Substring(index, length);
            index += length;
        }
    }

    private static bool IsPrintableAscii(string str)
    {
        foreach (var c in str)
        {
            if (c < 0x20 || c > 0x7e) return false;
        }
        return true;
    }

    private static IEnumerable<int> CodePoints(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                yield return char.ConvertToUtf32(s[i], s[i + 1]);
                i++;
            }
            else
            {
                yield return s[i];
            }
        }
    }

    public static bool IsCjkBreakChar(string segment) => CodePoints(segment).Any(cp => UnicodeTables.InRanges(UnicodeTables.CjkBreakRanges, cp));

    private static int EastAsianWidth(int cp) => UnicodeTables.InRanges(UnicodeTables.WideRanges, cp) ? 2 : 1;

    private static bool CouldBeEmoji(string segment, int cp) =>
        (cp >= 0x1f000 && cp <= 0x1fbff) || (cp >= 0x2300 && cp <= 0x23ff) || (cp >= 0x2600 && cp <= 0x27bf)
        || (cp >= 0x2b50 && cp <= 0x2b55) || segment.Contains((char)0xFE0F) || segment.Length > 2;

    /// <summary>Approximation of /^\p{RGI_Emoji}$/v using generated single-codepoint tables plus sequence structure.</summary>
    private static bool IsRgiEmoji(string segment)
    {
        var cps = CodePoints(segment).ToList();
        if (cps.Count == 0) return false;
        if (cps.Count == 1) return UnicodeTables.InRanges(UnicodeTables.RgiEmojiSingleRanges, cps[0]);
        if (cps.Count == 2 && cps[1] == 0xFE0F) return UnicodeTables.InRanges(UnicodeTables.RgiEmojiWithVs16Ranges, cps[0]);
        // Keycaps: [0-9#*] FE0F 20E3
        if (cps.Count == 3 && cps[1] == 0xFE0F && cps[2] == 0x20E3) return cps[0] is '#' or '*' or (>= '0' and <= '9');
        // Flags: two regional indicators.
        if (cps.Count == 2 && cps.All(c => c is >= 0x1F1E6 and <= 0x1F1FF)) return true;
        // Skin tones, ZWJ sequences and tag sequences built from emoji components.
        var first = cps[0];
        var firstIsEmoji = UnicodeTables.InRanges(UnicodeTables.RgiEmojiSingleRanges, first) || UnicodeTables.InRanges(UnicodeTables.RgiEmojiWithVs16Ranges, first);
        if (!firstIsEmoji) return false;
        return cps.Skip(1).All(c => c is 0x200D or 0xFE0F or (>= 0x1F3FB and <= 0x1F3FF) or (>= 0xE0020 and <= 0xE007F)
            || UnicodeTables.InRanges(UnicodeTables.RgiEmojiSingleRanges, c) || UnicodeTables.InRanges(UnicodeTables.RgiEmojiWithVs16Ranges, c));
    }

    private static bool AllInRanges(string segment, int[] ranges) => segment.Length > 0 && CodePoints(segment).All(cp => UnicodeTables.InRanges(ranges, cp) || (cp >= 0xD800 && cp <= 0xDFFF && ranges == UnicodeTables.ZeroWidthRanges));

    /// <summary>Terminal width of a single grapheme cluster.</summary>
    public static int GraphemeWidth(string segment)
    {
        if (segment == "\t") return 3;

        var cps = CodePoints(segment).ToList();
        if (cps.Count > 0 && cps.All(cp => UnicodeTables.InRanges(UnicodeTables.TerminalSpacingMarkRanges, cp))) return cps.Count;
        if (cps.Count > 0 && cps.All(cp => UnicodeTables.InRanges(UnicodeTables.ZeroWidthRanges, cp) || cp is >= 0xD800 and <= 0xDFFF)) return 0;

        if (CouldBeEmoji(segment, cps[0]) && IsRgiEmoji(segment)) return 2;

        // Strip leading non-printing code points (Default_Ignorable, Control, Format, Mark, Surrogate).
        var start = 0;
        while (start < cps.Count && (UnicodeTables.InRanges(UnicodeTables.NonPrintingRanges, cps[start]) || cps[start] is >= 0xD800 and <= 0xDFFF)) start++;
        if (start >= cps.Count) return 0;
        var cp = cps[start];

        if (cp is >= 0x1f1e6 and <= 0x1f1ff) return 2;

        var width = EastAsianWidth(cp);
        var followsMark = false;
        for (var i = start + 1; i < cps.Count; i++)
        {
            var c = cps[i];
            if (UnicodeTables.InRanges(UnicodeTables.TerminalSpacingMarkRanges, c))
            {
                width += 1;
                followsMark = false;
            }
            else if (UnicodeTables.InRanges(UnicodeTables.MarkRanges, c))
            {
                followsMark = true;
            }
            else if (!UnicodeTables.InRanges(UnicodeTables.NonPrintingRanges, c))
            {
                if (followsMark || (c >= 0xff00 && c <= 0xffef)) width += EastAsianWidth(c);
                else if (c is 0x0e33 or 0x0eb3) width += 1;
                followsMark = false;
            }
        }
        return width;
    }

    /// <summary>Visible width of a string in terminal columns (tabs count 3, escape sequences 0).</summary>
    public static int VisibleWidth(string str)
    {
        if (str.Length == 0) return 0;
        if (IsPrintableAscii(str)) return str.Length;

        lock (WidthCacheLock)
        {
            if (WidthCache.TryGetValue(str, out var cached)) return cached;
        }

        var clean = str.Contains('\t') ? str.Replace("\t", "   ") : str;
        if (clean.Contains('\e')) clean = StripTerminalSequences(clean);

        var width = 0;
        foreach (var g in Graphemes(clean)) width += GraphemeWidth(g);

        lock (WidthCacheLock)
        {
            if (WidthCache.Count >= WidthCacheSize && WidthCacheOrder.First is { } oldest)
            {
                WidthCache.Remove(oldest.Value);
                WidthCacheOrder.RemoveFirst();
            }
            if (WidthCache.TryAdd(str, width)) WidthCacheOrder.AddLast(str);
        }
        return width;
    }

    /// <summary>Remove ANSI (CSI m/G/K/H/J), OSC and APC sequences.</summary>
    public static string StripTerminalSequences(string str)
    {
        if (!str.Contains('\e')) return str;
        var sb = new StringBuilder(str.Length);
        var i = 0;
        while (i < str.Length)
        {
            if (ExtractAnsiCode(str, i) is { } ansi)
            {
                i += ansi.Length;
                continue;
            }
            sb.Append(str[i]);
            i++;
        }
        return sb.ToString();
    }

    /// <summary>Extract a CSI (ending in m/G/K/H/J), OSC or APC sequence at pos.</summary>
    public static AnsiCode? ExtractAnsiCode(string str, int pos)
    {
        if (pos >= str.Length || str[pos] != '\e' || pos + 1 >= str.Length) return null;
        var next = str[pos + 1];
        if (next == '[')
        {
            var j = pos + 2;
            while (j < str.Length && str[j] is not ('m' or 'G' or 'K' or 'H' or 'J')) j++;
            return j < str.Length ? new AnsiCode(str.Substring(pos, j + 1 - pos), j + 1 - pos) : null;
        }
        if (next is ']' or '_')
        {
            var j = pos + 2;
            while (j < str.Length)
            {
                if (str[j] == '\a') return new AnsiCode(str.Substring(pos, j + 1 - pos), j + 1 - pos);
                if (str[j] == '\e' && j + 1 < str.Length && str[j + 1] == '\\') return new AnsiCode(str.Substring(pos, j + 2 - pos), j + 2 - pos);
                j++;
            }
            return null;
        }
        return null;
    }

    /// <summary>The terminal-cell range occupied by the grapheme at a visible column.</summary>
    public static (int Start, int End)? GetGraphemeCellRange(string line, int column)
    {
        var currentCol = 0;
        var i = 0;
        while (i < line.Length)
        {
            if (ExtractAnsiCode(line, i) is { } ansi)
            {
                i += ansi.Length;
                continue;
            }
            var textEnd = i;
            while (textEnd < line.Length && ExtractAnsiCode(line, textEnd) is null) textEnd++;
            foreach (var g in Graphemes(line[i..textEnd]))
            {
                var w = GraphemeWidth(g);
                if (w > 0 && column >= currentCol && column < currentCol + w) return (currentCol, currentCol + w);
                currentCol += w;
            }
            i = textEnd;
        }
        return null;
    }

    [GeneratedRegex("^\\e\\]8;[^;]*;([^\\a\\e]*)(?:\\a|\\e\\\\)$")]
    private static partial Regex Osc8Link();

    /// <summary>The OSC 8 hyperlink URL covering a visible column.</summary>
    public static string? GetOsc8LinkAtColumn(string line, int column)
    {
        string? activeUrl = null;
        var currentCol = 0;
        var i = 0;
        while (i < line.Length)
        {
            if (ExtractAnsiCode(line, i) is { } ansi)
            {
                var m = Osc8Link().Match(ansi.Code);
                if (m.Success) activeUrl = m.Groups[1].Value.Length > 0 ? m.Groups[1].Value : null;
                i += ansi.Length;
                continue;
            }
            var textEnd = i;
            while (textEnd < line.Length && ExtractAnsiCode(line, textEnd) is null) textEnd++;
            foreach (var g in Graphemes(line[i..textEnd]))
            {
                var w = g == "\t" ? 3 : GraphemeWidth(g);
                if (column >= currentCol && column < currentCol + w) return activeUrl;
                currentCol += w;
            }
            i = textEnd;
        }
        return null;
    }

    /// <summary>Normalize text for terminal output: decompose Thai/Lao AM vowels and expand visible tabs.</summary>
    public static string NormalizeTerminalOutput(string str)
    {
        var normalized = str;
        if (normalized.IndexOfAny([(char)0x0E33, (char)0x0EB3]) >= 0)
        {
            normalized = normalized.Replace("\u0E33", "\u0E4D\u0E32").Replace("\u0EB3", "\u0ECD\u0EB2");
        }
        if (!normalized.Contains('\t')) return normalized;

        var sb = new StringBuilder(normalized.Length + 8);
        var i = 0;
        while (i < normalized.Length)
        {
            if (ExtractAnsiCode(normalized, i) is { } ansi)
            {
                sb.Append(ansi.Code);
                i += ansi.Length;
                continue;
            }
            if (normalized[i] == '\t') sb.Append("   ");
            else sb.Append(normalized[i]);
            i++;
        }
        return sb.ToString();
    }

    // ---- OSC 8 hyperlinks ----

    internal sealed record ActiveHyperlink(string Params, string Url, string Terminator);

    /// <summary>undefined (not OSC 8) → NotOsc8; null (close) → Close; otherwise the link.</summary>
    internal static (bool IsOsc8, ActiveHyperlink? Link) ParseOsc8Hyperlink(string ansiCode)
    {
        if (!ansiCode.StartsWith("\e]8;", StringComparison.Ordinal)) return (false, null);
        var terminator = ansiCode.EndsWith('\a') ? "\a" : "\e\\";
        var body = ansiCode[4..^terminator.Length];
        var separator = body.IndexOf(';');
        if (separator == -1) return (false, null);
        var url = body[(separator + 1)..];
        return url.Length == 0 ? (true, null) : (true, new ActiveHyperlink(body[..separator], url, terminator));
    }

    internal static string FormatOsc8Hyperlink(ActiveHyperlink link) => $"\e]8;{link.Params};{link.Url}{link.Terminator}";

    internal static string FormatOsc8Close(string terminator) => $"\e]8;;{terminator}";

    private static string GetActiveOsc8Close(string prefix)
    {
        if (!prefix.Contains("\e]8;")) return "";
        ActiveHyperlink? active = null;
        var i = 0;
        while (i < prefix.Length)
        {
            if (ExtractAnsiCode(prefix, i) is { } ansi)
            {
                var (isOsc8, link) = ParseOsc8Hyperlink(ansi.Code);
                if (isOsc8) active = link;
                i += ansi.Length;
            }
            else
            {
                i++;
            }
        }
        return active is null ? "" : FormatOsc8Close(active.Terminator);
    }

    internal static void UpdateTrackerFromText(string text, AnsiCodeTracker tracker)
    {
        var i = 0;
        while (i < text.Length)
        {
            if (ExtractAnsiCode(text, i) is { } ansi)
            {
                tracker.Process(ansi.Code);
                i += ansi.Length;
            }
            else
            {
                i++;
            }
        }
    }

    /// <summary>The background color SGR active at the end of an ANSI-styled string.</summary>
    public static string GetActiveBackgroundAnsi(string text)
    {
        var tracker = new AnsiCodeTracker();
        UpdateTrackerFromText(text, tracker);
        return tracker.GetActiveBackgroundCode();
    }

    // ---- wrapping ----

    private static List<string> SplitIntoTokensWithAnsi(string text)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var pendingAnsi = new StringBuilder();
        string? currentKind = null;
        var i = 0;

        void Flush()
        {
            if (current.Length == 0) return;
            tokens.Add(current.ToString());
            current.Clear();
            currentKind = null;
        }

        while (i < text.Length)
        {
            if (ExtractAnsiCode(text, i) is { } ansi)
            {
                pendingAnsi.Append(ansi.Code);
                i += ansi.Length;
                continue;
            }
            var end = i;
            while (end < text.Length && ExtractAnsiCode(text, end) is null) end++;

            foreach (var segment in Graphemes(text[i..end]))
            {
                var isSpace = segment == " ";
                if (!isSpace && IsCjkBreakChar(segment))
                {
                    Flush();
                    tokens.Add(pendingAnsi + segment);
                    pendingAnsi.Clear();
                    continue;
                }
                var kind = isSpace ? "space" : "word";
                if (current.Length > 0 && currentKind != kind) Flush();
                if (pendingAnsi.Length > 0)
                {
                    current.Append(pendingAnsi);
                    pendingAnsi.Clear();
                }
                currentKind = kind;
                current.Append(segment);
            }
            i = end;
        }

        if (pendingAnsi.Length > 0)
        {
            if (current.Length > 0) current.Append(pendingAnsi);
            else if (tokens.Count > 0) tokens[^1] += pendingAnsi.ToString();
            else current.Append(pendingAnsi);
        }
        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens;
    }

    [GeneratedRegex("\r\n|\r|\n")]
    private static partial Regex LineBreak();

    /// <summary>Word-wrap text preserving ANSI state across breaks. No padding.</summary>
    public static List<string> WrapTextWithAnsi(string text, int width)
    {
        if (string.IsNullOrEmpty(text)) return [""];
        var result = new List<string>();
        var tracker = new AnsiCodeTracker();
        foreach (var inputLine in LineBreak().Split(text))
        {
            var prefix = result.Count > 0 ? tracker.GetActiveCodes() : "";
            result.AddRange(WrapSingleLine(prefix + inputLine, width));
            UpdateTrackerFromText(inputLine, tracker);
        }
        return result.Count > 0 ? result : [""];
    }

    private static string TrimEndJs(string s)
    {
        var end = s.Length;
        while (end > 0 && IsJsWhitespace(s[end - 1])) end--;
        return s[..end];
    }

    public static bool IsJsWhitespace(char c) => UnicodeTables.InRanges(UnicodeTables.JsWhitespaceRanges, c);

    private static List<string> WrapSingleLine(string line, int width)
    {
        if (line.Length == 0) return [""];
        if (VisibleWidth(line) <= width) return [line];

        var wrapped = new List<string>();
        var tracker = new AnsiCodeTracker();
        var currentLine = "";
        var currentVisible = 0;

        foreach (var token in SplitIntoTokensWithAnsi(line))
        {
            var tokenWidth = VisibleWidth(token);
            var isWhitespace = token.Trim().Length == 0;

            if (tokenWidth > width && !isWhitespace)
            {
                if (currentLine.Length > 0)
                {
                    currentLine += tracker.GetLineEndReset();
                    wrapped.Add(currentLine);
                    currentLine = "";
                    currentVisible = 0;
                }
                var broken = BreakLongWord(token, width, tracker);
                for (var i = 0; i < broken.Count - 1; i++) wrapped.Add(broken[i]);
                currentLine = broken[^1];
                currentVisible = VisibleWidth(currentLine);
                continue;
            }

            if (currentVisible + tokenWidth > width && currentVisible > 0)
            {
                wrapped.Add(TrimEndJs(currentLine) + tracker.GetLineEndReset());
                if (isWhitespace)
                {
                    currentLine = tracker.GetActiveCodes();
                    currentVisible = 0;
                }
                else
                {
                    currentLine = tracker.GetActiveCodes() + token;
                    currentVisible = tokenWidth;
                }
            }
            else
            {
                currentLine += token;
                currentVisible += tokenWidth;
            }
            UpdateTrackerFromText(token, tracker);
        }

        if (currentLine.Length > 0) wrapped.Add(currentLine);
        return wrapped.Count > 0 ? wrapped.Select(TrimEndJs).ToList() : [""];
    }

    private static List<string> BreakLongWord(string word, int width, AnsiCodeTracker tracker)
    {
        var lines = new List<string>();
        var currentLine = new StringBuilder(tracker.GetActiveCodes());
        var currentWidth = 0;

        var segments = new List<(bool IsAnsi, string Value)>();
        var i = 0;
        while (i < word.Length)
        {
            if (ExtractAnsiCode(word, i) is { } ansi)
            {
                segments.Add((true, ansi.Code));
                i += ansi.Length;
                continue;
            }
            var end = i;
            while (end < word.Length && ExtractAnsiCode(word, end) is null) end++;
            foreach (var g in Graphemes(word[i..end])) segments.Add((false, g));
            i = end;
        }

        foreach (var (isAnsi, value) in segments)
        {
            if (isAnsi)
            {
                currentLine.Append(value);
                tracker.Process(value);
                continue;
            }
            if (value.Length == 0) continue;
            var gw = VisibleWidth(value);
            if (currentWidth + gw > width)
            {
                currentLine.Append(tracker.GetLineEndReset());
                lines.Add(currentLine.ToString());
                currentLine.Clear().Append(tracker.GetActiveCodes());
                currentWidth = 0;
            }
            currentLine.Append(value);
            currentWidth += gw;
        }
        if (currentLine.Length > 0) lines.Add(currentLine.ToString());
        return lines.Count > 0 ? lines : [""];
    }

    private const string PunctuationChars = "(){}[]<>.,;:'\"!?+-=*/\\|&%^$#@~`";

    public static bool IsWhitespaceChar(string ch) => ch.Length > 0 && IsJsWhitespace(ch[0]);

    public static bool IsPunctuationChar(string ch) => ch.Length > 0 && PunctuationChars.Contains(ch[0]);

    /// <summary>Apply a background to a line padded to the given width.</summary>
    public static string ApplyBackgroundToLine(string line, int width, Func<string, string> bg) =>
        bg(line + new string(' ', Math.Max(0, width - VisibleWidth(line))));

    // ---- truncation ----

    private static TextSlice TruncateFragmentToWidth(string text, int maxWidth)
    {
        if (maxWidth <= 0 || text.Length == 0) return new TextSlice("", 0);
        if (IsPrintableAscii(text))
        {
            var clipped = text.Length > maxWidth ? text[..maxWidth] : text;
            return new TextSlice(clipped, clipped.Length);
        }

        var result = new StringBuilder();
        var width = 0;
        var hasAnsi = text.Contains('\e');
        var hasTabs = text.Contains('\t');
        if (!hasAnsi && !hasTabs)
        {
            foreach (var g in Graphemes(text))
            {
                var w = GraphemeWidth(g);
                if (width + w > maxWidth) break;
                result.Append(g);
                width += w;
            }
            return new TextSlice(result.ToString(), width);
        }

        var i = 0;
        var pendingAnsi = new StringBuilder();
        while (i < text.Length)
        {
            if (ExtractAnsiCode(text, i) is { } ansi)
            {
                pendingAnsi.Append(ansi.Code);
                i += ansi.Length;
                continue;
            }
            if (text[i] == '\t')
            {
                if (width + 3 > maxWidth) break;
                result.Append(pendingAnsi).Append('\t');
                pendingAnsi.Clear();
                width += 3;
                i++;
                continue;
            }
            var end = i;
            while (end < text.Length && text[end] != '\t' && ExtractAnsiCode(text, end) is null) end++;
            foreach (var g in Graphemes(text[i..end]))
            {
                var w = GraphemeWidth(g);
                if (width + w > maxWidth) return new TextSlice(result.ToString(), width);
                result.Append(pendingAnsi).Append(g);
                pendingAnsi.Clear();
                width += w;
            }
            i = end;
        }
        return new TextSlice(result.ToString(), width);
    }

    private static string FinalizeTruncatedResult(string prefix, int prefixWidth, string ellipsis, int ellipsisWidth, int maxWidth, bool pad)
    {
        const string reset = "\e[0m";
        var close = GetActiveOsc8Close(prefix);
        var result = ellipsis.Length > 0 ? $"{prefix}{close}{reset}{ellipsis}{reset}" : $"{prefix}{close}{reset}";
        return pad ? result + new string(' ', Math.Max(0, maxWidth - (prefixWidth + ellipsisWidth))) : result;
    }

    /// <summary>Truncate to a visible width, appending an ellipsis when truncated; optionally pad to exactly maxWidth.</summary>
    public static string TruncateToWidth(string text, int maxWidth, string ellipsis = "...", bool pad = false)
    {
        if (maxWidth <= 0) return "";
        if (text.Length == 0) return pad ? new string(' ', maxWidth) : "";

        var ellipsisWidth = VisibleWidth(ellipsis);
        if (ellipsisWidth >= maxWidth)
        {
            var textWidth = VisibleWidth(text);
            if (textWidth <= maxWidth) return pad ? text + new string(' ', maxWidth - textWidth) : text;
            var clipped = TruncateFragmentToWidth(ellipsis, maxWidth);
            if (clipped.Width == 0) return pad ? new string(' ', maxWidth) : "";
            return FinalizeTruncatedResult("", 0, clipped.Text, clipped.Width, maxWidth, pad);
        }

        if (IsPrintableAscii(text))
        {
            if (text.Length <= maxWidth) return pad ? text + new string(' ', maxWidth - text.Length) : text;
            var target = maxWidth - ellipsisWidth;
            return FinalizeTruncatedResult(text[..target], target, ellipsis, ellipsisWidth, maxWidth, pad);
        }

        var targetWidth = maxWidth - ellipsisWidth;
        var result = new StringBuilder();
        var pending = new StringBuilder();
        var visibleSoFar = 0;
        var keptWidth = 0;
        var keepPrefix = true;
        var overflowed = false;
        bool exhausted;
        var hasAnsi = text.Contains('\e');
        var hasTabs = text.Contains('\t');

        if (!hasAnsi && !hasTabs)
        {
            foreach (var g in Graphemes(text))
            {
                var w = GraphemeWidth(g);
                if (keepPrefix && keptWidth + w <= targetWidth)
                {
                    result.Append(g);
                    keptWidth += w;
                }
                else
                {
                    keepPrefix = false;
                }
                visibleSoFar += w;
                if (visibleSoFar > maxWidth)
                {
                    overflowed = true;
                    break;
                }
            }
            exhausted = !overflowed;
        }
        else
        {
            var i = 0;
            while (i < text.Length)
            {
                if (ExtractAnsiCode(text, i) is { } ansi)
                {
                    pending.Append(ansi.Code);
                    i += ansi.Length;
                    continue;
                }
                if (text[i] == '\t')
                {
                    if (keepPrefix && keptWidth + 3 <= targetWidth)
                    {
                        result.Append(pending).Append('\t');
                        pending.Clear();
                        keptWidth += 3;
                    }
                    else
                    {
                        keepPrefix = false;
                        pending.Clear();
                    }
                    visibleSoFar += 3;
                    if (visibleSoFar > maxWidth)
                    {
                        overflowed = true;
                        break;
                    }
                    i++;
                    continue;
                }
                var end = i;
                while (end < text.Length && text[end] != '\t' && ExtractAnsiCode(text, end) is null) end++;
                foreach (var g in Graphemes(text[i..end]))
                {
                    var w = GraphemeWidth(g);
                    if (keepPrefix && keptWidth + w <= targetWidth)
                    {
                        result.Append(pending).Append(g);
                        pending.Clear();
                        keptWidth += w;
                    }
                    else
                    {
                        keepPrefix = false;
                        pending.Clear();
                    }
                    visibleSoFar += w;
                    if (visibleSoFar > maxWidth)
                    {
                        overflowed = true;
                        break;
                    }
                }
                if (overflowed) break;
                i = end;
            }
            exhausted = i >= text.Length;
        }

        if (!overflowed && exhausted) return pad ? text + new string(' ', Math.Max(0, maxWidth - visibleSoFar)) : text;
        return FinalizeTruncatedResult(result.ToString(), keptWidth, ellipsis, ellipsisWidth, maxWidth, pad);
    }

    // ---- slicing ----

    public static string SliceByColumn(string line, int startCol, int length, bool strict = false) => SliceWithWidth(line, startCol, length, strict).Text;

    public static TextSlice SliceWithWidth(string line, int startCol, int length, bool strict = false)
    {
        if (length <= 0) return new TextSlice("", 0);
        var endCol = startCol + length;
        var result = new StringBuilder();
        var resultWidth = 0;
        var currentCol = 0;
        var i = 0;
        var pending = new StringBuilder();

        while (i < line.Length)
        {
            if (ExtractAnsiCode(line, i) is { } ansi)
            {
                if (currentCol >= startCol && currentCol < endCol) result.Append(ansi.Code);
                else if (currentCol < startCol) pending.Append(ansi.Code);
                i += ansi.Length;
                continue;
            }
            var textEnd = i;
            while (textEnd < line.Length && ExtractAnsiCode(line, textEnd) is null) textEnd++;
            foreach (var g in Graphemes(line[i..textEnd]))
            {
                var w = GraphemeWidth(g);
                var inRange = currentCol >= startCol && currentCol < endCol;
                var fits = !strict || currentCol + w <= endCol;
                if (inRange && fits)
                {
                    result.Append(pending).Append(g);
                    pending.Clear();
                    resultWidth += w;
                }
                currentCol += w;
                if (currentCol >= endCol) break;
            }
            i = textEnd;
            if (currentCol >= endCol) break;
        }
        return new TextSlice(result.ToString(), resultWidth);
    }

    /// <summary>Extract content before and after an overlay region in one pass, preserving styling for "after".</summary>
    public static LineSegments ExtractSegments(string line, int beforeEnd, int afterStart, int afterLen, bool strictAfter = false)
    {
        var before = new StringBuilder();
        var after = new StringBuilder();
        int beforeWidth = 0, afterWidth = 0, currentCol = 0, i = 0;
        var pendingBefore = new StringBuilder();
        var afterStarted = false;
        var afterEnd = afterStart + afterLen;
        var tracker = new AnsiCodeTracker();

        bool Done() => afterLen <= 0 ? currentCol >= beforeEnd : currentCol >= afterEnd;

        while (i < line.Length)
        {
            if (ExtractAnsiCode(line, i) is { } ansi)
            {
                tracker.Process(ansi.Code);
                if (currentCol < beforeEnd) pendingBefore.Append(ansi.Code);
                else if (currentCol >= afterStart && currentCol < afterEnd && afterStarted) after.Append(ansi.Code);
                i += ansi.Length;
                continue;
            }
            var textEnd = i;
            while (textEnd < line.Length && ExtractAnsiCode(line, textEnd) is null) textEnd++;
            foreach (var g in Graphemes(line[i..textEnd]))
            {
                var w = GraphemeWidth(g);
                if (currentCol < beforeEnd && currentCol + w <= beforeEnd)
                {
                    before.Append(pendingBefore).Append(g);
                    pendingBefore.Clear();
                    beforeWidth += w;
                }
                else if (currentCol >= afterStart && currentCol < afterEnd)
                {
                    if (!strictAfter || currentCol + w <= afterEnd)
                    {
                        if (!afterStarted)
                        {
                            after.Append(tracker.GetActiveCodes());
                            afterStarted = true;
                        }
                        after.Append(g);
                        afterWidth += w;
                    }
                }
                currentCol += w;
                if (Done()) break;
            }
            i = textEnd;
            if (Done()) break;
        }
        return new LineSegments(before.ToString(), beforeWidth, after.ToString(), afterWidth);
    }
}

/// <summary>Tracks active SGR attributes and OSC 8 hyperlinks to carry styling across line breaks.</summary>
internal sealed partial class AnsiCodeTracker
{
    private bool _bold, _dim, _italic, _underline, _blink, _inverse, _hidden, _strikethrough;
    private string? _fg;
    private string? _bg;
    private TextUtils.ActiveHyperlink? _link;

    [GeneratedRegex("\\e\\[([\\d;]*)m")]
    private static partial Regex Sgr();

    public void Process(string ansiCode)
    {
        var (isOsc8, link) = TextUtils.ParseOsc8Hyperlink(ansiCode);
        if (isOsc8)
        {
            _link = link;
            return;
        }
        if (!ansiCode.EndsWith('m')) return;
        var match = Sgr().Match(ansiCode);
        if (!match.Success) return;
        var p = match.Groups[1].Value;
        if (p is "" or "0")
        {
            Reset();
            return;
        }

        var parts = p.Split(';');
        var i = 0;
        while (i < parts.Length)
        {
            _ = int.TryParse(parts[i], out var code);
            if (code is 38 or 48 && parts[i].Length > 0)
            {
                if (i + 2 < parts.Length && parts[i + 1] == "5")
                {
                    var color = $"{parts[i]};{parts[i + 1]};{parts[i + 2]}";
                    if (code == 38) _fg = color;
                    else _bg = color;
                    i += 3;
                    continue;
                }
                if (i + 4 < parts.Length && parts[i + 1] == "2")
                {
                    var color = $"{parts[i]};{parts[i + 1]};{parts[i + 2]};{parts[i + 3]};{parts[i + 4]}";
                    if (code == 38) _fg = color;
                    else _bg = color;
                    i += 5;
                    continue;
                }
            }
            if (parts[i].Length == 0)
            {
                // parseInt("") is NaN in JS: matches no case.
                i++;
                continue;
            }
            switch (code)
            {
                case 0: Reset(); break;
                case 1: _bold = true; break;
                case 2: _dim = true; break;
                case 3: _italic = true; break;
                case 4: _underline = true; break;
                case 5: _blink = true; break;
                case 7: _inverse = true; break;
                case 8: _hidden = true; break;
                case 9: _strikethrough = true; break;
                case 21: _bold = false; break;
                case 22: _bold = false; _dim = false; break;
                case 23: _italic = false; break;
                case 24: _underline = false; break;
                case 25: _blink = false; break;
                case 27: _inverse = false; break;
                case 28: _hidden = false; break;
                case 29: _strikethrough = false; break;
                case 39: _fg = null; break;
                case 49: _bg = null; break;
                default:
                    if (code is >= 30 and <= 37 or >= 90 and <= 97) _fg = code.ToString(CultureInfo.InvariantCulture);
                    else if (code is >= 40 and <= 47 or >= 100 and <= 107) _bg = code.ToString(CultureInfo.InvariantCulture);
                    break;
            }
            i++;
        }
    }

    private void Reset()
    {
        _bold = _dim = _italic = _underline = _blink = _inverse = _hidden = _strikethrough = false;
        _fg = null;
        _bg = null;
    }

    public void Clear()
    {
        Reset();
        _link = null;
    }

    public string GetActiveCodes()
    {
        var codes = new List<string>();
        if (_bold) codes.Add("1");
        if (_dim) codes.Add("2");
        if (_italic) codes.Add("3");
        if (_underline) codes.Add("4");
        if (_blink) codes.Add("5");
        if (_inverse) codes.Add("7");
        if (_hidden) codes.Add("8");
        if (_strikethrough) codes.Add("9");
        if (_fg is not null) codes.Add(_fg);
        if (_bg is not null) codes.Add(_bg);
        var result = codes.Count > 0 ? $"\e[{string.Join(";", codes)}m" : "";
        if (_link is not null) result += TextUtils.FormatOsc8Hyperlink(_link);
        return result;
    }

    public string GetActiveBackgroundCode() => _bg is null ? "" : $"\e[{_bg}m";

    public bool HasActiveCodes() => _bold || _dim || _italic || _underline || _blink || _inverse || _hidden || _strikethrough || _fg is not null || _bg is not null || _link is not null;

    public string GetLineEndReset()
    {
        var result = "";
        if (_underline) result += "\e[24m";
        if (_link is not null) result += TextUtils.FormatOsc8Close(_link.Terminator);
        return result;
    }
}
