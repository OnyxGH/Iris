using System.Text;

namespace Iris.CodingAgent.Modes.Interactive;

/// <summary>
/// Lightweight lexical highlighter for languages without a TextMate grammar: comments, strings, numbers, keywords,
/// literals, types and function names are mapped onto scope names. Grammars are approximations.
/// </summary>
public static class SyntaxHighlighter
{
    private sealed class Language
    {
        public string[] LineComments { get; init; } = [];
        public (string Open, string Close)[] BlockComments { get; init; } = [];
        public char[] StringQuotes { get; init; } = ['"', '\''];
        public HashSet<string> Keywords { get; init; } = [];
        public HashSet<string> Literals { get; init; } = [];
        public HashSet<string> BuiltIns { get; init; } = [];
        public HashSet<string> TypeIntroducers { get; init; } = [];
        public bool CallsAreFunctions { get; init; } = true;
        public bool CapitalizedAreTypes { get; init; }
        public bool VariablesWithDollar { get; init; }
        public string Kind { get; init; } = "code"; // code | json | markup | diff | markdown | yaml | ini
    }

    private static HashSet<string> Words(string words) => [.. words.Split(' ', StringSplitOptions.RemoveEmptyEntries)];

    private static readonly HashSet<string> CommonLiterals = Words("true false null");

    private static readonly Language CLike = new()
    {
        LineComments = ["//"],
        BlockComments = [("/*", "*/")],
        Keywords = Words("if else for while do switch case default break continue return goto sizeof typedef struct union enum static extern const volatile inline register auto signed unsigned"),
        Literals = Words("true false NULL nullptr"),
        BuiltIns = Words("int char float double void long short bool size_t uint8_t uint16_t uint32_t uint64_t int8_t int16_t int32_t int64_t"),
        TypeIntroducers = Words("struct union enum"),
    };

    private static readonly Dictionary<string, Language> Languages = BuildLanguages();

    private static Dictionary<string, Language> BuildLanguages()
    {
        var js = new Language
        {
            LineComments = ["//"],
            BlockComments = [("/*", "*/")],
            StringQuotes = ['"', '\'', '`'],
            Keywords = Words("as async await break case catch class const continue debugger default delete do else enum export extends finally for from function get if implements import in instanceof interface let new of package private protected public return set static super switch this throw try typeof var void while with yield"),
            Literals = Words("true false null undefined NaN Infinity"),
            BuiltIns = Words("Object Function Boolean Symbol Math Date Number BigInt String RegExp Array Map Set WeakMap WeakSet Promise JSON Error console window document process require module exports globalThis"),
            TypeIntroducers = Words("class extends implements interface"),
        };
        var ts = new Language
        {
            LineComments = js.LineComments,
            BlockComments = js.BlockComments,
            StringQuotes = js.StringQuotes,
            Keywords = [.. js.Keywords, .. Words("type namespace declare abstract readonly keyof infer is satisfies override")],
            Literals = js.Literals,
            BuiltIns = [.. js.BuiltIns, .. Words("string number boolean any unknown never void object bigint symbol Record Partial Readonly Pick Omit")],
            TypeIntroducers = [.. js.TypeIntroducers, "type"],
        };
        var python = new Language
        {
            LineComments = ["#"],
            StringQuotes = ['"', '\''],
            Keywords = Words("and as assert async await break class continue def del elif else except finally for from global if import in is lambda nonlocal not or pass raise return try while with yield match case"),
            Literals = Words("True False None"),
            BuiltIns = Words("print len range int str float list dict set tuple bool open super self isinstance type object Exception enumerate zip map filter sorted min max sum abs"),
            TypeIntroducers = Words("class"),
            CallsAreFunctions = false,
        };
        var bash = new Language
        {
            LineComments = ["#"],
            Keywords = Words("if then else elif fi case esac for select while until do done in function return break continue local export readonly declare unset shift"),
            Literals = CommonLiterals,
            BuiltIns = Words("echo cd pwd ls cat grep sed awk printf read test source eval exec exit set trap kill wait"),
            CallsAreFunctions = false,
            VariablesWithDollar = true,
        };
        var csharp = new Language
        {
            LineComments = ["//"],
            BlockComments = [("/*", "*/")],
            Keywords = Words("abstract as async await base break case catch checked class const continue default delegate do else enum event explicit extern finally fixed for foreach goto if implicit in interface internal is lock namespace new operator out override params private protected public readonly record ref return sealed sizeof stackalloc static struct switch this throw try typeof unchecked unsafe using var virtual volatile when where while yield init required get set"),
            Literals = Words("true false null"),
            BuiltIns = Words("bool byte char decimal double float int long object sbyte short string uint ulong ushort void dynamic nint nuint"),
            TypeIntroducers = Words("class struct interface enum record"),
        };
        var java = new Language
        {
            LineComments = ["//"],
            BlockComments = [("/*", "*/")],
            Keywords = Words("abstract assert break case catch class const continue default do else enum extends final finally for goto if implements import instanceof interface native new package private protected public return static strictfp super switch synchronized this throw throws transient try volatile while var record yield"),
            Literals = Words("true false null"),
            BuiltIns = Words("boolean byte char double float int long short void String Object Integer"),
            TypeIntroducers = Words("class interface enum extends implements record"),
        };
        var go = new Language
        {
            LineComments = ["//"],
            BlockComments = [("/*", "*/")],
            StringQuotes = ['"', '\'', '`'],
            Keywords = Words("break case chan const continue default defer else fallthrough for func go goto if import interface map package range return select struct switch type var"),
            Literals = Words("true false nil iota"),
            BuiltIns = Words("append cap close complex copy delete imag len make new panic print println real recover bool byte complex64 complex128 error float32 float64 int int8 int16 int32 int64 rune string uint uint8 uint16 uint32 uint64 uintptr any"),
            TypeIntroducers = Words("type"),
        };
        var rust = new Language
        {
            LineComments = ["//"],
            BlockComments = [("/*", "*/")],
            Keywords = Words("as async await break const continue crate dyn else enum extern fn for if impl in let loop match mod move mut pub ref return self Self static struct super trait type unsafe use where while"),
            Literals = Words("true false Some None Ok Err"),
            BuiltIns = Words("i8 i16 i32 i64 i128 isize u8 u16 u32 u64 u128 usize f32 f64 bool char str String Vec Option Result Box"),
            TypeIntroducers = Words("struct enum trait impl type"),
        };
        var ruby = new Language
        {
            LineComments = ["#"],
            Keywords = Words("alias and begin break case class def defined do else elsif end ensure for if in module next not or redo rescue retry return self super then undef unless until when while yield require"),
            Literals = Words("true false nil"),
            TypeIntroducers = Words("class module"),
            CallsAreFunctions = false,
        };
        var php = new Language
        {
            LineComments = ["//", "#"],
            BlockComments = [("/*", "*/")],
            Keywords = Words("abstract and as break case catch class clone const continue declare default do echo else elseif empty enddeclare endfor endforeach endif endswitch endwhile extends final finally fn for foreach function global if implements include instanceof interface isset list match namespace new or print private protected public readonly require return static switch throw trait try unset use var while yield"),
            Literals = Words("true false null TRUE FALSE NULL"),
            TypeIntroducers = Words("class interface trait extends implements"),
            VariablesWithDollar = true,
        };
        var kotlin = new Language
        {
            LineComments = ["//"],
            BlockComments = [("/*", "*/")],
            Keywords = Words("abstract annotation as break by catch class companion const constructor continue crossinline data do else enum external final finally for fun get if import in infix init inline inner interface internal is lateinit noinline object open operator out override package private protected public reified return sealed set super suspend tailrec this throw try typealias val var vararg when where while"),
            Literals = Words("true false null"),
            BuiltIns = Words("Int Long Short Byte Double Float Boolean Char String Unit Any Nothing"),
            TypeIntroducers = Words("class interface object"),
        };
        var swift = new Language
        {
            LineComments = ["//"],
            BlockComments = [("/*", "*/")],
            Keywords = Words("associatedtype class deinit enum extension fileprivate func import init inout internal let open operator private protocol public rethrows static struct subscript typealias var break case continue default defer do else fallthrough for guard if in repeat return switch where while as catch is super self Self throw throws try async await"),
            Literals = Words("true false nil"),
            BuiltIns = Words("Int Double Float Bool String Character Array Dictionary Set Optional"),
            TypeIntroducers = Words("class struct enum protocol extension"),
        };
        var lua = new Language
        {
            LineComments = ["--"],
            BlockComments = [("--[[", "]]")],
            Keywords = Words("and break do else elseif end for function goto if in local not or repeat return then until while"),
            Literals = Words("true false nil"),
            BuiltIns = Words("print pairs ipairs require type tostring tonumber table string math os io"),
        };
        var sql = new Language
        {
            LineComments = ["--"],
            BlockComments = [("/*", "*/")],
            Keywords = Words("select from where insert into values update set delete create table drop alter add index primary key foreign references join left right inner outer on group by order having limit offset as and or not null is in like between distinct union all case when then else end exists view default unique SELECT FROM WHERE INSERT INTO VALUES UPDATE SET DELETE CREATE TABLE DROP ALTER ADD INDEX PRIMARY KEY FOREIGN REFERENCES JOIN LEFT RIGHT INNER OUTER ON GROUP BY ORDER HAVING LIMIT OFFSET AS AND OR NOT NULL IS IN LIKE BETWEEN DISTINCT UNION ALL CASE WHEN THEN ELSE END EXISTS VIEW DEFAULT UNIQUE"),
            Literals = Words("true false TRUE FALSE"),
            BuiltIns = Words("int integer varchar text boolean date timestamp count sum avg min max INT INTEGER VARCHAR TEXT BOOLEAN DATE TIMESTAMP COUNT SUM AVG MIN MAX"),
            CallsAreFunctions = false,
        };
        var powershell = new Language
        {
            LineComments = ["#"],
            BlockComments = [("<#", "#>")],
            Keywords = Words("begin break catch class continue data do dynamicparam else elseif end exit filter finally for foreach from function if in param process return switch throw trap try until using var while"),
            Literals = Words("$true $false $null"),
            CallsAreFunctions = false,
            VariablesWithDollar = true,
        };
        var perl = new Language { LineComments = ["#"], Keywords = Words("my our local sub if elsif else unless while until for foreach last next redo return use require package"), CallsAreFunctions = false, VariablesWithDollar = true };
        var nix = new Language { LineComments = ["#"], BlockComments = [("/*", "*/")], Keywords = Words("let in with rec inherit if then else assert import"), Literals = Words("true false null"), CallsAreFunctions = false };
        var scala = new Language { LineComments = ["//"], BlockComments = [("/*", "*/")], Keywords = Words("abstract case catch class def do else extends final finally for forSome if implicit import lazy match new object override package private protected return sealed super this throw trait try type val var while with yield given using enum then"), Literals = Words("true false null"), TypeIntroducers = Words("class trait object extends with") };
        var dart = new Language { LineComments = ["//"], BlockComments = [("/*", "*/")], Keywords = Words("abstract as assert async await break case catch class const continue default deferred do dynamic else enum export extends extension external factory final finally for get if implements import in interface is late library mixin new on operator part required rethrow return set show static super switch sync this throw try typedef var void while with yield"), Literals = Words("true false null"), TypeIntroducers = Words("class extends implements with mixin") };
        var groovy = new Language { LineComments = ["//"], BlockComments = [("/*", "*/")], Keywords = Words("as assert break case catch class const continue def default do else enum extends final finally for goto if implements import in instanceof interface new package return super switch this throw throws trait try while"), Literals = Words("true false null"), TypeIntroducers = Words("class interface extends implements") };
        var cpp = new Language { LineComments = CLike.LineComments, BlockComments = CLike.BlockComments, Keywords = [.. CLike.Keywords, .. Words("class namespace template typename public private protected virtual override final new delete this operator using try catch throw friend mutable explicit constexpr noexcept decltype concept requires co_await co_return co_yield")], Literals = CLike.Literals, BuiltIns = [.. CLike.BuiltIns, .. Words("std string vector map set unique_ptr shared_ptr auto")], TypeIntroducers = [.. CLike.TypeIntroducers, "class"] };
        var yaml = new Language { LineComments = ["#"], Literals = Words("true false null yes no on off ~"), Kind = "yaml", CallsAreFunctions = false };
        var toml = new Language { LineComments = ["#"], Literals = Words("true false"), Kind = "ini", CallsAreFunctions = false };
        var json = new Language { Kind = "json", Literals = CommonLiterals, CallsAreFunctions = false };
        var markup = new Language { Kind = "markup", CallsAreFunctions = false };
        var css = new Language { BlockComments = [("/*", "*/")], Kind = "css", CallsAreFunctions = false };
        var diff = new Language { Kind = "diff", CallsAreFunctions = false };
        var markdown = new Language { Kind = "markdown", CallsAreFunctions = false };
        var dockerfile = new Language { LineComments = ["#"], Keywords = Words("FROM RUN CMD LABEL EXPOSE ENV ADD COPY ENTRYPOINT VOLUME USER WORKDIR ARG ONBUILD STOPSIGNAL HEALTHCHECK SHELL AS from run cmd label expose env add copy entrypoint volume user workdir arg"), CallsAreFunctions = false };
        var makefile = new Language { LineComments = ["#"], Keywords = Words("ifeq ifneq ifdef ifndef else endif include define endef export override"), CallsAreFunctions = false, VariablesWithDollar = true };
        var haskell = new Language { LineComments = ["--"], BlockComments = [("{-", "-}")], Keywords = Words("case class data default deriving do else if import in infix infixl infixr instance let module newtype of then type where"), Literals = Words("True False"), CallsAreFunctions = false, CapitalizedAreTypes = true };
        var elixir = new Language { LineComments = ["#"], Keywords = Words("after and catch do else end fn for if import in not or quote raise receive require rescue try unless unquote use when with def defp defmodule"), Literals = Words("true false nil"), CallsAreFunctions = false };
        var r = new Language { LineComments = ["#"], Keywords = Words("if else repeat while function for in next break"), Literals = Words("TRUE FALSE NULL NA Inf NaN T F") };
        var generic = new Language { LineComments = ["//", "#"], BlockComments = [("/*", "*/")], Literals = CommonLiterals };

        var map = new Dictionary<string, Language>(StringComparer.OrdinalIgnoreCase)
        {
            ["javascript"] = js, ["js"] = js, ["jsx"] = js, ["mjs"] = js, ["cjs"] = js,
            ["typescript"] = ts, ["ts"] = ts, ["tsx"] = ts, ["mts"] = ts, ["cts"] = ts,
            ["python"] = python, ["py"] = python, ["gyp"] = python, ["ipython"] = python,
            ["bash"] = bash, ["sh"] = bash, ["zsh"] = bash, ["shell"] = bash, ["console"] = bash, ["shellsession"] = bash, ["fish"] = bash,
            ["csharp"] = csharp, ["cs"] = csharp, ["c#"] = csharp,
            ["java"] = java, ["jsp"] = java,
            ["go"] = go, ["golang"] = go,
            ["rust"] = rust, ["rs"] = rust,
            ["ruby"] = ruby, ["rb"] = ruby, ["gemspec"] = ruby, ["podspec"] = ruby, ["thor"] = ruby, ["irb"] = ruby,
            ["php"] = php,
            ["kotlin"] = kotlin, ["kt"] = kotlin, ["kts"] = kotlin,
            ["swift"] = swift,
            ["lua"] = lua,
            ["sql"] = sql,
            ["powershell"] = powershell, ["ps"] = powershell, ["ps1"] = powershell, ["pwsh"] = powershell,
            ["perl"] = perl, ["pl"] = perl, ["pm"] = perl,
            ["nix"] = nix, ["nixos"] = nix,
            ["scala"] = scala,
            ["dart"] = dart,
            ["groovy"] = groovy,
            ["c"] = CLike, ["h"] = CLike,
            ["cpp"] = cpp, ["cc"] = cpp, ["c++"] = cpp, ["h++"] = cpp, ["hpp"] = cpp, ["hh"] = cpp, ["hxx"] = cpp, ["cxx"] = cpp,
            ["yaml"] = yaml, ["yml"] = yaml,
            ["toml"] = toml, ["ini"] = toml,
            ["json"] = json, ["jsonc"] = json, ["json5"] = json,
            ["html"] = markup, ["xml"] = markup, ["xhtml"] = markup, ["svg"] = markup, ["rss"] = markup, ["plist"] = markup, ["vue"] = markup,
            ["css"] = css, ["scss"] = css, ["sass"] = css, ["less"] = css,
            ["diff"] = diff, ["patch"] = diff,
            ["markdown"] = markdown, ["md"] = markdown, ["mkdown"] = markdown, ["mkd"] = markdown,
            ["dockerfile"] = dockerfile, ["docker"] = dockerfile,
            ["makefile"] = makefile, ["mk"] = makefile, ["mak"] = makefile, ["make"] = makefile, ["cmake"] = makefile,
            ["haskell"] = haskell, ["hs"] = haskell,
            ["elixir"] = elixir, ["ex"] = elixir, ["exs"] = elixir,
            ["r"] = r,
            ["objectivec"] = CLike, ["objc"] = CLike, ["erlang"] = generic, ["erl"] = generic, ["clojure"] = generic, ["clj"] = generic,
            ["ocaml"] = generic, ["ml"] = generic, ["vim"] = generic, ["graphql"] = generic, ["protobuf"] = generic, ["proto"] = generic,
            ["hcl"] = generic, ["tf"] = generic, ["julia"] = generic, ["zig"] = generic, ["fsharp"] = generic, ["fs"] = generic,
            ["plaintext"] = new Language { Kind = "plain" }, ["text"] = new Language { Kind = "plain" }, ["txt"] = new Language { Kind = "plain" },
        };
        return map;
    }

    public static bool SupportsLanguage(string name) => Languages.ContainsKey(name);

    public static string Highlight(string code, string language, IReadOnlyDictionary<string, Func<string, string>> theme)
    {
        if (!Languages.TryGetValue(language, out var lang)) return code;
        var sb = new StringBuilder(code.Length * 2);
        void Emit(string? scope, string text)
        {
            if (text.Length == 0) return;
            if (scope is not null && theme.TryGetValue(scope, out var fmt)) sb.Append(fmt(text));
            else sb.Append(text);
        }

        switch (lang.Kind)
        {
            case "plain":
                return code;
            case "diff":
                foreach (var (line, i) in code.Split('\n').Select((l, i) => (l, i)))
                {
                    if (i > 0) sb.Append('\n');
                    if (line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal)) Emit("meta", line);
                    else if (line.StartsWith('+')) Emit("addition", line);
                    else if (line.StartsWith('-')) Emit("deletion", line);
                    else if (line.StartsWith("@@", StringComparison.Ordinal)) Emit("meta", line);
                    else Emit(null, line);
                }
                return sb.ToString();
            case "markdown":
                HighlightMarkdown(code, Emit, sb);
                return sb.ToString();
            case "markup":
                HighlightMarkup(code, Emit);
                return sb.ToString();
        }

        var i2 = 0;
        var n = code.Length;
        var expectTypeName = false;
        var lineStart = true;
        while (i2 < n)
        {
            var c = code[i2];

            if (c == '\n')
            {
                sb.Append(c);
                i2++;
                lineStart = true;
                continue;
            }

            var matchedComment = false;
            foreach (var (open, close) in lang.BlockComments)
            {
                if (string.CompareOrdinal(code, i2, open, 0, open.Length) != 0) continue;
                var end = code.IndexOf(close, i2 + open.Length, StringComparison.Ordinal);
                var stop = end == -1 ? n : end + close.Length;
                Emit("comment", code[i2..stop]);
                i2 = stop;
                matchedComment = true;
                break;
            }
            if (matchedComment) continue;
            foreach (var marker in lang.LineComments)
            {
                if (string.CompareOrdinal(code, i2, marker, 0, marker.Length) != 0) continue;
                if (marker == "#" && lang.Kind == "code" && lang.VariablesWithDollar && i2 > 0 && code[i2 - 1] == '$') continue;
                var end = code.IndexOf('\n', i2);
                var stop = end == -1 ? n : end;
                Emit("comment", code[i2..stop]);
                i2 = stop;
                matchedComment = true;
                break;
            }
            if (matchedComment) continue;

            if (lang.Kind == "css")
            {
                if (c is '"' or '\'')
                {
                    var stop = ScanString(code, i2, c);
                    Emit("string", code[i2..stop]);
                    i2 = stop;
                    continue;
                }
                if (c == '#' || c == '.')
                {
                    var stop = i2 + 1;
                    while (stop < n && (char.IsLetterOrDigit(code[stop]) || code[stop] is '-' or '_')) stop++;
                    Emit(c == '#' && stop - i2 == 7 || c == '#' && stop - i2 == 4 ? "number" : "selector-class", code[i2..stop]);
                    i2 = stop;
                    continue;
                }
                if (char.IsLetter(c) || c == '-')
                {
                    var stop = i2;
                    while (stop < n && (char.IsLetterOrDigit(code[stop]) || code[stop] is '-' or '_')) stop++;
                    var word = code[i2..stop];
                    var k = stop;
                    while (k < n && code[k] is ' ' or '\t') k++;
                    Emit(k < n && code[k] == ':' ? "attribute" : null, word);
                    i2 = stop;
                    continue;
                }
                if (char.IsDigit(c))
                {
                    var stop = i2;
                    while (stop < n && (char.IsLetterOrDigit(code[stop]) || code[stop] is '.' or '%')) stop++;
                    Emit("number", code[i2..stop]);
                    i2 = stop;
                    continue;
                }
                sb.Append(c);
                i2++;
                continue;
            }

            if (lang.Kind is "yaml" or "ini" && lineStart)
            {
                var k = i2;
                while (k < n && code[k] is ' ' or '\t' or '-') k++;
                if (lang.Kind == "ini" && k < n && code[k] == '[')
                {
                    var end = code.IndexOf('\n', k);
                    var stop = end == -1 ? n : end;
                    sb.Append(code, i2, k - i2);
                    Emit("section", code[k..stop]);
                    i2 = stop;
                    lineStart = false;
                    continue;
                }
                var keyEnd = k;
                while (keyEnd < n && code[keyEnd] is not (':' or '=' or '\n' or '#')) keyEnd++;
                if (keyEnd < n && code[keyEnd] is ':' or '=' && keyEnd > k)
                {
                    sb.Append(code, i2, k - i2);
                    Emit("attr", code[k..keyEnd]);
                    i2 = keyEnd;
                    lineStart = false;
                    continue;
                }
            }
            lineStart = false;

            if (Array.IndexOf(lang.StringQuotes, c) >= 0 || (lang.Kind is "json" or "yaml" or "ini" && c is '"' or '\''))
            {
                var stop = ScanString(code, i2, c);
                var text = code[i2..stop];
                if (lang.Kind == "json")
                {
                    var k = stop;
                    while (k < n && code[k] is ' ' or '\t') k++;
                    Emit(k < n && code[k] == ':' ? "attr" : "string", text);
                }
                else
                {
                    Emit("string", text);
                }
                i2 = stop;
                continue;
            }

            if (char.IsDigit(c) || (c == '.' && i2 + 1 < n && char.IsDigit(code[i2 + 1]) && (i2 == 0 || !IsIdent(code[i2 - 1]))))
            {
                if (i2 > 0 && IsIdent(code[i2 - 1]))
                {
                    sb.Append(c);
                    i2++;
                    continue;
                }
                var stop = i2;
                if (c == '0' && stop + 1 < n && code[stop + 1] is 'x' or 'X' or 'b' or 'B' or 'o' or 'O') stop += 2;
                while (stop < n && (char.IsLetterOrDigit(code[stop]) || code[stop] is '.' or '_')) stop++;
                Emit("number", code[i2..stop]);
                i2 = stop;
                continue;
            }

            if (lang.VariablesWithDollar && c == '$' && i2 + 1 < n && (IsIdent(code[i2 + 1]) || code[i2 + 1] == '{'))
            {
                var stop = i2 + 1;
                if (code[stop] == '{')
                {
                    var end = code.IndexOf('}', stop);
                    stop = end == -1 ? n : end + 1;
                }
                else
                {
                    while (stop < n && IsIdent(code[stop])) stop++;
                }
                var word = code[i2..stop];
                Emit(lang.Literals.Contains(word) ? "literal" : "variable", word);
                i2 = stop;
                continue;
            }

            if (IsIdentStart(c))
            {
                var stop = i2;
                while (stop < n && IsIdent(code[stop])) stop++;
                var word = code[i2..stop];
                string? scope;
                if (expectTypeName && !lang.Keywords.Contains(word))
                {
                    scope = "title.class";
                    expectTypeName = false;
                }
                else if (lang.Keywords.Contains(word))
                {
                    scope = "keyword";
                    if (lang.TypeIntroducers.Contains(word)) expectTypeName = true;
                    else if (word is "def" or "fn" or "func" or "function" or "fun" or "sub") expectTypeName = false;
                }
                else if (lang.Literals.Contains(word))
                {
                    scope = "literal";
                }
                else if (lang.BuiltIns.Contains(word))
                {
                    scope = "built_in";
                }
                else
                {
                    var k = stop;
                    while (k < n && code[k] is ' ' or '\t') k++;
                    var previousWord = PreviousWord(code, i2);
                    if (previousWord is "def" or "fn" or "func" or "function" or "fun" or "sub") scope = "title.function";
                    else if (lang.CallsAreFunctions && k < n && code[k] == '(') scope = "title.function";
                    else if (lang.CapitalizedAreTypes && char.IsUpper(word[0])) scope = "type";
                    else scope = null;
                }
                Emit(scope, word);
                i2 = stop;
                continue;
            }

            sb.Append(c);
            i2++;
        }
        return sb.ToString();
    }

    private static bool IsIdentStart(char c) => char.IsLetter(c) || c == '_' || c == '$';

    private static bool IsIdent(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static string? PreviousWord(string code, int index)
    {
        var k = index - 1;
        while (k >= 0 && code[k] is ' ' or '\t') k--;
        var end = k + 1;
        while (k >= 0 && IsIdent(code[k])) k--;
        return end - (k + 1) > 0 ? code[(k + 1)..end] : null;
    }

    private static int ScanString(string code, int start, char quote)
    {
        var triple = quote is '"' or '\'' && start + 2 < code.Length && code[start + 1] == quote && code[start + 2] == quote;
        if (triple)
        {
            var close = new string(quote, 3);
            var end = code.IndexOf(close, start + 3, StringComparison.Ordinal);
            return end == -1 ? code.Length : end + 3;
        }
        var i = start + 1;
        while (i < code.Length)
        {
            var c = code[i];
            if (c == '\\')
            {
                i += 2;
                continue;
            }
            if (c == quote) return i + 1;
            if (c == '\n' && quote != '`') return i;
            i++;
        }
        return Math.Min(i, code.Length);
    }

    private static void HighlightMarkup(string code, Action<string?, string> emit)
    {
        var i = 0;
        var n = code.Length;
        while (i < n)
        {
            if (code.AsSpan(i).StartsWith("<!--"))
            {
                var end = code.IndexOf("-->", i + 4, StringComparison.Ordinal);
                var stop = end == -1 ? n : end + 3;
                emit("comment", code[i..stop]);
                i = stop;
                continue;
            }
            if (code[i] == '<')
            {
                var end = code.IndexOf('>', i);
                var stop = end == -1 ? n : end + 1;
                var tag = code[i..stop];
                var j = 0;
                emit("tag", tag[..Math.Min(tag.Length, tag.Length > 1 && tag[1] == '/' ? 2 : 1)]);
                j = tag.Length > 1 && tag[1] == '/' ? 2 : 1;
                var nameEnd = j;
                while (nameEnd < tag.Length && (char.IsLetterOrDigit(tag[nameEnd]) || tag[nameEnd] is '-' or ':' or '!' or '?')) nameEnd++;
                emit("name", tag[j..nameEnd]);
                j = nameEnd;
                while (j < tag.Length)
                {
                    var ch = tag[j];
                    if (ch is '"' or '\'')
                    {
                        var close = tag.IndexOf(ch, j + 1);
                        var s = close == -1 ? tag.Length : close + 1;
                        emit("string", tag[j..s]);
                        j = s;
                    }
                    else if (char.IsLetter(ch))
                    {
                        var s = j;
                        while (s < tag.Length && (char.IsLetterOrDigit(tag[s]) || tag[s] is '-' or ':' or '_')) s++;
                        emit("attr", tag[j..s]);
                        j = s;
                    }
                    else if (ch is '>' or '/')
                    {
                        emit("tag", tag[j..]);
                        j = tag.Length;
                    }
                    else
                    {
                        emit(null, ch.ToString());
                        j++;
                    }
                }
                i = stop;
                continue;
            }
            var next = code.IndexOf('<', i);
            var textStop = next == -1 ? n : next;
            emit(null, code[i..textStop]);
            i = textStop;
        }
    }

    private static void HighlightMarkdown(string code, Action<string?, string> emit, StringBuilder sb)
    {
        var inFence = false;
        foreach (var (line, index) in code.Split('\n').Select((l, i) => (l, i)))
        {
            if (index > 0) sb.Append('\n');
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                inFence = !inFence;
                emit("code", line);
            }
            else if (inFence)
            {
                emit("code", line);
            }
            else if (trimmed.StartsWith('#'))
            {
                emit("section", line);
            }
            else if (trimmed.StartsWith('>'))
            {
                emit("quote", line);
            }
            else if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal) || trimmed.StartsWith("+ ", StringComparison.Ordinal))
            {
                var lead = line.Length - trimmed.Length;
                emit(null, line[..lead]);
                emit("bullet", trimmed[..1]);
                emit(null, trimmed[1..]);
            }
            else
            {
                emit(null, line);
            }
        }
    }
}
