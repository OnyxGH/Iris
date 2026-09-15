namespace Iris.Ai;

public static class ModelUtils
{
    public static UsageCost CalculateCost(Model model, Usage usage)
    {
        var inputTokens = usage.Input + usage.CacheRead + usage.CacheWrite;
        ModelCostRates rates = model.Cost;
        long matchedThreshold = -1;
        foreach (var tier in model.Cost.Tiers ?? [])
        {
            if (inputTokens > tier.InputTokensAbove && tier.InputTokensAbove > matchedThreshold)
            {
                rates = tier;
                matchedThreshold = tier.InputTokensAbove;
            }
        }

        // Anthropic charges 2x base input for 1h cache writes.
        var longWrite = usage.CacheWrite1h ?? 0;
        var shortWrite = usage.CacheWrite - longWrite;
        usage.Cost.Input = rates.Input / 1000000 * usage.Input;
        usage.Cost.Output = rates.Output / 1000000 * usage.Output;
        usage.Cost.CacheRead = rates.CacheRead / 1000000 * usage.CacheRead;
        usage.Cost.CacheWrite = (rates.CacheWrite * shortWrite + rates.Input * 2 * longWrite) / 1000000;
        usage.Cost.Total = usage.Cost.Input + usage.Cost.Output + usage.Cost.CacheRead + usage.Cost.CacheWrite;
        return usage.Cost;
    }

    public static IReadOnlyList<ThinkingLevel> GetSupportedThinkingLevels(Model model)
    {
        if (!model.Reasoning) return [ThinkingLevel.Off];

        return ThinkingLevels.All.Where(level =>
        {
            var found = model.TryGetThinkingMapping(level, out var mapped);
            if (found && mapped is null) return false;
            if (level is ThinkingLevel.XHigh or ThinkingLevel.Max) return found;
            return true;
        }).ToList();
    }

    public static ThinkingLevel ClampThinkingLevel(Model model, ThinkingLevel level)
    {
        var available = GetSupportedThinkingLevels(model);
        if (available.Contains(level)) return level;

        var all = ThinkingLevels.All;
        var requestedIndex = IndexOf(all, level);
        if (requestedIndex == -1) return available.Count > 0 ? available[0] : ThinkingLevel.Off;

        for (var i = requestedIndex; i < all.Count; i++)
        {
            if (available.Contains(all[i])) return all[i];
        }
        for (var i = requestedIndex - 1; i >= 0; i--)
        {
            if (available.Contains(all[i])) return all[i];
        }
        return available.Count > 0 ? available[0] : ThinkingLevel.Off;
    }

    private static int IndexOf(IReadOnlyList<ThinkingLevel> list, ThinkingLevel level)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] == level) return i;
        }
        return -1;
    }

    /// <summary>True when both models are non-null and share id and provider.</summary>
    public static bool ModelsAreEqual(Model? a, Model? b) =>
        a is not null && b is not null && a.Id == b.Id && a.Provider == b.Provider;
}
