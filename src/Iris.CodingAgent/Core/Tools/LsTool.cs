using System.Globalization;
using System.Text.Json.Nodes;
using Iris.Agent;

namespace Iris.CodingAgent.Core.Tools;

/// <summary>Pluggable operations for the ls tool.</summary>
public sealed class LsOperations
{
    public Func<string, Task<bool>> Exists { get; init; } = path => Task.FromResult(ToolPaths.PathExists(path));

    /// <summary>Returns whether the path is a directory; throws when it does not exist.</summary>
    public Func<string, Task<bool>> IsDirectory { get; init; } = path =>
    {
        if (Directory.Exists(path)) return Task.FromResult(true);
        if (File.Exists(path)) return Task.FromResult(false);
        throw NodeCompat.NoEntry("stat", path);
    };

    public Func<string, Task<List<string>>> ReadDir { get; init; } = path =>
        Task.FromResult(new DirectoryInfo(path).EnumerateFileSystemInfos().Select(e => e.Name).ToList());
}

public static class LsTool
{
    public const string Snippet = "List directory contents";
    private const int DefaultLimit = 500;

    private static readonly JsonObject Schema = ToolSchema.Object([
        ("path", ToolSchema.String("Directory to list (default: current directory)"), true),
        ("limit", ToolSchema.Number("Maximum number of entries to return (default: 500)"), true),
    ]);

    public static ToolDefinition CreateDefinition(string cwd, LsOperations? operations = null)
    {
        var ops = operations ?? new LsOperations();
        return new ToolDefinition
        {
            Name = "ls",
            Label = "ls",
            Description = $"List directory contents. Returns entries sorted alphabetically, with '/' suffix for directories. Includes dotfiles. Output is truncated to {DefaultLimit} entries or {Truncate.DefaultMaxBytes / 1024}KB (whichever is hit first).",
            PromptSnippet = Snippet,
            Parameters = Schema.DeepClone().AsObject(),
            Execute = async (_, args, ct, _, ctx) =>
            {
                ToolSchema.ThrowIfAborted(ct);
                var path = ToolSchema.GetString(args, "path");
                var limitArg = ToolSchema.GetNumber(args, "limit");
                var dirPath = ToolPaths.ResolveToCwd(string.IsNullOrEmpty(path) ? "." : path, string.IsNullOrEmpty(ctx?.Cwd) ? cwd : ctx.Cwd);
                var effectiveLimit = limitArg ?? DefaultLimit;

                if (!await ops.Exists(dirPath)) throw new InvalidOperationException($"Path not found: {dirPath}");
                if (!await ops.IsDirectory(dirPath)) throw new InvalidOperationException($"Not a directory: {dirPath}");

                List<string> entries;
                try
                {
                    entries = await ops.ReadDir(dirPath);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"Cannot read directory: {ex.Message}");
                }
                ToolSchema.ThrowIfAborted(ct);

                // Sort alphabetically, case-insensitive (localeCompare on lowercased names).
                var comparer = StringComparer.Create(CultureInfo.InvariantCulture, CompareOptions.None);
                entries.Sort((a, b) => comparer.Compare(a.ToLowerInvariant(), b.ToLowerInvariant()));

                var results = new List<string>();
                var entryLimitReached = false;
                foreach (var entry in entries)
                {
                    if (results.Count >= effectiveLimit)
                    {
                        entryLimitReached = true;
                        break;
                    }
                    string suffix;
                    try
                    {
                        suffix = await ops.IsDirectory(Path.Combine(dirPath, entry)) ? "/" : "";
                    }
                    catch
                    {
                        continue;
                    }
                    results.Add(entry + suffix);
                }
                ToolSchema.ThrowIfAborted(ct);

                if (results.Count == 0) return AgentToolResult.Text("(empty directory)");

                var truncation = Truncate.TruncateHead(string.Join("\n", results), Truncate.Unlimited);
                var output = truncation.Content;
                var details = new JsonObject();
                var notices = new List<string>();
                if (entryLimitReached)
                {
                    var limitText = NodeCompat.FormatNumber(effectiveLimit);
                    notices.Add($"{limitText} entries limit reached. Use limit={NodeCompat.FormatNumber(effectiveLimit * 2)} for more");
                    details["entryLimitReached"] = effectiveLimit;
                }
                if (truncation.Truncated)
                {
                    notices.Add($"{Truncate.FormatSize(Truncate.DefaultMaxBytes)} limit reached");
                    details["truncation"] = truncation.ToJson();
                }
                if (notices.Count > 0) output += $"\n\n[{string.Join(". ", notices)}]";
                return AgentToolResult.Text(output, details.Count > 0 ? details : null);
            },
        };
    }

    public static AgentTool Create(string cwd, LsOperations? operations = null) => CreateDefinition(cwd, operations).ToAgentTool();
}
