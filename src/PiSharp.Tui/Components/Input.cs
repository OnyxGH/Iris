namespace PiSharp.Tui.Components;

/// <summary>Single-line text input with horizontal scrolling. Port of pi-tui Input.</summary>
public sealed class Input : IInputComponent, IFocusable
{
    private sealed record State(string Value, int Cursor);

    private string _value = "";
    private int _cursor;
    private readonly string _prompt;
    private readonly string _placeholder;
    private readonly Func<string, string> _placeholderStyle;
    private string _pasteBuffer = "";
    private bool _inPaste;
    private readonly KillRing _killRing = new();
    private string? _lastAction;
    private readonly UndoStack<State> _undo = new();

    public Action<string>? OnSubmit { get; set; }
    public Action? OnEscape { get; set; }
    public bool Focused { get; set; }

    public Input(string? prompt = null, string? placeholder = null, Func<string, string>? placeholderStyle = null)
    {
        _prompt = prompt ?? "> ";
        _placeholder = placeholder ?? "";
        _placeholderStyle = placeholderStyle ?? (t => t);
    }

    public string GetValue() => _value;

    public void SetValue(string value)
    {
        _value = value;
        _cursor = Math.Min(_cursor, value.Length);
    }

    private static int FirstGraphemeLength(string s) => s.Length == 0 ? 1 : TextUtils.Graphemes(s).First().Length;

    private static int LastGraphemeLength(string s) => s.Length == 0 ? 1 : TextUtils.Graphemes(s).Last().Length;

    public void HandleInput(string data)
    {
        if (data.Contains("\e[200~"))
        {
            _inPaste = true;
            _pasteBuffer = "";
            var idx = data.IndexOf("\e[200~", StringComparison.Ordinal);
            data = data.Remove(idx, 6);
        }
        if (_inPaste)
        {
            _pasteBuffer += data;
            var end = _pasteBuffer.IndexOf("\e[201~", StringComparison.Ordinal);
            if (end != -1)
            {
                HandlePaste(_pasteBuffer[..end]);
                _inPaste = false;
                var remaining = _pasteBuffer[(end + 6)..];
                _pasteBuffer = "";
                if (remaining.Length > 0) HandleInput(remaining);
            }
            return;
        }

        var kb = KeybindingsManager.Global;
        if (kb.Matches(data, "tui.select.cancel"))
        {
            OnEscape?.Invoke();
            return;
        }
        if (kb.Matches(data, "tui.editor.undo"))
        {
            Undo();
            return;
        }
        if (kb.Matches(data, "tui.input.submit") || data == "\n")
        {
            OnSubmit?.Invoke(_value);
            return;
        }
        if (kb.Matches(data, "tui.editor.deleteCharBackward"))
        {
            HandleBackspace();
            return;
        }
        if (kb.Matches(data, "tui.editor.deleteCharForward"))
        {
            HandleForwardDelete();
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
        if (kb.Matches(data, "tui.editor.deleteToLineStart"))
        {
            DeleteToLineStart();
            return;
        }
        if (kb.Matches(data, "tui.editor.deleteToLineEnd"))
        {
            DeleteToLineEnd();
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
        if (kb.Matches(data, "tui.editor.cursorLeft"))
        {
            _lastAction = null;
            if (_cursor > 0) _cursor -= LastGraphemeLength(_value[.._cursor]);
            return;
        }
        if (kb.Matches(data, "tui.editor.cursorRight"))
        {
            _lastAction = null;
            if (_cursor < _value.Length) _cursor += FirstGraphemeLength(_value[_cursor..]);
            return;
        }
        if (kb.Matches(data, "tui.editor.cursorLineStart"))
        {
            _lastAction = null;
            _cursor = 0;
            return;
        }
        if (kb.Matches(data, "tui.editor.cursorLineEnd"))
        {
            _lastAction = null;
            _cursor = _value.Length;
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

        if (Keys.DecodeKittyPrintable(data) is { } printable)
        {
            InsertCharacter(printable);
            return;
        }

        if (!data.Any(ch => ch < 32 || ch == 0x7f || ch is >= (char)0x80 and <= (char)0x9f)) InsertCharacter(data);
    }

    private void InsertCharacter(string ch)
    {
        if (TextUtils.IsWhitespaceChar(ch) || _lastAction != "type-word") PushUndo();
        _lastAction = "type-word";
        _value = _value[.._cursor] + ch + _value[_cursor..];
        _cursor += ch.Length;
    }

    private void HandleBackspace()
    {
        _lastAction = null;
        if (_cursor <= 0) return;
        PushUndo();
        var len = LastGraphemeLength(_value[.._cursor]);
        _value = _value[..(_cursor - len)] + _value[_cursor..];
        _cursor -= len;
    }

    private void HandleForwardDelete()
    {
        _lastAction = null;
        if (_cursor >= _value.Length) return;
        PushUndo();
        var len = FirstGraphemeLength(_value[_cursor..]);
        _value = _value[.._cursor] + _value[(_cursor + len)..];
    }

    private void DeleteToLineStart()
    {
        if (_cursor == 0) return;
        PushUndo();
        _killRing.Push(_value[.._cursor], prepend: true, accumulate: _lastAction == "kill");
        _lastAction = "kill";
        _value = _value[_cursor..];
        _cursor = 0;
    }

    private void DeleteToLineEnd()
    {
        if (_cursor >= _value.Length) return;
        PushUndo();
        _killRing.Push(_value[_cursor..], prepend: false, accumulate: _lastAction == "kill");
        _lastAction = "kill";
        _value = _value[.._cursor];
    }

    private void DeleteWordBackwards()
    {
        if (_cursor == 0) return;
        var wasKill = _lastAction == "kill";
        PushUndo();
        var old = _cursor;
        MoveWordBackwards();
        var from = _cursor;
        _cursor = old;
        _killRing.Push(_value[from.._cursor], prepend: true, accumulate: wasKill);
        _lastAction = "kill";
        _value = _value[..from] + _value[_cursor..];
        _cursor = from;
    }

    private void DeleteWordForward()
    {
        if (_cursor >= _value.Length) return;
        var wasKill = _lastAction == "kill";
        PushUndo();
        var old = _cursor;
        MoveWordForwards();
        var to = _cursor;
        _cursor = old;
        _killRing.Push(_value[_cursor..to], prepend: false, accumulate: wasKill);
        _lastAction = "kill";
        _value = _value[.._cursor] + _value[to..];
    }

    private void Yank()
    {
        if (_killRing.Peek() is not { Length: > 0 } text) return;
        PushUndo();
        _value = _value[.._cursor] + text + _value[_cursor..];
        _cursor += text.Length;
        _lastAction = "yank";
    }

    private void YankPop()
    {
        if (_lastAction != "yank" || _killRing.Count <= 1) return;
        PushUndo();
        var prev = _killRing.Peek() ?? "";
        _value = _value[..(_cursor - prev.Length)] + _value[_cursor..];
        _cursor -= prev.Length;
        _killRing.Rotate();
        var text = _killRing.Peek() ?? "";
        _value = _value[.._cursor] + text + _value[_cursor..];
        _cursor += text.Length;
        _lastAction = "yank";
    }

    private void PushUndo() => _undo.Push(new State(_value, _cursor));

    private void Undo()
    {
        if (!_undo.TryPop(out var snapshot)) return;
        _value = snapshot.Value;
        _cursor = snapshot.Cursor;
        _lastAction = null;
    }

    private void MoveWordBackwards()
    {
        if (_cursor == 0) return;
        _lastAction = null;
        _cursor = WordNavigation.FindWordBackward(_value, _cursor);
    }

    private void MoveWordForwards()
    {
        if (_cursor >= _value.Length) return;
        _lastAction = null;
        _cursor = WordNavigation.FindWordForward(_value, _cursor);
    }

    private void HandlePaste(string pasted)
    {
        _lastAction = null;
        PushUndo();
        var clean = pasted.Replace("\r\n", "").Replace("\r", "").Replace("\n", "").Replace("\t", "    ");
        _value = _value[.._cursor] + clean + _value[_cursor..];
        _cursor += clean.Length;
    }

    public void Invalidate()
    {
    }

    public List<string> Render(int width)
    {
        var available = width - TextUtils.VisibleWidth(_prompt);
        if (available <= 0) return [TextUtils.TruncateToWidth(_prompt, width, "")];

        if (_value.Length == 0 && _placeholder.Length > 0)
        {
            var placeholder = TextUtils.TruncateToWidth(_placeholder, available, "");
            var at = placeholder.Length > 0 ? TextUtils.Graphemes(placeholder).First() : " ";
            var after = placeholder.Length >= at.Length ? placeholder[at.Length..] : "";
            var marker = Focused ? TuiBase.CursorMarker : "";
            var withCursor = marker + $"\e[7m{_placeholderStyle(at)}\e[27m" + _placeholderStyle(after);
            return [_prompt + withCursor + new string(' ', Math.Max(0, available - TextUtils.VisibleWidth(withCursor)))];
        }

        string visibleText;
        var cursorDisplay = _cursor;
        var totalWidth = TextUtils.VisibleWidth(_value);
        if (totalWidth < available)
        {
            visibleText = _value;
        }
        else
        {
            var scrollWidth = _cursor == _value.Length ? available - 1 : available;
            var cursorCol = TextUtils.VisibleWidth(_value[.._cursor]);
            if (scrollWidth > 0)
            {
                var half = scrollWidth / 2;
                int startCol;
                if (cursorCol < half) startCol = 0;
                else if (cursorCol > totalWidth - half) startCol = Math.Max(0, totalWidth - scrollWidth);
                else startCol = Math.Max(0, cursorCol - half);
                visibleText = TextUtils.SliceByColumn(_value, startCol, scrollWidth, true);
                cursorDisplay = TextUtils.SliceByColumn(_value, startCol, Math.Max(0, cursorCol - startCol), true).Length;
            }
            else
            {
                visibleText = "";
                cursorDisplay = 0;
            }
        }

        cursorDisplay = Math.Min(cursorDisplay, visibleText.Length);
        var rest = visibleText[cursorDisplay..];
        var atCursor = rest.Length > 0 ? TextUtils.Graphemes(rest).First() : " ";
        var before = visibleText[..cursorDisplay];
        var afterCursor = rest.Length >= atCursor.Length && rest.Length > 0 ? rest[atCursor.Length..] : "";
        var cursorMarker = Focused ? TuiBase.CursorMarker : "";
        var textWithCursor = before + cursorMarker + $"\e[7m{atCursor}\e[27m" + afterCursor;
        return [_prompt + textWithCursor + new string(' ', Math.Max(0, available - TextUtils.VisibleWidth(textWithCursor)))];
    }
}
