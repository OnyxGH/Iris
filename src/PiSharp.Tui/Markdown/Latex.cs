using System.Text.RegularExpressions;

namespace PiSharp.Tui.Markdown;

/// <summary>Renders basic LaTeX math as terminal-friendly Unicode text. Port of pi-tui latex.ts.</summary>
public static partial class Latex
{
    private static readonly string NamedOperatorStart = char.ConvertFromUtf32(0xF0004);
    private static readonly string NamedOperatorEnd = char.ConvertFromUtf32(0xF0005);
    private static readonly string LayoutMarkerStart = char.ConvertFromUtf32(0xF0000);
    private static readonly string LayoutMarkerEnd = char.ConvertFromUtf32(0xF0001);
    private static readonly string ProtectedSpace = char.ConvertFromUtf32(0xF0002);

    private static readonly Regex NamedOperatorLeftSpacing = new($@"(?<=[\p{{L}}\p{{N}})\]}}]|{Regex.Escape(LayoutMarkerEnd)}){Regex.Escape(NamedOperatorStart)}");
    private static readonly Regex NamedOperatorRightSpacing = new($@"{Regex.Escape(NamedOperatorEnd)}(?=[\p{{L}}\p{{N}}√]|{Regex.Escape(LayoutMarkerStart)})");
    private static readonly Regex LayoutMarkerPattern = new($@"{Regex.Escape(LayoutMarkerStart)}([0-9]+){Regex.Escape(LayoutMarkerEnd)}");
    private static readonly Regex TrailingLayoutMarkerPattern = new($@"{Regex.Escape(LayoutMarkerStart)}([0-9]+){Regex.Escape(LayoutMarkerEnd)}\z");

    [GeneratedRegex(@"\s*([=+-])\s*")]
    private static partial Regex ScriptOperatorSpacing();

    [GeneratedRegex("^[A-Za-z]+$")]
    private static partial Regex AsciiLetters();

    [GeneratedRegex(@"^[\p{L}\p{N}.]+$")]
    private static partial Regex SimpleValue();

    [GeneratedRegex(@"^[\p{N}.]+$")]
    private static partial Regex NumericValue();

    [GeneratedRegex("[ \t]+")]
    private static partial Regex HorizontalSpace();

    [GeneratedRegex(@"^\\(limits|nolimits)(?![A-Za-z])")]
    private static partial Regex LimitsModifier();

    [GeneratedRegex(@"\\\\(?:\[[^\]\n]*\])?")]
    private static partial Regex EnvironmentRowSeparator();

    [GeneratedRegex(@"^\s*\{[^}]*\}")]
    private static partial Regex LeadingColumnSpec();

    [GeneratedRegex(@",\s*\z")]
    private static partial Regex TrailingComma();

    [GeneratedRegex(@"^(?:if|when|for|otherwise)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ConditionWord();

    private abstract record LayoutNode;

    private sealed record FractionNode(string Numerator, string Denominator) : LayoutNode;

    private sealed record OperatorNode(string Operator, string? Lower, string? Upper) : LayoutNode;

    private sealed record MatrixNode(List<string> Lines, int Baseline) : LayoutNode;

    private sealed record Layout(List<string> Lines, int Width, int Baseline);

    private static List<string> CodePoints(string value)
    {
        var result = new List<string>();
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                result.Add(value.Substring(i, 2));
                i++;
            }
            else
            {
                result.Add(value[i].ToString());
            }
        }
        return result;
    }

    private static bool IsJsSpace(string s) => s.Length > 0 && TextUtils.IsJsWhitespace(s[0]);

    private static string? ReplaceCharacters(string value, Dictionary<string, string> replacements)
    {
        var result = "";
        foreach (var ch in CodePoints(value))
        {
            if (!replacements.TryGetValue(ch, out var replacement)) return null;
            result += replacement;
        }
        return result;
    }

    private static string FormatScript(string value, bool sub)
    {
        value = value.Trim();
        var unicode = ReplaceCharacters(ScriptOperatorSpacing().Replace(value, "$1"), sub ? Subscripts : Superscripts);
        if (unicode is not null) return unicode;
        var prefix = sub ? "_" : "^";
        if (CodePoints(value).Count == 1 || (sub && AsciiLetters().IsMatch(value))) return prefix + value;
        return $"{prefix}({value})";
    }

    private static string FormatFraction(string numerator, string denominator)
    {
        numerator = numerator.Trim();
        denominator = denominator.Trim();
        var simpleNumerator = SimpleValue().IsMatch(numerator);
        var simpleDenominator = NumericValue().IsMatch(denominator) || CodePoints(denominator).Count == 1;
        return $"{(simpleNumerator ? numerator : $"({numerator})")}/{(simpleDenominator ? denominator : $"({denominator})")}";
    }

    private static string FormatRoot(string value, string symbol = "√")
    {
        value = value.Trim();
        return SimpleValue().IsMatch(value) ? symbol + value : $"{symbol}({value})";
    }

    private static string NormalizeOutput(string value)
    {
        var replaced = NamedOperatorLeftSpacing.Replace(value, " ").Replace(NamedOperatorStart, "");
        replaced = NamedOperatorRightSpacing.Replace(replaced, " ").Replace(NamedOperatorEnd, "");
        var lines = replaced.Split('\n').Select(l => HorizontalSpace().Replace(l, " ").Trim()).ToList();
        var kept = lines.Where((line, index) => line.Length > 0 || (index > 0 && index < lines.Count - 1));
        return string.Join("\n", kept).Trim();
    }

    private static string PadLayoutLine(string line, int width, bool centered = false)
    {
        var padding = Math.Max(0, width - TextUtils.VisibleWidth(line));
        var left = centered ? padding / 2 : 0;
        return new string(' ', left) + line + new string(' ', padding - left);
    }

    private static Layout JoinLayouts(List<Layout> layouts)
    {
        if (layouts.Count == 0) return new Layout([""], 0, 0);
        var baseline = layouts.Max(l => l.Baseline);
        var below = layouts.Max(l => l.Lines.Count - l.Baseline - 1);
        var lines = new List<string>();
        for (var row = 0; row <= baseline + below; row++)
        {
            var line = "";
            foreach (var layout in layouts)
            {
                var sourceRow = row - baseline + layout.Baseline;
                line += sourceRow >= 0 && sourceRow < layout.Lines.Count
                    ? PadLayoutLine(layout.Lines[sourceRow], layout.Width)
                    : new string(' ', layout.Width);
            }
            lines.Add(line.TrimEnd());
        }
        return new Layout(lines, layouts.Sum(l => l.Width), baseline);
    }

    private static Layout RenderLayout(string source, List<LayoutNode> nodes)
    {
        var renderedLines = new List<string>();
        var firstBaseline = 0;
        foreach (var sourceLine in source.Split('\n'))
        {
            var layouts = new List<Layout>();
            var position = 0;
            LayoutNode? previousNode = null;
            foreach (Match match in LayoutMarkerPattern.Matches(sourceLine))
            {
                var index = match.Index;
                var nodeIndex = int.Parse(match.Groups[1].Value);
                if (nodeIndex >= nodes.Count) continue;
                var node = nodes[nodeIndex];
                if (index > position)
                {
                    var sliced = sourceLine[position..index];
                    var trimmed = (previousNode is not null ? sliced.TrimStart() : sliced).TrimEnd();
                    var preserveLeading = previousNode is MatrixNode && sliced.Length > 0 && TextUtils.IsJsWhitespace(sliced[0]);
                    var preserveTrailing = node is MatrixNode && sliced.Length > 0 && TextUtils.IsJsWhitespace(sliced[^1]);
                    var text = trimmed.Length > 0
                        ? $"{(preserveLeading ? " " : "")}{trimmed}{(preserveTrailing ? " " : "")}"
                        : preserveLeading || preserveTrailing ? " " : "";
                    layouts.Add(new Layout([text], TextUtils.VisibleWidth(text), 0));
                }

                switch (node)
                {
                    case FractionNode fraction:
                    {
                        var numerator = RenderLayout(fraction.Numerator, nodes);
                        var denominator = RenderLayout(fraction.Denominator, nodes);
                        var contentWidth = Math.Max(Math.Max(numerator.Width, denominator.Width), 1);
                        var width = contentWidth + 2;
                        var lines = numerator.Lines.Select(l => PadLayoutLine(l, width, true)).ToList();
                        lines.Add($" {new string('─', contentWidth)} ");
                        lines.AddRange(denominator.Lines.Select(l => PadLayoutLine(l, width, true)));
                        layouts.Add(new Layout(lines, width, numerator.Lines.Count));
                        break;
                    }
                    case OperatorNode op:
                    {
                        var contentWidth = Math.Max(TextUtils.VisibleWidth(op.Operator), Math.Max(op.Lower is null ? 0 : TextUtils.VisibleWidth(op.Lower), op.Upper is null ? 0 : TextUtils.VisibleWidth(op.Upper)));
                        var lines = new List<string>();
                        if (op.Upper is not null) lines.Add($"{PadLayoutLine(op.Upper, contentWidth, true)} ");
                        lines.Add($"{PadLayoutLine(op.Operator, contentWidth, true)} ");
                        if (op.Lower is not null) lines.Add($"{PadLayoutLine(op.Lower, contentWidth, true)} ");
                        layouts.Add(new Layout(lines, contentWidth + 1, op.Upper is null ? 0 : 1));
                        break;
                    }
                    case MatrixNode matrix:
                    {
                        var width = matrix.Lines.Count == 0 ? 0 : Math.Max(0, matrix.Lines.Max(TextUtils.VisibleWidth));
                        layouts.Add(new Layout(matrix.Lines.Select(l => PadLayoutLine(l, width)).ToList(), width, matrix.Baseline));
                        break;
                    }
                }
                position = index + match.Length;
                previousNode = node;
            }

            if (position < sourceLine.Length)
            {
                var sliced = sourceLine[position..];
                var trimmed = previousNode is not null ? sliced.TrimStart() : sliced;
                var text = previousNode is MatrixNode && TextUtils.IsJsWhitespace(sliced[0]) ? " " + trimmed : trimmed;
                layouts.Add(new Layout([text], TextUtils.VisibleWidth(text), 0));
            }

            var lineLayout = JoinLayouts(layouts);
            if (renderedLines.Count == 0) firstBaseline = lineLayout.Baseline;
            renderedLines.AddRange(lineLayout.Lines);
        }
        return new Layout(renderedLines, renderedLines.Count == 0 ? 0 : Math.Max(0, renderedLines.Max(TextUtils.VisibleWidth)), firstBaseline);
    }

    /// <summary>Render a LaTeX math expression; returns null for unsupported or malformed syntax.</summary>
    public static string? Render(string source, bool display = false)
    {
        var layoutNodes = new List<LayoutNode>();
        var rendered = new Parser(source, layoutNodes, display).Render();
        if (rendered is null) return null;
        if (layoutNodes.Count == 0) return rendered.Replace(ProtectedSpace, " ");
        var lines = RenderLayout(rendered, layoutNodes).Lines;
        var nonEmpty = lines.Where(l => l.Trim().Length > 0).ToList();
        // Math.min() of an empty list is Infinity in JavaScript; slicing by it yields empty lines.
        var indentation = nonEmpty.Count == 0 ? int.MaxValue : nonEmpty.Min(l => l.Length - l.TrimStart().Length);
        return string.Join("\n", lines.Select(l => (indentation >= l.Length ? "" : l[indentation..]).TrimEnd())).TrimEnd().Replace(ProtectedSpace, " ");
    }

    private sealed class Parser(string source, List<LayoutNode> layoutNodes, bool display)
    {
        private int _position;
        private bool _supported = true;
        private bool _stackFractions = true;

        private char? At(int index) => index >= 0 && index < source.Length ? source[index] : null;

        public string? Render()
        {
            var rendered = ParseSequence();
            if (!_supported || _position != source.Length) return null;
            return NormalizeOutput(rendered);
        }

        private string ParseSequence(char? endCharacter = null)
        {
            var result = "";
            while (_position < source.Length)
            {
                var character = source[_position];
                if (endCharacter is not null && character == endCharacter)
                {
                    _position++;
                    return result;
                }
                if (character == '}')
                {
                    _supported = false;
                    return result;
                }
                if (character == '{')
                {
                    _position++;
                    result += ParseSequence('}');
                    continue;
                }
                if (character == '\\')
                {
                    var command = ParseCommand();
                    if (command == NegativeSpace)
                    {
                        result = result.TrimEnd();
                        if (result.EndsWith(NamedOperatorEnd, StringComparison.Ordinal)) result = result[..^NamedOperatorEnd.Length];
                    }
                    else
                    {
                        result += command;
                    }
                    continue;
                }
                if (character is '^' or '_')
                {
                    _position++;
                    result = result.TrimEnd();
                    var script = FormatScript(ParseRequiredArgument(false), character == '_');
                    result = result.EndsWith(NamedOperatorEnd, StringComparison.Ordinal)
                        ? result[..^NamedOperatorEnd.Length] + script + NamedOperatorEnd
                        : result + script;
                    continue;
                }
                if (TextUtils.IsJsWhitespace(character))
                {
                    result += ParseWhitespace();
                    continue;
                }
                if (character is '=' or '<' or '>')
                {
                    result = $"{result.TrimEnd()} {character} ";
                    _position++;
                    continue;
                }
                if (character == '&')
                {
                    _position++;
                    continue;
                }
                if (character == '~')
                {
                    _position++;
                    result += " ";
                    continue;
                }
                if (character == '.')
                {
                    var marker = TrailingLayoutMarkerPattern.Match(result);
                    if (marker.Success && int.TryParse(marker.Groups[1].Value, out var idx) && idx < layoutNodes.Count && layoutNodes[idx] is MatrixNode matrix)
                    {
                        matrix.Lines[^1] += character;
                        _position++;
                        continue;
                    }
                }
                result += character;
                _position++;
            }
            if (endCharacter is not null) _supported = false;
            return result;
        }

        private string ParseWhitespace()
        {
            while (_position < source.Length && TextUtils.IsJsWhitespace(source[_position])) _position++;
            return " ";
        }

        private static bool IsAsciiLetter(char c) => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z');

        private string ParseCommand()
        {
            _position++;
            if (_position >= source.Length)
            {
                _supported = false;
                return "";
            }

            var first = source[_position];
            if (first is '\n' or '\r')
            {
                _position++;
                if (first == '\r' && At(_position) == '\n') _position++;
                return " ";
            }

            string command;
            if (IsAsciiLetter(first))
            {
                var start = _position;
                while (_position < source.Length && IsAsciiLetter(source[_position])) _position++;
                command = source[start.._position];
            }
            else
            {
                command = first.ToString();
                _position++;
            }

            if (command == "\\") return "\n";
            if (SpacingCommands.Contains(command)) return " ";
            if (NegativeSpacingCommands.Contains(command)) return NegativeSpace;
            if (IgnoredCommands.Contains(command)) return "";
            if (command is "{" or "}" or "$" or "%" or "#" or "_" or "&") return command;
            if (command == "|") return "‖";
            if (command == "not")
            {
                var value = ParseRequiredArgument(false).Trim();
                if (NegatedSymbols.TryGetValue(value, out var negated)) return $" {negated} ";
                var characters = CodePoints(value);
                if (characters.Count == 0)
                {
                    _supported = false;
                    return "";
                }
                return $" {characters[0]}{(char)0x0338}{string.Concat(characters.Skip(1))} ";
            }
            if (LimitOperators.Contains(command)) return ParseOperator(command, "bracket", true, true);

            if (Symbols.TryGetValue(command, out var symbol))
            {
                if (DisplayLimitSymbols.Contains(command)) return ParseOperator(symbol, "script", true);
                return command is "cdot" or "times" || RelationCommands.Contains(command) ? $" {symbol} " : symbol;
            }
            if (NamedOperators.Contains(command)) return NamedOperatorStart + command + NamedOperatorEnd;
            if (SizeCommands.Contains(command)) return "";
            if (command is "left" or "middle" or "right")
            {
                if (At(_position) == '.') _position++;
                return "";
            }
            if (command is "frac" or "dfrac" or "tfrac")
            {
                var shouldStack = display && _stackFractions && command != "tfrac";
                var numerator = ParseRequiredArgument(!shouldStack);
                var denominator = ParseRequiredArgument(!shouldStack);
                if (shouldStack)
                {
                    layoutNodes.Add(new FractionNode(NormalizeOutput(numerator), NormalizeOutput(denominator)));
                    return $"{LayoutMarkerStart}{layoutNodes.Count - 1}{LayoutMarkerEnd}";
                }
                return FormatFraction(numerator, denominator);
            }
            if (command == "sqrt")
            {
                var degree = ParseOptionalArgument()?.Trim();
                var value = ParseRequiredArgument();
                return degree switch
                {
                    null or "2" => FormatRoot(value),
                    "3" => FormatRoot(value, "∛"),
                    "4" => FormatRoot(value, "∜"),
                    _ => FormatScript(degree, false) + FormatRoot(value),
                };
            }
            if (command is "boxed" or "fbox") return $"[{ParseRequiredArgument().Trim()}]";
            if (command is "binom" or "dbinom" or "tbinom")
            {
                var n = ParseRequiredArgument();
                var k = ParseRequiredArgument();
                return $"({n} choose {k})";
            }
            if (Accents.TryGetValue(command, out var accent))
            {
                var value = ParseRequiredArgument();
                return CodePoints(value).Count == 1 ? value + accent : $"{command}({value})";
            }
            if (command == "mathbb")
            {
                var value = ParseRequiredArgument();
                return string.Concat(CodePoints(value).Select(c => Blackboard.GetValueOrDefault(c, c)));
            }
            if (command == "operatorname")
            {
                var starred = At(_position) == '*';
                if (starred) _position++;
                var op = NormalizeOutput(ParseRequiredArgument()).Trim();
                return ParseOperator(op, "bracket", starred, true);
            }
            if (command is "mod" or "bmod") return " mod ";
            if (command is "pmod" or "pod")
            {
                var value = ParseRequiredArgument().Trim();
                return command == "pmod" ? $" (mod {value})" : $" ({value})";
            }
            if (command is "overset" or "stackrel")
            {
                var upper = ParseRequiredArgument();
                var value = ParseRequiredArgument().Trim();
                return value + FormatScript(upper, false);
            }
            if (command == "underset")
            {
                var lower = ParseRequiredArgument();
                var value = ParseRequiredArgument().Trim();
                return value + FormatScript(lower, true);
            }
            if (PlainWrappers.Contains(command))
            {
                var value = ParseRequiredArgument();
                return command.StartsWith("text", StringComparison.Ordinal) || command == "mbox" ? value : value.Trim();
            }
            if (command == "begin") return ParseEnvironment();
            if (command == "end")
            {
                _supported = false;
                return "";
            }

            _supported = false;
            return "\\" + command;
        }

        private string ParseOperator(string op, string inlineLowerStyle, bool displayLimits, bool spaced = false)
        {
            var useDisplayLimits = displayLimits;
            var modifierPosition = _position;
            while (modifierPosition < source.Length && source[modifierPosition] is ' ' or '\t') modifierPosition++;
            var modifier = LimitsModifier().Match(source[modifierPosition..]);
            if (modifier.Success)
            {
                useDisplayLimits = modifier.Groups[1].Value == "limits";
                _position = modifierPosition + modifier.Length;
            }

            string? lower = null;
            string? upper = null;
            while (true)
            {
                var scriptPosition = _position;
                while (scriptPosition < source.Length && source[scriptPosition] is ' ' or '\t') scriptPosition++;
                var kind = At(scriptPosition);
                if (kind is not ('_' or '^')) break;
                _position = scriptPosition + 1;
                var value = NormalizeOutput(ParseRequiredArgument(false)).Replace(" ", "");
                if (kind == '_')
                {
                    if (lower is not null) _supported = false;
                    lower = value;
                }
                else
                {
                    if (upper is not null) _supported = false;
                    upper = value;
                }
            }

            if (display && useDisplayLimits && (lower is not null || upper is not null))
            {
                layoutNodes.Add(new OperatorNode(op, lower, upper));
                return $"{LayoutMarkerStart}{layoutNodes.Count - 1}{LayoutMarkerEnd}";
            }

            var rendered = op;
            if (lower is not null) rendered += inlineLowerStyle == "bracket" ? $"[{lower}]" : FormatScript(lower, true);
            if (upper is not null) rendered += FormatScript(upper, false);
            return spaced ? $" {rendered} " : rendered;
        }

        private string ParseRequiredArgument(bool stackFractions = true)
        {
            var previous = _stackFractions;
            _stackFractions = previous && stackFractions;
            var value = ParseRequiredArgumentValue();
            _stackFractions = previous;
            return value;
        }

        private string ParseRequiredArgumentValue()
        {
            while (_position < source.Length && TextUtils.IsJsWhitespace(source[_position])) _position++;
            if (_position >= source.Length)
            {
                _supported = false;
                return "";
            }
            if (source[_position] == '{')
            {
                _position++;
                return ParseSequence('}');
            }
            if (source[_position] == '\\') return ParseCommand();
            var value = source[_position].ToString();
            _position++;
            return value;
        }

        private string? ParseOptionalArgument()
        {
            while (_position < source.Length && source[_position] is ' ' or '\t') _position++;
            if (At(_position) != '[') return null;
            var end = source.IndexOf(']', _position + 1);
            if (end < 0)
            {
                _supported = false;
                return null;
            }
            var value = source[(_position + 1)..end];
            _position = end + 1;
            return RenderNested(value);
        }

        private string? ReadRawGroup()
        {
            while (_position < source.Length && source[_position] is ' ' or '\t') _position++;
            if (At(_position) != '{')
            {
                _supported = false;
                return null;
            }
            var start = ++_position;
            var depth = 1;
            while (_position < source.Length)
            {
                var character = source[_position];
                if (character == '\\')
                {
                    _position += 2;
                    continue;
                }
                if (character == '{') depth++;
                if (character == '}') depth--;
                if (depth == 0)
                {
                    var value = source[start.._position];
                    _position++;
                    return value;
                }
                _position++;
            }
            _supported = false;
            return null;
        }

        private static string[] SplitEnvironmentRows(string body) => EnvironmentRowSeparator().Split(body);

        private string ParseEnvironment()
        {
            var environment = ReadRawGroup();
            if (string.IsNullOrEmpty(environment)) return "";
            var endMarker = $"\\end{{{environment}}}";
            var end = _position <= source.Length ? source.IndexOf(endMarker, _position, StringComparison.Ordinal) : -1;
            if (end < 0)
            {
                _supported = false;
                return "";
            }
            var body = source[_position..end];
            _position = end + endMarker.Length;

            if (environment is "equation" or "equation*" or "displaymath") return RenderNested(body).Trim();

            if (environment is "aligned" or "align" or "align*" or "alignedat" or "alignat" or "alignat*" or "gather" or "gathered" or "multline" or "multline*" or "split")
            {
                var alignedAt = environment is "alignedat" or "alignat" or "alignat*";
                var alignedBody = alignedAt ? LeadingColumnSpec().Replace(body, "", 1) : body;
                return string.Join("\n", SplitEnvironmentRows(alignedBody)
                    .Select(row =>
                    {
                        var cells = row.Split('&');
                        var src = alignedAt
                            ? string.Join(" ", Enumerable.Range(0, (cells.Length + 1) / 2).Select(i => string.Concat(cells.Skip(i * 2).Take(2))))
                            : string.Concat(cells);
                        return RenderNested(src).Trim();
                    })
                    .Where(s => s.Length > 0));
            }

            if (environment is "cases" or "cases*")
            {
                var rows = SplitEnvironmentRows(body)
                    .Select(row => row.Split('&').Select(cell => RenderNested(cell, false).Trim()).ToList())
                    .Where(row => row.Any(c => c.Length > 0))
                    .ToList();
                return string.Join("\n", rows.Select((row, index) =>
                {
                    var value = TrailingComma().Replace(row.Count > 0 ? row[0] : "", "");
                    var condition = row.Count > 1 ? row[1] : "";
                    var delimiter = index == 0 ? "⎧" : index == rows.Count - 1 ? "⎩" : "⎨";
                    var conditionPrefix = ConditionWord().IsMatch(condition) ? " " : " if ";
                    return $"{delimiter} {value}{(condition.Length > 0 ? conditionPrefix + condition : "")}";
                }));
            }

            if (environment is "array" or "matrix" or "smallmatrix" or "pmatrix" or "bmatrix" or "Bmatrix" or "vmatrix" or "Vmatrix")
            {
                var matrixBody = environment == "array" ? LeadingColumnSpec().Replace(body, "", 1) : body;
                return RenderMatrix(environment, matrixBody);
            }

            _supported = false;
            return body;
        }

        private string RenderMatrix(string environment, string body)
        {
            var matrix = SplitEnvironmentRows(body)
                .Select(row => row.Split('&').Select(cell => RenderNested(cell, false).Trim()).ToList())
                .Where(row => row.Any(c => c.Length > 0))
                .ToList();
            var columnCount = matrix.Count == 0 ? 0 : Math.Max(0, matrix.Max(r => r.Count));
            var columnWidths = Enumerable.Range(0, columnCount)
                .Select(column => matrix.Count == 0 ? 0 : Math.Max(0, matrix.Max(r => TextUtils.VisibleWidth(column < r.Count ? r[column] : ""))))
                .ToList();
            var rows = matrix.Select(row => string.Join(" │ ", Enumerable.Range(0, columnCount).Select(column =>
            {
                var cell = column < row.Count ? row[column] : "";
                return cell + string.Concat(Enumerable.Repeat(ProtectedSpace, Math.Max(0, columnWidths[column] - TextUtils.VisibleWidth(cell))));
            }))).ToList();

            List<string> lines;
            if (environment is "array" or "matrix" or "smallmatrix")
            {
                lines = rows;
            }
            else
            {
                string[]? delimiter = environment switch
                {
                    "pmatrix" => ["⎛", "⎞", "⎜", "⎟", "⎝", "⎠"],
                    "bmatrix" => ["⎡", "⎤", "⎢", "⎥", "⎣", "⎦"],
                    "Bmatrix" => ["⎧", "⎫", "⎨", "⎬", "⎩", "⎭"],
                    "vmatrix" => ["│", "│", "│", "│", "│", "│"],
                    "Vmatrix" => ["║", "║", "║", "║", "║", "║"],
                    _ => null,
                };
                if (delimiter is null)
                {
                    _supported = false;
                    return string.Join("\n", rows);
                }
                lines = rows.Select((row, index) =>
                {
                    var left = index == 0 ? delimiter[0] : index == rows.Count - 1 ? delimiter[4] : delimiter[2];
                    var right = index == 0 ? delimiter[1] : index == rows.Count - 1 ? delimiter[5] : delimiter[3];
                    return $"{left} {row} {right}";
                }).ToList();
            }

            if (lines.Count <= 1) return lines.Count > 0 ? lines[0] : "";
            layoutNodes.Add(new MatrixNode(lines, 0));
            return $"{LayoutMarkerStart}{layoutNodes.Count - 1}{LayoutMarkerEnd}";
        }

        private string RenderNested(string nestedSource, bool stackFractions = true)
        {
            var rendered = new Parser(nestedSource, layoutNodes, display && stackFractions).Render();
            if (rendered is null)
            {
                _supported = false;
                return nestedSource;
            }
            return rendered;
        }
    }
}
