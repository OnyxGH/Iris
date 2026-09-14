using System.Text.Json.Nodes;
using PiSharp.Ai.Utils;

namespace PiSharp.Ai.Providers;

/// <summary>Shared helpers for building provider options from SimpleStreamOptions. Port of api/simple-options.ts.</summary>
public static class SimpleOptions
{
    private const long ContextSafetyTokens = 4096;
    private const long MinMaxTokens = 1;

    /// <summary>Tokens always left for the answer when a thinking budget shares the response ceiling.</summary>
    public const int MinAnswerTokens = 1024;

    public static readonly ThinkingBudgets DefaultThinkingBudgets = new() { Minimal = 1024, Low = 2048, Medium = 8192, High = 16384 };

    public static long ClampMaxTokensToContext(Model model, Context context, long maxTokens)
    {
        if (model.ContextWindow <= 0) return Math.Max(MinMaxTokens, maxTokens);
        var available = model.ContextWindow - Estimate.EstimateContextTokens(context).Tokens - ContextSafetyTokens;
        return Math.Min(maxTokens, Math.Max(MinMaxTokens, available));
    }

    public static T BuildBaseOptions<T>(T target, Model model, Context context, SimpleStreamOptions? options, string? apiKey = null)
        where T : StreamOptions
    {
        if (options is not null) options.CopyTo(target);
        JsonObject? samplingParams = null;
        if (model.SamplingParams is not null || options?.SamplingParams is not null)
        {
            samplingParams = new JsonObject();
            foreach (var (k, v) in model.SamplingParams ?? []) samplingParams[k] = v?.DeepClone();
            foreach (var (k, v) in options?.SamplingParams ?? []) samplingParams[k] = v?.DeepClone();
        }
        target.SamplingParams = samplingParams;
        target.MaxTokens = (int)ClampMaxTokensToContext(model, context, options?.MaxTokens ?? model.MaxTokens);
        target.ApiKey = !string.IsNullOrEmpty(apiKey) ? apiKey : options?.ApiKey;
        return target;
    }

    /// <summary>xhigh and max fall back to high for token-budget providers.</summary>
    public static ThinkingLevel? ClampReasoning(ThinkingLevel? effort) =>
        effort is ThinkingLevel.XHigh or ThinkingLevel.Max ? ThinkingLevel.High : effort;

    public static int ThinkingBudgetForLevel(ThinkingLevel reasoningLevel, ThinkingBudgets? customBudgets)
    {
        var level = ClampReasoning(reasoningLevel)!.Value;
        int? Pick(Func<ThinkingBudgets, int?> f) => (customBudgets is not null ? f(customBudgets) : null) ?? f(DefaultThinkingBudgets);
        return level switch
        {
            ThinkingLevel.Minimal => Pick(b => b.Minimal)!.Value,
            ThinkingLevel.Low => Pick(b => b.Low)!.Value,
            ThinkingLevel.Medium => Pick(b => b.Medium)!.Value,
            _ => Pick(b => b.High)!.Value,
        };
    }

    public static long ClampThinkingBudgetToAnswerRoom(long thinkingBudget, long ceiling) =>
        Math.Min(thinkingBudget, Math.Max(0, ceiling - MinAnswerTokens));

    public static (long MaxTokens, long ThinkingBudget) AdjustMaxTokensForThinking(
        long? baseMaxTokens, long modelMaxTokens, ThinkingLevel reasoningLevel, ThinkingBudgets? customBudgets)
    {
        long thinkingBudget = ThinkingBudgetForLevel(reasoningLevel, customBudgets);
        var maxTokens = baseMaxTokens is null ? modelMaxTokens : Math.Min(baseMaxTokens.Value + thinkingBudget, modelMaxTokens);
        if (maxTokens <= thinkingBudget) thinkingBudget = ClampThinkingBudgetToAnswerRoom(thinkingBudget, maxTokens);
        return (maxTokens, thinkingBudget);
    }

    public static CacheRetention ResolveCacheRetention(CacheRetention? cacheRetention, IReadOnlyDictionary<string, string>? env)
    {
        if (cacheRetention is not null) return cacheRetention.Value;
        return ProviderEnv.Get("PI_CACHE_RETENTION", env) == "long" ? CacheRetention.Long : CacheRetention.Short;
    }
}
