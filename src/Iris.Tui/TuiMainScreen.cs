using System.Text;

namespace Iris.Tui;

/// <summary>TUI rendering into the terminal's main screen and scrollback with differential updates. Port of pi-tui tui-main-screen.ts.</summary>
public sealed class TuiMainScreen : TuiBase
{
    private const string KittySequencePrefix = "\e_G";

    private List<string> _previousLines = [];
    private HashSet<long> _previousKittyImageIds = [];
    private int _previousWidth;
    private int _previousHeight;
    private int _cursorRow;
    private int _hardwareCursorRow;
    private int _maxLinesRendered;
    private int _previousViewportTop;

    public TuiMainScreen(ITerminal terminal, UiDispatcher dispatcher, bool? showHardwareCursor = null, string? logDirectory = null)
        : base(terminal, dispatcher, showHardwareCursor, logDirectory)
    {
    }

    public override string Mode => "regular";

    protected override void ResetRenderState()
    {
        _previousLines = [];
        _previousWidth = -1;
        _previousHeight = -1;
        _cursorRow = 0;
        _hardwareCursorRow = 0;
        _maxLinesRendered = 0;
        _previousViewportTop = 0;
    }

    protected override void BeforeTerminalStop(bool preserveScreen)
    {
        if (preserveScreen || _previousLines.Count == 0) return;
        Terminal.Write(" ");
        var lineDiff = _previousLines.Count - _hardwareCursorRow;
        if (lineDiff > 0) Terminal.Write($"\e[{lineDiff}B");
        else if (lineDiff < 0) Terminal.Write($"\e[{-lineDiff}A");
        Terminal.Write("\r\n");
    }

    /// <summary>Streams output in bounded chunks.</summary>
    private sealed class BoundedWriter(Action<string> write)
    {
        private const int MaxChars = 1024 * 1024;
        private readonly StringBuilder _buffer = new();

        public void Append(string value)
        {
            _buffer.Append(value);
            if (_buffer.Length >= MaxChars) Flush();
        }

        public void Flush()
        {
            if (_buffer.Length == 0) return;
            write(_buffer.ToString());
            _buffer.Clear();
        }
    }

    private static (List<long> Ids, int Rows)? ParseKittyImageHeader(string line)
    {
        var start = line.IndexOf(KittySequencePrefix, StringComparison.Ordinal);
        if (start == -1) return null;
        var paramsStart = start + KittySequencePrefix.Length;
        var paramsEnd = line.IndexOf(';', paramsStart);
        if (paramsEnd == -1) return null;
        var ids = new List<long>();
        var rows = 1;
        foreach (var param in line[paramsStart..paramsEnd].Split(','))
        {
            var kv = param.Split('=', 2);
            if (kv.Length < 2 || !long.TryParse(kv[1], out var value) || value <= 0 || value > 0xffffffff) continue;
            if (kv[0] == "i") ids.Add(value);
            else if (kv[0] == "r") rows = (int)value;
        }
        return (ids, rows);
    }

    private static List<long> ExtractKittyImageIds(string line) => ParseKittyImageHeader(line)?.Ids ?? [];

    private static HashSet<long> CollectKittyImageIds(List<string> lines) => lines.SelectMany(ExtractKittyImageIds).ToHashSet();

    private static string DeleteKittyImages(IEnumerable<long> ids) => string.Concat(ids.Select(TerminalImage.DeleteKittyImage));

    private static int GetKittyImageReservedRows(List<string> lines, int index, int? maxIndex = null)
    {
        var rows = ParseKittyImageHeader(index < lines.Count ? lines[index] : "")?.Rows ?? 1;
        if (rows <= 1) return 1;
        var max = Math.Min(rows, Math.Min((maxIndex ?? lines.Count - 1) - index + 1, lines.Count - index));
        var reserved = 1;
        while (reserved < max)
        {
            var line = lines[index + reserved];
            if (TerminalImage.IsImageLine(line) || TextUtils.VisibleWidth(line) > 0) break;
            reserved++;
        }
        return reserved;
    }

    private (int First, int Last) ExpandChangedRangeForKittyImages(int firstChanged, int lastChanged, List<string> newLines)
    {
        int first = firstChanged, last = lastChanged;
        void Expand(List<string> lines)
        {
            for (var i = 0; i < lines.Count; i++)
            {
                if (ExtractKittyImageIds(lines[i]).Count == 0) continue;
                var blockEnd = i + GetKittyImageReservedRows(lines, i) - 1;
                if (i >= firstChanged || (i <= lastChanged && blockEnd >= firstChanged))
                {
                    first = Math.Min(first, i);
                    last = Math.Max(last, blockEnd);
                }
            }
        }
        Expand(_previousLines);
        Expand(newLines);
        return (first, last);
    }

    private string DeleteChangedKittyImages(int firstChanged, int lastChanged)
    {
        if (firstChanged < 0 || lastChanged < firstChanged) return "";
        var ids = new HashSet<long>();
        for (var i = firstChanged; i <= Math.Min(lastChanged, _previousLines.Count - 1); i++)
        {
            foreach (var id in ExtractKittyImageIds(_previousLines[i])) ids.Add(id);
        }
        return DeleteKittyImages(ids);
    }

    protected override void DoRender()
    {
        if (Stopped) return;
        var width = Terminal.Columns;
        var height = Terminal.Rows;
        var widthChanged = _previousWidth != 0 && _previousWidth != width;
        var heightChanged = _previousHeight != 0 && _previousHeight != height;
        var previousBufferLength = _previousHeight > 0 ? _previousViewportTop + _previousHeight : height;
        var prevViewportTop = heightChanged ? Math.Max(0, previousBufferLength - height) : _previousViewportTop;
        var viewportTop = prevViewportTop;
        var hardwareCursorRow = _hardwareCursorRow;

        int ComputeLineDiff(int targetRow) => (targetRow - viewportTop) - (hardwareCursorRow - prevViewportTop);

        var newLines = Render(width);
        if (HasOverlayEntries) newLines = CompositeOverlays(newLines, width, height);
        var cursorPos = ExtractCursorPosition(newLines, height);
        newLines = ApplyLineResets(newLines);

        void FullRender(bool clear)
        {
            FullRedrawCount++;
            var output = new BoundedWriter(Terminal.Write);
            output.Append("\e[?2026h");
            if (clear)
            {
                output.Append(DeleteKittyImages(_previousKittyImageIds));
                output.Append("\e[2J\e[H\e[3J");
            }
            for (var i = 0; i < newLines.Count; i++)
            {
                if (i > 0) output.Append("\r\n");
                var line = newLines[i];
                var reserved = TerminalImage.IsImageLine(line) ? GetKittyImageReservedRows(newLines, i) : 1;
                if (reserved > 1 && reserved <= height)
                {
                    for (var r = 1; r < reserved; r++) output.Append("\r\n");
                    output.Append($"\e[{reserved - 1}A");
                    output.Append(line);
                    output.Append($"\e[{reserved - 1}B");
                    i += reserved - 1;
                    continue;
                }
                output.Append(line);
            }
            output.Append("\e[?2026l");
            output.Flush();
            _cursorRow = Math.Max(0, newLines.Count - 1);
            _hardwareCursorRow = _cursorRow;
            _maxLinesRendered = clear ? newLines.Count : Math.Max(_maxLinesRendered, newLines.Count);
            _previousViewportTop = Math.Max(0, Math.Max(height, newLines.Count) - height);
            PositionHardwareCursor(cursorPos, newLines.Count);
            _previousLines = newLines;
            _previousKittyImageIds = CollectKittyImageIds(newLines);
            _previousWidth = width;
            _previousHeight = height;
        }

        if (_previousLines.Count == 0 && !widthChanged && !heightChanged)
        {
            FullRender(false);
            return;
        }
        if (widthChanged)
        {
            FullRender(true);
            return;
        }
        if (heightChanged && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TERMUX_VERSION")))
        {
            FullRender(true);
            return;
        }
        if (GetClearOnShrink() && newLines.Count < _maxLinesRendered && !HasOverlayEntries)
        {
            FullRender(true);
            return;
        }

        var firstChanged = -1;
        var lastChanged = -1;
        var maxLines = Math.Max(newLines.Count, _previousLines.Count);
        for (var i = 0; i < maxLines; i++)
        {
            var oldLine = i < _previousLines.Count ? _previousLines[i] : "";
            var newLine = i < newLines.Count ? newLines[i] : "";
            if (oldLine == newLine) continue;
            if (firstChanged == -1) firstChanged = i;
            lastChanged = i;
        }
        var appendedLines = newLines.Count > _previousLines.Count;
        if (appendedLines)
        {
            if (firstChanged == -1) firstChanged = _previousLines.Count;
            lastChanged = newLines.Count - 1;
        }
        if (firstChanged != -1) (firstChanged, lastChanged) = ExpandChangedRangeForKittyImages(firstChanged, lastChanged, newLines);
        var appendStart = appendedLines && firstChanged == _previousLines.Count && firstChanged > 0;

        if (firstChanged == -1)
        {
            PositionHardwareCursor(cursorPos, newLines.Count);
            _previousViewportTop = prevViewportTop;
            _previousHeight = height;
            return;
        }

        if (firstChanged >= newLines.Count)
        {
            if (_previousLines.Count > newLines.Count)
            {
                var output = new BoundedWriter(Terminal.Write);
                output.Append("\e[?2026h");
                output.Append(DeleteChangedKittyImages(firstChanged, lastChanged));
                var targetRow = Math.Max(0, newLines.Count - 1);
                if (targetRow < prevViewportTop)
                {
                    FullRender(true);
                    return;
                }
                var diff = ComputeLineDiff(targetRow);
                if (diff > 0) output.Append($"\e[{diff}B");
                else if (diff < 0) output.Append($"\e[{-diff}A");
                output.Append("\r");
                var extraLines = _previousLines.Count - newLines.Count;
                if (extraLines > height)
                {
                    FullRender(true);
                    return;
                }
                var clearStartOffset = newLines.Count == 0 ? 0 : 1;
                if (extraLines > 0 && clearStartOffset > 0) output.Append($"\e[{clearStartOffset}B");
                for (var i = 0; i < extraLines; i++)
                {
                    output.Append("\r\e[2K");
                    if (i < extraLines - 1) output.Append("\e[1B");
                }
                var moveBack = Math.Max(0, extraLines - 1 + clearStartOffset);
                if (moveBack > 0) output.Append($"\e[{moveBack}A");
                output.Append("\e[?2026l");
                output.Flush();
                _cursorRow = targetRow;
                _hardwareCursorRow = targetRow;
            }
            PositionHardwareCursor(cursorPos, newLines.Count);
            _previousLines = newLines;
            _previousKittyImageIds = CollectKittyImageIds(newLines);
            _previousWidth = width;
            _previousHeight = height;
            _previousViewportTop = prevViewportTop;
            return;
        }

        if (firstChanged < prevViewportTop)
        {
            FullRender(true);
            return;
        }

        var writer = new BoundedWriter(Terminal.Write);
        writer.Append("\e[?2026h");
        writer.Append(DeleteChangedKittyImages(firstChanged, lastChanged));
        var prevViewportBottom = prevViewportTop + height - 1;
        var moveTargetRow = appendStart ? firstChanged - 1 : firstChanged;
        if (moveTargetRow > prevViewportBottom)
        {
            var currentScreenRow = Math.Max(0, Math.Min(height - 1, hardwareCursorRow - prevViewportTop));
            var moveToBottom = height - 1 - currentScreenRow;
            if (moveToBottom > 0) writer.Append($"\e[{moveToBottom}B");
            var scroll = moveTargetRow - prevViewportBottom;
            writer.Append(string.Concat(Enumerable.Repeat("\r\n", scroll)));
            prevViewportTop += scroll;
            viewportTop += scroll;
            hardwareCursorRow = moveTargetRow;
        }

        var lineDiff = ComputeLineDiff(moveTargetRow);
        if (lineDiff > 0) writer.Append($"\e[{lineDiff}B");
        else if (lineDiff < 0) writer.Append($"\e[{-lineDiff}A");
        writer.Append(appendStart ? "\r\n" : "\r");

        var renderEnd = Math.Min(lastChanged, newLines.Count - 1);
        for (var i = firstChanged; i <= renderEnd; i++)
        {
            if (i > firstChanged) writer.Append("\r\n");
            var line = newLines[i];
            var isImage = TerminalImage.IsImageLine(line);
            var reserved = isImage ? GetKittyImageReservedRows(newLines, i, renderEnd) : 1;
            if (reserved > 1)
            {
                var imageStartScreenRow = i - viewportTop;
                if (imageStartScreenRow < 0 || imageStartScreenRow + reserved > height)
                {
                    FullRender(true);
                    return;
                }
                writer.Append("\e[2K");
                for (var r = 1; r < reserved; r++) writer.Append("\r\n\e[2K");
                writer.Append($"\e[{reserved - 1}A");
                writer.Append(line);
                writer.Append($"\e[{reserved - 1}B");
                i += reserved - 1;
                continue;
            }

            writer.Append("\e[2K");
            if (!isImage && TextUtils.VisibleWidth(line) > width)
            {
                var crashLogPath = Path.Combine(LogDirectory ?? Path.GetTempPath(), "pi-tui-crash.log");
                var crashData = new List<string>
                {
                    $"Crash at {DateTime.UtcNow:O}",
                    $"Terminal width: {width}",
                    $"Line {i} visible width: {TextUtils.VisibleWidth(line)}",
                    "",
                    "=== All rendered lines ===",
                };
                crashData.AddRange(newLines.Select((l, idx) => $"[{idx}] (w={TextUtils.VisibleWidth(l)}) {l}"));
                crashData.Add("");
                Directory.CreateDirectory(Path.GetDirectoryName(crashLogPath)!);
                File.WriteAllText(crashLogPath, string.Join("\n", crashData));
                Stop();
                throw new InvalidOperationException(string.Join("\n",
                    $"Rendered line {i} exceeds terminal width ({TextUtils.VisibleWidth(line)} > {width}).",
                    "",
                    "This is likely caused by a custom TUI component not truncating its output.",
                    "Use visibleWidth() to measure and truncateToWidth() to truncate lines.",
                    "",
                    $"Debug log written to: {crashLogPath}"));
            }
            writer.Append(line);
        }

        var finalCursorRow = renderEnd;
        if (_previousLines.Count > newLines.Count)
        {
            if (renderEnd < newLines.Count - 1)
            {
                writer.Append($"\e[{newLines.Count - 1 - renderEnd}B");
                finalCursorRow = newLines.Count - 1;
            }
            var extra = _previousLines.Count - newLines.Count;
            for (var i = newLines.Count; i < _previousLines.Count; i++) writer.Append("\r\n\e[2K");
            writer.Append($"\e[{extra}A");
        }

        writer.Append("\e[?2026l");
        writer.Flush();

        _cursorRow = Math.Max(0, newLines.Count - 1);
        _hardwareCursorRow = finalCursorRow;
        _maxLinesRendered = Math.Max(_maxLinesRendered, newLines.Count);
        _previousViewportTop = Math.Max(prevViewportTop, finalCursorRow - height + 1);
        PositionHardwareCursor(cursorPos, newLines.Count);
        _previousLines = newLines;
        _previousKittyImageIds = CollectKittyImageIds(newLines);
        _previousWidth = width;
        _previousHeight = height;
    }

    private void PositionHardwareCursor((int Row, int Col)? cursorPos, int totalLines)
    {
        if (cursorPos is not { } pos || totalLines <= 0)
        {
            Terminal.HideCursor();
            return;
        }
        var targetRow = Math.Max(0, Math.Min(pos.Row, totalLines - 1));
        var targetCol = Math.Max(0, pos.Col);
        var rowDelta = targetRow - _hardwareCursorRow;
        var buffer = "";
        if (rowDelta > 0) buffer += $"\e[{rowDelta}B";
        else if (rowDelta < 0) buffer += $"\e[{-rowDelta}A";
        buffer += $"\e[{targetCol + 1}G";
        Terminal.Write(buffer);
        _hardwareCursorRow = targetRow;
        if (GetShowHardwareCursor()) Terminal.ShowCursor();
        else Terminal.HideCursor();
    }
}
