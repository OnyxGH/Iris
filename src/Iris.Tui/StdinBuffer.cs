using System.Text;
using System.Text.RegularExpressions;

namespace Iris.Tui;

/// <summary>
/// Buffers raw input and emits complete key sequences and bracketed pastes (partial escape sequences can arrive split
/// across reads). Port of pi-tui stdin-buffer.ts. Timer callbacks are delivered through <see cref="Post"/>.
/// </summary>
public sealed partial class StdinBuffer : IDisposable
{
    private const char Esc = '\e';
    private const int DefaultSequenceTimeoutMs = 50;
    private const int DefaultEscapeTimeoutMs = 10;
    private const string PasteStart = "\e[200~";
    private const string PasteEnd = "\e[201~";

    private string _buffer = "";
    private Timer? _timer;
    private int _timerGeneration;
    private readonly int _timeoutMs;
    private readonly int _escapeTimeoutMs;
    private bool _pasteMode;
    private string _pasteBuffer = "";
    private int? _pendingKittyPrintableCodepoint;

    public event Action<string>? Data;
    public event Action<string>? Paste;

    /// <summary>Runs timer callbacks on the owner's thread. Defaults to synchronous invocation.</summary>
    public Action<Action> Post { get; set; } = action => action();

    public StdinBuffer(int? timeoutMs = null, int? escapeTimeoutMs = null)
    {
        _timeoutMs = timeoutMs ?? DefaultSequenceTimeoutMs;
        _escapeTimeoutMs = escapeTimeoutMs ?? DefaultEscapeTimeoutMs;
    }

    private enum Completion { Complete, Incomplete, NotEscape }

    [GeneratedRegex("^<\\d+;\\d+;\\d+[Mm]$")]
    private static partial Regex SgrMouse();

    [GeneratedRegex("^\\e\\[(\\d+)(?::\\d*)?(?::\\d+)?u$")]
    private static partial Regex UnmodifiedKittyPrintable();

    private static Completion IsCompleteSequence(string data)
    {
        if (data.Length == 0 || data[0] != Esc) return Completion.NotEscape;
        if (data.Length == 1) return Completion.Incomplete;
        var after = data[1..];
        if (after.StartsWith('['))
        {
            if (after.StartsWith("[M", StringComparison.Ordinal)) return data.Length >= 6 ? Completion.Complete : Completion.Incomplete;
            return IsCompleteCsi(data);
        }
        if (after.StartsWith(']')) return data.EndsWith("\e\\", StringComparison.Ordinal) || data.EndsWith('\a') ? Completion.Complete : Completion.Incomplete;
        if (after.StartsWith('P') || after.StartsWith('_')) return data.EndsWith("\e\\", StringComparison.Ordinal) ? Completion.Complete : Completion.Incomplete;
        if (after.StartsWith('O')) return after.Length >= 2 ? Completion.Complete : Completion.Incomplete;
        return Completion.Complete;
    }

    private static Completion IsCompleteCsi(string data)
    {
        if (data.Length < 3) return Completion.Incomplete;
        var payload = data[2..];
        var last = payload[^1];
        if (last is >= (char)0x40 and <= (char)0x7e)
        {
            if (payload.StartsWith('<'))
            {
                if (SgrMouse().IsMatch(payload)) return Completion.Complete;
                if (last is 'M' or 'm')
                {
                    var parts = payload[1..^1].Split(';');
                    if (parts.Length == 3 && parts.All(p => p.Length > 0 && p.All(char.IsAsciiDigit))) return Completion.Complete;
                }
                return Completion.Incomplete;
            }
            return Completion.Complete;
        }
        return Completion.Incomplete;
    }

    private static int? ParseUnmodifiedKittyPrintableCodepoint(string sequence)
    {
        var m = UnmodifiedKittyPrintable().Match(sequence);
        if (!m.Success || !int.TryParse(m.Groups[1].Value, out var cp)) return null;
        return cp >= 32 ? cp : null;
    }

    private static (List<string> Sequences, string Remainder) ExtractCompleteSequences(string buffer)
    {
        var sequences = new List<string>();
        var pos = 0;
        while (pos < buffer.Length)
        {
            var remaining = buffer[pos..];
            if (remaining[0] == Esc)
            {
                var seqEnd = 1;
                while (seqEnd <= remaining.Length)
                {
                    var candidate = remaining[..seqEnd];
                    var status = IsCompleteSequence(candidate);
                    if (status == Completion.Complete)
                    {
                        if (candidate == "\e\e" && seqEnd < remaining.Length && remaining[seqEnd] is '[' or ']' or 'O' or 'P' or '_')
                        {
                            sequences.Add("\e");
                            pos += 1;
                            break;
                        }
                        sequences.Add(candidate);
                        pos += seqEnd;
                        break;
                    }
                    if (status == Completion.Incomplete)
                    {
                        seqEnd++;
                    }
                    else
                    {
                        sequences.Add(candidate);
                        pos += seqEnd;
                        break;
                    }
                }
                if (seqEnd > remaining.Length) return (sequences, remaining);
            }
            else
            {
                // Keep surrogate pairs together.
                var take = char.IsHighSurrogate(remaining[0]) && remaining.Length > 1 && char.IsLowSurrogate(remaining[1]) ? 2 : 1;
                sequences.Add(remaining[..take]);
                pos += take;
            }
        }
        return (sequences, "");
    }

    private void CancelTimer()
    {
        Interlocked.Increment(ref _timerGeneration);
        _timer?.Dispose();
        _timer = null;
    }

    public void Process(string str)
    {
        CancelTimer();

        if (str.Length == 0 && _buffer.Length == 0)
        {
            EmitData("");
            return;
        }

        _buffer += str;

        if (_pasteMode)
        {
            _pasteBuffer += _buffer;
            _buffer = "";
            TryFinishPaste();
            return;
        }

        var startIndex = _buffer.IndexOf(PasteStart, StringComparison.Ordinal);
        if (startIndex != -1)
        {
            if (startIndex > 0)
            {
                foreach (var seq in ExtractCompleteSequences(_buffer[..startIndex]).Sequences) EmitData(seq);
            }
            _pendingKittyPrintableCodepoint = null;
            _pasteBuffer = _buffer[(startIndex + PasteStart.Length)..];
            _buffer = "";
            _pasteMode = true;
            TryFinishPaste();
            return;
        }

        var (sequences, remainder) = ExtractCompleteSequences(_buffer);
        _buffer = remainder;
        foreach (var seq in sequences) EmitData(seq);

        if (_buffer.Length > 0)
        {
            var timeout = _buffer == "\e" ? _escapeTimeoutMs : _timeoutMs;
            var generation = _timerGeneration;
            _timer = new Timer(_ => Post(() =>
            {
                if (generation != _timerGeneration) return;
                foreach (var seq in Flush()) EmitData(seq);
            }), null, timeout, Timeout.Infinite);
        }
    }

    private void TryFinishPaste()
    {
        var endIndex = _pasteBuffer.IndexOf(PasteEnd, StringComparison.Ordinal);
        if (endIndex == -1) return;
        var content = _pasteBuffer[..endIndex];
        var remaining = _pasteBuffer[(endIndex + PasteEnd.Length)..];
        _pasteMode = false;
        _pasteBuffer = "";
        _pendingKittyPrintableCodepoint = null;
        Paste?.Invoke(content);
        if (remaining.Length > 0) Process(remaining);
    }

    private void EmitData(string sequence)
    {
        int? raw = sequence.Length is 1 or 2 && char.ConvertToUtf32OrNull(sequence) is { } cp ? cp : null;
        if (raw is not null && raw == _pendingKittyPrintableCodepoint)
        {
            _pendingKittyPrintableCodepoint = null;
            return;
        }
        _pendingKittyPrintableCodepoint = ParseUnmodifiedKittyPrintableCodepoint(sequence);
        Data?.Invoke(sequence);
    }

    public List<string> Flush()
    {
        CancelTimer();
        if (_buffer.Length == 0) return [];
        var sequences = new List<string> { _buffer };
        _buffer = "";
        _pendingKittyPrintableCodepoint = null;
        return sequences;
    }

    public void Clear()
    {
        CancelTimer();
        _buffer = "";
        _pasteMode = false;
        _pasteBuffer = "";
        _pendingKittyPrintableCodepoint = null;
    }

    public string GetBuffer() => _buffer;

    public void Dispose() => Clear();
}

internal static class CharExtensions
{
    extension(char)
    {
        /// <summary>The single code point of a 1- or 2-char string, or null.</summary>
        public static int? ConvertToUtf32OrNull(string s)
        {
            if (s.Length == 1) return char.IsSurrogate(s[0]) ? s[0] : s[0];
            if (s.Length == 2 && char.IsHighSurrogate(s[0]) && char.IsLowSurrogate(s[1])) return char.ConvertToUtf32(s[0], s[1]);
            return null;
        }
    }
}
