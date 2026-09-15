using System.Text;
using System.Text.Json.Nodes;
using TextMateSharp.Grammars;
using TextMateSharp.Registry;
using TextMateSharp.Themes;
using TmFontStyle = TextMateSharp.Themes.FontStyle;
using TmTheme = TextMateSharp.Themes.Theme;

namespace Iris.CodingAgent.Modes.Interactive;

/// <summary>
/// Syntax highlighting with VS Code's TextMate grammars and the token colors of VS Code's "Dark Modern" theme
/// (which inherits Dark+ token colors). Light themes use "Light Modern" (Light+ token colors) so code stays readable.
/// </summary>
public static class TextMateHighlighter
{
    /// <summary>VS Code stops tokenizing longer lines (editor.maxTokenizationLineLength).</summary>
    private const int MaxTokenizationLineLength = 20_000;

    private sealed class Engine(ThemeName themeName, string defaultForeground)
    {
        private readonly Lazy<(RegistryOptions Options, Registry Registry)> _registry = new(() =>
        {
            var options = new RegistryOptions(themeName);
            return (options, new Registry(options));
        });
        private readonly Dictionary<string, IGrammar?> _grammars = [];

        public string DefaultForeground { get; } = defaultForeground;

        public TmTheme Theme => _registry.Value.Registry.GetTheme();

        public IGrammar? GetGrammar(string languageId)
        {
            lock (_grammars)
            {
                if (_grammars.TryGetValue(languageId, out var cached)) return cached;
                IGrammar? grammar = null;
                try
                {
                    var (options, registry) = _registry.Value;
                    if (options.GetScopeByLanguageId(languageId) is { Length: > 0 } scope) grammar = registry.LoadGrammar(scope);
                }
                catch
                {
                    grammar = null;
                }
                _grammars[languageId] = grammar;
                return grammar;
            }
        }
    }

    // Dark Modern: editor.foreground #CCCCCC. Light Modern: editor.foreground #3B3B3B.
    private static readonly Engine DarkModern = new(ThemeName.DarkPlus, "#CCCCCC");
    private static readonly Engine LightModern = new(ThemeName.LightPlus, "#3B3B3B");

    /// <summary>Markdown code fence language names mapped to VS Code language ids.</summary>
    private static readonly Dictionary<string, string> LanguageIds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bash"] = "shellscript", ["sh"] = "shellscript", ["shell"] = "shellscript", ["zsh"] = "shellscript", ["shellscript"] = "shellscript", ["console"] = "shellscript",
        ["javascript"] = "javascript", ["js"] = "javascript", ["mjs"] = "javascript", ["cjs"] = "javascript", ["jsx"] = "javascriptreact",
        ["typescript"] = "typescript", ["ts"] = "typescript", ["tsx"] = "typescriptreact", ["mts"] = "typescript", ["cts"] = "typescript",
        ["python"] = "python", ["py"] = "python", ["csharp"] = "csharp", ["cs"] = "csharp", ["c#"] = "csharp",
        ["c"] = "c", ["h"] = "c", ["cpp"] = "cpp", ["c++"] = "cpp", ["cc"] = "cpp", ["cxx"] = "cpp", ["hpp"] = "cpp", ["cuda"] = "cuda-cpp",
        ["go"] = "go", ["golang"] = "go", ["rust"] = "rust", ["rs"] = "rust", ["java"] = "java", ["ruby"] = "ruby", ["rb"] = "ruby",
        ["php"] = "php", ["lua"] = "lua", ["perl"] = "perl", ["pl"] = "perl", ["raku"] = "perl6", ["r"] = "r", ["swift"] = "swift",
        ["dart"] = "dart", ["groovy"] = "groovy", ["clojure"] = "clojure", ["clj"] = "clojure", ["coffeescript"] = "coffeescript", ["coffee"] = "coffeescript",
        ["fsharp"] = "fsharp", ["fs"] = "fsharp", ["vb"] = "vb", ["vbnet"] = "vb", ["powershell"] = "powershell", ["ps1"] = "powershell", ["pwsh"] = "powershell",
        ["bat"] = "bat", ["batch"] = "bat", ["cmd"] = "bat", ["dos"] = "bat",
        ["html"] = "html", ["htm"] = "html", ["xml"] = "xml", ["svg"] = "xml", ["xsl"] = "xsl", ["css"] = "css", ["scss"] = "scss", ["less"] = "less",
        ["json"] = "json", ["jsonc"] = "jsonc", ["json5"] = "jsonc", ["yaml"] = "yaml", ["yml"] = "yaml", ["ini"] = "ini", ["properties"] = "properties",
        ["markdown"] = "markdown", ["md"] = "markdown", ["sql"] = "sql", ["dockerfile"] = "dockerfile", ["docker"] = "dockerfile",
        ["makefile"] = "makefile", ["make"] = "makefile", ["diff"] = "diff", ["patch"] = "diff", ["julia"] = "julia", ["jl"] = "julia",
        ["tex"] = "tex", ["latex"] = "latex", ["bibtex"] = "bibtex", ["objectivec"] = "objective-c", ["objective-c"] = "objective-c", ["objc"] = "objective-c",
        ["hlsl"] = "hlsl", ["razor"] = "razor", ["cshtml"] = "razor", ["handlebars"] = "handlebars", ["hbs"] = "handlebars", ["pug"] = "jade", ["jade"] = "jade",
        ["pascal"] = "pascal", ["asciidoc"] = "asciidoc", ["x86asm"] = "x86asm", ["asm"] = "x86asm", ["typst"] = "typst", ["log"] = "log",
    };

    /// <summary>Dark Modern / Light Modern colors for highlight.js scopes, used for languages without a TextMate grammar.</summary>
    public static IReadOnlyDictionary<string, string> FallbackScopeColors(bool light) => light ? LightScopeColors : DarkScopeColors;

    private static readonly Dictionary<string, string> DarkScopeColors = new()
    {
        ["keyword"] = "#569CD6", ["built_in"] = "#4EC9B0", ["literal"] = "#569CD6", ["number"] = "#B5CEA8", ["regexp"] = "#D16969",
        ["string"] = "#CE9178", ["comment"] = "#6A9955", ["doctag"] = "#6A9955", ["meta"] = "#569CD6", ["function"] = "#DCDCAA",
        ["title"] = "#DCDCAA", ["class"] = "#4EC9B0", ["type"] = "#4EC9B0", ["tag"] = "#808080", ["name"] = "#569CD6", ["attr"] = "#9CDCFE",
        ["variable"] = "#9CDCFE", ["params"] = "#9CDCFE", ["operator"] = "#D4D4D4", ["punctuation"] = "#CCCCCC", ["addition"] = "#B5CEA8",
        ["deletion"] = "#CE9178",
    };

    private static readonly Dictionary<string, string> LightScopeColors = new()
    {
        ["keyword"] = "#0000FF", ["built_in"] = "#267F99", ["literal"] = "#0000FF", ["number"] = "#098658", ["regexp"] = "#811F3F",
        ["string"] = "#A31515", ["comment"] = "#008000", ["doctag"] = "#008000", ["meta"] = "#0000FF", ["function"] = "#795E26",
        ["title"] = "#795E26", ["class"] = "#267F99", ["type"] = "#267F99", ["tag"] = "#800000", ["name"] = "#800000", ["attr"] = "#E50000",
        ["variable"] = "#001080", ["params"] = "#001080", ["operator"] = "#000000", ["punctuation"] = "#3B3B3B", ["addition"] = "#098658",
        ["deletion"] = "#A31515",
    };

    public static bool SupportsLanguage(string language) => LanguageIds.ContainsKey(language);

    /// <summary>editor.foreground of Dark Modern / Light Modern.</summary>
    public static string EditorForeground(bool light) => light ? LightModern.DefaultForeground : DarkModern.DefaultForeground;

    private static string FgAnsi(string hex, string colorMode) => ThemeColors.FgAnsi(JsonValue.Create(hex), colorMode);

    /// <summary>Highlight code; returns null when no grammar is available for the language.</summary>
    public static string? Highlight(string code, string language, bool light, string colorMode)
    {
        if (!LanguageIds.TryGetValue(language, out var languageId)) return null;
        var engine = light ? LightModern : DarkModern;
        var grammar = engine.GetGrammar(languageId);
        if (grammar is null) return null;
        var theme = engine.Theme;
        var defaultAnsi = FgAnsi(engine.DefaultForeground, colorMode);
        var colorCache = new Dictionary<int, string>();
        var sb = new StringBuilder(code.Length * 3);
        IStateStack? state = null;
        var lines = code.Split('\n');
        var tokenize = true;
        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            if (lineIndex > 0) sb.Append('\n');
            var line = lines[lineIndex];
            if (line.Length == 0) continue;
            if (!tokenize || line.Length > MaxTokenizationLineLength)
            {
                sb.Append(defaultAnsi).Append(line).Append("\e[39m");
                continue;
            }
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var result = grammar.TokenizeLine(line, state, TimeSpan.FromSeconds(1));
            // Like VS Code, stop tokenizing a pathological block instead of stalling the UI.
            if (System.Diagnostics.Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(1)) tokenize = false;
            state = result.RuleStack;
            foreach (var token in result.Tokens)
            {
                var start = Math.Min(token.StartIndex, line.Length);
                var end = Math.Min(token.EndIndex, line.Length);
                if (end <= start) continue;
                var foreground = -1;
                var fontStyle = TmFontStyle.NotSet;
                foreach (var rule in theme.Match(token.Scopes))
                {
                    if (foreground <= 0 && rule.foreground > 0) foreground = rule.foreground;
                    if (fontStyle == TmFontStyle.NotSet && rule.fontStyle != TmFontStyle.NotSet) fontStyle = rule.fontStyle;
                }
                string ansi;
                if (foreground > 0)
                {
                    if (!colorCache.TryGetValue(foreground, out ansi!))
                    {
                        ansi = FgAnsi(theme.GetColor(foreground), colorMode);
                        colorCache[foreground] = ansi;
                    }
                }
                else
                {
                    ansi = defaultAnsi;
                }
                var text = line[start..end];
                var bold = fontStyle > 0 && fontStyle.HasFlag(TmFontStyle.Bold);
                var italic = fontStyle > 0 && fontStyle.HasFlag(TmFontStyle.Italic);
                var underline = fontStyle > 0 && fontStyle.HasFlag(TmFontStyle.Underline);
                var strikethrough = fontStyle > 0 && fontStyle.HasFlag(TmFontStyle.Strikethrough);
                if (bold) sb.Append("\e[1m");
                if (italic) sb.Append("\e[3m");
                if (underline) sb.Append("\e[4m");
                if (strikethrough) sb.Append("\e[9m");
                sb.Append(ansi).Append(text).Append("\e[39m");
                if (strikethrough) sb.Append("\e[29m");
                if (underline) sb.Append("\e[24m");
                if (italic) sb.Append("\e[23m");
                if (bold) sb.Append("\e[22m");
            }
        }
        return sb.ToString();
    }
}
