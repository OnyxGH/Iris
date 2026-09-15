using System.Text.Json.Nodes;
using Iris.Agent;
using Iris.Ai;

namespace Iris.Extensions;

/// <summary>Base type of all events delivered to extensions. <see cref="Type"/> is the wire name (e.g. "tool_call").</summary>
public abstract record ExtensionEvent(string Type);

/// <summary>An event whose handlers may return a result of type <typeparamref name="TResult"/>.</summary>
public abstract record ExtensionEvent<TResult>(string Type) : ExtensionEvent(Type) where TResult : class;

// ----- Session lifecycle -----

/// <summary>Reason: "startup" | "reload" | "new" | "resume" | "fork".</summary>
public sealed record SessionStartEvent(string Reason) : ExtensionEvent("session_start");

public sealed record SessionInfoChangedEvent(string? Name) : ExtensionEvent("session_info_changed");

/// <summary>Reason: "quit" | "new" | "resume" | "fork" | "reload".</summary>
public sealed record SessionShutdownEvent(string Reason, string? TargetSessionFile) : ExtensionEvent("session_shutdown");

/// <summary>Cancel a switch to another session (/new, /resume).</summary>
public sealed record SessionBeforeSwitchResult(bool Cancel = false);

/// <summary>Reason: "new" | "resume" | "import".</summary>
public sealed record SessionBeforeSwitchEvent(string Reason, string? TargetSessionFile) : ExtensionEvent<SessionBeforeSwitchResult>("session_before_switch");

public sealed record SessionBeforeForkResult(bool Cancel = false);

public sealed record SessionBeforeForkEvent(string EntryId, string Position) : ExtensionEvent<SessionBeforeForkResult>("session_before_fork");

public sealed record SessionBeforeCompactResult(bool Cancel = false);

/// <summary>Reason: "manual" | "threshold" | "overflow".</summary>
public sealed record SessionBeforeCompactEvent(string? CustomInstructions, string Reason, bool WillRetry) : ExtensionEvent<SessionBeforeCompactResult>("session_before_compact");

public sealed record SessionCompactEvent(Iris.CodingAgent.Core.CompactionEntry? CompactionEntry, bool FromExtension, string Reason) : ExtensionEvent("session_compact");

public sealed record SessionCompactFailedEvent(string Reason, string? ErrorMessage, bool Aborted, bool WillRetry, bool FromExtension) : ExtensionEvent("session_compact_failed");

public sealed record SessionBeforeTreeResult(bool Cancel = false);

public sealed record SessionBeforeTreeEvent(string TargetId, string? OldLeafId) : ExtensionEvent<SessionBeforeTreeResult>("session_before_tree");

public sealed record SessionTreeEvent(string? NewLeafId, string? OldLeafId, bool FromExtension) : ExtensionEvent("session_tree");

// ----- Model -----

/// <summary>Source: "set" | "cycle" | "restore".</summary>
public sealed record ModelSelectEvent(Model Model, Model? PreviousModel, string Source) : ExtensionEvent("model_select");

public sealed record ThinkingLevelSelectEvent(ThinkingLevel Level, ThinkingLevel PreviousLevel) : ExtensionEvent("thinking_level_select");

// ----- Agent loop -----

public sealed record AgentStartedEvent() : ExtensionEvent("agent_start");

public sealed record AgentEndedEvent(IReadOnlyList<Message> Messages) : ExtensionEvent("agent_end");

/// <summary>The agent finished and all follow-up work (queued messages, retries, compaction) has settled.</summary>
public sealed record AgentIdleEvent() : ExtensionEvent("agent_settled");

public sealed record TurnStartedEvent(int TurnIndex) : ExtensionEvent("turn_start");

public sealed record TurnEndedEvent(int TurnIndex, Message Message, IReadOnlyList<ToolResultMessage> ToolResults) : ExtensionEvent("turn_end");

public sealed record MessageStartedEvent(Message Message) : ExtensionEvent("message_start");

public sealed record MessageUpdatedEvent(Message Message, AssistantMessageEvent AssistantMessageEvent) : ExtensionEvent("message_update");

/// <summary>Return a replacement message with the same role to change what is stored.</summary>
public sealed record MessageEndResult(Message Message);

public sealed record MessageEndedEvent(Message Message) : ExtensionEvent<MessageEndResult>("message_end");

public sealed record ToolExecutionStartedEvent(string ToolCallId, string ToolName, JsonObject Args) : ExtensionEvent("tool_execution_start");

public sealed record ToolExecutionUpdatedEvent(string ToolCallId, string ToolName, JsonObject Args, AgentToolResult PartialResult) : ExtensionEvent("tool_execution_update");

public sealed record ToolExecutionEndedEvent(string ToolCallId, string ToolName, AgentToolResult Result, bool IsError) : ExtensionEvent("tool_execution_end");

// ----- Hooks -----

/// <summary>Block a tool call before it runs; the first blocking handler wins.</summary>
public sealed record ToolCallResult(bool Block, string? Reason = null)
{
    public static ToolCallResult Blocked(string? reason = null) => new(true, reason);
}

public sealed record ToolCallEvent(string ToolName, string ToolCallId, JsonObject Input) : ExtensionEvent<ToolCallResult>("tool_call");

/// <summary>Null properties keep the current values; modifications chain across handlers.</summary>
public sealed record ToolResultResult(List<ContentBlock>? Content = null, JsonNode? Details = null, bool? IsError = null);

public sealed record ToolResultEvent(string ToolName, string ToolCallId, JsonObject Input, List<ContentBlock> Content, JsonNode? Details, bool IsError) : ExtensionEvent<ToolResultResult>("tool_result");

/// <summary>Action: "continue" | "transform" | "handled".</summary>
public sealed record InputResult(string Action, string? Text = null, List<ImageContent>? Images = null)
{
    public static InputResult Continue { get; } = new("continue");

    public static InputResult Handled { get; } = new("handled");

    public static InputResult Transform(string text, List<ImageContent>? images = null) => new("transform", text, images);
}

/// <summary>Source: "interactive" | "rpc" | "extension" | ...</summary>
public sealed record InputEvent(string Text, IReadOnlyList<ImageContent>? Images, string Source) : ExtensionEvent<InputResult>("input");

public sealed record InjectedMessage(string CustomType, string Content, bool Display = true, JsonNode? Details = null);

/// <summary>Inject messages before the prompt and/or replace the system prompt for this run.</summary>
public sealed record BeforeAgentStartResult(List<InjectedMessage>? Messages = null, string? SystemPrompt = null);

public sealed record BeforeAgentStartEvent(string Prompt, IReadOnlyList<ImageContent>? Images, string SystemPrompt) : ExtensionEvent<BeforeAgentStartResult>("before_agent_start");

/// <summary>Replace the messages sent to the model for this request.</summary>
public sealed record ContextResult(List<Message> Messages);

public sealed record ContextEvent(IReadOnlyList<Message> Messages) : ExtensionEvent<ContextResult>("context");

public sealed record ResourcesDiscoverResult(List<string>? SkillPaths = null, List<string>? PromptPaths = null, List<string>? ThemePaths = null);

/// <summary>Reason: "startup" | "reload".</summary>
public sealed record ResourcesDiscoverEvent(string Cwd, string Reason) : ExtensionEvent<ResourcesDiscoverResult>("resources_discover");
