using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace Iris.CodingAgent.Utils;

/// <summary>
/// Minimal minimatch-compatible glob matching: *, **, ?, [...] character classes, {a,b} braces and leading "!"
/// negation are supported. Paths use "/" separators.
/// </summary>
public static class Glob
{
    private static readonly ConcurrentDictionary<(string, bool, bool), Regex> Cache = new();

    public static bool IsMatch(string input, string pattern, bool noCase = false, bool dot = false, bool matchBase = false)
    {
        var negate = false;
        while (pattern.StartsWith('!'))
        {
            negate = !negate;
            pattern = pattern[1..];
        }
        var target = input.Replace('\\', '/');
        if (matchBase && !pattern.Contains('/')) target = target[(target.LastIndexOf('/') + 1)..];
        var regex = Cache.GetOrAdd((pattern, noCase, dot), key => new Regex(ToRegex(key.Item1, key.Item3), RegexOptions.CultureInvariant | (key.Item2 ? RegexOptions.IgnoreCase : RegexOptions.None)));
        return regex.IsMatch(target) != negate;
    }

    public static bool HasMagic(string pattern) => pattern.IndexOfAny(['*', '?', '[', '{']) >= 0;

    private static IEnumerable<string> ExpandBraces(string pattern)
    {
        var depth = 0;
        var start = -1;
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '\\')
            {
                i++;
                continue;
            }
            if (c == '{')
            {
                if (depth == 0) start = i;
                depth++;
            }
            else if (c == '}' && depth > 0)
            {
                depth--;
                if (depth == 0 && start >= 0)
                {
                    var inner = pattern[(start + 1)..i];
                    var options = SplitTopLevel(inner);
                    if (options.Count < 2) break;
                    var prefix = pattern[..start];
                    var suffix = pattern[(i + 1)..];
                    return options.SelectMany(o => ExpandBraces(prefix + o + suffix)).ToList();
                }
            }
        }
        return [pattern];
    }

    private static List<string> SplitTopLevel(string inner)
    {
        var parts = new List<string>();
        var depth = 0;
        var current = new StringBuilder();
        foreach (var c in inner)
        {
            if (c == '{') depth++;
            if (c == '}') depth--;
            if (c == ',' && depth == 0)
            {
                parts.Add(current.ToString());
                current.Clear();
                continue;
            }
            current.Append(c);
        }
        parts.Add(current.ToString());
        return parts;
    }

    private static string ToRegex(string pattern, bool dot)
    {
        var alternatives = ExpandBraces(pattern).Select(p => SegmentRegex(p, dot));
        return "^(?:" + string.Join("|", alternatives) + ")$";
    }

    private static string SegmentRegex(string pattern, bool dot)
    {
        var sb = new StringBuilder();
        var noDot = dot ? "" : "(?!\\.)";
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            var atSegmentStart = i == 0 || pattern[i - 1] == '/';
            switch (c)
            {
                case '*':
                    if (i + 1 < pattern.Length && pattern[i + 1] == '*')
                    {
                        var prevSlash = i == 0 || pattern[i - 1] == '/';
                        var nextSlash = i + 2 >= pattern.Length || pattern[i + 2] == '/';
                        if (prevSlash && nextSlash)
                        {
                            i += 2;
                            if (i < pattern.Length)
                            {
                                // "**/" matches zero or more directories.
                                sb.Append(dot ? "(?:[^/]*/)*" : "(?:(?!\\.)[^/]*/)*");
                            }
                            else
                            {
                                sb.Append(dot ? ".*" : "(?:(?!\\.)[^/]*(?:/(?!\\.)[^/]*)*)?");
                            }
                            continue;
                        }
                        i++;
                    }
                    if (atSegmentStart) sb.Append(noDot);
                    sb.Append("[^/]*");
                    break;
                case '?':
                    if (atSegmentStart) sb.Append(noDot);
                    sb.Append("[^/]");
                    break;
                case '[':
                {
                    var end = pattern.IndexOf(']', i + 1);
                    if (end < 0)
                    {
                        sb.Append("\\[");
                        break;
                    }
                    var cls = pattern[(i + 1)..end];
                    if (cls.StartsWith('!')) cls = "^" + cls[1..];
                    sb.Append('[').Append(cls.Replace("\\", "\\\\")).Append(']');
                    i = end;
                    break;
                }
                case '\\':
                    if (i + 1 < pattern.Length)
                    {
                        sb.Append(Regex.Escape(pattern[i + 1].ToString()));
                        i++;
                    }
                    break;
                default:
                    sb.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }
        return sb.ToString();
    }
}

/// <summary>ANSI styling helpers mirroring the chalk calls used by the CLI.</summary>
public static class Chalk
{
    public static bool Enabled { get; set; } = !Console.IsOutputRedirected && Environment.GetEnvironmentVariable("NO_COLOR") is null;

    private static string Wrap(string text, string open, string close) => Enabled ? $"\e[{open}m{text}\e[{close}m" : text;

    public static string Red(string text) => Wrap(text, "31", "39");
    public static string Green(string text) => Wrap(text, "32", "39");
    public static string Yellow(string text) => Wrap(text, "33", "39");
    public static string Blue(string text) => Wrap(text, "34", "39");
    public static string Cyan(string text) => Wrap(text, "36", "39");
    public static string Gray(string text) => Wrap(text, "90", "39");
    public static string Dim(string text) => Wrap(text, "2", "22");
    public static string Bold(string text) => Wrap(text, "1", "22");
}
