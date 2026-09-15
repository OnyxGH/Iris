using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Iris.Ai;

[JsonConverter(typeof(JsonStringEnumConverter<StopReason>))]
public enum StopReason
{
    [JsonStringEnumMemberName("pending")] Pending,
    [JsonStringEnumMemberName("stop")] Stop,
    [JsonStringEnumMemberName("length")] Length,
    [JsonStringEnumMemberName("toolUse")] ToolUse,
    [JsonStringEnumMemberName("error")] Error,
    [JsonStringEnumMemberName("aborted")] Aborted,
    [JsonStringEnumMemberName("deferred")] Deferred,
}

public sealed class UsageCost
{
    public double Input { get; set; }
    public double Output { get; set; }
    public double CacheRead { get; set; }
    public double CacheWrite { get; set; }
    public double Total { get; set; }

    public UsageCost Clone() => (UsageCost)MemberwiseClone();
}

public sealed class Usage
{
    public long Input { get; set; }
    public long Output { get; set; }
    public long CacheRead { get; set; }
    public long CacheWrite { get; set; }

    /// <summary>Subset of CacheWrite written with 1h retention. Only Anthropic reports this split.</summary>
    public long? CacheWrite1h { get; set; }

    /// <summary>Reasoning tokens (subset of Output) when the provider reports them.</summary>
    public long? Reasoning { get; set; }

    public long TotalTokens { get; set; }

    public UsageCost Cost { get; set; } = new();

    public static Usage Empty() => new();

    public Usage Clone()
    {
        var clone = (Usage)MemberwiseClone();
        clone.Cost = Cost.Clone();
        return clone;
    }
}

public sealed class DiagnosticErrorInfo
{
    public string? Name { get; set; }
    public string Message { get; set; } = "";
    public string? Stack { get; set; }
    public JsonNode? Code { get; set; }
}

public sealed class AssistantMessageDiagnostic
{
    public string Type { get; set; } = "";
    public long Timestamp { get; set; }
    public DiagnosticErrorInfo? Error { get; set; }
    public JsonObject? Details { get; set; }
}

public sealed class DeferredHandle
{
    public string Provider { get; set; } = "";
    public string ModelId { get; set; } = "";
    public string Api { get; set; } = "";
    public string Id { get; set; } = "";
    public long? ExpiresAt { get; set; }
    public long? PollAfterMs { get; set; }
    public JsonNode? Data { get; set; }
}

/// <summary>
/// Base for all messages. The "role" discriminator is resolved through <see cref="MessageTypeRegistry"/>,
/// so higher layers (agent, coding agent, extensions) can add their own message roles.
/// </summary>
public abstract class Message
{
    [JsonPropertyOrder(-100)]
    public abstract string Role { get; }

    /// <summary>Unix timestamp in milliseconds.</summary>
    [JsonPropertyOrder(100)]
    public long Timestamp { get; set; }

    public Message CloneMessage() => CloneCore();

    /// <summary>Top-level copy sharing nested content (object spread in TS).</summary>
    public Message ShallowCopy() => (Message)MemberwiseClone();

    protected virtual Message CloneCore() => (Message)MemberwiseClone();
}

public sealed class UserMessage : Message
{
    public UserMessage() { }

    public UserMessage(UserContent content, long? timestamp = null)
    {
        Content = content;
        Timestamp = timestamp ?? TimeUtil.NowMs();
    }

    public override string Role => "user";

    public UserContent Content { get; set; } = UserContent.FromText("");

    protected override Message CloneCore()
    {
        var clone = (UserMessage)base.CloneCore();
        clone.Content = Content.Clone();
        return clone;
    }
}

public sealed class AssistantMessage : Message
{
    public override string Role => "assistant";

    public List<ContentBlock> Content { get; set; } = [];

    public string Api { get; set; } = "";

    public string Provider { get; set; } = "";

    public string Model { get; set; } = "";

    /// <summary>Concrete response model when different from the requested model.</summary>
    public string? ResponseModel { get; set; }

    public string? ResponseId { get; set; }

    public string? ProviderThinkingLevel { get; set; }

    public List<AssistantMessageDiagnostic>? Diagnostics { get; set; }

    public Usage Usage { get; set; } = new();

    public StopReason StopReason { get; set; }

    public DeferredHandle? Deferred { get; set; }

    public string? ErrorMessage { get; set; }

    public string? RawStopReason { get; set; }

    public bool? EndTurn { get; set; }

    protected override Message CloneCore()
    {
        var clone = (AssistantMessage)base.CloneCore();
        clone.Content = Content.Select(b => b.Clone()).ToList();
        clone.Usage = Usage.Clone();
        clone.Diagnostics = Diagnostics?.ToList();
        return clone;
    }

    public AssistantMessage Clone() => (AssistantMessage)CloneCore();

    public static AssistantMessage CreateEmpty(Model model, StopReason stopReason = StopReason.Pending) => new()
    {
        Api = model.Api,
        Provider = model.Provider,
        Model = model.Id,
        StopReason = stopReason,
        Timestamp = TimeUtil.NowMs(),
    };
}

public sealed class ToolResultMessage : Message
{
    public override string Role => "toolResult";

    public string ToolCallId { get; set; } = "";

    public string ToolName { get; set; } = "";

    /// <summary>Text and image blocks.</summary>
    public List<ContentBlock> Content { get; set; } = [];

    public JsonNode? Details { get; set; }

    /// <summary>Usage from the tool execution itself, if available.</summary>
    public Usage? Usage { get; set; }

    /// <summary>Names from Context.Tools that became available after this result.</summary>
    public List<string>? AddedToolNames { get; set; }

    public bool IsError { get; set; }

    protected override Message CloneCore()
    {
        var clone = (ToolResultMessage)base.CloneCore();
        clone.Content = Content.Select(b => b.Clone()).ToList();
        clone.Details = Details?.DeepClone();
        return clone;
    }
}

/// <summary>A message whose role is not registered. Preserved verbatim for round-tripping.</summary>
public sealed class UnknownMessage : Message
{
    public UnknownMessage(JsonObject raw)
    {
        Raw = raw;
        Timestamp = raw["timestamp"] is JsonValue v && v.TryGetValue<long>(out var ts) ? ts : 0;
    }

    [JsonIgnore]
    public JsonObject Raw { get; }

    public override string Role => Raw["role"]?.GetValue<string>() ?? "unknown";
}
