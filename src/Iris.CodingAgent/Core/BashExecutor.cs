using System.Security.Cryptography;
using System.Text;
using Iris.CodingAgent.Core.Tools;
using Iris.CodingAgent.Utils;

namespace Iris.CodingAgent.Core;

public sealed class BashResult
{
    /// <summary>Combined stdout + stderr (sanitized, possibly truncated).</summary>
    public string Output { get; init; } = "";

    /// <summary>Exit code; null when killed or cancelled.</summary>
    public int? ExitCode { get; init; }

    public bool Cancelled { get; init; }
    public bool Truncated { get; init; }

    /// <summary>Temp file with the full output when it exceeded the truncation threshold.</summary>
    public string? FullOutputPath { get; init; }
}

/// <summary>User-initiated bash execution (the "!" prompt prefix) with streaming.</summary>
public static class BashExecutor
{
    public static async Task<BashResult> ExecuteWithOperationsAsync(
        string command, string cwd, IBashOperations operations, Action<string>? onChunk = null, CancellationToken cancellationToken = default)
    {
        var chunks = new LinkedList<string>();
        long outputChars = 0;
        const long maxOutputChars = Truncate.DefaultMaxBytes * 2L;
        string? tempFilePath = null;
        StreamWriter? tempFile = null;
        long totalBytes = 0;
        var decoder = new UTF8Encoding(false, false).GetDecoder();
        var gate = new object();

        void EnsureTempFile()
        {
            if (tempFilePath is not null) return;
            tempFilePath = Path.Combine(Path.GetTempPath(), $"iris-bash-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8))}.log");
            tempFile = new StreamWriter(tempFilePath, false, new UTF8Encoding(false));
            foreach (var chunk in chunks) tempFile.Write(chunk);
        }

        void OnData(ReadOnlyMemory<byte> data)
        {
            lock (gate)
            {
                totalBytes += data.Length;
                var chars = new char[decoder.GetCharCount(data.Span, flush: false)];
                var count = decoder.GetChars(data.Span, chars, flush: false);
                var text = ShellUtils.SanitizeBinaryOutput(AnsiUtils.StripAnsi(new string(chars, 0, count))).Replace("\r", "");

                if (totalBytes > Truncate.DefaultMaxBytes) EnsureTempFile();
                tempFile?.Write(text);

                chunks.AddLast(text);
                outputChars += text.Length;
                while (outputChars > maxOutputChars && chunks.Count > 1)
                {
                    outputChars -= chunks.First!.Value.Length;
                    chunks.RemoveFirst();
                }
                onChunk?.Invoke(text);
            }
        }

        BashResult Finish(int? exitCode, bool cancelled)
        {
            lock (gate)
            {
                var fullOutput = string.Concat(chunks);
                var truncation = Truncate.TruncateTail(fullOutput);
                if (truncation.Truncated) EnsureTempFile();
                tempFile?.Dispose();
                tempFile = null;
                return new BashResult
                {
                    Output = truncation.Truncated ? truncation.Content : fullOutput,
                    ExitCode = cancelled ? null : exitCode,
                    Cancelled = cancelled,
                    Truncated = truncation.Truncated,
                    FullOutputPath = tempFilePath,
                };
            }
        }

        try
        {
            var exitCode = await operations.ExecAsync(command, cwd, new ShellExecOptions { OnData = OnData, CancellationToken = cancellationToken });
            return Finish(exitCode, cancellationToken.IsCancellationRequested);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            return Finish(null, true);
        }
        finally
        {
            lock (gate) tempFile?.Dispose();
        }
    }
}
