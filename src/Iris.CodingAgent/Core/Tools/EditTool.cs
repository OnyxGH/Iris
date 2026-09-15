using System.Text.Json;
using System.Text.Json.Nodes;
using Iris.Agent;
using Iris.CodingAgent.Utils;

namespace Iris.CodingAgent.Core.Tools;

/// <summary>Pluggable operations for the edit tool.</summary>
public sealed class EditOperations
{
    public Func<string, CancellationToken, Task<byte[]>> ReadFile { get; init; } = (path, ct) => File.ReadAllBytesAsync(path, ct);

    public Func<string, string, CancellationToken, Task> WriteFile { get; init; } = (path, content, _) => File.WriteAllTextAsync(path, content);

    /// <summary>Check that the file is readable and writable; throw otherwise.</summary>
    public Func<string, Task> Access { get; init; } = path =>
    {
        if (!File.Exists(path) && !Directory.Exists(path)) throw new EditAccessException("ENOENT");
        if (File.Exists(path) && new FileInfo(path).IsReadOnly) throw new EditAccessException("EACCES");
        return Task.CompletedTask;
    };
}

/// <summary>Access failure carrying a Node-style error code.</summary>
public sealed class EditAccessException(string code) : IOException(code)
{
    public string Code { get; } = code;
}

/// <summary>Port of core/tools/edit.ts.</summary>
public static class EditTool
{
    public const string Snippet = "Make precise file edits with exact text replacement, including multiple disjoint edits in one call";

    public static readonly string[] Guidelines =
    [
        "Use edit for precise changes (edits[].oldText must match exactly)",
        "When changing multiple separate locations in one file, use one edit call with multiple entries in edits[] instead of multiple edit calls",
        "Each edits[].oldText is matched against the original file, not after earlier edits are applied. Do not emit overlapping or nested edits. Merge nearby changes into one edit.",
        "Keep edits[].oldText as small as possible while still being unique in the file. Do not pad with large unchanged regions.",
    ];

    private static readonly JsonObject Schema = ToolSchema.Object([
        ("path", ToolSchema.String("Path to the file to edit (relative or absolute)"), false),
        ("edits", new JsonObject
        {
            ["type"] = "array",
            ["items"] = ToolSchema.Object([
                ("oldText", ToolSchema.String("Exact text for one targeted replacement. It must be unique in the original file and must not overlap with any other edits[].oldText in the same call."), false),
                ("newText", ToolSchema.String("Replacement text for this targeted edit."), false),
            ]),
            ["description"] = "One or more targeted replacements. Each edit is matched against the original file, not incrementally. Do not include overlapping or nested edits. If two changes touch the same block or nearby lines, merge them into one edit instead.",
        }, false),
    ]);

    private static bool IsSingleEditInput(JsonNode? value) =>
        value is JsonObject obj && obj["oldText"] is JsonValue o && o.GetValueKind() == JsonValueKind.String
        && obj["newText"] is JsonValue n && n.GetValueKind() == JsonValueKind.String;

    /// <summary>Compatibility shim for models that send edits as a JSON string, a single object, or legacy top-level oldText/newText.</summary>
    public static JsonObject PrepareArguments(JsonObject args)
    {
        if (args["edits"] is JsonValue editsValue && editsValue.GetValueKind() == JsonValueKind.String)
        {
            try
            {
                var parsed = JsonNode.Parse(editsValue.GetValue<string>());
                if (parsed is JsonArray) args["edits"] = parsed;
                else if (IsSingleEditInput(parsed)) args["edits"] = new JsonArray(parsed);
            }
            catch (JsonException)
            {
            }
        }
        else if (IsSingleEditInput(args["edits"]))
        {
            var single = args["edits"]!;
            args.Remove("edits");
            args["edits"] = new JsonArray(single);
        }

        if (args["oldText"] is not JsonValue oldText || oldText.GetValueKind() != JsonValueKind.String
            || args["newText"] is not JsonValue newText || newText.GetValueKind() != JsonValueKind.String)
        {
            return args;
        }

        var edits = args["edits"] is JsonArray existing ? existing.DeepClone().AsArray() : new JsonArray();
        edits.Add(new JsonObject { ["oldText"] = oldText.GetValue<string>(), ["newText"] = newText.GetValue<string>() });
        var result = new JsonObject();
        foreach (var (key, value) in args)
        {
            if (key is "oldText" or "newText" or "edits") continue;
            result[key] = value?.DeepClone();
        }
        result["edits"] = edits;
        return result;
    }

    public static ToolDefinition CreateDefinition(string cwd, EditOperations? operations = null)
    {
        var ops = operations ?? new EditOperations();
        return new ToolDefinition
        {
            Name = "edit",
            Label = "edit",
            Description = "Edit a single file using exact text replacement. Every edits[].oldText must match a unique, non-overlapping region of the original file. If two changes affect the same block or nearby lines, merge them into one edit instead of emitting overlapping edits. Do not include large unchanged regions just to connect distant changes.",
            PromptSnippet = Snippet,
            PromptGuidelines = Guidelines,
            Parameters = Schema.DeepClone().AsObject(),
            ConstrainedSampling = ToolSchema.PreferJsonSchema,
            RenderShell = "self",
            PrepareArguments = PrepareArguments,
            Execute = (_, args, ct, _, ctx) =>
            {
                if (args["edits"] is not JsonArray editsArray || editsArray.Count == 0)
                {
                    throw new InvalidOperationException("Edit tool input is invalid. edits must contain at least one replacement.");
                }
                var path = ToolSchema.GetString(args, "path") ?? "";
                var edits = editsArray.Select(e => new EditReplacement(
                    e?["oldText"]?.GetValue<string>() ?? "",
                    e?["newText"]?.GetValue<string>() ?? "")).ToList();
                var absolutePath = ToolPaths.ResolveToCwd(path, string.IsNullOrEmpty(ctx?.Cwd) ? cwd : ctx.Cwd);

                return FileMutationQueue.RunAsync(absolutePath, async () =>
                {
                    ToolSchema.ThrowIfAborted(ct);
                    try
                    {
                        await ops.Access(absolutePath);
                    }
                    catch (Exception ex)
                    {
                        ToolSchema.ThrowIfAborted(ct);
                        var errorMessage = ex is EditAccessException access ? $"Error code: {access.Code}" : $"Error: {ex.Message}";
                        throw new InvalidOperationException($"Could not edit file: {path}. {errorMessage}.");
                    }
                    ToolSchema.ThrowIfAborted(ct);

                    if (Directory.Exists(absolutePath)) throw NodeCompat.IsDirectory("read");
                    var buffer = await ops.ReadFile(absolutePath, CancellationToken.None);
                    var rawContent = System.Text.Encoding.UTF8.GetString(buffer);
                    ToolSchema.ThrowIfAborted(ct);

                    var (bom, content) = TextHelpers.SplitBom(rawContent);
                    var originalEnding = EditDiff.DetectLineEnding(content);
                    var normalizedContent = EditDiff.NormalizeToLF(content);
                    var (baseContent, newContent) = EditDiff.ApplyEditsToNormalizedContent(normalizedContent, edits, path);
                    ToolSchema.ThrowIfAborted(ct);

                    var finalContent = bom + EditDiff.RestoreLineEndings(newContent, originalEnding);
                    await ops.WriteFile(absolutePath, finalContent, CancellationToken.None);
                    ToolSchema.ThrowIfAborted(ct);

                    var diffResult = EditDiff.GenerateDiffString(baseContent, newContent);
                    var patch = EditDiff.GenerateUnifiedPatch(path, baseContent, newContent);
                    var details = new JsonObject { ["diff"] = diffResult.Diff, ["patch"] = patch };
                    if (diffResult.FirstChangedLine is { } first) details["firstChangedLine"] = first;
                    return AgentToolResult.Text($"Successfully replaced {edits.Count} block(s) in {path}.", details);
                });
            },
        };
    }

    public static AgentTool Create(string cwd, EditOperations? operations = null) => CreateDefinition(cwd, operations).ToAgentTool();
}
