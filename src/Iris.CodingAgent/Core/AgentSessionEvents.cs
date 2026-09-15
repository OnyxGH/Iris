using Iris.Agent;
using Iris.Ai;
using Iris.CodingAgent.Core.Compaction;

namespace Iris.CodingAgent.Core;

/// <summary>Events emitted by AgentSession: core agent events plus session-level events.</summary>
public abstract record AgentSessionEvent(string Type);

/// <summary>A core agent event forwarded as-is (everything except agent_end).</summary>
public sealed record AgentCoreSessionEvent(AgentEvent Event) : AgentSessionEvent(Event.Type);

public sealed record SessionAgentEndEvent(IReadOnlyList<Message> Messages, bool WillRetry) : AgentSessionEvent("agent_end");

public sealed record AgentSettledEvent() : AgentSessionEvent("agent_settled");

public sealed record QueueUpdateEvent(IReadOnlyList<string> Steering, IReadOnlyList<string> FollowUp) : AgentSessionEvent("queue_update");

/// <summary>Reason is "manual" | "threshold" | "overflow".</summary>
public sealed record CompactionStartEvent(string Reason) : AgentSessionEvent("compaction_start");

public sealed record EntryAppendedEvent(SessionEntry Entry) : AgentSessionEvent("entry_appended");

public sealed record SessionInfoChangedEvent(string? Name) : AgentSessionEvent("session_info_changed");

public sealed record ThinkingLevelChangedEvent(ThinkingLevel Level) : AgentSessionEvent("thinking_level_changed");

public sealed record CompactionEndEvent(string Reason, CompactionResult? Result, bool Aborted, bool WillRetry, string? ErrorMessage = null) : AgentSessionEvent("compaction_end");

public sealed record AutoRetryStartEvent(int Attempt, int MaxAttempts, long DelayMs, string ErrorMessage) : AgentSessionEvent("auto_retry_start");

public sealed record AutoRetryEndEvent(bool Success, int Attempt, string? FinalError = null) : AgentSessionEvent("auto_retry_end");

public sealed record SummarizationRetryScheduledEvent(int Attempt, int MaxAttempts, long DelayMs, string ErrorMessage) : AgentSessionEvent("summarization_retry_scheduled");

/// <summary>Source is "branchSummary" or "compaction" (with a reason).</summary>
public sealed record SummarizationRetryAttemptStartEvent(string Source, string? Reason = null) : AgentSessionEvent("summarization_retry_attempt_start");

public sealed record SummarizationRetryFinishedEvent() : AgentSessionEvent("summarization_retry_finished");

public sealed record BashExecutionUpdateEvent(string? Id, string Delta) : AgentSessionEvent("bash_execution_update");

public sealed record ContextUsage(long? Tokens, long ContextWindow, double? Percent);

public sealed record ModelCycleResult(Model Model, ThinkingLevel ThinkingLevel, bool IsScoped);

public sealed record SessionTokenStats(long Input, long Output, long CacheRead, long CacheWrite, long Total);

public sealed record SessionStats(
    string? SessionFile,
    string SessionId,
    int UserMessages,
    int AssistantMessages,
    int ToolCalls,
    int ToolResults,
    int TotalMessages,
    SessionTokenStats Tokens,
    double Cost,
    ContextUsage? ContextUsage);

public sealed record ToolInfo(string Name, string Description, System.Text.Json.Nodes.JsonObject Parameters, IReadOnlyList<string>? PromptGuidelines, SourceInfo SourceInfo);

public sealed record ParsedSkillBlock(string Name, string Location, string Content, string? UserMessage);

public sealed class PromptOptions
{
    /// <summary>Dispatch extension commands and expand skill commands and prompt templates. Default: true.</summary>
    public bool ExpandPromptTemplates { get; init; } = true;

    public List<ImageContent>? Images { get; init; }

    /// <summary>"steer" | "followUp"; required while streaming.</summary>
    public string? StreamingBehavior { get; init; }

    /// <summary>"interactive" | "rpc" | "extension" | ...</summary>
    public string Source { get; init; } = "interactive";

    /// <summary>Observes prompt preflight acceptance (true) or rejection (false). Used by RPC mode.</summary>
    public Action<bool>? PreflightResult { get; init; }
}

public sealed record NavigateTreeResult(bool Cancelled, string? EditorText = null, bool Aborted = false, BranchSummaryEntry? SummaryEntry = null);

public static class AuthGuidance
{
    private const string UnknownProvider = "unknown";

    public static string GetProviderLoginHelp() => string.Join("\n",
        "Use /login to log into a provider via OAuth or API key. See:",
        $"  {Path.Combine(Config.AppConfig.DocsPath, "providers.md")}",
        $"  {Path.Combine(Config.AppConfig.DocsPath, "models.md")}");

    public static string FormatNoModelsAvailableMessage() => $"No models available. {GetProviderLoginHelp()}";

    public static string FormatNoModelSelectedMessage() => $"No model selected.\n\n{GetProviderLoginHelp()}\n\nThen use /model to select a model.";

    public static string FormatNoApiKeyFoundMessage(string provider) =>
        $"No API key found for {(provider == UnknownProvider ? "the selected model" : provider)}.\n\n{GetProviderLoginHelp()}";
}
