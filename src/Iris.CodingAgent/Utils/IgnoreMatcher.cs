using System.Text;
using System.Text.RegularExpressions;

namespace Iris.CodingAgent.Utils;

/// <summary>
/// gitignore-style matcher: rules are relative posix paths,
/// the last matching rule wins, and a path is ignored when any parent directory is ignored.
/// </summary>
public sealed class IgnoreMatcher
{
    private static readonly string[] IgnoreFileNames = [".gitignore", ".ignore", ".fdignore"];

    private readonly List<(Regex Regex, bool Negated)> _rules = [];
    private readonly Dictionary<string, bool> _cache = new(StringComparer.Ordinal);

    public void Add(IEnumerable<string> patterns)
    {
        foreach (var raw in patterns)
        {
            var rule = CreateRule(raw);
            if (rule is not null) _rules.Add(rule.Value);
        }
        _cache.Clear();
    }

    /// <summary>Whether a relative posix path (directories with a trailing "/") is ignored.</summary>
    public bool Ignores(string path)
    {
        if (_rules.Count == 0 || path.Length == 0) return false;
        return IgnoresCached(path);
    }

    private bool IgnoresCached(string path)
    {
        if (_cache.TryGetValue(path, out var cached)) return cached;

        var trimmed = path.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        var result = slash > 0 && IgnoresCached(trimmed[..(slash + 1)]) || TestRules(path);
        _cache[path] = result;
        return result;
    }

    private bool TestRules(string path)
    {
        var ignored = false;
        foreach (var (regex, negated) in _rules)
        {
            if (negated == !ignored) continue;
            if (regex.IsMatch(path)) ignored = !negated;
        }
        return ignored;
    }

    private static (Regex, bool)? CreateRule(string pattern)
    {
        // Trailing unescaped whitespace is not significant.
        var end = pattern.Length;
        while (end > 0 && pattern[end - 1] == ' ' && !(end > 1 && pattern[end - 2] == '\\')) end--;
        pattern = pattern[..end];
        if (pattern.Length == 0 || pattern.StartsWith('#')) return null;

        var negated = false;
        if (pattern.StartsWith('!'))
        {
            negated = true;
            pattern = pattern[1..];
        }
        else if (pattern.StartsWith("\\!", StringComparison.Ordinal) || pattern.StartsWith("\\#", StringComparison.Ordinal))
        {
            pattern = pattern[1..];
        }

        var dirOnly = pattern.EndsWith('/');
        if (dirOnly) pattern = pattern.TrimEnd('/');
        if (pattern.Length == 0) return null;

        var anchored = pattern.StartsWith('/') || pattern.Contains('/');
        if (pattern.StartsWith('/')) pattern = pattern[1..];

        var sb = new StringBuilder(anchored ? "^" : "(?:^|/)");
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*')
            {
                var atStart = i == 0 || pattern[i - 1] == '/';
                var atEnd = i + 2 == pattern.Length;
                var beforeSlash = i + 2 < pattern.Length && pattern[i + 2] == '/';
                if (atStart && beforeSlash)
                {
                    sb.Append("(?:.*/)?");
                    i += 2;
                    continue;
                }
                if (atStart && atEnd)
                {
                    sb.Append(".*");
                    i += 1;
                    continue;
                }
                sb.Append("[^/]*");
                i += 1;
                continue;
            }
            switch (c)
            {
                case '*':
                    sb.Append("[^/]*");
                    break;
                case '?':
                    sb.Append("[^/]");
                    break;
                case '[':
                {
                    var close = pattern.IndexOf(']', i + 1);
                    if (close < 0)
                    {
                        sb.Append("\\[");
                        break;
                    }
                    var cls = pattern[(i + 1)..close];
                    if (cls.StartsWith('!')) cls = "^" + cls[1..];
                    sb.Append('[').Append(cls.Replace("\\", "\\\\")).Append(']');
                    i = close;
                    break;
                }
                case '\\' when i + 1 < pattern.Length:
                    sb.Append(Regex.Escape(pattern[i + 1].ToString()));
                    i++;
                    break;
                default:
                    sb.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }
        sb.Append(dirOnly ? "/" : "(?=$|/$)");

        try
        {
            return (new Regex(sb.ToString(), RegexOptions.CultureInvariant), negated);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    public static string ToPosixPath(string path) => path.Replace(Path.DirectorySeparatorChar, '/');

    private static string? PrefixIgnorePattern(string line, string prefix)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0) return null;
        if (trimmed.StartsWith('#') && !trimmed.StartsWith("\\#", StringComparison.Ordinal)) return null;

        var pattern = line;
        var negated = false;
        if (pattern.StartsWith('!'))
        {
            negated = true;
            pattern = pattern[1..];
        }
        else if (pattern.StartsWith("\\!", StringComparison.Ordinal))
        {
            pattern = pattern[1..];
        }
        if (pattern.StartsWith('/')) pattern = pattern[1..];
        var prefixed = prefix.Length > 0 ? prefix + pattern : pattern;
        return negated ? "!" + prefixed : prefixed;
    }

    /// <summary>Add rules from .gitignore/.ignore/.fdignore in dir, prefixed relative to rootDir.</summary>
    public void AddIgnoreFiles(string dir, string rootDir)
    {
        var relativeDir = Path.GetRelativePath(rootDir, dir);
        var prefix = relativeDir is "." or "" ? "" : ToPosixPath(relativeDir) + "/";
        foreach (var fileName in IgnoreFileNames)
        {
            var ignorePath = Path.Combine(dir, fileName);
            if (!File.Exists(ignorePath)) continue;
            try
            {
                var patterns = File.ReadAllText(ignorePath)
                    .Split('\n')
                    .Select(l => l.TrimEnd('\r'))
                    .Select(l => PrefixIgnorePattern(l, prefix))
                    .OfType<string>()
                    .ToList();
                if (patterns.Count > 0) Add(patterns);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
