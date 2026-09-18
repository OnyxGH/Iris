using System.Text.Json.Nodes;
using Iris.Ai;
using Iris.Ai.Providers;

namespace Iris.Agent;

/// <summary>
/// Stream function used by the agent loop. Must not throw for request/model/runtime failures; failures are encoded
/// in the returned stream.
/// </summary>
public delegate Task<AssistantMessageEventStream> StreamFn(Model model, Context context, SimpleStreamOptions? options);

public enum ToolExecutionMode
{
    Sequential,
    Parallel,
}

public enum QueueMode
{
    All,
    OneAtATime,
}

public static class QueueModes
{
    public static string ToWire(this QueueMode mode) => mode == QueueMode.All ? "all" : "one-at-a-time";

    public static QueueMode Parse(string? value) => value == "all" ? QueueMode.All : QueueMode.OneAtATime;
}

/// <summary>Final or partial result produced by a tool.</summary>
public sealed class AgentToolResult
{
    public List<ContentBlock> Content { get; set; } = [];

    /// <summary>Arbitrary structured details for logs or UI rendering.</summary>
    public JsonNode? Details { get; set; }

    public Usage? Usage { get; set; }

    public List<string>? AddedToolNames { get; set; }

    /// <summary>Hint that the agent should stop after the current tool batch.</summary>
    public bool? Terminate { get; set; }

    public static AgentToolResult Text(string text, JsonNode? details = null) =>
        new() { Content = [new TextContent(text)], Details = details };
}

public delegate void AgentToolUpdateCallback(AgentToolResult partialResult);

public delegate Task<AgentToolResult> AgentToolExecute(string toolCallId, JsonObject args, CancellationToken cancellationToken, AgentToolUpdateCallback? onUpdate);

/// <summary>Tool definition used by the agent runtime.</summary>
public class AgentTool : Tool
{
    /// <summary>Human-readable label for UI display.</summary>
    public string Label { get; set; } = "";

    /// <summary>Optional compatibility shim for raw tool-call arguments before schema validation.</summary>
    public Func<JsonObject, JsonObject>? PrepareArguments { get; set; }

    /// <summary>Execute the tool call. Throw on failure instead of encoding errors in content.</summary>
    public required AgentToolExecute Execute { get; set; }

    /// <summary>"never" | "safe".</summary>
    public string? Replay { get; set; }

    /// <summary>Per-tool execution mode override.</summary>
    public ToolExecutionMode? ExecutionMode { get; set; }
}

/// <summary>Context snapshot passed into the low-level agent loop.</summary>
public sealed class AgentContext
{
    public string SystemPrompt { get; set; } = "";

    public List<Message> Messages { get; set; } = [];

    public List<AgentTool>? Tools { get; set; }
}

public sealed class BeforeToolCallResult
{
    public bool Block { get; init; }
    public string? Reason { get; init; }
    public bool? Terminate { get; init; }
}

public sealed class AfterToolCallResult
{
    public List<ContentBlock>? Content { get; init; }
    public JsonNode? Details { get; init; }
    public bool? IsError { get; init; }
    public Usage? Usage { get; init; }
    public bool? Terminate { get; init; }
}

public sealed record BeforeToolCallContext(AssistantMessage AssistantMessage, ToolCall ToolCall, JsonObject Args, AgentContext Context);

public sealed record AfterToolCallContext(AssistantMessage AssistantMessage, ToolCall ToolCall, JsonObject Args, AgentToolResult Result, bool IsError, AgentContext Context);

public record ShouldStopAfterTurnContext(AssistantMessage Message, IReadOnlyList<ToolResultMessage> ToolResults, AgentContext Context, IReadOnlyList<Message> NewMessages);

public sealed record PrepareNextTurnContext(AssistantMessage Message, IReadOnlyList<ToolResultMessage> ToolResults, AgentContext Context, IReadOnlyList<Message> NewMessages)
    : ShouldStopAfterTurnContext(Message, ToolResults, Context, NewMessages);

/// <summary>Replacement runtime state used by the agent loop before starting another provider request.</summary>
public sealed class AgentLoopTurnUpdate
{
    public AgentContext? Context { get; init; }
    public Model? Model { get; init; }
    public ThinkingLevel? ThinkingLevel { get; init; }
}

public sealed class AgentLoopConfig : SimpleStreamOptions
{
    public required Model Model { get; set; }

    /// <summary>Converts agent messages to LLM messages before each call. Must not throw.</summary>
    public required Func<IReadOnlyList<Message>, Task<List<Message>>> ConvertToLlm { get; set; }

    public Func<List<Message>, CancellationToken, Task<List<Message>>>? TransformContext { get; set; }

    public Func<string, Task<string?>>? GetApiKey { get; set; }

    public Func<ShouldStopAfterTurnContext, Task<bool>>? ShouldStopAfterTurn { get; set; }

    public Func<PrepareNextTurnContext, Task<AgentLoopTurnUpdate?>>? PrepareNextTurn { get; set; }

    public Func<Task<List<Message>>>? GetSteeringMessages { get; set; }

    public Func<Task<List<Message>>>? GetFollowUpMessages { get; set; }

    /// <summary>Default: parallel.</summary>
    public ToolExecutionMode? ToolExecution { get; set; }

    public Func<BeforeToolCallContext, CancellationToken, Task<BeforeToolCallResult?>>? BeforeToolCall { get; set; }

    public Func<AfterToolCallContext, CancellationToken, Task<AfterToolCallResult?>>? AfterToolCall { get; set; }

    public AgentLoopConfig CloneConfig() => (AgentLoopConfig)ShallowClone();
}

/// <summary>Events emitted by the agent for UI updates.</summary>
public abstract record AgentEvent(string Type);

public sealed record AgentStartEvent() : AgentEvent("agent_start");

public sealed record AgentEndEvent(IReadOnlyList<Message> Messages) : AgentEvent("agent_end");

public sealed record TurnStartEvent() : AgentEvent("turn_start");

public sealed record TurnEndEvent(Message Message, IReadOnlyList<ToolResultMessage> ToolResults) : AgentEvent("turn_end");

public sealed record MessageStartEvent(Message Message) : AgentEvent("message_start");

public sealed record MessageUpdateEvent(Message Message, AssistantMessageEvent AssistantMessageEvent) : AgentEvent("message_update");

public sealed record MessageEndEvent(Message Message) : AgentEvent("message_end");

public sealed record ToolExecutionStartEvent(string ToolCallId, string ToolName, JsonObject Args) : AgentEvent("tool_execution_start");

public sealed record ToolExecutionUpdateEvent(string ToolCallId, string ToolName, JsonObject Args, AgentToolResult PartialResult) : AgentEvent("tool_execution_update");

public sealed record ToolExecutionEndEvent(string ToolCallId, string ToolName, AgentToolResult Result, bool IsError) : AgentEvent("tool_execution_end");

/// <summary>Configure the fallback stream function used when callers omit one.</summary>
public static class DefaultStreamFn
{
    private static StreamFn? _default;

    public static void Set(StreamFn? streamFn) => _default = streamFn;

    /// <summary>The configured fallback, or null when none has been set.</summary>
    public static StreamFn? Current => _default;

    public static StreamFn Get() =>
        _default ?? throw new InvalidOperationException("No default stream function configured. Pass streamFn explicitly or call DefaultStreamFn.Set().");

    /// <summary>Adapter for synchronous stream implementations such as <see cref="IApiStreams.StreamSimple"/>.</summary>
    public static StreamFn FromSync(Func<Model, Context, SimpleStreamOptions?, AssistantMessageEventStream> fn) =>
        (model, context, options) => Task.FromResult(fn(model, context, options));
}
