using System.Text.Json.Nodes;
using PiSharp.Agent;
using PiSharp.Ai;

namespace PiSharp.CodingAgent.Core.Tools;

/// <summary>
/// Context passed to tool and extension handlers. Port of the ExtensionContext interface; UI-related members will be
/// added together with the extension runtime.
/// </summary>
public class ExtensionContext
{
    /// <summary>"tui" | "print" | "json" | "rpc".</summary>
    public string Mode { get; init; } = "print";

    public bool HasUI { get; init; }

    public required string Cwd { get; init; }

    public SessionManager? SessionManager { get; init; }

    public ModelRegistry? ModelRegistry { get; init; }

    public Func<Model?> GetModel { get; init; } = () => null;

    public Model? Model => GetModel();

    public Func<IReadOnlyList<ScopedModel>> GetScopedModels { get; init; } = () => [];

    public IReadOnlyList<ScopedModel> ScopedModels => GetScopedModels();

    public Func<ThinkingLevel?> GetThinkingLevel { get; init; } = () => null;

    public ThinkingLevel? ThinkingLevel => GetThinkingLevel();

    public Func<bool> IsIdle { get; init; } = () => true;

    public Func<bool> IsProjectTrusted { get; init; } = () => false;

    public Func<CancellationToken?> GetSignal { get; init; } = () => null;

    public Action Abort { get; init; } = () => { };

    public Func<bool> HasPendingMessages { get; init; } = () => false;

    public Action Shutdown { get; init; } = () => { };

    public Func<string> GetSystemPrompt { get; init; } = () => "";
}

public delegate Task<AgentToolResult> ToolDefinitionExecute(
    string toolCallId,
    JsonObject args,
    CancellationToken cancellationToken,
    AgentToolUpdateCallback? onUpdate,
    ExtensionContext? ctx);

/// <summary>
/// Definition-first tool description used by the coding agent (built-in tools and extension tools). Port of
/// ToolDefinition; renderers are attached separately by the TUI.
/// </summary>
public sealed class ToolDefinition
{
    public required string Name { get; init; }

    public required string Label { get; init; }

    public required string Description { get; init; }

    /// <summary>One-line snippet for the Available tools section of the default system prompt.</summary>
    public string? PromptSnippet { get; init; }

    /// <summary>Guideline bullets appended to the default system prompt when this tool is active.</summary>
    public IReadOnlyList<string>? PromptGuidelines { get; init; }

    public required JsonObject Parameters { get; init; }

    public ConstrainedSamplingConfig? ConstrainedSampling { get; init; }

    /// <summary>"default" | "self".</summary>
    public string? RenderShell { get; init; }

    public Func<JsonObject, JsonObject>? PrepareArguments { get; init; }

    public ToolExecutionMode? ExecutionMode { get; init; }

    public required ToolDefinitionExecute Execute { get; init; }

    /// <summary>Opaque renderer hooks (renderCall / renderResult) provided by extensions or the TUI.</summary>
    public object? Renderers { get; set; }

    /// <summary>Wrap into an AgentTool for the core runtime. Port of wrapToolDefinition.</summary>
    public AgentTool ToAgentTool(Func<ExtensionContext>? ctxFactory = null) => new()
    {
        Name = Name,
        Label = Label,
        Description = Description,
        Parameters = Parameters,
        ConstrainedSampling = ConstrainedSampling,
        PrepareArguments = PrepareArguments,
        ExecutionMode = ExecutionMode,
        Execute = (id, args, ct, onUpdate) => Execute(id, args, ct, onUpdate, ctxFactory?.Invoke()),
    };

    /// <summary>Synthesize a minimal definition from a plain AgentTool. Port of createToolDefinitionFromAgentTool.</summary>
    public static ToolDefinition FromAgentTool(AgentTool tool) => new()
    {
        Name = tool.Name,
        Label = tool.Label,
        Description = tool.Description,
        Parameters = tool.Parameters,
        ConstrainedSampling = tool.ConstrainedSampling,
        PrepareArguments = tool.PrepareArguments,
        ExecutionMode = tool.ExecutionMode,
        Execute = (id, args, ct, onUpdate, _) => tool.Execute(id, args, ct, onUpdate),
    };
}

/// <summary>Small helpers for building TypeBox-equivalent JSON schemas and reading tool arguments.</summary>
public static class ToolSchema
{
    public static JsonObject String(string description) => new() { ["type"] = "string", ["description"] = description };

    public static JsonObject Number(string description) => new() { ["type"] = "number", ["description"] = description };

    public static JsonObject Boolean(string description) => new() { ["type"] = "boolean", ["description"] = description };

    /// <summary>Type.Object(properties) with required = properties not listed as optional.</summary>
    public static JsonObject Object(IEnumerable<(string Name, JsonObject Schema, bool Optional)> properties)
    {
        var props = new JsonObject();
        var required = new JsonArray();
        foreach (var (name, schema, optional) in properties)
        {
            props[name] = schema;
            if (!optional) required.Add(name);
        }
        var result = new JsonObject { ["type"] = "object", ["properties"] = props };
        if (required.Count > 0) result["required"] = required;
        return result;
    }

    public static ConstrainedSamplingConfig PreferJsonSchema => new() { Type = "json_schema", Strict = "prefer" };

    public static string? GetString(JsonObject args, string name) =>
        args[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static double? GetNumber(JsonObject args, string name) =>
        args[name] is JsonNode node && PiSharp.Ai.Json.PiJson.TryGetNumber(node, out var d) ? d : null;

    public static bool GetBool(JsonObject args, string name) =>
        args[name] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    public static void ThrowIfAborted(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) throw new OperationAbortedException();
    }
}

/// <summary>Error thrown when a tool operation is aborted. Message matches pi's "Operation aborted".</summary>
public sealed class OperationAbortedException(string message = "Operation aborted") : Exception(message);

/// <summary>Node-style formatting and error helpers so tool output matches pi.</summary>
public static class NodeCompat
{
    /// <summary>Format a double the way JavaScript's String(number) does for common values.</summary>
    public static string FormatNumber(double value) =>
        value == Math.Floor(value) && Math.Abs(value) < 1e21
            ? ((long)value).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Number.prototype.toFixed for finite values below 1e21.</summary>
    public static string ToFixed(double value, int digits) => value.ToString("F" + digits, System.Globalization.CultureInfo.InvariantCulture);

    public static IOException NoEntry(string syscall, string path) =>
        new FileNotFoundException($"ENOENT: no such file or directory, {syscall} '{path}'", path);

    public static IOException IsDirectory(string syscall) => new($"EISDIR: illegal operation on a directory, {syscall}");

    /// <summary>Throw ENOENT when the path does not exist (fs.access equivalent).</summary>
    public static void Access(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) throw NoEntry("access", path);
    }

    /// <summary>Read a file as UTF-8 without stripping the BOM (Buffer.toString("utf-8") equivalent).</summary>
    public static async Task<string> ReadUtf8Async(string path, CancellationToken cancellationToken = default)
    {
        if (Directory.Exists(path)) throw IsDirectory("read");
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }
}
