using System.Globalization;
using System.Text.RegularExpressions;

namespace Iris.Tui;

/// <summary>Emacs-style kill ring.</summary>
public sealed class KillRing
{
    private readonly List<string> _ring = [];

    public void Push(string text, bool prepend, bool accumulate = false)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (accumulate && _ring.Count > 0)
        {
            var last = _ring[^1];
            _ring[^1] = prepend ? text + last : last + text;
        }
        else
        {
            _ring.Add(text);
        }
    }

    public string? Peek() => _ring.Count > 0 ? _ring[^1] : null;

    public void Rotate()
    {
        if (_ring.Count <= 1) return;
        var last = _ring[^1];
        _ring.RemoveAt(_ring.Count - 1);
        _ring.Insert(0, last);
    }

    public int Count => _ring.Count;
}

/// <summary>Undo stack of snapshots. Callers pass immutable or cloned state.</summary>
public sealed class UndoStack<T>
{
    private readonly List<T> _stack = [];

    public void Push(T state) => _stack.Add(state);

    public bool TryPop(out T state)
    {
        if (_stack.Count == 0)
        {
            state = default!;
            return false;
        }
        state = _stack[^1];
        _stack.RemoveAt(_stack.Count - 1);
        return true;
    }

    public void Clear() => _stack.Clear();

    public int Count => _stack.Count;
}

public readonly record struct WordSegment(string Segment, int Index, bool IsWordLike);

/// <summary>
/// Word segmentation approximating Intl.Segmenter("word") (UAX #29 with ICU dictionary breaking for CJK approximated per
/// character): word-like runs join letters and digits across mid-letter/mid-number punctuation and underscores,
/// horizontal whitespace runs group together, other characters are single segments.
/// </summary>
public static class WordSegmenter
{
    private enum Kind { Letter, Numeric, ExtendNumLet, MidLetter, MidNum, MidNumLet, Space, Newline, Cjk, Han, Hiragana, Katakana, Other }

    private static Kind Classify(string grapheme)
    {
        var cp = char.ConvertToUtf32OrNull(grapheme) ?? (char.IsHighSurrogate(grapheme[0]) && grapheme.Length > 1 ? char.ConvertToUtf32(grapheme[0], grapheme[1]) : grapheme[0]);
        // ICU groups same-script ideographic/kana runs into words (its dictionary segmentation is approximated per script).
        if (cp is (>= 0x4E00 and <= 0x9FFF) or (>= 0x3400 and <= 0x4DBF) or (>= 0xF900 and <= 0xFAFF) or (>= 0x20000 and <= 0x3FFFF) or 0x3005 or 0x3007) return Kind.Han;
        if (cp is >= 0x3041 and <= 0x309F) return Kind.Hiragana;
        if (cp is (>= 0x30A0 and <= 0x30FF) or (>= 0x31F0 and <= 0x31FF) or (>= 0xFF66 and <= 0xFF9F)) return Kind.Katakana;
        if (TextUtils.IsCjkBreakChar(grapheme)) return Kind.Cjk;
        var c = grapheme[0];
        if (c is '\n' or '\r') return Kind.Newline;
        if (c is ' ' or '\t' || (cp < 0x10000 && char.IsWhiteSpace(c) && c is not ('\n' or '\r' or '\v' or '\f'))) return Kind.Space;
        if (c == '_') return Kind.ExtendNumLet;
        if (c is '\'' or '.' or '’' or '‘' or '․' or '﹒' or '＇' or '．') return Kind.MidNumLet;
        if (c is ':' or '·' or '״' or '‧' or '︓' or '﹕' or '：') return Kind.MidLetter;
        if (c is ',' or ';' or ';' or '؉' or '؊' or '،' or '؍' or '٬' or '߸' or '⁄' or '︐' or '︔' or '﹐' or '﹔' or '，' or '；') return Kind.MidNum;
        var category = CharUnicodeInfo.GetUnicodeCategory(cp);
        return category switch
        {
            UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
                or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter
                or UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark => Kind.Letter,
            UnicodeCategory.DecimalDigitNumber => Kind.Numeric,
            UnicodeCategory.ConnectorPunctuation => Kind.ExtendNumLet,
            _ => Kind.Other,
        };
    }

    public static IEnumerable<WordSegment> Segment(string text)
    {
        var graphemes = new List<(string G, int Index, Kind Kind)>();
        var idx = 0;
        foreach (var g in TextUtils.Graphemes(text))
        {
            graphemes.Add((g, idx, Classify(g)));
            idx += g.Length;
        }

        var i = 0;
        while (i < graphemes.Count)
        {
            var start = i;
            var kind = graphemes[i].Kind;
            if (kind is Kind.Letter or Kind.Numeric or Kind.ExtendNumLet)
            {
                i++;
                while (i < graphemes.Count)
                {
                    var k = graphemes[i].Kind;
                    if (k is Kind.Letter or Kind.Numeric or Kind.ExtendNumLet)
                    {
                        i++;
                        continue;
                    }
                    // Mid punctuation joins when surrounded by compatible characters (WB6/7, WB11/12).
                    if (i + 1 < graphemes.Count && k is Kind.MidLetter or Kind.MidNum or Kind.MidNumLet)
                    {
                        var prev = graphemes[i - 1].Kind;
                        var next = graphemes[i + 1].Kind;
                        var letterJoin = prev == Kind.Letter && next == Kind.Letter && k is Kind.MidLetter or Kind.MidNumLet;
                        var numJoin = prev == Kind.Numeric && next == Kind.Numeric && k is Kind.MidNum or Kind.MidNumLet;
                        if (letterJoin || numJoin)
                        {
                            i += 2;
                            continue;
                        }
                    }
                    break;
                }
                var isWordLike = graphemes.Skip(start).Take(i - start).Any(g => g.Kind is Kind.Letter or Kind.Numeric);
                yield return MakeSegment(text, graphemes, start, i, isWordLike);
            }
            else if (kind == Kind.Space)
            {
                i++;
                while (i < graphemes.Count && graphemes[i].Kind == Kind.Space) i++;
                yield return MakeSegment(text, graphemes, start, i, false);
            }
            else if (kind is Kind.Han or Kind.Hiragana or Kind.Katakana)
            {
                i++;
                while (i < graphemes.Count && graphemes[i].Kind == kind) i++;
                yield return MakeSegment(text, graphemes, start, i, true);
            }
            else if (kind == Kind.Cjk)
            {
                i++;
                yield return MakeSegment(text, graphemes, start, i, true);
            }
            else
            {
                i++;
                yield return MakeSegment(text, graphemes, start, i, false);
            }
        }
    }

    private static WordSegment MakeSegment(string text, List<(string G, int Index, Kind Kind)> graphemes, int start, int end, bool isWordLike)
    {
        var from = graphemes[start].Index;
        var to = end < graphemes.Count ? graphemes[end].Index : text.Length;
        return new WordSegment(text[from..to], from, isWordLike);
    }
}

/// <summary>Word-wise cursor movement.</summary>
public static partial class WordNavigation
{
    [GeneratedRegex("[(){}\\[\\]<>.,;:'\"!?+\\-=*/\\\\|&%^$#@~`]")]
    private static partial Regex Punctuation();

    public static int FindWordBackward(string text, int cursor, Func<string, IEnumerable<WordSegment>>? segment = null, Func<string, bool>? isAtomic = null)
    {
        if (cursor <= 0) return 0;
        var before = text[..cursor];
        var segments = (segment?.Invoke(before) ?? WordSegmenter.Segment(before)).ToList();
        var newCursor = cursor;

        bool Atomic(string s) => isAtomic?.Invoke(s) == true;

        while (segments.Count > 0 && !Atomic(segments[^1].Segment) && TextUtils.IsWhitespaceChar(segments[^1].Segment))
        {
            newCursor -= segments[^1].Segment.Length;
            segments.RemoveAt(segments.Count - 1);
        }
        if (segments.Count == 0) return newCursor;

        var last = segments[^1];
        if (Atomic(last.Segment))
        {
            newCursor -= last.Segment.Length;
        }
        else if (last.IsWordLike)
        {
            var matches = Punctuation().Matches(last.Segment);
            if (matches.Count == 0) newCursor -= last.Segment.Length;
            else newCursor -= last.Segment.Length - (matches[^1].Index + matches[^1].Length);
        }
        else
        {
            while (segments.Count > 0 && !Atomic(segments[^1].Segment) && !segments[^1].IsWordLike && !TextUtils.IsWhitespaceChar(segments[^1].Segment))
            {
                newCursor -= segments[^1].Segment.Length;
                segments.RemoveAt(segments.Count - 1);
            }
        }
        return newCursor;
    }

    public static int FindWordForward(string text, int cursor, Func<string, IEnumerable<WordSegment>>? segment = null, Func<string, bool>? isAtomic = null)
    {
        if (cursor >= text.Length) return text.Length;
        var after = text[cursor..];
        using var iterator = (segment?.Invoke(after) ?? WordSegmenter.Segment(after)).GetEnumerator();
        var hasNext = iterator.MoveNext();
        var newCursor = cursor;

        bool Atomic(string s) => isAtomic?.Invoke(s) == true;

        while (hasNext && !Atomic(iterator.Current.Segment) && TextUtils.IsWhitespaceChar(iterator.Current.Segment))
        {
            newCursor += iterator.Current.Segment.Length;
            hasNext = iterator.MoveNext();
        }
        if (!hasNext) return newCursor;

        var current = iterator.Current;
        if (Atomic(current.Segment))
        {
            newCursor += current.Segment.Length;
        }
        else if (current.IsWordLike)
        {
            var m = Punctuation().Match(current.Segment);
            newCursor += m.Success ? m.Index : current.Segment.Length;
        }
        else
        {
            while (hasNext && !Atomic(iterator.Current.Segment) && !iterator.Current.IsWordLike && !TextUtils.IsWhitespaceChar(iterator.Current.Segment))
            {
                newCursor += iterator.Current.Segment.Length;
                hasNext = iterator.MoveNext();
            }
        }
        return newCursor;
    }
}

public readonly record struct FuzzyMatchResult(bool Matches, double Score);

/// <summary>Fuzzy matching: query characters in order; lower score is better.</summary>
public static partial class Fuzzy
{
    [GeneratedRegex("^(?<letters>[a-z]+)(?<digits>[0-9]+)$")]
    private static partial Regex LettersDigits();

    [GeneratedRegex("^(?<digits>[0-9]+)(?<letters>[a-z]+)$")]
    private static partial Regex DigitsLetters();

    [GeneratedRegex(@"[\s/]+")]
    private static partial Regex TokenSeparator();

    private static bool IsBoundary(char c) => TextUtils.IsJsWhitespace(c) || c is '-' or '_' or '.' or '/' or ':';

    public static FuzzyMatchResult Match(string query, string text)
    {
        var queryLower = query.ToLowerInvariant();
        var textLower = text.ToLowerInvariant();

        FuzzyMatchResult MatchQuery(string normalizedQuery)
        {
            if (normalizedQuery.Length == 0) return new(true, 0);
            if (normalizedQuery.Length > textLower.Length) return new(false, 0);

            var queryIndex = 0;
            double score = 0;
            var lastMatchIndex = -1;
            var consecutive = 0;
            while (queryIndex < normalizedQuery.Length)
            {
                // Vectorized search for the next query character; scoring and ordering are unchanged.
                var i = textLower.IndexOf(normalizedQuery[queryIndex], lastMatchIndex + 1);
                if (i == -1) break;
                var isWordBoundary = i == 0 || IsBoundary(textLower[i - 1]);
                if (lastMatchIndex == i - 1)
                {
                    consecutive++;
                    score -= consecutive * 5;
                }
                else
                {
                    consecutive = 0;
                    if (lastMatchIndex >= 0) score += (i - lastMatchIndex - 1) * 2;
                }
                if (isWordBoundary) score -= 10;
                score += i * 0.1;
                lastMatchIndex = i;
                queryIndex++;
            }
            if (queryIndex < normalizedQuery.Length) return new(false, 0);
            if (normalizedQuery == textLower) score -= 100;
            return new(true, score);
        }

        var primary = MatchQuery(queryLower);
        if (primary.Matches) return primary;

        var swapped = LettersDigits().Match(queryLower) is { Success: true } ld
            ? ld.Groups["digits"].Value + ld.Groups["letters"].Value
            : DigitsLetters().Match(queryLower) is { Success: true } dl
                ? dl.Groups["letters"].Value + dl.Groups["digits"].Value
                : "";
        if (swapped.Length == 0) return primary;
        var swappedMatch = MatchQuery(swapped);
        return swappedMatch.Matches ? new(true, swappedMatch.Score + 5) : primary;
    }

    /// <summary>Filter and sort by match quality; whitespace- and slash-separated tokens must all match.</summary>
    public static List<T> Filter<T>(IReadOnlyList<T> items, string query, Func<T, string> getText)
    {
        if (query.Trim().Length == 0) return [.. items];
        var tokens = TokenSeparator().Split(query.Trim()).Where(t => t.Length > 0).ToList();
        if (tokens.Count == 0) return [.. items];

        var results = new List<(T Item, double Score)>();
        foreach (var item in items)
        {
            var text = getText(item);
            double total = 0;
            var all = true;
            foreach (var token in tokens)
            {
                var match = Match(token, text);
                if (!match.Matches)
                {
                    all = false;
                    break;
                }
                total += match.Score;
            }
            if (all) results.Add((item, total));
        }
        return results.OrderBy(r => r.Score).Select(r => r.Item).ToList();
    }
}
