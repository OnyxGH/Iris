using System.Globalization;
using Iris.Ai;
using Iris.CodingAgent.Core;
using Iris.CodingAgent.Core.Tools;
using Iris.CodingAgent.Utils;
using Iris.Tui;

namespace Iris.CodingAgent.Cli;

/// <summary>Thrown by CLI helpers to exit with an error.</summary>
public sealed class CliExitException(int exitCode, string? message = null) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}

public static class InitialMessageBuilder
{
    public static async Task<(string Text, List<ImageContent> Images)> ProcessFileArgumentsAsync(IEnumerable<string> fileArgs, bool autoResizeImages = true)
    {
        var text = new System.Text.StringBuilder();
        var images = new List<ImageContent>();
        foreach (var fileArg in fileArgs)
        {
            var absolutePath = Path.GetFullPath(ToolPaths.ResolveReadPath(fileArg, Directory.GetCurrentDirectory()));
            if (!File.Exists(absolutePath))
            {
                throw new CliExitException(1, Chalk.Red($"Error: File not found: {absolutePath}"));
            }
            if (new FileInfo(absolutePath).Length == 0) continue;

            var mimeType = await MimeDetect.DetectSupportedImageMimeTypeFromFileAsync(absolutePath);
            if (mimeType is not null)
            {
                var processed = await ImageProcessor.ProcessAsync(await File.ReadAllBytesAsync(absolutePath), mimeType, autoResizeImages);
                if (!processed.Ok)
                {
                    text.Append($"<file name=\"{absolutePath}\">{processed.Message}</file>\n");
                    continue;
                }
                images.Add(new ImageContent(processed.Data, processed.MimeType));
                text.Append(processed.Hints.Count > 0
                    ? $"<file name=\"{absolutePath}\">{string.Join("\n", processed.Hints)}</file>\n"
                    : $"<file name=\"{absolutePath}\"></file>\n");
            }
            else
            {
                try
                {
                    var content = TextHelpers.StripBom(await File.ReadAllTextAsync(absolutePath));
                    text.Append($"<file name=\"{absolutePath}\">\n{content}\n</file>\n");
                }
                catch (Exception ex)
                {
                    throw new CliExitException(1, Chalk.Red($"Error: Could not read file {absolutePath}: {ex.Message}"));
                }
            }
        }
        return (text.ToString(), images);
    }

    /// <summary>Combine stdin, @file text and the first CLI message into one initial prompt (consumes that message).</summary>
    public static (string? Message, List<ImageContent>? Images) Build(CliArgs parsed, string? fileText, List<ImageContent>? fileImages, string? stdinContent)
    {
        var parts = new List<string>();
        if (stdinContent is not null) parts.Add(stdinContent);
        if (!string.IsNullOrEmpty(fileText)) parts.Add(fileText);
        if (parsed.Messages.Count > 0)
        {
            parts.Add(parsed.Messages[0]);
            parsed.Messages.RemoveAt(0);
        }
        return (parts.Count > 0 ? string.Concat(parts) : null, fileImages is { Count: > 0 } ? fileImages : null);
    }
}

public static class ModelLister
{
    private static string FormatTokenCount(long count)
    {
        if (count >= 1_000_000)
        {
            var millions = count / 1_000_000.0;
            return millions % 1 == 0 ? $"{millions.ToString(CultureInfo.InvariantCulture)}M" : $"{millions.ToString("F1", CultureInfo.InvariantCulture)}M";
        }
        if (count >= 1_000)
        {
            var thousands = count / 1_000.0;
            return thousands % 1 == 0 ? $"{thousands.ToString(CultureInfo.InvariantCulture)}K" : $"{thousands.ToString("F1", CultureInfo.InvariantCulture)}K";
        }
        return count.ToString(CultureInfo.InvariantCulture);
    }

    public static async Task ListAsync(ModelRuntime runtime, string? searchPattern, TextWriter output, CancellationToken cancellationToken = default)
    {
        if (runtime.GetError() is { } loadError) Console.Error.WriteLine(Chalk.Yellow($"Warning: errors loading models.json:\n{loadError}"));

        var models = (await runtime.GetAvailableAsync(null, cancellationToken)).ToList();
        if (models.Count == 0)
        {
            output.WriteLine(AuthGuidance.FormatNoModelsAvailableMessage());
            return;
        }

        var filtered = string.IsNullOrEmpty(searchPattern) ? models : Fuzzy.Filter(models, searchPattern, m => $"{m.Provider} {m.Id}");
        if (filtered.Count == 0)
        {
            output.WriteLine($"No models matching \"{searchPattern}\"");
            return;
        }

        var comparer = StringComparer.Create(CultureInfo.InvariantCulture, CompareOptions.None);
        filtered = filtered.OrderBy(m => m.Provider, comparer).ThenBy(m => m.Id, comparer).ToList();

        var headers = new[] { "provider", "model", "context", "max-out", "thinking", "images" };
        var rows = filtered.Select(m => new[]
        {
            m.Provider, m.Id, FormatTokenCount(m.ContextWindow), FormatTokenCount(m.MaxTokens),
            m.Reasoning ? "yes" : "no", m.Input.Contains("image") ? "yes" : "no",
        }).ToList();
        var widths = headers.Select((h, i) => Math.Max(h.Length, rows.Max(r => r[i].Length))).ToArray();

        output.WriteLine(string.Join("  ", headers.Select((h, i) => h.PadRight(widths[i]))));
        foreach (var row in rows) output.WriteLine(string.Join("  ", row.Select((c, i) => c.PadRight(widths[i]))));
    }
}
