using System.Text.RegularExpressions;

namespace Iris.CodingAgent.Utils;

public static partial class TextHelpers
{
    private const char Bom = '\uFEFF';

    public static string StripBom(string content) => content.Length > 0 && content[0] == Bom ? content[1..] : content;

    public static (string Bom, string Text) SplitBom(string content) =>
        content.Length > 0 && content[0] == Bom ? ("\uFEFF", content[1..]) : ("", content);

    [GeneratedRegex("\"(?:\\\\.|[^\"\\\\])*\"|//[^\\n]*")]
    private static partial Regex JsonLineComments();

    [GeneratedRegex("\"(?:\\\\.|[^\"\\\\])*\"|,(\\s*[}\\]])")]
    private static partial Regex JsonTrailingCommas();

    /// <summary>Strip // line comments and trailing commas from JSON, leaving string literals untouched.</summary>
    public static string StripJsonComments(string input)
    {
        var withoutComments = JsonLineComments().Replace(input, m => m.Value[0] == '"' ? m.Value : "");
        return JsonTrailingCommas().Replace(withoutComments, m => m.Groups[1].Success ? m.Groups[1].Value : m.Value[0] == '"' ? m.Value : "");
    }
}

/// <summary>ANSI escape stripping (derived from ansi-regex / strip-ansi, MIT).</summary>
public static partial class AnsiUtils
{
    // OSC: ESC ] ... ST (BEL, ESC \, or 0x9C); CSI and related sequences.
    [GeneratedRegex("(?:\\u001B\\][\\s\\S]*?(?:\\u0007|\\u001B\\u005C|\\u009C))|[\\u001B\\u009B][\\[\\]()#;?]*(?:\\d{1,4}(?:[;:]\\d{0,4})*)?[\\dA-PR-TZcf-nq-uy=><~]")]
    private static partial Regex AnsiRegex();

    public static string StripAnsi(string value)
    {
        if (value.IndexOf('\x1B') < 0 && value.IndexOf('\x9B') < 0) return value;
        return AnsiRegex().Replace(value, "");
    }
}
