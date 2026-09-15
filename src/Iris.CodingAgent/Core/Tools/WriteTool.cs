using System.Text.Json.Nodes;
using Iris.Agent;

namespace Iris.CodingAgent.Core.Tools;

/// <summary>Pluggable operations for the write tool.</summary>
public sealed class WriteOperations
{
    public Func<string, string, CancellationToken, Task> WriteFile { get; init; } = (path, content, _) =>
        File.WriteAllTextAsync(path, content);

    public Func<string, Task> Mkdir { get; init; } = dir =>
    {
        Directory.CreateDirectory(dir);
        return Task.CompletedTask;
    };
}

public static class WriteTool
{
    public const string Snippet = "Create or overwrite files";
    public static readonly string[] Guidelines = ["Use write only for new files or complete rewrites."];

    private static readonly JsonObject Schema = ToolSchema.Object([
        ("path", ToolSchema.String("Path to the file to write (relative or absolute)"), false),
        ("content", ToolSchema.String("Content to write to the file"), false),
    ]);

    public static ToolDefinition CreateDefinition(string cwd, WriteOperations? operations = null)
    {
        var ops = operations ?? new WriteOperations();
        return new ToolDefinition
        {
            Name = "write",
            Label = "write",
            Description = "Write content to a file. Creates the file if it doesn't exist, overwrites if it does. Automatically creates parent directories.",
            PromptSnippet = Snippet,
            PromptGuidelines = Guidelines,
            Parameters = Schema.DeepClone().AsObject(),
            ConstrainedSampling = ToolSchema.PreferJsonSchema,
            Execute = (_, args, ct, _, ctx) =>
            {
                var path = ToolSchema.GetString(args, "path") ?? "";
                var content = ToolSchema.GetString(args, "content") ?? "";
                var absolutePath = ToolPaths.ResolveToCwd(path, string.IsNullOrEmpty(ctx?.Cwd) ? cwd : ctx.Cwd);
                var dir = Path.GetDirectoryName(absolutePath) ?? absolutePath;
                return FileMutationQueue.RunAsync(absolutePath, async () =>
                {
                    // Check cancellation after each await instead of aborting mid-operation so the queue stays locked
                    // until the in-flight filesystem operation has settled.
                    ToolSchema.ThrowIfAborted(ct);
                    await ops.Mkdir(dir);
                    ToolSchema.ThrowIfAborted(ct);
                    await ops.WriteFile(absolutePath, content, CancellationToken.None);
                    ToolSchema.ThrowIfAborted(ct);
                    return AgentToolResult.Text($"Successfully wrote to {path}");
                });
            },
        };
    }

    public static AgentTool Create(string cwd, WriteOperations? operations = null) => CreateDefinition(cwd, operations).ToAgentTool();
}
