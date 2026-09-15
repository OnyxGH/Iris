using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Iris.CodingAgent.Core.Tools;

public sealed class TruncationResult
{
    public string Content { get; init; } = "";
    public bool Truncated { get; init; }

    /// <summary>"lines", "bytes", or null when not truncated.</summary>
    public string? TruncatedBy { get; init; }

    public int TotalLines { get; init; }
    public int TotalBytes { get; init; }
    public int OutputLines { get; init; }
    public int OutputBytes { get; init; }
    public bool LastLinePartial { get; init; }
    public bool FirstLineExceedsLimit { get; init; }
    public long MaxLines { get; init; }
    public int MaxBytes { get; init; }

    public JsonObject ToJson() => new()
    {
        ["content"] = Content,
        ["truncated"] = Truncated,
        ["truncatedBy"] = TruncatedBy,
        ["totalLines"] = TotalLines,
        ["totalBytes"] = TotalBytes,
        ["outputLines"] = OutputLines,
        ["outputBytes"] = OutputBytes,
        ["lastLinePartial"] = LastLinePartial,
        ["firstLineExceedsLimit"] = FirstLineExceedsLimit,
        ["maxLines"] = MaxLines,
        ["maxBytes"] = MaxBytes,
    };
}

/// <summary>Shared truncation utilities for tool outputs. Port of core/tools/truncate.ts.</summary>
public static class Truncate
{
    public const int DefaultMaxLines = 2000;
    public const int DefaultMaxBytes = 50 * 1024;
    public const int GrepMaxLineLength = 500;

    /// <summary>JavaScript's Number.MAX_SAFE_INTEGER, used as "no line limit".</summary>
    public const long Unlimited = 9007199254740991;

    public static int ByteLength(string text) => Encoding.UTF8.GetByteCount(text);

    private static List<string> SplitLinesForCounting(string content)
    {
        if (content.Length == 0) return [];
        var lines = content.Split('\n').ToList();
        if (content.EndsWith('\n')) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes}B";
        if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("F1", CultureInfo.InvariantCulture) + "KB";
        return (bytes / (1024.0 * 1024.0)).ToString("F1", CultureInfo.InvariantCulture) + "MB";
    }

    /// <summary>Keep the first N lines/bytes. Never returns partial lines.</summary>
    public static TruncationResult TruncateHead(string content, long? maxLinesOption = null, int? maxBytesOption = null)
    {
        var maxLines = maxLinesOption ?? DefaultMaxLines;
        var maxBytes = maxBytesOption ?? DefaultMaxBytes;
        var totalBytes = ByteLength(content);
        var lines = SplitLinesForCounting(content);
        var totalLines = lines.Count;

        if (totalLines <= maxLines && totalBytes <= maxBytes)
        {
            return new TruncationResult
            {
                Content = content, Truncated = false, TruncatedBy = null, TotalLines = totalLines, TotalBytes = totalBytes,
                OutputLines = totalLines, OutputBytes = totalBytes, MaxLines = maxLines, MaxBytes = maxBytes,
            };
        }

        var firstLineBytes = ByteLength(lines.Count > 0 ? lines[0] : "");
        if (firstLineBytes > maxBytes)
        {
            return new TruncationResult
            {
                Content = "", Truncated = true, TruncatedBy = "bytes", TotalLines = totalLines, TotalBytes = totalBytes,
                OutputLines = 0, OutputBytes = 0, FirstLineExceedsLimit = true, MaxLines = maxLines, MaxBytes = maxBytes,
            };
        }

        var output = new List<string>();
        var outputBytesCount = 0;
        var truncatedBy = "lines";
        for (var i = 0; i < lines.Count && i < maxLines; i++)
        {
            var lineBytes = ByteLength(lines[i]) + (i > 0 ? 1 : 0);
            if (outputBytesCount + lineBytes > maxBytes)
            {
                truncatedBy = "bytes";
                break;
            }
            output.Add(lines[i]);
            outputBytesCount += lineBytes;
        }
        if (output.Count >= maxLines && outputBytesCount <= maxBytes) truncatedBy = "lines";

        var outputContent = string.Join("\n", output);
        return new TruncationResult
        {
            Content = outputContent, Truncated = true, TruncatedBy = truncatedBy, TotalLines = totalLines, TotalBytes = totalBytes,
            OutputLines = output.Count, OutputBytes = ByteLength(outputContent), MaxLines = maxLines, MaxBytes = maxBytes,
        };
    }

    /// <summary>Keep the last N lines/bytes. May return a partial first line when the last line exceeds the byte limit.</summary>
    public static TruncationResult TruncateTail(string content, long? maxLinesOption = null, int? maxBytesOption = null)
    {
        var maxLines = maxLinesOption ?? DefaultMaxLines;
        var maxBytes = maxBytesOption ?? DefaultMaxBytes;
        var totalBytes = ByteLength(content);
        var lines = SplitLinesForCounting(content);
        var totalLines = lines.Count;

        if (totalLines <= maxLines && totalBytes <= maxBytes)
        {
            return new TruncationResult
            {
                Content = content, Truncated = false, TruncatedBy = null, TotalLines = totalLines, TotalBytes = totalBytes,
                OutputLines = totalLines, OutputBytes = totalBytes, MaxLines = maxLines, MaxBytes = maxBytes,
            };
        }

        var output = new List<string>();
        var outputBytesCount = 0;
        var truncatedBy = "lines";
        var lastLinePartial = false;
        for (var i = lines.Count - 1; i >= 0 && output.Count < maxLines; i--)
        {
            var line = lines[i];
            var lineBytes = ByteLength(line) + (output.Count > 0 ? 1 : 0);
            if (outputBytesCount + lineBytes > maxBytes)
            {
                truncatedBy = "bytes";
                if (output.Count == 0)
                {
                    var truncatedLine = TruncateStringToBytesFromEnd(line, maxBytes);
                    output.Insert(0, truncatedLine);
                    outputBytesCount = ByteLength(truncatedLine);
                    lastLinePartial = true;
                }
                break;
            }
            output.Insert(0, line);
            outputBytesCount += lineBytes;
        }
        if (output.Count >= maxLines && outputBytesCount <= maxBytes) truncatedBy = "lines";

        var outputContent = string.Join("\n", output);
        return new TruncationResult
        {
            Content = outputContent, Truncated = true, TruncatedBy = truncatedBy, TotalLines = totalLines, TotalBytes = totalBytes,
            OutputLines = output.Count, OutputBytes = ByteLength(outputContent), LastLinePartial = lastLinePartial,
            MaxLines = maxLines, MaxBytes = maxBytes,
        };
    }

    private static string TruncateStringToBytesFromEnd(string str, int maxBytes)
    {
        var buf = Encoding.UTF8.GetBytes(str);
        if (buf.Length <= maxBytes) return str;
        var start = buf.Length - maxBytes;
        while (start < buf.Length && (buf[start] & 0xc0) == 0x80) start++;
        return Encoding.UTF8.GetString(buf, start, buf.Length - start);
    }

    /// <summary>Truncate a single line to max characters, adding a "... [truncated]" suffix.</summary>
    public static (string Text, bool WasTruncated) TruncateLine(string line, int maxChars = GrepMaxLineLength)
    {
        if (line.Length <= maxChars) return (line, false);
        return ($"{line[..maxChars]}... [truncated]", true);
    }
}
