using System.Text;

namespace PiSharp.CodingAgent.Utils;

public sealed class DiffChange
{
    public int Count { get; set; }
    public bool Added { get; set; }
    public bool Removed { get; set; }
    public string Value { get; set; } = "";
    internal DiffChange? Previous { get; set; }
    public List<string>? Lines { get; set; }
}

/// <summary>
/// Line diff and unified patch generation. Faithful port of jsdiff 8 (diffLines / createTwoFilesPatch) so diffs match
/// pi's output byte for byte.
/// </summary>
public static class LineDiff
{
    private sealed class PathState
    {
        public int OldPos;
        public DiffChange? LastComponent;
    }

    private static List<string> Tokenize(string value)
    {
        var parts = new List<string>();
        var start = 0;
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\n')
            {
                parts.Add(value[start..i]);
                parts.Add("\n");
                start = i + 1;
            }
            else if (value[i] == '\r' && i + 1 < value.Length && value[i + 1] == '\n')
            {
                parts.Add(value[start..i]);
                parts.Add("\r\n");
                start = i + 2;
                i++;
            }
        }
        parts.Add(value[start..]);
        if (parts.Count > 0 && parts[^1].Length == 0) parts.RemoveAt(parts.Count - 1);

        var lines = new List<string>();
        for (var i = 0; i < parts.Count; i++)
        {
            if (i % 2 == 1) lines[^1] += parts[i];
            else lines.Add(parts[i]);
        }
        return lines.Where(l => l.Length > 0).ToList();
    }

    public static List<DiffChange> DiffLines(string oldStr, string newStr)
    {
        var oldTokens = Tokenize(oldStr);
        var newTokens = Tokenize(newStr);
        var newLen = newTokens.Count;
        var oldLen = oldTokens.Count;
        var editLength = 1;
        var maxEditLength = newLen + oldLen;
        var bestPath = new Dictionary<int, PathState?> { [0] = new PathState { OldPos = -1 } };

        var newPos = ExtractCommon(bestPath[0]!, newTokens, oldTokens, 0);
        if (bestPath[0]!.OldPos + 1 >= oldLen && newPos + 1 >= newLen)
        {
            return BuildValues(bestPath[0]!.LastComponent, newTokens, oldTokens);
        }

        var minDiagonal = int.MinValue;
        var maxDiagonal = int.MaxValue;

        while (editLength <= maxEditLength)
        {
            for (var diagonal = Math.Max(minDiagonal, -editLength); diagonal <= Math.Min(maxDiagonal, editLength); diagonal += 2)
            {
                bestPath.TryGetValue(diagonal - 1, out var removePath);
                bestPath.TryGetValue(diagonal + 1, out var addPath);
                if (removePath is not null) bestPath[diagonal - 1] = null;

                var canAdd = false;
                if (addPath is not null)
                {
                    var addPathNewPos = addPath.OldPos - diagonal;
                    canAdd = addPathNewPos >= 0 && addPathNewPos < newLen;
                }
                var canRemove = removePath is not null && removePath.OldPos + 1 < oldLen;
                if (!canAdd && !canRemove)
                {
                    bestPath[diagonal] = null;
                    continue;
                }

                PathState basePath;
                if (!canRemove || (canAdd && removePath!.OldPos < addPath!.OldPos))
                {
                    basePath = AddToPath(addPath!, added: true, removed: false, oldPosInc: 0);
                }
                else
                {
                    basePath = AddToPath(removePath!, added: false, removed: true, oldPosInc: 1);
                }

                newPos = ExtractCommon(basePath, newTokens, oldTokens, diagonal);
                if (basePath.OldPos + 1 >= oldLen && newPos + 1 >= newLen)
                {
                    return BuildValues(basePath.LastComponent, newTokens, oldTokens);
                }
                bestPath[diagonal] = basePath;
                if (basePath.OldPos + 1 >= oldLen) maxDiagonal = Math.Min(maxDiagonal, diagonal - 1);
                if (newPos + 1 >= newLen) minDiagonal = Math.Max(minDiagonal, diagonal + 1);
            }
            editLength++;
        }
        return [];
    }

    private static PathState AddToPath(PathState path, bool added, bool removed, int oldPosInc)
    {
        var last = path.LastComponent;
        if (last is not null && last.Added == added && last.Removed == removed)
        {
            return new PathState
            {
                OldPos = path.OldPos + oldPosInc,
                LastComponent = new DiffChange { Count = last.Count + 1, Added = added, Removed = removed, Previous = last.Previous },
            };
        }
        return new PathState
        {
            OldPos = path.OldPos + oldPosInc,
            LastComponent = new DiffChange { Count = 1, Added = added, Removed = removed, Previous = last },
        };
    }

    private static int ExtractCommon(PathState basePath, List<string> newTokens, List<string> oldTokens, int diagonal)
    {
        var oldPos = basePath.OldPos;
        var newPos = oldPos - diagonal;
        var common = 0;
        while (newPos + 1 < newTokens.Count && oldPos + 1 < oldTokens.Count && oldTokens[oldPos + 1] == newTokens[newPos + 1])
        {
            newPos++;
            oldPos++;
            common++;
        }
        if (common > 0) basePath.LastComponent = new DiffChange { Count = common, Previous = basePath.LastComponent };
        basePath.OldPos = oldPos;
        return newPos;
    }

    private static List<DiffChange> BuildValues(DiffChange? last, List<string> newTokens, List<string> oldTokens)
    {
        var components = new List<DiffChange>();
        while (last is not null)
        {
            components.Add(last);
            var next = last.Previous;
            last.Previous = null;
            last = next;
        }
        components.Reverse();

        int newPos = 0, oldPos = 0;
        foreach (var component in components)
        {
            if (!component.Removed)
            {
                component.Value = string.Concat(newTokens.Skip(newPos).Take(component.Count));
                newPos += component.Count;
                if (!component.Added) oldPos += component.Count;
            }
            else
            {
                component.Value = string.Concat(oldTokens.Skip(oldPos).Take(component.Count));
                oldPos += component.Count;
            }
        }
        return components;
    }

    private static List<string> SplitLines(string text)
    {
        var hasTrailingNl = text.EndsWith('\n');
        var result = text.Split('\n').Select(l => l + "\n").ToList();
        if (hasTrailingNl) result.RemoveAt(result.Count - 1);
        else result[^1] = result[^1][..^1];
        return result;
    }

    private sealed class Hunk
    {
        public int OldStart;
        public int OldLines;
        public int NewStart;
        public int NewLines;
        public List<string> Lines = [];
    }

    /// <summary>Unified patch with "--- old" / "+++ new" headers only (jsdiff FILE_HEADERS_ONLY).</summary>
    public static string CreateTwoFilesPatch(string oldFileName, string newFileName, string oldStr, string newStr, int context = 4)
    {
        var diff = DiffLines(oldStr, newStr);
        diff.Add(new DiffChange { Value = "", Lines = [] });

        var hunks = new List<Hunk>();
        int oldRangeStart = 0, newRangeStart = 0, oldLine = 1, newLine = 1;
        var curRange = new List<string>();
        static IEnumerable<string> ContextLines(IEnumerable<string> lines) => lines.Select(l => " " + l);

        for (var i = 0; i < diff.Count; i++)
        {
            var current = diff[i];
            var lines = current.Lines ??= SplitLines(current.Value);
            if (current.Added || current.Removed)
            {
                if (oldRangeStart == 0)
                {
                    var prev = i > 0 ? diff[i - 1] : null;
                    oldRangeStart = oldLine;
                    newRangeStart = newLine;
                    if (prev is not null)
                    {
                        curRange = context > 0 ? ContextLines(prev.Lines!.Skip(Math.Max(0, prev.Lines!.Count - context))).ToList() : [];
                        oldRangeStart -= curRange.Count;
                        newRangeStart -= curRange.Count;
                    }
                }
                foreach (var line in lines) curRange.Add((current.Added ? "+" : "-") + line);
                if (current.Added) newLine += lines.Count;
                else oldLine += lines.Count;
            }
            else
            {
                if (oldRangeStart != 0)
                {
                    if (lines.Count <= context * 2 && i < diff.Count - 2)
                    {
                        curRange.AddRange(ContextLines(lines));
                    }
                    else
                    {
                        var contextSize = Math.Min(lines.Count, context);
                        curRange.AddRange(ContextLines(lines.Take(contextSize)));
                        hunks.Add(new Hunk
                        {
                            OldStart = oldRangeStart,
                            OldLines = oldLine - oldRangeStart + contextSize,
                            NewStart = newRangeStart,
                            NewLines = newLine - newRangeStart + contextSize,
                            Lines = curRange,
                        });
                        oldRangeStart = 0;
                        newRangeStart = 0;
                        curRange = [];
                    }
                }
                oldLine += lines.Count;
                newLine += lines.Count;
            }
        }

        foreach (var hunk in hunks)
        {
            for (var i = 0; i < hunk.Lines.Count; i++)
            {
                if (hunk.Lines[i].EndsWith('\n'))
                {
                    hunk.Lines[i] = hunk.Lines[i][..^1];
                }
                else
                {
                    hunk.Lines.Insert(i + 1, "\\ No newline at end of file");
                    i++;
                }
            }
        }

        var sb = new StringBuilder();
        sb.Append("--- ").Append(oldFileName).Append('\n');
        sb.Append("+++ ").Append(newFileName);
        foreach (var hunk in hunks)
        {
            var oldStart = hunk.OldLines == 0 ? hunk.OldStart - 1 : hunk.OldStart;
            var newStart = hunk.NewLines == 0 ? hunk.NewStart - 1 : hunk.NewStart;
            sb.Append('\n').Append($"@@ -{oldStart},{hunk.OldLines} +{newStart},{hunk.NewLines} @@");
            foreach (var line in hunk.Lines) sb.Append('\n').Append(line);
        }
        return sb.Append('\n').ToString();
    }
}
