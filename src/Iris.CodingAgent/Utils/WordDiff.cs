namespace Iris.CodingAgent.Utils;

/// <summary>Word diff. Faithful</summary>
public static class WordDiff
{
    private sealed class PathState
    {
        public int OldPos;
        public DiffChange? LastComponent;
    }

    private static bool IsWordChar(int cp) =>
        cp is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or 0xAD
            or (>= 0xC0 and <= 0xD6) or (>= 0xD8 and <= 0xF6) or (>= 0xF8 and <= 0x2C6) or (>= 0x2C8 and <= 0x2D7)
            or (>= 0x2DE and <= 0x2FF) or (>= 0x1E00 and <= 0x1EFF);

    private static bool IsJsSpace(char c) => Iris.Tui.TextUtils.IsJsWhitespace(c);

    private static bool ContainsSpace(string s) => s.Any(IsJsSpace);

    private static List<string> Parts(string value)
    {
        var parts = new List<string>();
        var i = 0;
        while (i < value.Length)
        {
            var len = char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]) ? 2 : 1;
            var code = len == 2 ? char.ConvertToUtf32(value[i], value[i + 1]) : value[i];
            if (IsWordChar(code))
            {
                var start = i;
                while (i < value.Length && IsWordChar(value[i])) i++;
                parts.Add(value[start..i]);
            }
            else if (IsJsSpace(value[i]))
            {
                var start = i;
                while (i < value.Length && IsJsSpace(value[i])) i++;
                parts.Add(value[start..i]);
            }
            else
            {
                parts.Add(value.Substring(i, len));
                i += len;
            }
        }
        return parts;
    }

    private static List<string> Tokenize(string value)
    {
        var tokens = new List<string>();
        string? prevPart = null;
        foreach (var part in Parts(value))
        {
            if (ContainsSpace(part))
            {
                if (prevPart is null) tokens.Add(part);
                else tokens[^1] += part;
            }
            else if (prevPart is not null && ContainsSpace(prevPart))
            {
                if (tokens[^1] == prevPart) tokens[^1] += part;
                else tokens.Add(prevPart + part);
            }
            else
            {
                tokens.Add(part);
            }
            prevPart = part;
        }
        return tokens;
    }

    private static bool TokensEqual(string left, string right) => left.Trim() == right.Trim();

    private static string Join(IEnumerable<string> tokens) =>
        string.Concat(tokens.Select((t, i) => i == 0 ? t : TrimLeadingSpace(t)));

    private static string TrimLeadingSpace(string s)
    {
        var i = 0;
        while (i < s.Length && IsJsSpace(s[i])) i++;
        return s[i..];
    }

    public static List<DiffChange> DiffWords(string oldStr, string newStr)
    {
        var oldTokens = Tokenize(oldStr).Where(t => t.Length > 0).ToList();
        var newTokens = Tokenize(newStr).Where(t => t.Length > 0).ToList();
        var newLen = newTokens.Count;
        var oldLen = oldTokens.Count;
        var maxEditLength = newLen + oldLen;
        var bestPath = new Dictionary<int, PathState?> { [0] = new PathState { OldPos = -1 } };

        var newPos = ExtractCommon(bestPath[0]!, newTokens, oldTokens, 0);
        if (bestPath[0]!.OldPos + 1 >= oldLen && newPos + 1 >= newLen) return PostProcess(BuildValues(bestPath[0]!.LastComponent, newTokens, oldTokens));

        var minDiagonal = int.MinValue;
        var maxDiagonal = int.MaxValue;
        for (var editLength = 1; editLength <= maxEditLength; editLength++)
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

                var basePath = !canRemove || (canAdd && removePath!.OldPos < addPath!.OldPos)
                    ? AddToPath(addPath!, true, false, 0)
                    : AddToPath(removePath!, false, true, 1);

                newPos = ExtractCommon(basePath, newTokens, oldTokens, diagonal);
                if (basePath.OldPos + 1 >= oldLen && newPos + 1 >= newLen) return PostProcess(BuildValues(basePath.LastComponent, newTokens, oldTokens));
                bestPath[diagonal] = basePath;
                if (basePath.OldPos + 1 >= oldLen) maxDiagonal = Math.Min(maxDiagonal, diagonal - 1);
                if (newPos + 1 >= newLen) minDiagonal = Math.Max(minDiagonal, diagonal + 1);
            }
        }
        return [];
    }

    private static PathState AddToPath(PathState path, bool added, bool removed, int oldPosInc)
    {
        var last = path.LastComponent;
        if (last is not null && last.Added == added && last.Removed == removed)
        {
            return new PathState { OldPos = path.OldPos + oldPosInc, LastComponent = new DiffChange { Count = last.Count + 1, Added = added, Removed = removed, Previous = last.Previous } };
        }
        return new PathState { OldPos = path.OldPos + oldPosInc, LastComponent = new DiffChange { Count = 1, Added = added, Removed = removed, Previous = last } };
    }

    private static int ExtractCommon(PathState basePath, List<string> newTokens, List<string> oldTokens, int diagonal)
    {
        var oldPos = basePath.OldPos;
        var newPos = oldPos - diagonal;
        var common = 0;
        while (newPos + 1 < newTokens.Count && oldPos + 1 < oldTokens.Count && TokensEqual(oldTokens[oldPos + 1], newTokens[newPos + 1]))
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
                component.Value = Join(newTokens.Skip(newPos).Take(component.Count));
                newPos += component.Count;
                if (!component.Added) oldPos += component.Count;
            }
            else
            {
                component.Value = Join(oldTokens.Skip(oldPos).Take(component.Count));
                oldPos += component.Count;
            }
        }
        return components;
    }

    private static List<DiffChange> PostProcess(List<DiffChange> changes)
    {
        DiffChange? lastKeep = null;
        DiffChange? insertion = null;
        DiffChange? deletion = null;
        foreach (var change in changes)
        {
            if (change.Added)
            {
                insertion = change;
            }
            else if (change.Removed)
            {
                deletion = change;
            }
            else
            {
                if (insertion is not null || deletion is not null) Dedupe(lastKeep, deletion, insertion, change);
                lastKeep = change;
                insertion = null;
                deletion = null;
            }
        }
        if (insertion is not null || deletion is not null) Dedupe(lastKeep, deletion, insertion, null);
        return changes;
    }

    private static string LeadingWs(string s)
    {
        var i = 0;
        while (i < s.Length && IsJsSpace(s[i])) i++;
        return s[..i];
    }

    private static string TrailingWs(string s)
    {
        var i = s.Length - 1;
        while (i >= 0 && IsJsSpace(s[i])) i--;
        return s[(i + 1)..];
    }

    private static string LongestCommonPrefix(string a, string b)
    {
        var i = 0;
        while (i < a.Length && i < b.Length && a[i] == b[i]) i++;
        return a[..i];
    }

    private static string LongestCommonSuffix(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0 || a[^1] != b[^1]) return "";
        var i = 0;
        while (i < a.Length && i < b.Length && a[a.Length - (i + 1)] == b[b.Length - (i + 1)]) i++;
        return a[(a.Length - i)..];
    }

    private static string ReplacePrefix(string s, string oldPrefix, string newPrefix)
    {
        if (!s.StartsWith(oldPrefix, StringComparison.Ordinal)) throw new InvalidOperationException($"string doesn't start with prefix; this is a bug");
        return newPrefix + s[oldPrefix.Length..];
    }

    private static string ReplaceSuffix(string s, string oldSuffix, string newSuffix)
    {
        if (oldSuffix.Length == 0) return s + newSuffix;
        if (!s.EndsWith(oldSuffix, StringComparison.Ordinal)) throw new InvalidOperationException($"string doesn't end with suffix; this is a bug");
        return s[..^oldSuffix.Length] + newSuffix;
    }

    private static string MaximumOverlap(string a, string b) => b[..OverlapCount(a, b)];

    private static int OverlapCount(string a, string b)
    {
        var startA = a.Length > b.Length ? a.Length - b.Length : 0;
        var endB = a.Length < b.Length ? a.Length : b.Length;
        if (endB == 0) return 0;
        var map = new int[endB];
        var k = 0;
        map[0] = 0;
        for (var j = 1; j < endB; j++)
        {
            map[j] = b[j] == b[k] ? map[k] : k;
            while (k > 0 && b[j] != b[k]) k = map[k];
            if (b[j] == b[k]) k++;
        }
        k = 0;
        for (var i = startA; i < a.Length; i++)
        {
            while (k > 0 && a[i] != b[k]) k = map[k];
            if (a[i] == b[k]) k++;
        }
        return k;
    }

    private static void Dedupe(DiffChange? startKeep, DiffChange? deletion, DiffChange? insertion, DiffChange? endKeep)
    {
        if (deletion is not null && insertion is not null)
        {
            var oldWsPrefix = LeadingWs(deletion.Value);
            var oldWsSuffix = TrailingWs(deletion.Value);
            var newWsPrefix = LeadingWs(insertion.Value);
            var newWsSuffix = TrailingWs(insertion.Value);
            if (startKeep is not null)
            {
                var common = LongestCommonPrefix(oldWsPrefix, newWsPrefix);
                startKeep.Value = ReplaceSuffix(startKeep.Value, newWsPrefix, common);
                deletion.Value = ReplacePrefix(deletion.Value, common, "");
                insertion.Value = ReplacePrefix(insertion.Value, common, "");
            }
            if (endKeep is not null)
            {
                var common = LongestCommonSuffix(oldWsSuffix, newWsSuffix);
                endKeep.Value = ReplacePrefix(endKeep.Value, newWsSuffix, common);
                deletion.Value = ReplaceSuffix(deletion.Value, common, "");
                insertion.Value = ReplaceSuffix(insertion.Value, common, "");
            }
        }
        else if (insertion is not null)
        {
            if (startKeep is not null) insertion.Value = insertion.Value[LeadingWs(insertion.Value).Length..];
            if (endKeep is not null) endKeep.Value = endKeep.Value[LeadingWs(endKeep.Value).Length..];
        }
        else if (startKeep is not null && endKeep is not null && deletion is not null)
        {
            var newWsFull = LeadingWs(endKeep.Value);
            var delWsStart = LeadingWs(deletion.Value);
            var delWsEnd = TrailingWs(deletion.Value);
            var newWsStart = LongestCommonPrefix(newWsFull, delWsStart);
            deletion.Value = ReplacePrefix(deletion.Value, newWsStart, "");
            var newWsEnd = LongestCommonSuffix(ReplacePrefix(newWsFull, newWsStart, ""), delWsEnd);
            deletion.Value = ReplaceSuffix(deletion.Value, newWsEnd, "");
            endKeep.Value = ReplacePrefix(endKeep.Value, newWsFull, newWsEnd);
            startKeep.Value = ReplaceSuffix(startKeep.Value, newWsFull, newWsFull[..(newWsFull.Length - newWsEnd.Length)]);
        }
        else if (endKeep is not null && deletion is not null)
        {
            var endKeepWsPrefix = LeadingWs(endKeep.Value);
            var deletionWsSuffix = TrailingWs(deletion.Value);
            deletion.Value = ReplaceSuffix(deletion.Value, MaximumOverlap(deletionWsSuffix, endKeepWsPrefix), "");
        }
        else if (startKeep is not null && deletion is not null)
        {
            var startKeepWsSuffix = TrailingWs(startKeep.Value);
            var deletionWsPrefix = LeadingWs(deletion.Value);
            deletion.Value = ReplacePrefix(deletion.Value, MaximumOverlap(startKeepWsSuffix, deletionWsPrefix), "");
        }
    }
}
