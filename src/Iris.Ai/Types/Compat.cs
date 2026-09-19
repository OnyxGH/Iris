using System.Text.Json.Nodes;

namespace Iris.Ai;

/// <summary>Compatibility settings for OpenAI-compatible completions APIs (model.compat).</summary>
public sealed class OpenAICompletionsCompat
{
    public bool? SupportsStore { get; set; }
    public bool? SupportsDeveloperRole { get; set; }
    public bool? SupportsReasoningEffort { get; set; }
    public bool? SupportsUsageInStreaming { get; set; }
    public bool? SupportsFinishReason { get; set; }
    /// <summary>"max_completion_tokens" or "max_tokens".</summary>
    public string? MaxTokensField { get; set; }
    public bool? RequiresToolResultName { get; set; }
    public bool? RequiresAssistantAfterToolResult { get; set; }
    public bool? RequiresThinkingAsText { get; set; }
    public bool? RequiresReasoningContentOnAssistantMessages { get; set; }
    /// <summary>openai | openrouter | deepseek | together | baseten | zai | qwen | chat-template | qwen-chat-template | string-thinking | ant-ling.</summary>
    public string? ThinkingFormat { get; set; }
    /// <summary>Values are literals or {"$var": "thinking.enabled|thinking.effort|thinking.budget", "omitWhenOff": bool}.</summary>
    public JsonObject? ChatTemplateKwargs { get; set; }
    public JsonObject? ChatTemplateArgs { get; set; }
    public JsonObject? OpenRouterRouting { get; set; }
    public VercelGatewayRouting? VercelGatewayRouting { get; set; }
    public bool? ZaiToolStream { get; set; }
    /// <summary>"thinking_token_budget" | "thinking_budget" | "thinking_budget_tokens".</summary>
    public string? ThinkingTokenBudgetField { get; set; }
    public bool? SupportsThinkingTokenBudget { get; set; }
    /// <summary>
    /// Default thinking token budget per level ("minimal".."xhigh") for thinkingTokenBudgetField; a null value sends no
    /// budget for that level. User thinkingBudgets settings take precedence.
    /// </summary>
    public JsonObject? ThinkingTokenBudgets { get; set; }
    public bool? SupportsOpenAIGrammarTools { get; set; }
    public bool? SupportsStrictMode { get; set; }
    /// <summary>"anthropic".</summary>
    public string? CacheControlFormat { get; set; }
    public bool? SendSessionAffinityHeaders { get; set; }
    /// <summary>"kimi".</summary>
    public string? DeferredToolsMode { get; set; }
    /// <summary>"openai" | "openai-nosession" | "openrouter".</summary>
    public string? SessionAffinityFormat { get; set; }
    public bool? SupportsLongCacheRetention { get; set; }
    public double? VllmPriority { get; set; }
}

public sealed class VercelGatewayRouting
{
    public List<string>? Only { get; set; }
    public List<string>? Order { get; set; }
}

/// <summary>Compatibility settings for OpenAI Responses APIs.</summary>
public sealed class OpenAIResponsesCompat
{
    public bool? SupportsDeveloperRole { get; set; }
    public string? SessionAffinityFormat { get; set; }
    public bool? SupportsLongCacheRetention { get; set; }
    public bool? SupportsStrictMode { get; set; }
    public bool? SupportsOpenAIGrammarTools { get; set; }
    public bool? SupportsAdditionalTools { get; set; }
    public bool? SupportsToolSearch { get; set; }
    public bool? SupportsExplicitPromptCacheMode { get; set; }
    public bool? SupportsMaxOutputTokens { get; set; }
}

public sealed class AnthropicAllowedFallbackModel
{
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    public ModelCost Cost { get; set; } = new();
}

/// <summary>Compatibility settings for Anthropic Messages-compatible APIs.</summary>
public sealed class AnthropicMessagesCompat
{
    public bool? SupportsEagerToolInputStreaming { get; set; }
    public bool? SupportsLongCacheRetention { get; set; }
    public bool? SendSessionAffinityHeaders { get; set; }
    public string? SessionAffinityFormat { get; set; }
    public bool? SupportsCacheControlOnTools { get; set; }
    public bool? SupportsTemperature { get; set; }
    public bool? ForceAdaptiveThinking { get; set; }
    public bool? AllowEmptySignature { get; set; }
    public bool? SupportsStrictTools { get; set; }
    public bool? SupportsMidConvoEffort { get; set; }
    public List<AnthropicAllowedFallbackModel>? AllowedFallbackModels { get; set; }
    public bool? SupportsToolReferences { get; set; }
}
