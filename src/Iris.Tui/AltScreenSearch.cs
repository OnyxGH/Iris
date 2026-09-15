using System.Text;
using System.Text.RegularExpressions;
using Iris.Tui.Components;

namespace Iris.Tui;

/// <summary>Transient messages composited by the alternate-screen renderer. Port of pi-tui AltScreenFlashContainer.</summary>
public sealed class AltScreenFlashContainer(UiDispatcher dispatcher, Action requestRender) : IComponent
{
    private const int DefaultDurationMs = 1000;

    private sealed record FlashEntry(int Id, string Message, IDisposable Timer);

    private readonly List<FlashEntry> _entries = [];
    private int _nextId;

    public void Flash(string message, int durationMs = DefaultDurationMs)
    {
        var id = _nextId++;
        var timer = dispatcher.SetTimeout(() =>
        {
            var index = _entries.FindIndex(e => e.Id == id);
            if (index == -1) return;
            _entries.RemoveAt(index);
            requestRender();
        }, Math.Max(0, durationMs));
        _entries.Add(new FlashEntry(id, message, timer));
        requestRender();
    }

    public void Dispose()
    {
        foreach (var entry in _entries) entry.Timer.Dispose();
        _entries.Clear();
    }

    public void Invalidate()
    {
    }

    public List<string> Render(int width) =>
        _entries.Select(e => $"\e[7m{TextUtils.TruncateToWidth($" {e.Message} ", width, "")}\e[27m").ToList();
}

public sealed record AltScreenSearchSegment(int Row, int StartCol, int EndCol)
{
    public int EndCol { get; set; } = EndCol;
}

public sealed record AltScreenSearchMatch(List<AltScreenSearchSegment> Segments)
{
    public string Key => Segments.Count > 0 ? $"{Segments[0].Row}:{Segments[0].StartCol}:{Segments[^1].Row}:{Segments[^1].EndCol}" : "";
}

/// <summary>Cache the searchable corpus and matches while rendered transcript lines remain unchanged. Port of pi-tui AltScreenSearchIndex.</summary>
public sealed partial class AltScreenSearchIndex
{
    private sealed record SourceSpan(int TextStart, int TextEnd, int Row, int StartCol, int EndCol, bool LinearColumns);

    private List<string>? _sourceLines;
    private (string Text, List<SourceSpan> Spans)? _corpus;
    private string? _normalizedQuery;
    private List<AltScreenSearchMatch> _matches = [];

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    public (List<AltScreenSearchMatch> Matches, bool Changed) Search(IReadOnlyList<string> lines, string query)
    {
        var sourceChanged = _sourceLines is null || _sourceLines.Count != lines.Count;
        if (!sourceChanged)
        {
            for (var index = 0; index < lines.Count; index++)
            {
                if (!ReferenceEquals(_sourceLines![index], lines[index]) && _sourceLines[index] != lines[index])
                {
                    sourceChanged = true;
                    break;
                }
            }
        }
        if (sourceChanged || _corpus is null)
        {
            _sourceLines = [.. lines];
            _corpus = BuildCorpus(lines);
        }
        var normalized = Whitespace().Replace(query, " ").Trim();
        var changed = sourceChanged || normalized != _normalizedQuery;
        if (changed)
        {
            _normalizedQuery = normalized;
            _matches = FindMatches(_corpus.Value, normalized);
        }
        return (_matches, changed);
    }

    private static bool IsPrintableAscii(string line)
    {
        foreach (var c in line)
        {
            if (c < 0x20 || c > 0x7e) return false;
        }
        return true;
    }

    private static (string Text, List<SourceSpan> Spans) BuildCorpus(IReadOnlyList<string> lines)
    {
        var text = new StringBuilder();
        var spans = new List<SourceSpan>();
        var pendingSeparator = false;

        void AppendSeparator()
        {
            if (!pendingSeparator) return;
            text.Append(' ');
            pendingSeparator = false;
        }

        for (var row = 0; row < lines.Count; row++)
        {
            var line = TextUtils.StripTerminalSequences(lines[row]);
            var column = 0;
            if (IsPrintableAscii(line))
            {
                var index = 0;
                while (index < line.Length)
                {
                    if (line[index] == ' ')
                    {
                        if (text.Length > 0) pendingSeparator = true;
                        column++;
                        index++;
                        continue;
                    }
                    var end = index + 1;
                    while (end < line.Length && line[end] != ' ') end++;
                    AppendSeparator();
                    var chunk = line[index..end];
                    spans.Add(new SourceSpan(text.Length, text.Length + chunk.Length, row, column, column + chunk.Length, true));
                    text.Append(chunk);
                    column += chunk.Length;
                    index = end;
                }
            }
            else
            {
                foreach (var grapheme in TextUtils.Graphemes(line))
                {
                    var width = TextUtils.VisibleWidth(grapheme);
                    if (grapheme.All(char.IsWhiteSpace))
                    {
                        if (text.Length > 0) pendingSeparator = true;
                        column += width;
                        continue;
                    }
                    AppendSeparator();
                    spans.Add(new SourceSpan(text.Length, text.Length + grapheme.Length, row, column, column + width, false));
                    text.Append(grapheme);
                    column += width;
                }
            }
            if (text.Length > 0) pendingSeparator = true;
        }
        return (text.ToString(), spans);
    }

    private static List<AltScreenSearchMatch> FindMatches((string Text, List<SourceSpan> Spans) corpus, string normalizedQuery)
    {
        var matches = new List<AltScreenSearchMatch>();
        if (normalizedQuery.Length == 0) return matches;
        var spanIndex = 0;
        var spans = corpus.Spans;
        var position = 0;
        while (position <= corpus.Text.Length)
        {
            var start = corpus.Text.IndexOf(normalizedQuery, position, StringComparison.OrdinalIgnoreCase);
            if (start < 0) break;
            var end = start + normalizedQuery.Length;
            position = Math.Max(end, start + 1);
            while (spanIndex < spans.Count && spans[spanIndex].TextEnd <= start) spanIndex++;
            var segments = new List<AltScreenSearchSegment>();
            for (var index = spanIndex; index < spans.Count; index++)
            {
                var span = spans[index];
                if (span.TextStart >= end) break;
                if (span.TextEnd <= start) continue;
                var startCol = span.LinearColumns ? span.StartCol + Math.Max(start, span.TextStart) - span.TextStart : span.StartCol;
                var endCol = span.LinearColumns ? span.StartCol + Math.Min(end, span.TextEnd) - span.TextStart : span.EndCol;
                if (segments.Count > 0 && segments[^1].Row == span.Row && startCol <= segments[^1].EndCol) segments[^1].EndCol = Math.Max(segments[^1].EndCol, endCol);
                else segments.Add(new AltScreenSearchSegment(span.Row, startCol, endCol));
            }
            while (spanIndex < spans.Count && spans[spanIndex].TextEnd <= end) spanIndex++;
            if (segments.Count > 0) matches.Add(new AltScreenSearchMatch(segments));
        }
        return matches;
    }
}

/// <summary>Transcript search box. Port of pi-tui AltScreenSearchComponent.</summary>
public sealed class AltScreenSearchComponent(Action<string> onQueryChange, Func<string, bool, string>? navigationButtonStyle = null) : IInputComponent, IFocusable
{
    private readonly Input _input = new(" ", "Find in transcript", text => $"\e[2m{text}\e[22m");
    private readonly Func<string, bool, string> _navigationButtonStyle = navigationButtonStyle ?? ((text, _) => text);
    private int _resultCount;
    private int _resultIndex = -1;
    private int _previousButtonStart = -1;
    private int _previousButtonEnd = -1;
    private int _nextButtonStart = -1;
    private int _nextButtonEnd = -1;
    private int? _hoveredNavigationDirection;
    private bool _focused;

    public bool Focused
    {
        get => _focused;
        set
        {
            _focused = value;
            _input.Focused = value;
        }
    }

    public void SetResult(int index, int count)
    {
        _resultIndex = index;
        _resultCount = count;
    }

    public int? GetNavigationDirectionAt(int row, int column)
    {
        if (row != 2) return null;
        if (column >= _previousButtonStart && column < _previousButtonEnd) return -1;
        if (column >= _nextButtonStart && column < _nextButtonEnd) return 1;
        return null;
    }

    public bool SetHoveredNavigationDirection(int? direction)
    {
        if (direction == _hoveredNavigationDirection) return false;
        _hoveredNavigationDirection = direction;
        return true;
    }

    public void HandleInput(string data)
    {
        var previous = _input.GetValue();
        _input.HandleInput(data);
        var query = _input.GetValue();
        if (query != previous) onQueryChange(query);
    }

    public void Invalidate() => _input.Invalidate();

    private static string FormatKey(string? key) => key is null
        ? "Unbound"
        : string.Join("+", key.Split('+').Select(part => part.Length == 0 ? part : char.ToUpperInvariant(part[0]) + part[1..]));

    public List<string> Render(int width)
    {
        var safeWidth = Math.Max(1, width);
        var innerWidth = Math.Max(0, safeWidth - 2);
        var keybindings = KeybindingsManager.Global;
        var previousKey = FormatKey(keybindings.GetKeys("tui.altScreen.searchPrevious").FirstOrDefault());
        var nextKey = FormatKey(keybindings.GetKeys("tui.altScreen.searchNext").FirstOrDefault());
        var query = _input.GetValue();
        var result = query.Length == 0 ? "" : _resultCount == 0 ? "No matches" : $"{_resultIndex + 1}/{_resultCount}";
        var visibleResult = TextUtils.TruncateToWidth(result, Math.Max(0, innerWidth - 3), "");
        var resultText = visibleResult.Length > 0 ? $"\e[2m {visibleResult} \e[22m" : "";
        var inputWidth = Math.Max(0, innerWidth - TextUtils.VisibleWidth(resultText));
        var inputLine = TextUtils.TruncateToWidth(_input.Render(Math.Max(1, inputWidth)).FirstOrDefault() ?? "", inputWidth, "");
        var inputPadding = new string(' ', Math.Max(0, inputWidth - TextUtils.VisibleWidth(inputLine)));
        var content = inputLine + inputPadding + resultText;

        var previousButton = $"↑ {previousKey}";
        var nextButton = $"↓ {nextKey}";
        var separator = " · ";
        const int outerGapWidth = 1;
        var availableControlsWidth = Math.Max(0, innerWidth - outerGapWidth * 2 - 1);
        var controlsWidth = TextUtils.VisibleWidth(previousButton) + TextUtils.VisibleWidth(separator) + TextUtils.VisibleWidth(nextButton);
        if (controlsWidth > availableControlsWidth)
        {
            previousButton = "↑";
            nextButton = "↓";
            separator = " ";
            controlsWidth = 3;
        }
        var showButtons = controlsWidth <= availableControlsWidth;
        var renderedButtons = showButtons
            ? _navigationButtonStyle(previousButton, _hoveredNavigationDirection == -1) + separator + _navigationButtonStyle(nextButton, _hoveredNavigationDirection == 1)
            : "";
        var outerGapsWidth = showButtons ? outerGapWidth * 2 : 0;
        var rightRuleWidth = renderedButtons.Length > 0 && innerWidth > controlsWidth + outerGapsWidth ? 1 : 0;
        var leftRuleWidth = Math.Max(0, innerWidth - (showButtons ? controlsWidth : 0) - outerGapsWidth - rightRuleWidth);
        var previousStart = 1 + leftRuleWidth + outerGapWidth;
        _previousButtonStart = showButtons ? previousStart : -1;
        _previousButtonEnd = showButtons ? previousStart + TextUtils.VisibleWidth(previousButton) : -1;
        _nextButtonStart = showButtons ? _previousButtonEnd + TextUtils.VisibleWidth(separator) : -1;
        _nextButtonEnd = showButtons ? _nextButtonStart + TextUtils.VisibleWidth(nextButton) : -1;

        if (safeWidth == 1) return ["┌", "│", "└"];
        var gap = renderedButtons.Length > 0 ? " " : "";
        return
        [
            $"┌{new string('─', innerWidth)}┐",
            $"│{content}│",
            $"└{new string('─', leftRuleWidth)}{gap}{renderedButtons}{gap}{new string('─', rightRuleWidth)}┘",
        ];
    }
}
