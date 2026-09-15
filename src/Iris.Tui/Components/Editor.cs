using System.Text.RegularExpressions;

namespace Iris.Tui.Components;

/// <summary>Interface for editor components (custom editors from extensions).</summary>
public interface IEditorComponent : IInputComponent
{
    string GetText();

    void SetText(string text);

    Action<string>? OnSubmit { get; set; }

    Action<string>? OnChange { get; set; }

    void AddToHistory(string text)
    {
    }

    void InsertTextAtCursor(string text)
    {
    }

    string GetExpandedText() => GetText();

    void SetAutocompleteProvider(IAutocompleteProvider provider)
    {
    }

    Func<string, string>? BorderColor
    {
        get => null;
        set { }
    }

    void SetPaddingX(int padding)
    {
    }

    void SetAutocompleteMaxVisible(int maxVisible)
    {
    }
}

public readonly record struct TextChunk(string Text, int StartIndex, int EndIndex);

public sealed class EditorTheme
{
    public required Func<string, string> BorderColor { get; init; }
    public required SelectListTheme SelectList { get; init; }
}

public sealed class EditorOptions
{
    public int? PaddingX { get; init; }
    public int? AutocompleteMaxVisible { get; init; }
}

/// <summary>Multi-line editor with word wrap, autocomplete, history, kill ring and undo.</summary>
public partial class Editor : IEditorComponent, IFocusable, IMouseComponent
{
    [GeneratedRegex(@"\[paste #(\d+)( (\+\d+ lines|\d+ chars))?\]")]
    private static partial Regex PasteMarkerRegex();

    [GeneratedRegex(@"^\[paste #(\d+)( (\+\d+ lines|\d+ chars))?\]$")]
    private static partial Regex PasteMarkerSingle();

    [GeneratedRegex(@"\x1b\[(\d+);5u")]
    private static partial Regex CsiUCtrl();

    [GeneratedRegex("[a-zA-Z0-9.\\-_]")]
    private static partial Regex AutocompleteWordChar();

    [GeneratedRegex(@"^[/~.]")]
    private static partial Regex PathStart();

    [GeneratedRegex(@"\w")]
    private static partial Regex WordChar();

    private static bool IsPasteMarker(string segment) => segment.Length >= 10 && PasteMarkerSingle().IsMatch(segment);

    private sealed class EditorState
    {
        public List<string> Lines { get; set; } = [""];
        public int CursorLine { get; set; }
        public int CursorCol { get; set; }

        public EditorState Clone() => new() { Lines = [.. Lines], CursorLine = CursorLine, CursorCol = CursorCol };
    }

    private sealed record EditorSnapshot(EditorState State, Dictionary<int, string> Pastes, int PasteCounter);

    private sealed record LayoutLine(string Text, bool HasCursor, int? CursorPos = null);

    private readonly record struct VisualLine(int LogicalLine, int StartCol, int Length);

    private static readonly SelectListLayoutOptions SlashCommandSelectListLayout = new() { MinPrimaryColumnWidth = 12, MaxPrimaryColumnWidth = 32 };
    private const int AttachmentAutocompleteDebounceMs = 20;
    private static readonly string[] DefaultAutocompleteTriggerCharacters = ["@", "#"];

    private EditorState _state = new();
    public bool Focused { get; set; }

    protected TuiBase Tui { get; }
    private readonly EditorTheme _theme;
    private int _paddingX;
    private int _lastWidth = 80;
    private int _scrollOffset;
    private int _renderedVisibleLineCount;
    private int _renderedAutocompleteHeight;

    public Func<string, string>? BorderColor { get; set; }

    private IAutocompleteProvider? _autocompleteProvider;
    private List<string> _autocompleteTriggerCharacters = [.. DefaultAutocompleteTriggerCharacters];
    private Regex _autocompleteTriggerPattern;
    private Regex _autocompleteDebouncePattern;
    private SelectList? _autocompleteList;
    private string? _autocompleteState; // "regular" | "force" | null
    private string _autocompletePrefix = "";
    private int _autocompleteMaxVisible;
    private CancellationTokenSource? _autocompleteAbort;
    private IDisposable? _autocompleteDebounceTimer;
    private Task _autocompleteRequestTask = Task.CompletedTask;
    private int _autocompleteStartToken;
    private int _autocompleteRequestId;

    private Dictionary<int, string> _pastes = [];
    private int _pasteCounter;
    private string _pasteBuffer = "";
    private bool _isInPaste;

    private readonly List<string> _history = [];
    private int _historyIndex = -1;
    private EditorState? _historyDraft;

    private readonly KillRing _killRing = new();
    private string? _lastAction; // "kill" | "yank" | "type-word"
    private string? _jumpMode; // "forward" | "backward"
    private int? _preferredVisualCol;
    private int? _snappedFromCursorCol;
    private readonly UndoStack<EditorSnapshot> _undoStack = new();

    public Action<string>? OnSubmit { get; set; }
    public Action<string>? OnChange { get; set; }
    public bool DisableSubmit { get; set; }

    public Editor(TuiBase tui, EditorTheme theme, EditorOptions? options = null)
    {
        Tui = tui;
        _theme = theme;
        BorderColor = theme.BorderColor;
        _paddingX = Math.Max(0, options?.PaddingX ?? 0);
        _autocompleteMaxVisible = Math.Max(3, Math.Min(20, options?.AutocompleteMaxVisible ?? 5));
        _autocompleteTriggerPattern = BuildTriggerPattern(_autocompleteTriggerCharacters);
        _autocompleteDebouncePattern = BuildDebouncePattern(_autocompleteTriggerCharacters);
    }

    private static string EscapeCharacterClass(string value) => Regex.Replace(value, @"[\\^$.*+?()[\]{}|-]", "\\$0");

    private static Regex BuildTriggerPattern(List<string> chars) =>
        new($"(?:^|[\\s])[{string.Concat(chars.Select(EscapeCharacterClass))}][^\\s]*$");

    private static Regex BuildDebouncePattern(List<string> chars)
    {
        var withoutAt = string.Concat(chars.Where(c => c != "@").Select(EscapeCharacterClass));
        // An empty JS character class never matches; mirror that with a negative lookahead.
        var classPart = withoutAt.Length > 0 ? $"[{withoutAt}][^\\s]*" : "(?!)";
        return new Regex($"(?:^|[ \\t])(?:@(?:\"[^\"]*|[^\\s]*)|{classPart})$");
    }

    private static string CreateScrollBorder(string direction, int hiddenLineCount, int width)
    {
        var available = Math.Max(0, width);
        var label = $" {direction} {hiddenLineCount} more ";
        var labelWidth = TextUtils.VisibleWidth(label);
        if (labelWidth + 2 <= available)
        {
            var left = (available - labelWidth) / 2;
            return new string('─', left) + label + new string('─', available - left - labelWidth);
        }
        var indicator = $"─── {direction} {hiddenLineCount} more ";
        var remaining = available - TextUtils.VisibleWidth(indicator);
        if (remaining >= 0) return indicator + new string('─', remaining);
        var ellipsis = "..."[..Math.Min(3, available)];
        return TextUtils.SliceByColumn(indicator, 0, available - ellipsis.Length, true) + ellipsis;
    }

    // ----- Segmentation -----

    private static IEnumerable<WordSegment> GraphemeSegments(string text)
    {
        var index = 0;
        foreach (var g in TextUtils.Graphemes(text))
        {
            yield return new WordSegment(g, index, false);
            index += g.Length;
        }
    }

    private IEnumerable<WordSegment> Segment(string text, bool word)
    {
        var baseSegments = word ? WordSegmenter.Segment(text) : GraphemeSegments(text);
        if (_pastes.Count == 0 || !text.Contains("[paste #")) return baseSegments;

        var markers = new List<(int Start, int End)>();
        foreach (Match m in PasteMarkerRegex().Matches(text))
        {
            if (_pastes.ContainsKey(int.Parse(m.Groups[1].Value))) markers.Add((m.Index, m.Index + m.Length));
        }
        if (markers.Count == 0) return baseSegments;

        var result = new List<WordSegment>();
        var markerIdx = 0;
        foreach (var seg in baseSegments)
        {
            while (markerIdx < markers.Count && markers[markerIdx].End <= seg.Index) markerIdx++;
            if (markerIdx < markers.Count && seg.Index >= markers[markerIdx].Start && seg.Index < markers[markerIdx].End)
            {
                var marker = markers[markerIdx];
                if (seg.Index == marker.Start) result.Add(new WordSegment(text[marker.Start..marker.End], marker.Start, false));
            }
            else
            {
                result.Add(seg);
            }
        }
        return result;
    }

    /// <summary>Split a line into word-wrapped chunks; falls back to grapheme breaks for overlong words.</summary>
    public static List<TextChunk> WordWrapLine(string line, int maxWidth, IReadOnlyList<WordSegment>? preSegmented = null)
    {
        if (string.IsNullOrEmpty(line) || maxWidth <= 0) return [new TextChunk("", 0, 0)];
        if (TextUtils.VisibleWidth(line) <= maxWidth) return [new TextChunk(line, 0, line.Length)];

        var chunks = new List<TextChunk>();
        var segments = preSegmented ?? GraphemeSegments(line).ToList();
        var currentWidth = 0;
        var chunkStart = 0;
        var wrapOppIndex = -1;
        var wrapOppWidth = 0;

        for (var i = 0; i < segments.Count; i++)
        {
            var seg = segments[i];
            var grapheme = seg.Segment;
            var gWidth = TextUtils.VisibleWidth(grapheme);
            var charIndex = seg.Index;
            var isWs = !IsPasteMarker(grapheme) && TextUtils.IsWhitespaceChar(grapheme);

            if (currentWidth + gWidth > maxWidth)
            {
                if (wrapOppIndex >= 0 && currentWidth - wrapOppWidth + gWidth <= maxWidth)
                {
                    chunks.Add(new TextChunk(line[chunkStart..wrapOppIndex], chunkStart, wrapOppIndex));
                    chunkStart = wrapOppIndex;
                    currentWidth -= wrapOppWidth;
                }
                else if (chunkStart < charIndex)
                {
                    chunks.Add(new TextChunk(line[chunkStart..charIndex], chunkStart, charIndex));
                    chunkStart = charIndex;
                    currentWidth = 0;
                }
                wrapOppIndex = -1;
            }

            if (gWidth > maxWidth)
            {
                var sub = WordWrapLine(grapheme, maxWidth);
                for (var j = 0; j < sub.Count - 1; j++)
                {
                    chunks.Add(new TextChunk(sub[j].Text, charIndex + sub[j].StartIndex, charIndex + sub[j].EndIndex));
                }
                var last = sub[^1];
                chunkStart = charIndex + last.StartIndex;
                currentWidth = TextUtils.VisibleWidth(last.Text);
                wrapOppIndex = -1;
                continue;
            }

            currentWidth += gWidth;

            if (i + 1 < segments.Count)
            {
                var next = segments[i + 1];
                if (isWs && (IsPasteMarker(next.Segment) || !TextUtils.IsWhitespaceChar(next.Segment)))
                {
                    wrapOppIndex = next.Index;
                    wrapOppWidth = currentWidth;
                }
                else if (!isWs && !TextUtils.IsWhitespaceChar(next.Segment))
                {
                    var isCjk = !IsPasteMarker(grapheme) && TextUtils.IsCjkBreakChar(grapheme);
                    var nextIsCjk = !IsPasteMarker(next.Segment) && TextUtils.IsCjkBreakChar(next.Segment);
                    if (isCjk || nextIsCjk)
                    {
                        wrapOppIndex = next.Index;
                        wrapOppWidth = currentWidth;
                    }
                }
            }
        }

        chunks.Add(new TextChunk(line[chunkStart..], chunkStart, line.Length));
        return chunks;
    }

    // ----- Configuration -----

    public int GetPaddingX() => _paddingX;

    public void SetPaddingX(int padding)
    {
        var next = Math.Max(0, padding);
        if (_paddingX == next) return;
        _paddingX = next;
        Tui.RequestRender();
    }

    public int GetAutocompleteMaxVisible() => _autocompleteMaxVisible;

    public void SetAutocompleteMaxVisible(int maxVisible)
    {
        var next = Math.Max(3, Math.Min(20, maxVisible));
        if (_autocompleteMaxVisible == next) return;
        _autocompleteMaxVisible = next;
        Tui.RequestRender();
    }

    public void SetAutocompleteProvider(IAutocompleteProvider provider)
    {
        CancelAutocomplete();
        _autocompleteProvider = provider;
        SetAutocompleteTriggerCharacters(provider.TriggerCharacters ?? []);
    }

    public void AddToHistory(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return;
        if (_history.Count > 0 && _history[0] == trimmed) return;
        _history.Insert(0, trimmed);
        if (_history.Count > 100) _history.RemoveAt(_history.Count - 1);
    }

    private string CurrentLine => _state.CursorLine < _state.Lines.Count ? _state.Lines[_state.CursorLine] : "";

    private bool IsEditorEmpty() => _state.Lines.Count == 1 && _state.Lines[0] == "";

    private bool IsOnFirstVisualLine() => FindCurrentVisualLine(BuildVisualLineMap(_lastWidth)) == 0;

    private bool IsOnLastVisualLine()
    {
        var lines = BuildVisualLineMap(_lastWidth);
        return FindCurrentVisualLine(lines) == lines.Count - 1;
    }

    private void NavigateHistory(int direction)
    {
        _lastAction = null;
        if (_history.Count == 0) return;
        var newIndex = _historyIndex - direction;
        if (newIndex < -1 || newIndex >= _history.Count) return;

        if (_historyIndex == -1 && newIndex >= 0)
        {
            PushUndoSnapshot();
            _historyDraft = _state.Clone();
        }

        _historyIndex = newIndex;
        if (_historyIndex == -1)
        {
            var draft = _historyDraft;
            _historyDraft = null;
            if (draft is not null)
            {
                _state = draft;
                _preferredVisualCol = null;
                _snappedFromCursorCol = null;
                _scrollOffset = 0;
                OnChange?.Invoke(GetText());
            }
            else
            {
                SetTextInternal("");
            }
        }
        else
        {
            SetTextInternal(_history[_historyIndex], direction == -1 ? "start" : "end");
        }
    }

    private void ExitHistoryBrowsing()
    {
        _historyIndex = -1;
        _historyDraft = null;
    }

    private void SetTextInternal(string text, string cursorPlacement = "end")
    {
        var lines = text.Split('\n').ToList();
        _state.Lines = lines.Count == 0 ? [""] : lines;
        _state.CursorLine = cursorPlacement == "start" ? 0 : _state.Lines.Count - 1;
        SetCursorCol(cursorPlacement == "start" ? 0 : CurrentLine.Length);
        _scrollOffset = 0;
        OnChange?.Invoke(GetText());
    }

    public virtual void Invalidate()
    {
    }

    protected virtual string RenderTopBorder(int width, int hiddenLineCount)
    {
        var border = hiddenLineCount > 0 ? CreateScrollBorder("↑", hiddenLineCount, width) : new string('─', Math.Max(0, width));
        return (BorderColor ?? (s => s))(border);
    }

    protected virtual string RenderBottomBorder(int width, int hiddenLineCount)
    {
        var border = hiddenLineCount > 0 ? CreateScrollBorder("↓", hiddenLineCount, width) : new string('─', Math.Max(0, width));
        return (BorderColor ?? (s => s))(border);
    }

    public virtual List<string> Render(int width)
    {
        var maxPadding = Math.Max(0, (width - 1) / 2);
        var paddingX = Math.Min(_paddingX, maxPadding);
        var contentWidth = Math.Max(1, width - paddingX * 2);
        var layoutWidth = Math.Max(1, contentWidth - (paddingX > 0 ? 0 : 1));
        _lastWidth = layoutWidth;

        var layoutLines = LayoutText(layoutWidth);
        var maxVisibleLines = Math.Max(5, (int)Math.Floor(Tui.Terminal.Rows * 0.3));
        var cursorLineIndex = layoutLines.FindIndex(l => l.HasCursor);
        if (cursorLineIndex == -1) cursorLineIndex = 0;

        if (cursorLineIndex < _scrollOffset) _scrollOffset = cursorLineIndex;
        else if (cursorLineIndex >= _scrollOffset + maxVisibleLines) _scrollOffset = cursorLineIndex - maxVisibleLines + 1;
        var maxScroll = Math.Max(0, layoutLines.Count - maxVisibleLines);
        _scrollOffset = Math.Max(0, Math.Min(_scrollOffset, maxScroll));

        var visibleLines = layoutLines.Skip(_scrollOffset).Take(maxVisibleLines).ToList();
        _renderedVisibleLineCount = visibleLines.Count;
        _renderedAutocompleteHeight = 0;
        var result = new List<string>();
        var leftPadding = new string(' ', paddingX);
        var rightPadding = leftPadding;

        result.Add(RenderTopBorder(width, _scrollOffset));

        foreach (var layoutLine in visibleLines)
        {
            var displayText = layoutLine.Text;
            var lineVisibleWidth = TextUtils.VisibleWidth(layoutLine.Text);
            var cursorInPadding = false;

            if (layoutLine.HasCursor && layoutLine.CursorPos is { } cursorPos)
            {
                cursorPos = Math.Min(cursorPos, displayText.Length);
                var before = displayText[..cursorPos];
                var after = displayText[cursorPos..];
                var marker = Focused ? TuiBase.CursorMarker : "";
                if (after.Length > 0)
                {
                    var firstGrapheme = Segment(after, false).FirstOrDefault().Segment ?? "";
                    var restAfter = after[firstGrapheme.Length..];
                    displayText = before + marker + $"\e[7m{firstGrapheme}\e[0m" + restAfter;
                }
                else
                {
                    displayText = before + marker + "\e[7m \e[0m";
                    lineVisibleWidth++;
                    if (lineVisibleWidth > contentWidth && paddingX > 0) cursorInPadding = true;
                }
            }

            var padding = new string(' ', Math.Max(0, contentWidth - lineVisibleWidth));
            var lineRightPadding = cursorInPadding ? rightPadding[1..] : rightPadding;
            result.Add($"{leftPadding}{displayText}{padding}{lineRightPadding}");
        }

        var linesBelow = layoutLines.Count - (_scrollOffset + visibleLines.Count);
        result.Add(RenderBottomBorder(width, linesBelow));

        if (_autocompleteState is not null && _autocompleteList is not null)
        {
            var autocompleteLines = _autocompleteList.Render(contentWidth);
            _renderedAutocompleteHeight = autocompleteLines.Count;
            foreach (var line in autocompleteLines)
            {
                var linePadding = new string(' ', Math.Max(0, contentWidth - TextUtils.VisibleWidth(line)));
                result.Add($"{leftPadding}{line}{linePadding}{rightPadding}");
            }
        }

        return result;
    }

    public virtual TuiMouseEventResult? HandleMouse(TuiMouseEvent mouseEvent)
    {
        var maxPadding = Math.Max(0, (mouseEvent.Width - 1) / 2);
        var paddingX = Math.Min(_paddingX, maxPadding);
        var autocompleteStartRow = _renderedVisibleLineCount + 2;
        if (_autocompleteState is not null && _autocompleteList is not null
            && mouseEvent.Y >= autocompleteStartRow && mouseEvent.Y < autocompleteStartRow + _renderedAutocompleteHeight)
        {
            var result = _autocompleteList.HandleMouse(mouseEvent with
            {
                X = mouseEvent.X - paddingX,
                Y = mouseEvent.Y - autocompleteStartRow,
                Width = Math.Max(1, mouseEvent.Width - paddingX * 2),
                Height = _renderedAutocompleteHeight,
            });
            return result is null ? null : result with { Focus = true };
        }

        // Leave press/drag/release unhandled so screen-level text selection can run over the editor rows.
        // The renderer synthesizes a click when press and release land on the same cell, which positions the cursor.
        if (mouseEvent.Type != TuiMouseEventType.Click || mouseEvent.Button != TuiMouseButton.Left) return null;
        if (mouseEvent.Y <= 0 || mouseEvent.Y > _renderedVisibleLineCount) return TuiMouseEventResult.HandledFocus;

        var visualLines = BuildVisualLineMap(_lastWidth);
        var visualLineIndex = _scrollOffset + mouseEvent.Y - 1;
        if (visualLineIndex < 0 || visualLineIndex >= visualLines.Count) return TuiMouseEventResult.HandledFocus;
        var visualLine = visualLines[visualLineIndex];
        var logicalLine = visualLine.LogicalLine < _state.Lines.Count ? _state.Lines[visualLine.LogicalLine] : "";
        var chunkEnd = Math.Min(logicalLine.Length, visualLine.StartCol + visualLine.Length);
        var chunk = logicalLine[Math.Min(visualLine.StartCol, chunkEnd)..chunkEnd];
        var targetColumn = Math.Max(0, mouseEvent.X - paddingX);
        var visibleColumn = 0;
        var targetIndex = chunk.Length;
        var lastGraphemeIndex = 0;
        var index = 0;
        foreach (var grapheme in TextUtils.Graphemes(chunk))
        {
            var nextColumn = visibleColumn + TextUtils.VisibleWidth(grapheme);
            lastGraphemeIndex = index;
            if (targetColumn < nextColumn)
            {
                targetIndex = index;
                break;
            }
            visibleColumn = nextColumn;
            index += grapheme.Length;
        }
        var isLastSegment = visualLineIndex == visualLines.Count - 1 || visualLines[visualLineIndex + 1].LogicalLine != visualLine.LogicalLine;
        if (!isLastSegment && targetIndex == chunk.Length && chunk.Length > 0) targetIndex = lastGraphemeIndex;

        _state.CursorLine = visualLine.LogicalLine;
        SetCursorCol(visualLine.StartCol + targetIndex);
        _lastAction = null;
        ExitHistoryBrowsing();
        if (_autocompleteState is not null) UpdateAutocomplete();
        return TuiMouseEventResult.HandledFocus;
    }

    public virtual void HandleInput(string data)
    {
        var kb = KeybindingsManager.Global;

        if (_jumpMode is not null)
        {
            if (kb.Matches(data, "tui.editor.jumpForward") || kb.Matches(data, "tui.editor.jumpBackward"))
            {
                _jumpMode = null;
                return;
            }
            var jumpPrintable = Keys.DecodePrintableKey(data) ?? (data.Length > 0 && data[0] >= 32 ? data : null);
            if (jumpPrintable is not null)
            {
                var direction = _jumpMode;
                _jumpMode = null;
                JumpToChar(jumpPrintable, direction);
                return;
            }
            _jumpMode = null;
        }

        var pasteStart = data.IndexOf("\e[200~", StringComparison.Ordinal);
        if (pasteStart != -1)
        {
            _isInPaste = true;
            _pasteBuffer = "";
            data = data.Remove(pasteStart, 6);
        }

        if (_isInPaste)
        {
            _pasteBuffer += data;
            var endIndex = _pasteBuffer.IndexOf("\e[201~", StringComparison.Ordinal);
            if (endIndex != -1)
            {
                var pasteContent = _pasteBuffer[..endIndex];
                if (pasteContent.Length > 0) HandlePaste(pasteContent);
                _isInPaste = false;
                var remaining = _pasteBuffer[(endIndex + 6)..];
                _pasteBuffer = "";
                if (remaining.Length > 0) HandleInput(remaining);
            }
            return;
        }

        if (kb.Matches(data, "tui.input.copy")) return;

        if (kb.Matches(data, "tui.editor.undo"))
        {
            Undo();
            return;
        }

        if (_autocompleteState is not null && _autocompleteList is not null)
        {
            if (kb.Matches(data, "tui.select.cancel"))
            {
                CancelAutocomplete();
                return;
            }
            if (kb.Matches(data, "tui.select.up") || kb.Matches(data, "tui.select.down"))
            {
                _autocompleteList.HandleInput(data);
                return;
            }
            if (kb.Matches(data, "tui.input.tab"))
            {
                if (_autocompleteList.GetSelectedItem() is { } selected && _autocompleteProvider is not null)
                {
                    ApplyCompletion(new AutocompleteItem(selected.Value, selected.Label, selected.Description), _autocompletePrefix);
                    CancelAutocomplete();
                    OnChange?.Invoke(GetText());
                }
                return;
            }
            if (kb.Matches(data, "tui.select.confirm"))
            {
                if (_autocompleteList.GetSelectedItem() is { } selected && _autocompleteProvider is not null)
                {
                    ApplyCompletion(new AutocompleteItem(selected.Value, selected.Label, selected.Description), _autocompletePrefix);
                    if (_autocompletePrefix.StartsWith('/'))
                    {
                        CancelAutocomplete();
                        // Fall through to submit
                    }
                    else
                    {
                        CancelAutocomplete();
                        OnChange?.Invoke(GetText());
                        return;
                    }
                }
            }
        }

        if (kb.Matches(data, "tui.input.tab") && _autocompleteState is null)
        {
            HandleTabCompletion();
            return;
        }

        if (kb.Matches(data, "tui.editor.deleteToLineEnd"))
        {
            DeleteToEndOfLine();
            return;
        }
        if (kb.Matches(data, "tui.editor.deleteToLineStart"))
        {
            DeleteToStartOfLine();
            return;
        }
        if (kb.Matches(data, "tui.editor.deleteWordBackward"))
        {
            DeleteWordBackwards();
            return;
        }
        if (kb.Matches(data, "tui.editor.deleteWordForward"))
        {
            DeleteWordForward();
            return;
        }
        if (kb.Matches(data, "tui.editor.deleteCharBackward") || Keys.Matches(data, "shift+backspace"))
        {
            HandleBackspace();
            return;
        }
        if (kb.Matches(data, "tui.editor.deleteCharForward") || Keys.Matches(data, "shift+delete"))
        {
            HandleForwardDelete();
            return;
        }
        if (kb.Matches(data, "tui.editor.yank"))
        {
            Yank();
            return;
        }
        if (kb.Matches(data, "tui.editor.yankPop"))
        {
            YankPop();
            return;
        }
        if (kb.Matches(data, "tui.editor.historyPrevious"))
        {
            CancelAutocomplete();
            NavigateHistory(-1);
            return;
        }
        if (kb.Matches(data, "tui.editor.historyNext"))
        {
            CancelAutocomplete();
            NavigateHistory(1);
            return;
        }
        if (kb.Matches(data, "tui.editor.cursorLineStart"))
        {
            MoveToLineStart();
            return;
        }
        if (kb.Matches(data, "tui.editor.cursorLineEnd"))
        {
            MoveToLineEnd();
            return;
        }
        if (kb.Matches(data, "tui.editor.cursorWordLeft"))
        {
            MoveWordBackwards();
            return;
        }
        if (kb.Matches(data, "tui.editor.cursorWordRight"))
        {
            MoveWordForwards();
            return;
        }

        if (kb.Matches(data, "tui.input.newLine")
            || (data.Length > 1 && data[0] == '\n')
            || data == "\e\r"
            || data == "\e[13;2~"
            || (data.Length > 1 && data.Contains('\e') && data.Contains('\r'))
            || data == "\n")
        {
            if (ShouldSubmitOnBackslashEnter(data, kb))
            {
                HandleBackspace();
                SubmitValue();
                return;
            }
            AddNewLine();
            return;
        }

        if (kb.Matches(data, "tui.input.submit"))
        {
            if (DisableSubmit) return;
            var line = CurrentLine;
            if (_state.CursorCol > 0 && _state.CursorCol <= line.Length && line[_state.CursorCol - 1] == '\\')
            {
                HandleBackspace();
                AddNewLine();
                return;
            }
            SubmitValue();
            return;
        }

        if (kb.Matches(data, "tui.editor.cursorUp"))
        {
            if (IsOnFirstVisualLine() && (IsEditorEmpty() || _historyIndex > -1 || _state.CursorCol == 0)) NavigateHistory(-1);
            else if (IsOnFirstVisualLine()) MoveToLineStart();
            else MoveCursor(-1, 0);
            return;
        }
        if (kb.Matches(data, "tui.editor.cursorDown"))
        {
            if (_historyIndex > -1 && IsOnLastVisualLine()) NavigateHistory(1);
            else if (IsOnLastVisualLine()) MoveToLineEnd();
            else MoveCursor(1, 0);
            return;
        }
        if (kb.Matches(data, "tui.editor.cursorRight"))
        {
            MoveCursor(0, 1);
            return;
        }
        if (kb.Matches(data, "tui.editor.cursorLeft"))
        {
            MoveCursor(0, -1);
            return;
        }
        if (kb.Matches(data, "tui.editor.pageUp"))
        {
            PageScroll(-1);
            return;
        }
        if (kb.Matches(data, "tui.editor.pageDown"))
        {
            PageScroll(1);
            return;
        }
        if (kb.Matches(data, "tui.editor.jumpForward"))
        {
            _jumpMode = "forward";
            return;
        }
        if (kb.Matches(data, "tui.editor.jumpBackward"))
        {
            _jumpMode = "backward";
            return;
        }
        if (Keys.Matches(data, "shift+space"))
        {
            InsertCharacter(" ");
            return;
        }

        if (Keys.DecodePrintableKey(data) is { } printable)
        {
            InsertCharacter(printable);
            return;
        }

        if (data.Length > 0 && data[0] >= 32) InsertCharacter(data);
    }

    private void ApplyCompletion(AutocompleteItem item, string prefix)
    {
        PushUndoSnapshot();
        _lastAction = null;
        var result = _autocompleteProvider!.ApplyCompletion(_state.Lines, _state.CursorLine, _state.CursorCol, item, prefix);
        _state.Lines = result.Lines;
        _state.CursorLine = result.CursorLine;
        SetCursorCol(result.CursorCol);
    }

    private List<LayoutLine> LayoutText(int contentWidth)
    {
        var layoutLines = new List<LayoutLine>();
        if (_state.Lines.Count == 0 || IsEditorEmpty())
        {
            layoutLines.Add(new LayoutLine("", true, 0));
            return layoutLines;
        }

        for (var i = 0; i < _state.Lines.Count; i++)
        {
            var line = _state.Lines[i];
            var isCurrentLine = i == _state.CursorLine;
            if (TextUtils.VisibleWidth(line) <= contentWidth)
            {
                layoutLines.Add(isCurrentLine ? new LayoutLine(line, true, _state.CursorCol) : new LayoutLine(line, false));
                continue;
            }

            var chunks = WordWrapLine(line, contentWidth, Segment(line, false).ToList());
            for (var chunkIndex = 0; chunkIndex < chunks.Count; chunkIndex++)
            {
                var chunk = chunks[chunkIndex];
                var cursorPos = _state.CursorCol;
                var isLastChunk = chunkIndex == chunks.Count - 1;
                var hasCursor = false;
                var adjusted = 0;
                if (isCurrentLine)
                {
                    if (isLastChunk)
                    {
                        hasCursor = cursorPos >= chunk.StartIndex;
                        adjusted = cursorPos - chunk.StartIndex;
                    }
                    else
                    {
                        hasCursor = cursorPos >= chunk.StartIndex && cursorPos < chunk.EndIndex;
                        if (hasCursor) adjusted = Math.Min(cursorPos - chunk.StartIndex, chunk.Text.Length);
                    }
                }
                layoutLines.Add(hasCursor ? new LayoutLine(chunk.Text, true, adjusted) : new LayoutLine(chunk.Text, false));
            }
        }
        return layoutLines;
    }

    public string GetText() => string.Join("\n", _state.Lines);

    private string ExpandPasteMarkers(string text)
    {
        var result = text;
        foreach (var (pasteId, content) in _pastes)
        {
            result = Regex.Replace(result, $@"\[paste #{pasteId}( (\+\d+ lines|\d+ chars))?\]", _ => content);
        }
        return result;
    }

    public string GetExpandedText() => ExpandPasteMarkers(GetText());

    public List<string> GetLines() => [.. _state.Lines];

    public (int Line, int Col) GetCursor() => (_state.CursorLine, _state.CursorCol);

    public void SetText(string text)
    {
        CancelAutocomplete();
        _lastAction = null;
        ExitHistoryBrowsing();
        var normalized = NormalizeText(text);
        if (GetText() != normalized) PushUndoSnapshot();
        _pastes.Clear();
        _pasteCounter = 0;
        SetTextInternal(normalized);
    }

    public void InsertTextAtCursor(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        CancelAutocomplete();
        PushUndoSnapshot();
        _lastAction = null;
        ExitHistoryBrowsing();
        InsertTextAtCursorInternal(text);
    }

    private static string NormalizeText(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\t", "    ");

    private void InsertTextAtCursorInternal(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var normalized = NormalizeText(text);
        var inserted = normalized.Split('\n');
        var currentLine = CurrentLine;
        var col = Math.Min(_state.CursorCol, currentLine.Length);
        var before = currentLine[..col];
        var after = currentLine[col..];

        if (inserted.Length == 1)
        {
            _state.Lines[_state.CursorLine] = before + normalized + after;
            SetCursorCol(_state.CursorCol + normalized.Length);
        }
        else
        {
            var newLines = new List<string>();
            newLines.AddRange(_state.Lines.Take(_state.CursorLine));
            newLines.Add(before + inserted[0]);
            newLines.AddRange(inserted.Skip(1).Take(inserted.Length - 2));
            newLines.Add(inserted[^1] + after);
            newLines.AddRange(_state.Lines.Skip(_state.CursorLine + 1));
            _state.Lines = newLines;
            _state.CursorLine += inserted.Length - 1;
            SetCursorCol(inserted[^1].Length);
        }
        OnChange?.Invoke(GetText());
    }

    private void InsertCharacter(string ch, bool skipUndoCoalescing = false)
    {
        ExitHistoryBrowsing();
        if (!skipUndoCoalescing)
        {
            if (TextUtils.IsWhitespaceChar(ch) || _lastAction != "type-word") PushUndoSnapshot();
            _lastAction = "type-word";
        }

        var line = CurrentLine;
        _state.Lines[_state.CursorLine] = line[.._state.CursorCol] + ch + line[_state.CursorCol..];
        SetCursorCol(_state.CursorCol + ch.Length);
        OnChange?.Invoke(GetText());

        if (_autocompleteState is null)
        {
            if (ch == "/" && IsAtStartOfMessage())
            {
                TryTriggerAutocomplete();
            }
            else if (_autocompleteTriggerCharacters.Contains(ch))
            {
                var textBeforeCursor = CurrentLine[.._state.CursorCol];
                var charBefore = textBeforeCursor.Length >= 2 ? textBeforeCursor[^2] : '\0';
                if (textBeforeCursor.Length == 1 || charBefore == ' ' || charBefore == '\t') TryTriggerAutocomplete();
            }
            else if (AutocompleteWordChar().IsMatch(ch))
            {
                var textBeforeCursor = CurrentLine[.._state.CursorCol];
                if (IsInSlashCommandContext(textBeforeCursor)) TryTriggerAutocomplete();
                else if (_autocompleteTriggerPattern.IsMatch(textBeforeCursor)) TryTriggerAutocomplete();
            }
        }
        else
        {
            UpdateAutocomplete();
        }
    }

    private void HandlePaste(string pastedText)
    {
        CancelAutocomplete();
        ExitHistoryBrowsing();
        _lastAction = null;
        PushUndoSnapshot();

        var decoded = CsiUCtrl().Replace(pastedText, m =>
        {
            var cp = int.Parse(m.Groups[1].Value);
            if (cp is >= 97 and <= 122) return ((char)(cp - 96)).ToString();
            if (cp is >= 65 and <= 90) return ((char)(cp - 64)).ToString();
            return m.Value;
        });
        var clean = NormalizeText(decoded);
        var filtered = new string(clean.Where(c => c == '\n' || c >= 32).ToArray());

        if (PathStart().IsMatch(filtered))
        {
            var line = CurrentLine;
            if (_state.CursorCol > 0 && WordChar().IsMatch(line[_state.CursorCol - 1].ToString())) filtered = " " + filtered;
        }

        var pastedLines = filtered.Split('\n');
        var totalChars = filtered.Length;
        if (pastedLines.Length > 10 || totalChars > 1000)
        {
            _pasteCounter++;
            var pasteId = _pasteCounter;
            _pastes[pasteId] = filtered;
            var marker = pastedLines.Length > 10 ? $"[paste #{pasteId} +{pastedLines.Length} lines]" : $"[paste #{pasteId} {totalChars} chars]";
            InsertTextAtCursorInternal(marker);
            return;
        }
        InsertTextAtCursorInternal(filtered);
    }

    private void AddNewLine()
    {
        CancelAutocomplete();
        ExitHistoryBrowsing();
        _lastAction = null;
        PushUndoSnapshot();
        var line = CurrentLine;
        _state.Lines[_state.CursorLine] = line[.._state.CursorCol];
        _state.Lines.Insert(_state.CursorLine + 1, line[_state.CursorCol..]);
        _state.CursorLine++;
        SetCursorCol(0);
        OnChange?.Invoke(GetText());
    }

    private bool ShouldSubmitOnBackslashEnter(string data, KeybindingsManager kb)
    {
        if (DisableSubmit) return false;
        if (!Keys.Matches(data, "enter")) return false;
        var submitKeys = kb.GetKeys("tui.input.submit");
        if (!submitKeys.Contains("shift+enter") && !submitKeys.Contains("shift+return")) return false;
        var line = CurrentLine;
        return _state.CursorCol > 0 && line[_state.CursorCol - 1] == '\\';
    }

    private void SubmitValue()
    {
        CancelAutocomplete();
        var result = ExpandPasteMarkers(GetText()).Trim();
        _state = new EditorState();
        _pastes.Clear();
        _pasteCounter = 0;
        ExitHistoryBrowsing();
        _scrollOffset = 0;
        _undoStack.Clear();
        _lastAction = null;
        OnChange?.Invoke("");
        OnSubmit?.Invoke(result);
    }

    private void HandleBackspace()
    {
        ExitHistoryBrowsing();
        _lastAction = null;

        if (_state.CursorCol > 0)
        {
            PushUndoSnapshot();
            var line = CurrentLine;
            var beforeCursor = line[.._state.CursorCol];
            var lastGrapheme = Segment(beforeCursor, false).LastOrDefault().Segment ?? "";
            var graphemeLength = lastGrapheme.Length > 0 ? lastGrapheme.Length : 1;
            var pasteMatch = PasteMarkerSingle().Match(lastGrapheme);
            if (pasteMatch.Success)
            {
                var targetId = int.Parse(pasteMatch.Groups[1].Value);
                _pastes.Remove(targetId);
                _pasteCounter--;
                foreach (var id in _pastes.Keys.Where(id => id > targetId).OrderBy(id => id).ToList())
                {
                    _pastes[id - 1] = _pastes[id];
                    _pastes.Remove(id);
                }
                _state.Lines = _state.Lines.Select(l => PasteMarkerRegex().Replace(l, m =>
                {
                    var x = int.Parse(m.Groups[1].Value);
                    return x <= targetId ? m.Value : $"[paste #{x - 1}{m.Groups[2].Value}]";
                })).ToList();
            }

            line = CurrentLine;
            var start = Math.Max(0, _state.CursorCol - graphemeLength);
            _state.Lines[_state.CursorLine] = line[..start] + line[Math.Min(_state.CursorCol, line.Length)..];
            SetCursorCol(start);
        }
        else if (_state.CursorLine > 0)
        {
            PushUndoSnapshot();
            var currentLine = CurrentLine;
            var previousLine = _state.Lines[_state.CursorLine - 1];
            _state.Lines[_state.CursorLine - 1] = previousLine + currentLine;
            _state.Lines.RemoveAt(_state.CursorLine);
            _state.CursorLine--;
            SetCursorCol(previousLine.Length);
        }

        OnChange?.Invoke(GetText());
        RetriggerAutocompleteAfterDelete();
    }

    private void RetriggerAutocompleteAfterDelete()
    {
        if (_autocompleteState is not null)
        {
            UpdateAutocomplete();
            return;
        }
        var textBeforeCursor = CurrentLine[.._state.CursorCol];
        if (IsInSlashCommandContext(textBeforeCursor)) TryTriggerAutocomplete();
        else if (_autocompleteTriggerPattern.IsMatch(textBeforeCursor)) TryTriggerAutocomplete();
    }

    private void SetCursorCol(int col)
    {
        _state.CursorCol = col;
        _preferredVisualCol = null;
        _snappedFromCursorCol = null;
    }

    private void MoveToVisualLine(List<VisualLine> visualLines, int currentVisualLine, int targetVisualLine)
    {
        if (currentVisualLine < 0 || currentVisualLine >= visualLines.Count || targetVisualLine < 0 || targetVisualLine >= visualLines.Count) return;
        var currentVL = visualLines[currentVisualLine];
        var targetVL = visualLines[targetVisualLine];

        int currentVisualCol;
        if (_snappedFromCursorCol is { } snapped)
        {
            var vlIndex = FindVisualLineAt(visualLines, currentVL.LogicalLine, snapped);
            currentVisualCol = snapped - visualLines[vlIndex].StartCol;
        }
        else
        {
            currentVisualCol = _state.CursorCol - currentVL.StartCol;
        }

        var isLastSource = currentVisualLine == visualLines.Count - 1 || visualLines[currentVisualLine + 1].LogicalLine != currentVL.LogicalLine;
        var sourceMax = isLastSource ? currentVL.Length : Math.Max(0, currentVL.Length - 1);
        var isLastTarget = targetVisualLine == visualLines.Count - 1 || visualLines[targetVisualLine + 1].LogicalLine != targetVL.LogicalLine;
        var targetMax = isLastTarget ? targetVL.Length : Math.Max(0, targetVL.Length - 1);
        var moveToCol = ComputeVerticalMoveColumn(currentVisualCol, sourceMax, targetMax);

        _state.CursorLine = targetVL.LogicalLine;
        var logicalLine = _state.Lines[targetVL.LogicalLine];
        _state.CursorCol = Math.Min(targetVL.StartCol + moveToCol, logicalLine.Length);

        foreach (var seg in Segment(logicalLine, false))
        {
            if (seg.Index > _state.CursorCol) break;
            if (seg.Segment.Length <= 1) continue;
            if (_state.CursorCol >= seg.Index + seg.Segment.Length) continue;

            var isContinuation = seg.Index < targetVL.StartCol;
            var isMovingDown = targetVisualLine > currentVisualLine;
            if (isContinuation && isMovingDown)
            {
                var segEnd = seg.Index + seg.Segment.Length;
                var next = targetVisualLine + 1;
                while (next < visualLines.Count && visualLines[next].LogicalLine == targetVL.LogicalLine && visualLines[next].StartCol < segEnd) next++;
                if (next < visualLines.Count)
                {
                    MoveToVisualLine(visualLines, currentVisualLine, next);
                    return;
                }
            }
            _snappedFromCursorCol = _state.CursorCol;
            _state.CursorCol = seg.Index;
            return;
        }
        _snappedFromCursorCol = null;
    }

    private int ComputeVerticalMoveColumn(int currentVisualCol, int sourceMaxVisualCol, int targetMaxVisualCol)
    {
        var hasPreferred = _preferredVisualCol is not null;
        var cursorInMiddle = currentVisualCol < sourceMaxVisualCol;
        var targetTooShort = targetMaxVisualCol < currentVisualCol;

        if (!hasPreferred || cursorInMiddle)
        {
            if (targetTooShort)
            {
                _preferredVisualCol = currentVisualCol;
                return targetMaxVisualCol;
            }
            _preferredVisualCol = null;
            return currentVisualCol;
        }

        var targetCantFitPreferred = targetMaxVisualCol < _preferredVisualCol!.Value;
        if (targetTooShort || targetCantFitPreferred) return targetMaxVisualCol;
        var result = _preferredVisualCol.Value;
        _preferredVisualCol = null;
        return result;
    }

    private void MoveToLineStart()
    {
        _lastAction = null;
        SetCursorCol(0);
    }

    private void MoveToLineEnd()
    {
        _lastAction = null;
        SetCursorCol(CurrentLine.Length);
    }

    private void MergeWithPreviousLine()
    {
        var currentLine = CurrentLine;
        var previousLine = _state.Lines[_state.CursorLine - 1];
        _state.Lines[_state.CursorLine - 1] = previousLine + currentLine;
        _state.Lines.RemoveAt(_state.CursorLine);
        _state.CursorLine--;
        SetCursorCol(previousLine.Length);
    }

    private void MergeWithNextLine()
    {
        var currentLine = CurrentLine;
        var nextLine = _state.Lines[_state.CursorLine + 1];
        _state.Lines[_state.CursorLine] = currentLine + nextLine;
        _state.Lines.RemoveAt(_state.CursorLine + 1);
    }

    private void DeleteToStartOfLine()
    {
        ExitHistoryBrowsing();
        var currentLine = CurrentLine;
        if (_state.CursorCol > 0)
        {
            PushUndoSnapshot();
            _killRing.Push(currentLine[.._state.CursorCol], prepend: true, accumulate: _lastAction == "kill");
            _lastAction = "kill";
            _state.Lines[_state.CursorLine] = currentLine[_state.CursorCol..];
            SetCursorCol(0);
        }
        else if (_state.CursorLine > 0)
        {
            PushUndoSnapshot();
            _killRing.Push("\n", prepend: true, accumulate: _lastAction == "kill");
            _lastAction = "kill";
            MergeWithPreviousLine();
        }
        OnChange?.Invoke(GetText());
    }

    private void DeleteToEndOfLine()
    {
        ExitHistoryBrowsing();
        var currentLine = CurrentLine;
        if (_state.CursorCol < currentLine.Length)
        {
            PushUndoSnapshot();
            _killRing.Push(currentLine[_state.CursorCol..], prepend: false, accumulate: _lastAction == "kill");
            _lastAction = "kill";
            _state.Lines[_state.CursorLine] = currentLine[.._state.CursorCol];
        }
        else if (_state.CursorLine < _state.Lines.Count - 1)
        {
            PushUndoSnapshot();
            _killRing.Push("\n", prepend: false, accumulate: _lastAction == "kill");
            _lastAction = "kill";
            MergeWithNextLine();
        }
        OnChange?.Invoke(GetText());
    }

    private void DeleteWordBackwards()
    {
        ExitHistoryBrowsing();
        var currentLine = CurrentLine;
        if (_state.CursorCol == 0)
        {
            if (_state.CursorLine > 0)
            {
                PushUndoSnapshot();
                _killRing.Push("\n", prepend: true, accumulate: _lastAction == "kill");
                _lastAction = "kill";
                MergeWithPreviousLine();
            }
        }
        else
        {
            PushUndoSnapshot();
            var wasKill = _lastAction == "kill";
            var oldCol = _state.CursorCol;
            MoveWordBackwards();
            var deleteFrom = _state.CursorCol;
            SetCursorCol(oldCol);
            _killRing.Push(currentLine[deleteFrom.._state.CursorCol], prepend: true, accumulate: wasKill);
            _lastAction = "kill";
            _state.Lines[_state.CursorLine] = currentLine[..deleteFrom] + currentLine[_state.CursorCol..];
            SetCursorCol(deleteFrom);
        }
        OnChange?.Invoke(GetText());
    }

    private void DeleteWordForward()
    {
        ExitHistoryBrowsing();
        var currentLine = CurrentLine;
        if (_state.CursorCol >= currentLine.Length)
        {
            if (_state.CursorLine < _state.Lines.Count - 1)
            {
                PushUndoSnapshot();
                _killRing.Push("\n", prepend: false, accumulate: _lastAction == "kill");
                _lastAction = "kill";
                MergeWithNextLine();
            }
        }
        else
        {
            PushUndoSnapshot();
            var wasKill = _lastAction == "kill";
            var oldCol = _state.CursorCol;
            MoveWordForwards();
            var deleteTo = _state.CursorCol;
            SetCursorCol(oldCol);
            _killRing.Push(currentLine[_state.CursorCol..deleteTo], prepend: false, accumulate: wasKill);
            _lastAction = "kill";
            _state.Lines[_state.CursorLine] = currentLine[.._state.CursorCol] + currentLine[deleteTo..];
        }
        OnChange?.Invoke(GetText());
    }

    private void HandleForwardDelete()
    {
        ExitHistoryBrowsing();
        _lastAction = null;
        var currentLine = CurrentLine;
        if (_state.CursorCol < currentLine.Length)
        {
            PushUndoSnapshot();
            var first = Segment(currentLine[_state.CursorCol..], false).FirstOrDefault().Segment ?? "";
            var len = first.Length > 0 ? first.Length : 1;
            _state.Lines[_state.CursorLine] = currentLine[.._state.CursorCol] + currentLine[(_state.CursorCol + len)..];
        }
        else if (_state.CursorLine < _state.Lines.Count - 1)
        {
            PushUndoSnapshot();
            MergeWithNextLine();
        }
        OnChange?.Invoke(GetText());
        RetriggerAutocompleteAfterDelete();
    }

    private List<VisualLine> BuildVisualLineMap(int width)
    {
        var visualLines = new List<VisualLine>();
        for (var i = 0; i < _state.Lines.Count; i++)
        {
            var line = _state.Lines[i];
            if (line.Length == 0)
            {
                visualLines.Add(new VisualLine(i, 0, 0));
            }
            else if (TextUtils.VisibleWidth(line) <= width)
            {
                visualLines.Add(new VisualLine(i, 0, line.Length));
            }
            else
            {
                foreach (var chunk in WordWrapLine(line, width, Segment(line, false).ToList()))
                {
                    visualLines.Add(new VisualLine(i, chunk.StartIndex, chunk.EndIndex - chunk.StartIndex));
                }
            }
        }
        return visualLines;
    }

    private static int FindVisualLineAt(List<VisualLine> visualLines, int line, int col)
    {
        for (var i = 0; i < visualLines.Count; i++)
        {
            var vl = visualLines[i];
            if (vl.LogicalLine != line) continue;
            var offset = col - vl.StartCol;
            var isLast = i == visualLines.Count - 1 || visualLines[i + 1].LogicalLine != vl.LogicalLine;
            if (offset >= 0 && (offset < vl.Length || (isLast && offset == vl.Length))) return i;
        }
        return visualLines.Count - 1;
    }

    private int FindCurrentVisualLine(List<VisualLine> visualLines) => FindVisualLineAt(visualLines, _state.CursorLine, _state.CursorCol);

    private void MoveCursor(int deltaLine, int deltaCol)
    {
        _lastAction = null;
        var visualLines = BuildVisualLineMap(_lastWidth);
        var currentVisualLine = FindCurrentVisualLine(visualLines);

        if (deltaLine != 0)
        {
            var target = currentVisualLine + deltaLine;
            if (target >= 0 && target < visualLines.Count) MoveToVisualLine(visualLines, currentVisualLine, target);
        }

        if (deltaCol != 0)
        {
            var currentLine = CurrentLine;
            if (deltaCol > 0)
            {
                if (_state.CursorCol < currentLine.Length)
                {
                    var first = Segment(currentLine[_state.CursorCol..], false).FirstOrDefault().Segment ?? "";
                    SetCursorCol(_state.CursorCol + (first.Length > 0 ? first.Length : 1));
                }
                else if (_state.CursorLine < _state.Lines.Count - 1)
                {
                    _state.CursorLine++;
                    SetCursorCol(0);
                }
                else if (currentVisualLine >= 0 && currentVisualLine < visualLines.Count)
                {
                    _preferredVisualCol = _state.CursorCol - visualLines[currentVisualLine].StartCol;
                }
            }
            else
            {
                if (_state.CursorCol > 0)
                {
                    var last = Segment(currentLine[.._state.CursorCol], false).LastOrDefault().Segment ?? "";
                    SetCursorCol(_state.CursorCol - (last.Length > 0 ? last.Length : 1));
                }
                else if (_state.CursorLine > 0)
                {
                    _state.CursorLine--;
                    SetCursorCol(CurrentLine.Length);
                }
            }
        }

        if (_autocompleteState is not null) UpdateAutocomplete();
    }

    private void PageScroll(int direction)
    {
        _lastAction = null;
        var pageSize = Math.Max(5, (int)Math.Floor(Tui.Terminal.Rows * 0.3));
        var visualLines = BuildVisualLineMap(_lastWidth);
        var current = FindCurrentVisualLine(visualLines);
        var target = Math.Max(0, Math.Min(visualLines.Count - 1, current + direction * pageSize));
        MoveToVisualLine(visualLines, current, target);
    }

    private void MoveWordBackwards()
    {
        _lastAction = null;
        var currentLine = CurrentLine;
        if (_state.CursorCol == 0)
        {
            if (_state.CursorLine > 0)
            {
                _state.CursorLine--;
                SetCursorCol(CurrentLine.Length);
            }
            return;
        }
        SetCursorCol(WordNavigation.FindWordBackward(currentLine, _state.CursorCol, t => Segment(t, true), IsPasteMarker));
    }

    private void MoveWordForwards()
    {
        _lastAction = null;
        var currentLine = CurrentLine;
        if (_state.CursorCol >= currentLine.Length)
        {
            if (_state.CursorLine < _state.Lines.Count - 1)
            {
                _state.CursorLine++;
                SetCursorCol(0);
            }
            return;
        }
        SetCursorCol(WordNavigation.FindWordForward(currentLine, _state.CursorCol, t => Segment(t, true), IsPasteMarker));
    }

    private void Yank()
    {
        if (_killRing.Count == 0) return;
        PushUndoSnapshot();
        InsertYankedText(_killRing.Peek()!);
        _lastAction = "yank";
    }

    private void YankPop()
    {
        if (_lastAction != "yank" || _killRing.Count <= 1) return;
        PushUndoSnapshot();
        DeleteYankedText();
        _killRing.Rotate();
        InsertYankedText(_killRing.Peek()!);
        _lastAction = "yank";
    }

    private void InsertYankedText(string text)
    {
        ExitHistoryBrowsing();
        var lines = text.Split('\n');
        var currentLine = CurrentLine;
        var before = currentLine[.._state.CursorCol];
        var after = currentLine[_state.CursorCol..];
        if (lines.Length == 1)
        {
            _state.Lines[_state.CursorLine] = before + text + after;
            SetCursorCol(_state.CursorCol + text.Length);
        }
        else
        {
            _state.Lines[_state.CursorLine] = before + lines[0];
            for (var i = 1; i < lines.Length - 1; i++) _state.Lines.Insert(_state.CursorLine + i, lines[i]);
            var lastLineIndex = _state.CursorLine + lines.Length - 1;
            _state.Lines.Insert(lastLineIndex, lines[^1] + after);
            _state.CursorLine = lastLineIndex;
            SetCursorCol(lines[^1].Length);
        }
        OnChange?.Invoke(GetText());
    }

    private void DeleteYankedText()
    {
        var yanked = _killRing.Peek();
        if (string.IsNullOrEmpty(yanked)) return;
        var yankLines = yanked.Split('\n');
        if (yankLines.Length == 1)
        {
            var currentLine = CurrentLine;
            var start = Math.Max(0, _state.CursorCol - yanked.Length);
            _state.Lines[_state.CursorLine] = currentLine[..start] + currentLine[_state.CursorCol..];
            SetCursorCol(start);
        }
        else
        {
            var startLine = _state.CursorLine - (yankLines.Length - 1);
            var startCol = _state.Lines[startLine].Length - yankLines[0].Length;
            var afterCursor = CurrentLine[_state.CursorCol..];
            var beforeYank = _state.Lines[startLine][..startCol];
            _state.Lines.RemoveRange(startLine, yankLines.Length);
            _state.Lines.Insert(startLine, beforeYank + afterCursor);
            _state.CursorLine = startLine;
            SetCursorCol(startCol);
        }
        OnChange?.Invoke(GetText());
    }

    private void PushUndoSnapshot() => _undoStack.Push(new EditorSnapshot(_state.Clone(), new Dictionary<int, string>(_pastes), _pasteCounter));

    private void Undo()
    {
        ExitHistoryBrowsing();
        if (!_undoStack.TryPop(out var snapshot)) return;
        _state.Lines = [.. snapshot.State.Lines];
        _state.CursorLine = snapshot.State.CursorLine;
        _state.CursorCol = snapshot.State.CursorCol;
        _pastes = snapshot.Pastes;
        _pasteCounter = snapshot.PasteCounter;
        _lastAction = null;
        _preferredVisualCol = null;
        OnChange?.Invoke(GetText());
    }

    private void JumpToChar(string ch, string? direction)
    {
        _lastAction = null;
        var isForward = direction == "forward";
        var lines = _state.Lines;
        var end = isForward ? lines.Count : -1;
        var step = isForward ? 1 : -1;
        for (var lineIdx = _state.CursorLine; lineIdx != end; lineIdx += step)
        {
            var line = lines[lineIdx];
            var isCurrent = lineIdx == _state.CursorLine;
            int idx;
            if (isForward)
            {
                var from = isCurrent ? _state.CursorCol + 1 : 0;
                idx = from > line.Length ? -1 : line.IndexOf(ch, from, StringComparison.Ordinal);
            }
            else
            {
                var from = isCurrent ? _state.CursorCol - 1 : line.Length;
                // JS lastIndexOf clamps negative positions to 0.
                from = Math.Max(0, from);
                var searchEnd = Math.Min(line.Length, from + ch.Length);
                idx = searchEnd == 0 ? (ch.Length == 0 ? 0 : -1) : line.LastIndexOf(ch, searchEnd - 1, searchEnd, StringComparison.Ordinal);
            }
            if (idx != -1)
            {
                _state.CursorLine = lineIdx;
                SetCursorCol(idx);
                return;
            }
        }
    }

    private bool IsSlashMenuAllowed() => _state.CursorLine == 0;

    private bool IsAtStartOfMessage()
    {
        if (!IsSlashMenuAllowed()) return false;
        var before = CurrentLine[.._state.CursorCol].Trim();
        return before is "" or "/";
    }

    private bool IsInSlashCommandContext(string textBeforeCursor) => IsSlashMenuAllowed() && textBeforeCursor.TrimStart().StartsWith('/');

    // ----- Autocomplete -----

    private static int GetBestAutocompleteMatchIndex(List<AutocompleteItem> items, string prefix)
    {
        if (prefix.Length == 0) return -1;
        var firstPrefix = -1;
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].Value == prefix) return i;
            if (firstPrefix == -1 && items[i].Value.StartsWith(prefix, StringComparison.Ordinal)) firstPrefix = i;
        }
        return firstPrefix;
    }

    private SelectList CreateAutocompleteList(string prefix, List<AutocompleteItem> items)
    {
        var layout = prefix.StartsWith('/') ? SlashCommandSelectListLayout : null;
        var list = new SelectList(items.Select(i => new SelectItem(i.Value, i.Label, i.Description)).ToList(), _autocompleteMaxVisible, _theme.SelectList, layout);
        list.OnSelect = selected =>
        {
            if (_autocompleteProvider is null) return;
            ApplyCompletion(new AutocompleteItem(selected.Value, selected.Label, selected.Description), _autocompletePrefix);
            CancelAutocomplete();
            OnChange?.Invoke(GetText());
        };
        return list;
    }

    private void TryTriggerAutocomplete(bool explicitTab = false) => RequestAutocomplete(false, explicitTab);

    private void HandleTabCompletion()
    {
        if (_autocompleteProvider is null) return;
        var beforeCursor = CurrentLine[.._state.CursorCol];
        if (IsInSlashCommandContext(beforeCursor) && !beforeCursor.TrimStart().Contains(' ')) RequestAutocomplete(false, true);
        else RequestAutocomplete(true, true);
    }

    private void RequestAutocomplete(bool force, bool explicitTab)
    {
        if (_autocompleteProvider is null) return;
        if (force && !_autocompleteProvider.ShouldTriggerFileCompletion(_state.Lines, _state.CursorLine, _state.CursorCol)) return;

        CancelAutocompleteRequest();
        var startToken = ++_autocompleteStartToken;
        var debounceMs = GetAutocompleteDebounceMs(force, explicitTab);
        if (debounceMs > 0)
        {
            _autocompleteDebounceTimer = Tui.Dispatcher.SetTimeout(() =>
            {
                _autocompleteDebounceTimer = null;
                _ = StartAutocompleteRequestAsync(startToken, force, explicitTab);
            }, debounceMs);
            return;
        }
        _ = StartAutocompleteRequestAsync(startToken, force, explicitTab);
    }

    private async Task StartAutocompleteRequestAsync(int startToken, bool force, bool explicitTab)
    {
        var previous = _autocompleteRequestTask;
        _autocompleteRequestTask = Run();
        await _autocompleteRequestTask;

        async Task Run()
        {
            try
            {
                await previous;
            }
            catch
            {
                // Previous request failures don't block new ones.
            }
            if (startToken != _autocompleteStartToken || _autocompleteProvider is null) return;
            var controller = new CancellationTokenSource();
            _autocompleteAbort = controller;
            var requestId = ++_autocompleteRequestId;
            await RunAutocompleteRequestAsync(requestId, controller, GetText(), _state.CursorLine, _state.CursorCol, force, explicitTab);
        }
    }

    private void SetAutocompleteTriggerCharacters(IReadOnlyList<string> triggerCharacters)
    {
        var next = new List<string>(DefaultAutocompleteTriggerCharacters);
        foreach (var ch in triggerCharacters)
        {
            if (ch.Length != 1 || ch == "/" || TextUtils.IsWhitespaceChar(ch) || next.Contains(ch)) continue;
            next.Add(ch);
        }
        _autocompleteTriggerCharacters = next;
        _autocompleteTriggerPattern = BuildTriggerPattern(next);
        _autocompleteDebouncePattern = BuildDebouncePattern(next);
    }

    private int GetAutocompleteDebounceMs(bool force, bool explicitTab)
    {
        if (explicitTab || force) return 0;
        return _autocompleteDebouncePattern.IsMatch(CurrentLine[.._state.CursorCol]) ? AttachmentAutocompleteDebounceMs : 0;
    }

    private async Task RunAutocompleteRequestAsync(int requestId, CancellationTokenSource controller, string snapshotText, int snapshotLine, int snapshotCol, bool force, bool explicitTab)
    {
        if (_autocompleteProvider is null) return;
        AutocompleteSuggestions? suggestions;
        try
        {
            suggestions = await _autocompleteProvider.GetSuggestionsAsync([.. _state.Lines], _state.CursorLine, _state.CursorCol, force, controller.Token);
        }
        catch
        {
            suggestions = null;
        }

        var current = !controller.IsCancellationRequested
            && requestId == _autocompleteRequestId
            && GetText() == snapshotText
            && _state.CursorLine == snapshotLine
            && _state.CursorCol == snapshotCol;
        if (!current) return;
        _autocompleteAbort = null;

        if (suggestions is null || suggestions.Items.Count == 0)
        {
            CancelAutocomplete();
            Tui.RequestRender();
            return;
        }

        if (force && explicitTab && suggestions.Items.Count == 1)
        {
            ApplyCompletion(suggestions.Items[0], suggestions.Prefix);
            OnChange?.Invoke(GetText());
            Tui.RequestRender();
            return;
        }

        _autocompletePrefix = suggestions.Prefix;
        _autocompleteList = CreateAutocompleteList(suggestions.Prefix, suggestions.Items);
        var best = GetBestAutocompleteMatchIndex(suggestions.Items, suggestions.Prefix);
        if (best >= 0) _autocompleteList.SetSelectedIndex(best);
        _autocompleteState = force ? "force" : "regular";
        Tui.RequestRender();
    }

    private void CancelAutocompleteRequest()
    {
        _autocompleteStartToken++;
        _autocompleteDebounceTimer?.Dispose();
        _autocompleteDebounceTimer = null;
        _autocompleteAbort?.Cancel();
        _autocompleteAbort = null;
    }

    private void CancelAutocomplete()
    {
        CancelAutocompleteRequest();
        _autocompleteState = null;
        _autocompleteList = null;
        _autocompletePrefix = "";
    }

    public bool IsShowingAutocomplete() => _autocompleteState is not null;

    private void UpdateAutocomplete()
    {
        if (_autocompleteState is null || _autocompleteProvider is null) return;
        RequestAutocomplete(_autocompleteState == "force", false);
    }
}
