using PiSharp.Ai.Json;

namespace PiSharp.Ai.Utils;

public sealed record ContextUsageEstimate(long Tokens, long UsageTokens, long TrailingTokens, int? LastUsageIndex);

/// <summary>Heuristic token estimation. Port of pi-ai utils/estimate.ts.</summary>
public static class Estimate
{
    private const int CharsPerToken = 4;
    private const int EstimatedImageChars = 4800;

    public static long CalculateContextTokens(Usage usage) =>
        usage.TotalTokens != 0 ? usage.TotalTokens : usage.Input + usage.Output + usage.CacheRead + usage.CacheWrite;

    private static long CeilDiv(long chars) => (long)Math.Ceiling(chars / (double)CharsPerToken);

    public static long EstimateTextTokens(string text) => CeilDiv(text.Length);

    public static long EstimateContentTokens(UserContent content) =>
        content.Text is not null ? CeilDiv(content.Text.Length) : EstimateContentTokens(content.Blocks!);

    public static long EstimateContentTokens(IEnumerable<ContentBlock> content)
    {
        long chars = 0;
        foreach (var block in content)
        {
            chars += block is TextContent t ? t.Text.Length : EstimatedImageChars;
        }
        return CeilDiv(chars);
    }

    public static long EstimateMessageTokens(Message message)
    {
        switch (message)
        {
            case UserMessage user:
                return EstimateContentTokens(user.Content);
            case ToolResultMessage tr:
                return EstimateContentTokens(tr.Content);
            case AssistantMessage assistant:
                long chars = 0;
                foreach (var block in assistant.Content)
                {
                    chars += block switch
                    {
                        TextContent t => t.Text.Length,
                        ThinkingContent th => th.Thinking.Length,
                        ToolCall tc => tc.Name.Length + PiJson.Stringify(tc.Arguments).Length,
                        _ => 0,
                    };
                }
                return CeilDiv(chars);
            default:
                return 0;
        }
    }

    private static (Usage Usage, int Index)? GetLastAssistantUsageInfo(IReadOnlyList<Message> messages)
    {
        var latestPrefixTimestamp = long.MinValue;
        (Usage, int)? usageInfo = null;

        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            if (message is AssistantMessage assistant)
            {
                var usageAppliesToPrefix = assistant.Timestamp >= latestPrefixTimestamp;
                if (usageAppliesToPrefix
                    && assistant.StopReason != StopReason.Aborted
                    && assistant.StopReason != StopReason.Error
                    && CalculateContextTokens(assistant.Usage) > 0)
                {
                    usageInfo = (assistant.Usage, i);
                }
            }
            latestPrefixTimestamp = Math.Max(latestPrefixTimestamp, message.Timestamp);
        }
        return usageInfo;
    }

    public static ContextUsageEstimate EstimateMessages(IReadOnlyList<Message> messages)
    {
        var usageInfo = GetLastAssistantUsageInfo(messages);
        if (usageInfo is { } info)
        {
            var usageTokens = CalculateContextTokens(info.Usage);
            long trailing = 0;
            for (var i = info.Index + 1; i < messages.Count; i++) trailing += EstimateMessageTokens(messages[i]);
            return new ContextUsageEstimate(usageTokens + trailing, usageTokens, trailing, info.Index);
        }

        long tokens = 0;
        foreach (var message in messages) tokens += EstimateMessageTokens(message);
        return new ContextUsageEstimate(tokens, 0, tokens, null);
    }

    private static long EstimateToolsTokens(IEnumerable<Tool>? tools)
    {
        var list = tools?.ToList();
        if (list is null || list.Count == 0) return 0;
        return EstimateTextTokens(PiJson.Serialize(list));
    }

    public static ContextUsageEstimate EstimateContextTokens(Context context)
    {
        var estimate = EstimateMessages(context.Messages);
        if (estimate.LastUsageIndex is { } lastIndex)
        {
            var addedNames = context.Messages.Skip(lastIndex + 1)
                .OfType<ToolResultMessage>()
                .SelectMany(m => m.AddedToolNames ?? [])
                .ToHashSet();
            var addedToolTokens = EstimateToolsTokens(context.Tools?.Where(t => addedNames.Contains(t.Name)));
            return estimate with
            {
                Tokens = estimate.Tokens + addedToolTokens,
                TrailingTokens = estimate.TrailingTokens + addedToolTokens,
            };
        }

        var prefixTokens = (context.SystemPrompt is { Length: > 0 } sp ? EstimateTextTokens(sp) : 0) + EstimateToolsTokens(context.Tools);
        return estimate with
        {
            Tokens = estimate.Tokens + prefixTokens,
            TrailingTokens = estimate.TrailingTokens + prefixTokens,
        };
    }
}
