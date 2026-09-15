using System.Text;
using System.Text.RegularExpressions;

namespace Iris.Tui.Markdown;

/// <summary>
/// Translates JavaScript regular expression sources to equivalent .NET patterns: ASCII-only \d, \w and \b, JavaScript
/// whitespace for \s, end-of-input-only $ (without the m flag) and line-terminator-aware . and m-mode anchors.
/// Named groups keep their JavaScript meaning but .NET numbers them after unnamed groups, so callers must not rely on
/// positional numbers for patterns that mix both.
/// </summary>
public static class JsRegex
{
    private const string JsWhitespaceClass = @"\t\n\x0B\f\r \u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000\uFEFF";
    private const string WordClass = "a-zA-Z0-9_";
    private const string LineTerminators = @"\n\r\u2028\u2029";

    public static Regex Create(string source, string flags = "") => new(Translate(source, flags), ToOptions(flags));

    public static RegexOptions ToOptions(string flags)
    {
        var options = RegexOptions.CultureInvariant | RegexOptions.Compiled;
        if (flags.Contains('i')) options |= RegexOptions.IgnoreCase;
        return options;
    }

    public static string Translate(string source, string flags = "")
    {
        var multiline = flags.Contains('m');
        var dotAll = flags.Contains('s');
        var sb = new StringBuilder(source.Length + 32);
        var inClass = false;
        for (var i = 0; i < source.Length; i++)
        {
            var c = source[i];
            if (c == '\\' && i + 1 < source.Length)
            {
                var n = source[++i];
                if (inClass)
                {
                    sb.Append(n switch
                    {
                        'd' => "0-9",
                        'w' => WordClass,
                        's' => JsWhitespaceClass,
                        '/' => "/",
                        _ => "\\" + n,
                    });
                }
                else
                {
                    sb.Append(n switch
                    {
                        'd' => "[0-9]",
                        'D' => "[^0-9]",
                        'w' => $"[{WordClass}]",
                        'W' => $"[^{WordClass}]",
                        's' => $"[{JsWhitespaceClass}]",
                        'S' => $"[^{JsWhitespaceClass}]",
                        'b' => $"(?:(?<=[{WordClass}])(?![{WordClass}])|(?<![{WordClass}])(?=[{WordClass}]))",
                        'B' => $"(?:(?<=[{WordClass}])(?=[{WordClass}])|(?<![{WordClass}])(?![{WordClass}]))",
                        '/' => "/",
                        _ => "\\" + n,
                    });
                }
                continue;
            }

            if (inClass)
            {
                if (c == ']') inClass = false;
                // .NET treats "-[" as character class subtraction; JavaScript treats "[" literally.
                if (c == '[') sb.Append("\\[");
                else sb.Append(c);
                continue;
            }

            switch (c)
            {
                case '[':
                    inClass = true;
                    sb.Append(c);
                    // A leading ']' (or '^]') is a literal in .NET but closes an empty class in JS; not used by callers.
                    if (i + 1 < source.Length && source[i + 1] == '^')
                    {
                        sb.Append('^');
                        i++;
                    }
                    break;
                case '$':
                    sb.Append(multiline ? $"(?=[{LineTerminators}]|\\z)" : "\\z");
                    break;
                case '^':
                    sb.Append(multiline ? $"(?<=\\A|[{LineTerminators}])" : "\\A");
                    break;
                case '.':
                    sb.Append(dotAll ? "[\\s\\S]" : $"[^{LineTerminators}]");
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>JavaScript <c>exec</c> from <paramref name="lastIndex"/> for global regexes.</summary>
    public static Match? ExecFrom(Regex regex, string input, ref int lastIndex)
    {
        if (lastIndex > input.Length)
        {
            lastIndex = 0;
            return null;
        }
        var match = regex.Match(input, lastIndex);
        if (!match.Success)
        {
            lastIndex = 0;
            return null;
        }
        lastIndex = match.Index + match.Length;
        return match;
    }

    public static int CodePointCount(string s)
    {
        var count = 0;
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) i++;
            count++;
        }
        return count;
    }

    public static int FirstCodePointLength(string s) =>
        s.Length >= 2 && char.IsHighSurrogate(s[0]) && char.IsLowSurrogate(s[1]) ? 2 : Math.Min(1, s.Length);
}
