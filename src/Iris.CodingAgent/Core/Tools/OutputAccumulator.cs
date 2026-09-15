using System.Security.Cryptography;
using System.Text;

namespace Iris.CodingAgent.Core.Tools;

public sealed record OutputSnapshot(string Content, TruncationResult Truncation, string? FullOutputPath);

/// <summary>
/// Incrementally tracks streaming output with bounded memory: decodes chunks with a streaming UTF-8 decoder, keeps a
/// decoded tail for display snapshots, and spills the raw output to a temp file when it needs to be preserved.
/// Port of core/tools/output-accumulator.ts.
/// </summary>
public sealed class OutputAccumulator
{
    private readonly int _maxLines;
    private readonly int _maxBytes;
    private readonly int _maxRollingBytes;
    private readonly string _tempFilePrefix;
    private readonly Decoder _decoder = new UTF8Encoding(false, false).GetDecoder();

    private List<byte[]> _rawChunks = [];
    private StringBuilder _tailText = new();
    private long _tailBytes;
    private bool _tailStartsAtLineBoundary = true;
    private long _totalRawBytes;
    private long _totalDecodedBytes;
    private int _completedLines;
    private int _totalLines;
    private long _currentLineBytes;
    private bool _hasOpenLine;
    private bool _finished;

    private string? _tempFilePath;
    private FileStream? _tempFileStream;

    public OutputAccumulator(int? maxLines = null, int? maxBytes = null, string? tempFilePrefix = null)
    {
        _maxLines = maxLines ?? Truncate.DefaultMaxLines;
        _maxBytes = maxBytes ?? Truncate.DefaultMaxBytes;
        _maxRollingBytes = Math.Max(_maxBytes * 2, 1);
        _tempFilePrefix = tempFilePrefix ?? "pi-output";
    }

    public void Append(ReadOnlySpan<byte> data)
    {
        if (_finished) throw new InvalidOperationException("Cannot append to a finished output accumulator");
        _totalRawBytes += data.Length;

        var chars = new char[_decoder.GetCharCount(data, flush: false)];
        var count = _decoder.GetChars(data, chars, flush: false);
        AppendDecodedText(new string(chars, 0, count));

        if (_tempFileStream is not null || ShouldUseTempFile())
        {
            EnsureTempFile();
            _tempFileStream?.Write(data);
        }
        else if (data.Length > 0)
        {
            _rawChunks.Add(data.ToArray());
        }
    }

    public void Finish()
    {
        if (_finished) return;
        _finished = true;
        var chars = new char[_decoder.GetCharCount([], flush: true)];
        var count = _decoder.GetChars([], chars, flush: true);
        AppendDecodedText(new string(chars, 0, count));
        if (ShouldUseTempFile()) EnsureTempFile();
    }

    public OutputSnapshot Snapshot(bool persistIfTruncated = false)
    {
        var tail = Truncate.TruncateTail(GetSnapshotText(), _maxLines, _maxBytes);
        var truncated = _totalLines > _maxLines || _totalDecodedBytes > _maxBytes;
        var truncatedBy = truncated ? tail.TruncatedBy ?? (_totalDecodedBytes > _maxBytes ? "bytes" : "lines") : null;
        var truncation = new TruncationResult
        {
            Content = tail.Content,
            Truncated = truncated,
            TruncatedBy = truncatedBy,
            TotalLines = _totalLines,
            TotalBytes = (int)Math.Min(int.MaxValue, _totalDecodedBytes),
            OutputLines = tail.OutputLines,
            OutputBytes = tail.OutputBytes,
            LastLinePartial = tail.LastLinePartial,
            FirstLineExceedsLimit = tail.FirstLineExceedsLimit,
            MaxLines = _maxLines,
            MaxBytes = _maxBytes,
        };
        if (persistIfTruncated && truncation.Truncated) EnsureTempFile();
        return new OutputSnapshot(truncation.Content, truncation, _tempFilePath);
    }

    public async Task CloseTempFileAsync()
    {
        if (_tempFileStream is null) return;
        var stream = _tempFileStream;
        _tempFileStream = null;
        await stream.FlushAsync();
        await stream.DisposeAsync();
    }

    public long GetLastLineBytes() => _currentLineBytes;

    private void AppendDecodedText(string text)
    {
        if (text.Length == 0) return;
        var bytes = Truncate.ByteLength(text);
        _totalDecodedBytes += bytes;
        _tailText.Append(text);
        _tailBytes += bytes;
        if (_tailBytes > _maxRollingBytes * 2L) TrimTail();

        var newlines = 0;
        var lastNewline = -1;
        for (var i = text.IndexOf('\n'); i != -1; i = text.IndexOf('\n', i + 1))
        {
            newlines++;
            lastNewline = i;
        }
        if (newlines == 0)
        {
            _currentLineBytes += bytes;
            _hasOpenLine = true;
        }
        else
        {
            _completedLines += newlines;
            var tail = text[(lastNewline + 1)..];
            _currentLineBytes = Truncate.ByteLength(tail);
            _hasOpenLine = tail.Length > 0;
        }
        _totalLines = _completedLines + (_hasOpenLine ? 1 : 0);
    }

    private void TrimTail()
    {
        var buffer = Encoding.UTF8.GetBytes(_tailText.ToString());
        if (buffer.Length <= _maxRollingBytes)
        {
            _tailBytes = buffer.Length;
            return;
        }
        var start = buffer.Length - _maxRollingBytes;
        while (start < buffer.Length && (buffer[start] & 0xc0) == 0x80) start++;
        _tailStartsAtLineBoundary = start == 0 ? _tailStartsAtLineBoundary : buffer[start - 1] == 0x0a;
        var text = Encoding.UTF8.GetString(buffer, start, buffer.Length - start);
        _tailText = new StringBuilder(text);
        _tailBytes = Truncate.ByteLength(text);
    }

    private string GetSnapshotText()
    {
        var text = _tailText.ToString();
        if (_tailStartsAtLineBoundary) return text;
        var firstNewline = text.IndexOf('\n');
        return firstNewline == -1 ? text : text[(firstNewline + 1)..];
    }

    private bool ShouldUseTempFile() =>
        _totalRawBytes > _maxBytes || _totalDecodedBytes > _maxBytes || _totalLines > _maxLines;

    private void EnsureTempFile()
    {
        if (_tempFilePath is not null) return;
        var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        _tempFilePath = Path.Combine(Path.GetTempPath(), $"{_tempFilePrefix}-{id}.log");
        _tempFileStream = new FileStream(_tempFilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        foreach (var chunk in _rawChunks) _tempFileStream.Write(chunk);
        _rawChunks = [];
    }
}
