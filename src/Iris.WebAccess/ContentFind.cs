using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Iris.WebAccess;

public sealed record FindQueryResult(string Query, int MatchCount);

public sealed record FindResult(string Text, int MatchCount, int ReturnedMatches, IReadOnlyList<FindQueryResult> QueryResults);

/// <summary>
/// Finds passages in stored content: literal ("exact", "case-insensitive") or token-based "fuzzy" matching, returning
/// merged excerpts with context, bounded to a fixed output size.
/// </summary>
public static partial class ContentFind
{
    private const int ContextChars = 400;
    private const int MaxOutputChars = 20_000;

    private sealed record Match(string Query, int Start, int End);

    private sealed class Range(int start, int end)
    {
        public int Start { get; set; } = start;
        public int End { get; set; } = end;
    }

    private sealed record Occurrence(string Query, List<Match> Matches, int[] Starts, int[] Ends);

    private sealed record MatchingQuery(string Id, int Order, Occurrence Occurrence);

    private sealed record RangeCount(MatchingQuery Query, int Count, int FirstStart);

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex TokenPattern();

    [GeneratedRegex(@"[^\n]+(?:\n(?!\n)[^\n]+)*")]
    private static partial Regex ParagraphPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();

    public static bool IsValidMode(string mode) => mode is "exact" or "case-insensitive" or "fuzzy";

    public static FindResult Find(string text, IEnumerable<string> queries, string mode)
    {
        var normalizedQueries = queries.Select(q => q.Trim()).Where(q => q.Length > 0).Distinct().ToList();
        var occurrences = normalizedQueries.Select(query =>
        {
            var matches = mode == "fuzzy" ? FuzzyMatches(text, query) : LiteralMatches(text, query, mode == "case-insensitive");
            return new Occurrence(query, matches, [.. matches.Select(m => m.Start)], [.. matches.Select(m => m.End)]);
        }).ToList();
        var allMatches = occurrences.SelectMany(o => o.Matches).ToList();
        var queryResults = occurrences.Select(o => new FindQueryResult(o.Query, o.Matches.Count)).ToList();
        var heading = allMatches.Count > 0 ? $"Text matches ({mode})" : $"Text matches ({mode}): no matches";
        var missing = queryResults.Where(r => r.MatchCount == 0).Select(r => $"\"{r.Query}\"").ToList();
        var matchingQueries = occurrences.Where(o => o.Matches.Count > 0).Select((o, index) => new MatchingQuery($"Q{index + 1}", index, o)).ToList();

        var whitespaceRuns = WhitespacePattern().Matches(text).Select(m => (Start: m.Index, End: m.Index + m.Length)).ToList();
        var whitespaceStarts = whitespaceRuns.Select(r => r.Start).ToArray();
        var whitespaceEnds = whitespaceRuns.Select(r => r.End).ToArray();
        var whitespaceSavings = new int[whitespaceRuns.Count + 1];
        for (var i = 0; i < whitespaceRuns.Count; i++) whitespaceSavings[i + 1] = whitespaceSavings[i] + whitespaceRuns[i].End - whitespaceRuns[i].Start - 1;

        int NormalizedLength(int start, int end)
        {
            var firstRun = UpperBound(whitespaceStarts, start) - 1;
            if (firstRun >= 0 && whitespaceEnds[firstRun] > start) start = whitespaceEnds[firstRun];
            if (start >= end) return 0;
            var lastRun = UpperBound(whitespaceStarts, end - 1) - 1;
            if (lastRun >= 0 && whitespaceEnds[lastRun] >= end) end = whitespaceStarts[lastRun];
            if (start >= end) return 0;
            var first = LowerBound(whitespaceStarts, start);
            var last = UpperBound(whitespaceEnds, end);
            return end - start - (whitespaceSavings[last] - whitespaceSavings[first]);
        }

        List<RangeCount> RangeCounts(Range range) =>
        [
            .. matchingQueries.SelectMany(q =>
            {
                var first = LowerBound(q.Occurrence.Starts, range.Start);
                var last = UpperBound(q.Occurrence.Ends, range.End);
                return last > first ? [new RangeCount(q, last - first, q.Occurrence.Starts[first])] : Array.Empty<RangeCount>();
            }).OrderBy(c => c.FirstStart).ThenBy(c => c.Query.Order),
        ];

        var legend = matchingQueries.Count > 0 ? $"Queries: {string.Join(", ", matchingQueries.Select(q => $"{q.Id} = \"{q.Occurrence.Query}\""))}" : "";
        var missingNotice = missing.Count > 0 ? $"No matches: {string.Join(", ", missing)}" : "";

        (int Length, int ReturnedMatches) Measure(List<Range> ranges, bool overflow = false, List<string>? omitted = null)
        {
            var length = heading.Length;
            var returned = 0;
            if (overflow && legend.Length > 0) length += 2 + legend.Length;
            for (var index = 0; index < ranges.Count; index++)
            {
                var range = ranges[index];
                var counts = RangeCounts(range);
                if (counts.Count == 0) continue;
                var labelsLength = counts.Aggregate(2 * (counts.Count - 1), (total, c) =>
                    total + (overflow ? c.Query.Id.Length : c.Query.Occurrence.Query.Length + 2) + 2 + c.Count.ToString(CultureInfo.InvariantCulture).Length);
                var snippetLength = NormalizedLength(range.Start, range.End) + (range.Start > 0 ? 1 : 0) + (range.End < text.Length ? 1 : 0);
                length += 2 + (index + 1).ToString(CultureInfo.InvariantCulture).Length + 2 + labelsLength + 1 + snippetLength;
                returned += counts.Sum(c => c.Count);
            }
            if (missingNotice.Length > 0) length += 2 + missingNotice.Length;
            if (omitted is { Count: > 0 }) length += 2 + $"No representative excerpt: {string.Join(", ", omitted)}.".Length;
            if (returned < allMatches.Count) length += 2 + $"Showing {returned} of {allMatches.Count} matches.".Length;
            return (length, returned);
        }

        FindResult Format(List<Range> ranges, bool overflow = false, List<string>? omitted = null)
        {
            var sections = new List<string> { heading };
            var returned = 0;
            if (overflow && legend.Length > 0) sections.Add(legend);
            for (var index = 0; index < ranges.Count; index++)
            {
                var range = ranges[index];
                var contained = RangeCounts(range);
                if (contained.Count == 0) continue;
                var prefix = range.Start > 0 ? "…" : "";
                var suffix = range.End < text.Length ? "…" : "";
                var snippet = $"{prefix}{WhitespacePattern().Replace(text[range.Start..range.End], " ").Trim()}{suffix}";
                var counts = string.Join(", ", contained.Select(c => $"{(overflow ? c.Query.Id : $"\"{c.Query.Occurrence.Query}\"")} ×{c.Count}"));
                sections.Add($"{index + 1}. {counts}\n{snippet}");
                returned += contained.Sum(c => c.Count);
            }
            if (missingNotice.Length > 0) sections.Add(missingNotice);
            if (omitted is { Count: > 0 }) sections.Add($"No representative excerpt: {string.Join(", ", omitted)}.");
            if (returned < allMatches.Count) sections.Add($"Showing {returned} of {allMatches.Count} matches.");
            return new FindResult(string.Join("\n\n", sections), allMatches.Count, returned, queryResults);
        }

        var fullRanges = ContextRanges(text.Length, allMatches);
        var full = Measure(fullRanges);
        if (full.ReturnedMatches == allMatches.Count && full.Length <= MaxOutputChars) return Format(fullRanges);

        // Too much to show: keep one representative match per query, then widen with context while it fits.
        var selectedRanges = new List<Range>();
        var omittedIds = matchingQueries.Select(q => q.Id).ToList();
        if (Measure(selectedRanges, true, omittedIds).Length > MaxOutputChars)
        {
            return new FindResult(
                $"{heading}\n\nUnable to format bounded excerpts: query metadata exceeds {MaxOutputChars} characters.\n\nShowing 0 of {allMatches.Count} matches.",
                allMatches.Count, 0, queryResults);
        }
        var witnesses = new List<Match>();
        foreach (var query in matchingQueries)
        {
            var proposedOmitted = omittedIds.Where(id => id != query.Id).ToList();
            var currentLength = Measure(selectedRanges, true, omittedIds).Length;
            (Match Witness, List<Range> Ranges, int Cost)? selected = null;
            foreach (var witness in query.Occurrence.Matches)
            {
                var proposedRanges = MergeRanges([.. selectedRanges, new Range(witness.Start, witness.End)]);
                var summary = Measure(proposedRanges, true, proposedOmitted);
                var cost = summary.Length - currentLength;
                if (summary.Length <= MaxOutputChars
                    && (selected is null || cost < selected.Value.Cost
                        || cost == selected.Value.Cost && (witness.Start < selected.Value.Witness.Start || witness.Start == selected.Value.Witness.Start && witness.End < selected.Value.Witness.End)))
                {
                    selected = (witness, proposedRanges, cost);
                }
            }
            if (selected is { } chosen)
            {
                selectedRanges = chosen.Ranges;
                omittedIds = proposedOmitted;
                witnesses.Add(chosen.Witness);
            }
        }
        foreach (var witness in witnesses)
        {
            var proposed = MergeRanges([.. selectedRanges, new Range(Math.Max(0, witness.Start - ContextChars), Math.Min(text.Length, witness.End + ContextChars))]);
            if (Measure(proposed, true, omittedIds).Length <= MaxOutputChars) selectedRanges = proposed;
        }
        var allOccurrences = MergeRanges([.. selectedRanges, .. allMatches.Select(m => new Range(m.Start, m.End))]);
        if (Measure(allOccurrences, true, omittedIds).Length <= MaxOutputChars) selectedRanges = allOccurrences;
        return Format(selectedRanges, true, omittedIds);
    }

    private static string Normalize(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) builder.Append(c);
        }
        return builder.ToString().ToLowerInvariant();
    }

    private static bool EditDistanceWithin(string left, string right, int maximum)
    {
        if (Math.Abs(left.Length - right.Length) > maximum) return false;
        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        for (var i = 1; i <= left.Length; i++)
        {
            var current = new int[right.Length + 1];
            current[0] = i;
            var rowMinimum = i;
            for (var j = 1; j <= right.Length; j++)
            {
                current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), previous[j - 1] + (left[i - 1] == right[j - 1] ? 0 : 1));
                rowMinimum = Math.Min(rowMinimum, current[j]);
            }
            if (rowMinimum > maximum) return false;
            previous = current;
        }
        return previous[right.Length] <= maximum;
    }

    private static List<Match> LiteralMatches(string text, string query, bool caseInsensitive)
    {
        var comparison = caseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var matches = new List<Match>();
        for (var start = text.IndexOf(query, comparison); start >= 0; start = text.IndexOf(query, start + Math.Max(query.Length, 1), comparison))
        {
            matches.Add(new Match(query, start, start + query.Length));
            if (start + Math.Max(query.Length, 1) > text.Length) break;
        }
        return matches;
    }

    private static int MaxDistance(string token) => token.Length >= 9 ? 2 : token.Length >= 5 ? 1 : 0;

    private static List<Match> FuzzyMatches(string text, string query)
    {
        var queryTokens = TokenPattern().Matches(Normalize(query)).Select(m => m.Value).ToList();
        if (queryTokens.Count == 0) return [];
        var matches = new List<Match>();
        foreach (System.Text.RegularExpressions.Match paragraph in ParagraphPattern().Matches(text))
        {
            if (paragraph.Value.Trim().Length == 0) continue;
            var tokens = TokenPattern().Matches(paragraph.Value).ToList();
            var matched = queryTokens.Where(qt => tokens.Any(token => EditDistanceWithin(qt, Normalize(token.Value), MaxDistance(qt)))).ToList();
            var required = queryTokens.Count == 1 ? 1 : (int)Math.Ceiling(queryTokens.Count * 0.6);
            if (matched.Count < required) continue;
            var first = tokens.First(token => matched.Any(qt => EditDistanceWithin(qt, Normalize(token.Value), MaxDistance(qt))));
            var start = paragraph.Index + first.Index;
            matches.Add(new Match(query, start, start + first.Length));
        }
        return matches;
    }

    private static List<Range> MergeRanges(IEnumerable<Range> ranges)
    {
        var merged = new List<Range>();
        foreach (var range in ranges.OrderBy(r => r.Start).ThenBy(r => r.End))
        {
            if (merged.Count > 0 && range.Start <= merged[^1].End) merged[^1].End = Math.Max(merged[^1].End, range.End);
            else merged.Add(new Range(range.Start, range.End));
        }
        return merged;
    }

    private static List<Range> ContextRanges(int textLength, List<Match> matches) =>
        MergeRanges(matches.Select(m => new Range(Math.Max(0, m.Start - ContextChars), Math.Min(textLength, m.End + ContextChars))));

    private static int LowerBound(int[] values, int target)
    {
        int low = 0, high = values.Length;
        while (low < high)
        {
            var middle = (low + high) >>> 1;
            if (values[middle] < target) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private static int UpperBound(int[] values, int target)
    {
        int low = 0, high = values.Length;
        while (low < high)
        {
            var middle = (low + high) >>> 1;
            if (values[middle] <= target) low = middle + 1;
            else high = middle;
        }
        return low;
    }
}
