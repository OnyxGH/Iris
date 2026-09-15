using System.Text.Json.Nodes;
using Iris.Agent;
using Iris.Ai;
using Iris.CodingAgent.Utils;

namespace Iris.CodingAgent.Core.Tools;

/// <summary>Pluggable operations for the read tool (for example to delegate to SSH).</summary>
public sealed class ReadOperations
{
    public Func<string, CancellationToken, Task<byte[]>> ReadFile { get; init; } = async (path, ct) =>
    {
        if (Directory.Exists(path)) throw NodeCompat.IsDirectory("read");
        return await File.ReadAllBytesAsync(path, ct);
    };

    public Func<string, Task> Access { get; init; } = path =>
    {
        NodeCompat.Access(path);
        return Task.CompletedTask;
    };

    public Func<string, CancellationToken, Task<string?>>? DetectImageMimeType { get; init; } = async (path, ct) =>
        Directory.Exists(path) ? null : await MimeDetect.DetectSupportedImageMimeTypeFromFileAsync(path, ct);
}

public sealed class ReadToolOptions
{
    /// <summary>Whether to auto-resize images to 2000x2000 max. Default: true.</summary>
    public bool AutoResizeImages { get; init; } = true;

    public ReadOperations? Operations { get; init; }
}

public static class ReadTool
{
    public const string Snippet = "Read file contents";
    public static readonly string[] Guidelines = ["Use read to examine files instead of cat or sed."];

    private static readonly JsonObject Schema = ToolSchema.Object([
        ("path", ToolSchema.String("Path to the file to read (relative or absolute)"), false),
        ("offset", ToolSchema.Number("Line number to start reading from (1-indexed)"), true),
        ("limit", ToolSchema.Number("Maximum number of lines to read"), true),
    ]);

    private static string? GetNonVisionImageNote(Model? model) =>
        model is null || model.Input.Contains("image")
            ? null
            : "[Current model does not support images. The image will be omitted from this request.]";

    public static ToolDefinition CreateDefinition(string cwd, ReadToolOptions? options = null)
    {
        var autoResizeImages = options?.AutoResizeImages ?? true;
        var ops = options?.Operations ?? new ReadOperations();
        return new ToolDefinition
        {
            Name = "read",
            Label = "read",
            Description = $"Read the contents of a file. Supports text files and images (jpg, png, gif, webp, bmp). Images are sent as attachments. For text files, output is truncated to {Truncate.DefaultMaxLines} lines or {Truncate.DefaultMaxBytes / 1024}KB (whichever is hit first). Use offset/limit for large files. When you need the full file, continue with offset until complete.",
            PromptSnippet = Snippet,
            PromptGuidelines = Guidelines,
            Parameters = Schema.DeepClone().AsObject(),
            ConstrainedSampling = ToolSchema.PreferJsonSchema,
            Execute = async (_, args, ct, _, ctx) =>
            {
                ToolSchema.ThrowIfAborted(ct);
                var path = ToolSchema.GetString(args, "path") ?? "";
                var offset = ToolSchema.GetNumber(args, "offset");
                var limit = ToolSchema.GetNumber(args, "limit");

                var absolutePath = ToolPaths.ResolveReadPath(path, string.IsNullOrEmpty(ctx?.Cwd) ? cwd : ctx.Cwd);
                ToolSchema.ThrowIfAborted(ct);
                await ops.Access(absolutePath);
                ToolSchema.ThrowIfAborted(ct);

                var mimeType = ops.DetectImageMimeType is null ? null : await ops.DetectImageMimeType(absolutePath, ct);
                var nonVisionImageNote = GetNonVisionImageNote(ctx?.Model);
                var content = new List<ContentBlock>();
                JsonNode? details = null;

                if (!string.IsNullOrEmpty(mimeType))
                {
                    var buffer = await ops.ReadFile(absolutePath, ct);
                    var processed = await ImageProcessor.ProcessAsync(buffer, mimeType, autoResizeImages);
                    if (!processed.Ok)
                    {
                        var textNote = $"Read image file [{mimeType}]\n{processed.Message}";
                        if (nonVisionImageNote is not null) textNote += $"\n{nonVisionImageNote}";
                        content.Add(new TextContent(textNote));
                    }
                    else
                    {
                        var textNote = $"Read image file [{processed.MimeType}]";
                        if (processed.Hints.Count > 0) textNote += $"\n{string.Join("\n", processed.Hints)}";
                        if (nonVisionImageNote is not null) textNote += $"\n{nonVisionImageNote}";
                        content.Add(new TextContent(textNote));
                        content.Add(new ImageContent(processed.Data, processed.MimeType));
                    }
                }
                else
                {
                    var buffer = await ops.ReadFile(absolutePath, ct);
                    var textContent = System.Text.Encoding.UTF8.GetString(buffer);
                    var allLines = textContent.Split('\n');
                    var totalFileLines = allLines.Length;
                    var startLine = offset is { } o && o != 0 && !double.IsNaN(o) ? (long)Math.Max(0, o - 1) : 0;
                    var startLineDisplay = startLine + 1;
                    if (startLine >= allLines.Length)
                    {
                        throw new InvalidOperationException($"Offset {NodeCompat.FormatNumber(offset!.Value)} is beyond end of file ({allLines.Length} lines total)");
                    }

                    string selectedContent;
                    long? userLimitedLines = null;
                    if (limit is { } l)
                    {
                        var endLine = (long)Math.Max(startLine, Math.Min(startLine + l, allLines.Length));
                        selectedContent = string.Join("\n", allLines[(int)startLine..(int)endLine]);
                        userLimitedLines = endLine - startLine;
                    }
                    else
                    {
                        selectedContent = string.Join("\n", allLines[(int)startLine..]);
                    }

                    var truncation = Truncate.TruncateHead(selectedContent);
                    string outputText;
                    if (truncation.FirstLineExceedsLimit)
                    {
                        var firstLineSize = Truncate.FormatSize(Truncate.ByteLength(allLines[startLine]));
                        outputText = $"[Line {startLineDisplay} is {firstLineSize}, exceeds {Truncate.FormatSize(Truncate.DefaultMaxBytes)} limit. Use bash: sed -n '{startLineDisplay}p' {path} | head -c {Truncate.DefaultMaxBytes}]";
                        details = new JsonObject { ["truncation"] = truncation.ToJson() };
                    }
                    else if (truncation.Truncated)
                    {
                        var endLineDisplay = startLineDisplay + truncation.OutputLines - 1;
                        var nextOffset = endLineDisplay + 1;
                        outputText = truncation.Content;
                        outputText += truncation.TruncatedBy == "lines"
                            ? $"\n\n[Showing lines {startLineDisplay}-{endLineDisplay} of {totalFileLines}. Use offset={nextOffset} to continue.]"
                            : $"\n\n[Showing lines {startLineDisplay}-{endLineDisplay} of {totalFileLines} ({Truncate.FormatSize(Truncate.DefaultMaxBytes)} limit). Use offset={nextOffset} to continue.]";
                        details = new JsonObject { ["truncation"] = truncation.ToJson() };
                    }
                    else if (userLimitedLines is { } limited && startLine + limited < allLines.Length)
                    {
                        var remaining = allLines.Length - (startLine + limited);
                        var nextOffset = startLine + limited + 1;
                        outputText = $"{truncation.Content}\n\n[{remaining} more lines in file. Use offset={nextOffset} to continue.]";
                    }
                    else
                    {
                        outputText = truncation.Content;
                    }
                    content.Add(new TextContent(outputText));
                }

                ToolSchema.ThrowIfAborted(ct);
                return new AgentToolResult { Content = content, Details = details };
            },
        };
    }

    public static AgentTool Create(string cwd, ReadToolOptions? options = null) => CreateDefinition(cwd, options).ToAgentTool();
}
