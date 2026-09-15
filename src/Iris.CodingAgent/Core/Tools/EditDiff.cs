using System.Text;
using Iris.CodingAgent.Utils;

namespace Iris.CodingAgent.Core.Tools;

public sealed record EditReplacement(string OldText, string NewText);

public sealed record FuzzyMatchResult(bool Found, int Index, int MatchLength, bool UsedFuzzyMatch, string ContentForReplacement);

public sealed record EditDiffResult(string Diff, int? FirstChangedLine);

/// <summary>Shared diff computation utilities for the edit tool.</summary>
public static class EditDiff
{
    private sealed record TextReplacement(int EditIndex, int MatchIndex, int MatchLength, string NewText);

    private readonly record struct LineSpan(int Start, int End);

    public static string DetectLineEnding(string content)
    {
        var crlfIdx = content.IndexOf("\r\n", StringComparison.Ordinal);
        var lfIdx = content.IndexOf('\n');
        if (lfIdx == -1 || crlfIdx == -1) return "\n";
        return crlfIdx < lfIdx ? "\r\n" : "\n";
    }

    public static string NormalizeToLF(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');

    public static string RestoreLineEndings(string text, string ending) => ending == "\r\n" ? text.Replace("\n", "\r\n") : text;

    private static bool IsJsWhitespace(char c) => char.IsWhiteSpace(c) || c == (char)0xFEFF;

    /// <summary>
    /// Normalize text for fuzzy matching: NFKC, strip trailing whitespace per line, and map smart quotes, Unicode
    /// dashes and special spaces to ASCII.
    /// </summary>
    public static string NormalizeForFuzzyMatch(string text)
    {
        string normalized;
        try
        {
            normalized = text.Normalize(NormalizationForm.FormKC);
        }
        catch (Exception)
        {
            normalized = text;
        }

        var lines = normalized.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var end = line.Length;
            while (end > 0 && IsJsWhitespace(line[end - 1])) end--;
            lines[i] = line[..end];
        }
        var joined = string.Join("\n", lines);

        var sb = new StringBuilder(joined.Length);
        foreach (var c in joined)
        {
            sb.Append((int)c switch
            {
                0x2018 or 0x2019 or 0x201A or 0x201B => '\'',
                0x201C or 0x201D or 0x201E or 0x201F => '"',
                >= 0x2010 and <= 0x2015 or 0x2212 => '-',
                0x00A0 or >= 0x2002 and <= 0x200A or 0x202F or 0x205F or 0x3000 => ' ',
                _ => c,
            });
        }
        return sb.ToString();
    }

    private static List<string> SplitLinesWithEndings(string content)
    {
        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < content.Length; i++)
        {
            if (content[i] != '\n') continue;
            lines.Add(content[start..(i + 1)]);
            start = i + 1;
        }
        if (start < content.Length) lines.Add(content[start..]);
        return lines;
    }

    private static List<LineSpan> GetLineSpans(string content)
    {
        var offset = 0;
        return SplitLinesWithEndings(content).Select(line =>
        {
            var span = new LineSpan(offset, offset + line.Length);
            offset = span.End;
            return span;
        }).ToList();
    }

    private static (int StartLine, int EndLine) GetReplacementLineRange(List<LineSpan> lines, TextReplacement replacement)
    {
        var replacementStart = replacement.MatchIndex;
        var replacementEnd = replacement.MatchIndex + replacement.MatchLength;
        var startLine = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            if (replacementStart >= lines[i].Start && replacementStart < lines[i].End)
            {
                startLine = i;
                break;
            }
        }
        if (startLine == -1) throw new InvalidOperationException("Replacement range is outside the base content.");

        var endLine = startLine;
        while (endLine < lines.Count && lines[endLine].End < replacementEnd) endLine++;
        if (endLine >= lines.Count) throw new InvalidOperationException("Replacement range is outside the base content.");
        return (startLine, endLine + 1);
    }

    private static string ApplyReplacements(string content, IReadOnlyList<TextReplacement> replacements, int offset = 0)
    {
        var result = content;
        for (var i = replacements.Count - 1; i >= 0; i--)
        {
            var replacement = replacements[i];
            var matchIndex = replacement.MatchIndex - offset;
            result = result[..matchIndex] + replacement.NewText + result[(matchIndex + replacement.MatchLength)..];
        }
        return result;
    }

    private static string ApplyReplacementsPreservingUnchangedLines(string originalContent, string baseContent, List<TextReplacement> replacements)
    {
        var originalLines = SplitLinesWithEndings(originalContent);
        var baseLines = GetLineSpans(baseContent);
        if (originalLines.Count != baseLines.Count)
        {
            throw new InvalidOperationException("Cannot preserve unchanged lines because the base content has a different line count.");
        }

        var groups = new List<(int StartLine, int EndLine, List<TextReplacement> Replacements)>();
        foreach (var replacement in replacements.OrderBy(r => r.MatchIndex))
        {
            var range = GetReplacementLineRange(baseLines, replacement);
            if (groups.Count > 0 && range.StartLine < groups[^1].EndLine)
            {
                var current = groups[^1];
                current.Replacements.Add(replacement);
                groups[^1] = (current.StartLine, Math.Max(current.EndLine, range.EndLine), current.Replacements);
                continue;
            }
            groups.Add((range.StartLine, range.EndLine, [replacement]));
        }

        var originalLineIndex = 0;
        var result = new StringBuilder();
        foreach (var group in groups)
        {
            for (var i = originalLineIndex; i < group.StartLine; i++) result.Append(originalLines[i]);
            var groupStartOffset = baseLines[group.StartLine].Start;
            var groupEndOffset = baseLines[group.EndLine - 1].End;
            result.Append(ApplyReplacements(baseContent[groupStartOffset..groupEndOffset], group.Replacements, groupStartOffset));
            originalLineIndex = group.EndLine;
        }
        for (var i = originalLineIndex; i < originalLines.Count; i++) result.Append(originalLines[i]);
        return result.ToString();
    }

    /// <summary>Find oldText in content, trying an exact match first, then a fuzzy match in normalized space.</summary>
    public static FuzzyMatchResult FuzzyFindText(string content, string oldText)
    {
        var exactIndex = content.IndexOf(oldText, StringComparison.Ordinal);
        if (exactIndex != -1) return new FuzzyMatchResult(true, exactIndex, oldText.Length, false, content);

        var fuzzyContent = NormalizeForFuzzyMatch(content);
        var fuzzyOldText = NormalizeForFuzzyMatch(oldText);
        var fuzzyIndex = fuzzyContent.IndexOf(fuzzyOldText, StringComparison.Ordinal);
        if (fuzzyIndex == -1) return new FuzzyMatchResult(false, -1, 0, false, content);
        return new FuzzyMatchResult(true, fuzzyIndex, fuzzyOldText.Length, true, fuzzyContent);
    }

    private static int CountOccurrences(string content, string oldText)
    {
        var fuzzyContent = NormalizeForFuzzyMatch(content);
        var fuzzyOldText = NormalizeForFuzzyMatch(oldText);
        if (fuzzyOldText.Length == 0) return fuzzyContent.Length;
        var count = 0;
        var index = 0;
        while ((index = fuzzyContent.IndexOf(fuzzyOldText, index, StringComparison.Ordinal)) != -1)
        {
            count++;
            index += fuzzyOldText.Length;
        }
        return count;
    }

    /// <summary>Apply one or more exact-text replacements to LF-normalized content.</summary>
    public static (string BaseContent, string NewContent) ApplyEditsToNormalizedContent(string normalizedContent, IReadOnlyList<EditReplacement> edits, string path)
    {
        var normalizedEdits = edits.Select(e => new EditReplacement(NormalizeToLF(e.OldText), NormalizeToLF(e.NewText))).ToList();
        var total = normalizedEdits.Count;

        for (var i = 0; i < total; i++)
        {
            if (normalizedEdits[i].OldText.Length == 0)
            {
                throw new InvalidOperationException(total == 1
                    ? $"oldText must not be empty in {path}."
                    : $"edits[{i}].oldText must not be empty in {path}.");
            }
        }

        var usedFuzzyMatch = normalizedEdits.Any(e => FuzzyFindText(normalizedContent, e.OldText).UsedFuzzyMatch);
        var replacementBaseContent = usedFuzzyMatch ? NormalizeForFuzzyMatch(normalizedContent) : normalizedContent;

        var matched = new List<TextReplacement>();
        for (var i = 0; i < total; i++)
        {
            var edit = normalizedEdits[i];
            var matchResult = FuzzyFindText(replacementBaseContent, edit.OldText);
            if (!matchResult.Found)
            {
                throw new InvalidOperationException(total == 1
                    ? $"Could not find the exact text in {path}. The old text must match exactly including all whitespace and newlines."
                    : $"Could not find edits[{i}] in {path}. The oldText must match exactly including all whitespace and newlines.");
            }

            var occurrences = CountOccurrences(replacementBaseContent, edit.OldText);
            if (occurrences > 1)
            {
                throw new InvalidOperationException(total == 1
                    ? $"Found {occurrences} occurrences of the text in {path}. The text must be unique. Please provide more context to make it unique."
                    : $"Found {occurrences} occurrences of edits[{i}] in {path}. Each oldText must be unique. Please provide more context to make it unique.");
            }
            matched.Add(new TextReplacement(i, matchResult.Index, matchResult.MatchLength, edit.NewText));
        }

        matched = matched.OrderBy(m => m.MatchIndex).ToList();
        for (var i = 1; i < matched.Count; i++)
        {
            var previous = matched[i - 1];
            var current = matched[i];
            if (previous.MatchIndex + previous.MatchLength > current.MatchIndex)
            {
                throw new InvalidOperationException(
                    $"edits[{previous.EditIndex}] and edits[{current.EditIndex}] overlap in {path}. Merge them into one edit or target disjoint regions.");
            }
        }

        var newContent = usedFuzzyMatch
            ? ApplyReplacementsPreservingUnchangedLines(normalizedContent, replacementBaseContent, matched)
            : ApplyReplacements(replacementBaseContent, matched);

        if (normalizedContent == newContent)
        {
            throw new InvalidOperationException(total == 1
                ? $"No changes made to {path}. The replacement produced identical content. This might indicate an issue with special characters or the text not existing as expected."
                : $"No changes made to {path}. The replacements produced identical content.");
        }
        return (normalizedContent, newContent);
    }

    public static string GenerateUnifiedPatch(string path, string oldContent, string newContent, int contextLines = 4) =>
        LineDiff.CreateTwoFilesPatch(path, path, oldContent, newContent, contextLines);

    /// <summary>Display-oriented diff with line numbers and context.</summary>
    public static EditDiffResult GenerateDiffString(string oldContent, string newContent, int contextLines = 4)
    {
        var parts = LineDiff.DiffLines(oldContent, newContent);
        var output = new List<string>();
        var maxLineNum = Math.Max(oldContent.Split('\n').Length, newContent.Split('\n').Length);
        var width = maxLineNum.ToString().Length;
        var blank = "".PadLeft(width, ' ');

        int oldLineNum = 1, newLineNum = 1;
        var lastWasChange = false;
        int? firstChangedLine = null;

        string Num(int n) => n.ToString().PadLeft(width, ' ');

        for (var i = 0; i < parts.Count; i++)
        {
            var part = parts[i];
            var raw = part.Value.Split('\n').ToList();
            if (raw[^1] == "") raw.RemoveAt(raw.Count - 1);

            if (part.Added || part.Removed)
            {
                firstChangedLine ??= newLineNum;
                foreach (var line in raw)
                {
                    if (part.Added)
                    {
                        output.Add($"+{Num(newLineNum)} {line}");
                        newLineNum++;
                    }
                    else
                    {
                        output.Add($"-{Num(oldLineNum)} {line}");
                        oldLineNum++;
                    }
                }
                lastWasChange = true;
                continue;
            }

            var hasTrailingChange = i < parts.Count - 1 && (parts[i + 1].Added || parts[i + 1].Removed);
            var hasLeadingChange = lastWasChange;

            void Context(IEnumerable<string> lines)
            {
                foreach (var line in lines)
                {
                    output.Add($" {Num(oldLineNum)} {line}");
                    oldLineNum++;
                    newLineNum++;
                }
            }

            void Skip(int skipped)
            {
                output.Add($" {blank} ...");
                oldLineNum += skipped;
                newLineNum += skipped;
            }

            if (hasLeadingChange && hasTrailingChange)
            {
                if (raw.Count <= contextLines * 2)
                {
                    Context(raw);
                }
                else
                {
                    var leading = raw.Take(contextLines).ToList();
                    var trailing = raw.Skip(raw.Count - contextLines).ToList();
                    Context(leading);
                    Skip(raw.Count - leading.Count - trailing.Count);
                    Context(trailing);
                }
            }
            else if (hasLeadingChange)
            {
                var shown = raw.Take(contextLines).ToList();
                Context(shown);
                var skipped = raw.Count - shown.Count;
                if (skipped > 0) Skip(skipped);
            }
            else if (hasTrailingChange)
            {
                var skipped = Math.Max(0, raw.Count - contextLines);
                if (skipped > 0) Skip(skipped);
                Context(raw.Skip(skipped));
            }
            else
            {
                oldLineNum += raw.Count;
                newLineNum += raw.Count;
            }
            lastWasChange = false;
        }

        return new EditDiffResult(string.Join("\n", output), firstChangedLine);
    }

    /// <summary>Compute the diff for edits without applying them. Used for preview rendering.</summary>
    public static async Task<(EditDiffResult? Result, string? Error)> ComputeEditsDiffAsync(string path, IReadOnlyList<EditReplacement> edits, string cwd)
    {
        var absolutePath = ToolPaths.ResolveToCwd(path, cwd);
        try
        {
            if (!File.Exists(absolutePath)) return (null, $"Could not edit file: {path}. Error code: ENOENT.");
            var rawContent = await File.ReadAllTextAsync(absolutePath);
            var (_, content) = TextHelpers.SplitBom(rawContent);
            var (baseContent, newContent) = ApplyEditsToNormalizedContent(NormalizeToLF(content), edits, path);
            return (GenerateDiffString(baseContent, newContent), null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }
}
