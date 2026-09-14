using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using PiSharp.Ai.Json;

namespace PiSharp.Ai;

/// <summary>Well-known API identifiers.</summary>
public static class KnownApis
{
    public const string OpenAICompletions = "openai-completions";
    public const string MistralConversations = "mistral-conversations";
    public const string OpenAIResponses = "openai-responses";
    public const string AzureOpenAIResponses = "azure-openai-responses";
    public const string OpenAICodexResponses = "openai-codex-responses";
    public const string AnthropicMessages = "anthropic-messages";
    public const string BedrockConverseStream = "bedrock-converse-stream";
    public const string GoogleGenerativeAI = "google-generative-ai";
    public const string GoogleVertex = "google-vertex";
    public const string PiMessages = "pi-messages";
}

[JsonConverter(typeof(JsonStringEnumConverter<ThinkingLevel>))]
public enum ThinkingLevel
{
    [JsonStringEnumMemberName("off")] Off,
    [JsonStringEnumMemberName("minimal")] Minimal,
    [JsonStringEnumMemberName("low")] Low,
    [JsonStringEnumMemberName("medium")] Medium,
    [JsonStringEnumMemberName("high")] High,
    [JsonStringEnumMemberName("xhigh")] XHigh,
    [JsonStringEnumMemberName("max")] Max,
}

public static class ThinkingLevels
{
    /// <summary>All levels in order, including "off".</summary>
    public static readonly IReadOnlyList<ThinkingLevel> All =
    [
        ThinkingLevel.Off, ThinkingLevel.Minimal, ThinkingLevel.Low, ThinkingLevel.Medium,
        ThinkingLevel.High, ThinkingLevel.XHigh, ThinkingLevel.Max,
    ];

    public static string ToWire(this ThinkingLevel level) => level switch
    {
        ThinkingLevel.Off => "off",
        ThinkingLevel.Minimal => "minimal",
        ThinkingLevel.Low => "low",
        ThinkingLevel.Medium => "medium",
        ThinkingLevel.High => "high",
        ThinkingLevel.XHigh => "xhigh",
        ThinkingLevel.Max => "max",
        _ => "off",
    };

    public static bool TryParse(string? value, out ThinkingLevel level)
    {
        switch (value)
        {
            case "off": level = ThinkingLevel.Off; return true;
            case "minimal": level = ThinkingLevel.Minimal; return true;
            case "low": level = ThinkingLevel.Low; return true;
            case "medium": level = ThinkingLevel.Medium; return true;
            case "high": level = ThinkingLevel.High; return true;
            case "xhigh": level = ThinkingLevel.XHigh; return true;
            case "max": level = ThinkingLevel.Max; return true;
            default: level = ThinkingLevel.Off; return false;
        }
    }

    public static ThinkingLevel? Parse(string? value) => TryParse(value, out var level) ? level : null;
}

public sealed class ThinkingBudgets
{
    public int? Minimal { get; set; }
    public int? Low { get; set; }
    public int? Medium { get; set; }
    public int? High { get; set; }
}

public class ModelCostRates
{
    /// <summary>$/million tokens.</summary>
    public double Input { get; set; }
    public double Output { get; set; }
    public double CacheRead { get; set; }
    public double CacheWrite { get; set; }
}

public sealed class ModelCostTier : ModelCostRates
{
    /// <summary>Use this tier for requests whose total input usage exceeds this token count.</summary>
    public long InputTokensAbove { get; set; }
}

public sealed class ModelCost : ModelCostRates
{
    public List<ModelCostTier>? Tiers { get; set; }
}

/// <summary>
/// A model definition. Mirrors pi-ai's Model interface. Compat settings are kept as raw JSON because
/// their shape depends on <see cref="Api"/>; use <see cref="GetCompat{T}"/> to read a typed view.
/// </summary>
public sealed class Model
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public string Api { get; set; } = "";

    public string Provider { get; set; } = "";

    public string BaseUrl { get; set; } = "";

    public bool Reasoning { get; set; }

    /// <summary>
    /// Maps pi thinking levels ("off", "minimal", ...) to provider values. Missing keys use provider defaults;
    /// a null value marks the level as unsupported.
    /// </summary>
    public Dictionary<string, string?>? ThinkingLevelMap { get; set; }

    /// <summary>"text" and/or "image".</summary>
    public List<string> Input { get; set; } = ["text"];

    public ModelCost Cost { get; set; } = new();

    public long ContextWindow { get; set; }

    public long MaxTokens { get; set; }

    public JsonObject? SamplingParams { get; set; }

    public Dictionary<string, string>? Headers { get; set; }

    public JsonObject? Compat { get; set; }

    public bool SupportsImages => Input.Contains("image");

    public T GetCompat<T>() where T : new() =>
        Compat is null ? new T() : Compat.Deserialize<T>(PiJson.Options) ?? new T();

    /// <summary>Look up a thinking level mapping. Returns (found, value) where value null means unsupported.</summary>
    public bool TryGetThinkingMapping(ThinkingLevel level, out string? value)
    {
        value = null;
        return ThinkingLevelMap is not null && ThinkingLevelMap.TryGetValue(level.ToWire(), out value);
    }

    public Model Clone()
    {
        var clone = (Model)MemberwiseClone();
        clone.ThinkingLevelMap = ThinkingLevelMap is null ? null : new Dictionary<string, string?>(ThinkingLevelMap);
        clone.Input = [.. Input];
        clone.Headers = Headers is null ? null : new Dictionary<string, string>(Headers);
        clone.Compat = (JsonObject?)Compat?.DeepClone();
        clone.SamplingParams = (JsonObject?)SamplingParams?.DeepClone();
        return clone;
    }

    public Model WithBaseUrl(string baseUrl)
    {
        var clone = Clone();
        clone.BaseUrl = baseUrl;
        return clone;
    }

    public override string ToString() => $"{Provider}/{Id}";
}

/// <summary>Provider-side constrained sampling config for a tool.</summary>
public sealed class ConstrainedSamplingConfig
{
    /// <summary>"json_schema" or "grammar".</summary>
    public string Type { get; set; } = "json_schema";

    /// <summary>For json_schema: "prefer" or "require".</summary>
    public string? Strict { get; set; }

    /// <summary>For grammar: keys "openai_lark" / "openai_regex".</summary>
    public Dictionary<string, string>? Variants { get; set; }
}

/// <summary>Tool definition sent to the model. Parameters is a JSON Schema object.</summary>
public class Tool
{
    public string Name { get; set; } = "";

    public string Description { get; set; } = "";

    public JsonObject Parameters { get; set; } = new() { ["type"] = "object", ["properties"] = new JsonObject() };

    public ConstrainedSamplingConfig? ConstrainedSampling { get; set; }
}

public sealed class Context
{
    public string? SystemPrompt { get; set; }

    public List<Message> Messages { get; set; } = [];

    public List<Tool>? Tools { get; set; }
}

public static class TimeUtil
{
    public static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
