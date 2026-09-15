using System.Text.RegularExpressions;

namespace Iris.Ai.Utils;

/// <summary>Context overflow detection.</summary>
public static class Overflow
{
    private const RegexOptions I = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static readonly Regex[] OverflowPatterns =
    [
        new(@"prompt is too long", I),
        new(@"request_too_large", I),
        new(@"input is too long for requested model", I),
        new(@"exceeds the context window", I),
        new(@"exceeds (?:the )?(?:model'?s )?maximum context length(?: of [\d,]+ tokens?|\s*\([\d,]+\))", I),
        new(@"input token count.*exceeds the maximum", I),
        new(@"maximum prompt length is \d+", I),
        new(@"reduce the length of the messages", I),
        new(@"maximum context length is \d+ tokens", I),
        new(@"exceeds (?:the )?maximum allowed input length of [\d,]+ tokens?", I),
        new(@"input \(\d+ tokens\) is longer than the model'?s context length \(\d+ tokens\)", I),
        new(@"exceeds the limit of \d+", I),
        new(@"exceeds the available context size", I),
        new(@"greater than the context length", I),
        new(@"context window exceeds limit", I),
        new(@"exceeded model token limit", I),
        new(@"too large for model with \d+ maximum context length", I),
        new(@"prompt has [\d,]+ tokens?, but the configured context size is [\d,]+ tokens?", I),
        new(@"model_context_window_exceeded", I),
        new(@"prompt too long; exceeded (?:max )?context length", I),
        new(@"range of input length should be", I),
        new(@"context[_ ]length[_ ]exceeded", I),
        new(@"too many tokens", I),
        new(@"token limit exceeded", I),
        new(@"^4(?:00|13)\s*(?:status code)?\s*\(no body\)", I),
    ];

    private static readonly Regex[] NonOverflowPatterns =
    [
        new(@"^(Throttling error|Service unavailable):", I),
        new(@"rate limit", I),
        new(@"too many requests", I),
    ];

    public static bool IsContextOverflow(AssistantMessage message, long? contextWindow = null)
    {
        if (message.StopReason == StopReason.Error && !string.IsNullOrEmpty(message.ErrorMessage))
        {
            var error = message.ErrorMessage;
            var isNonOverflow = NonOverflowPatterns.Any(p => p.IsMatch(error));
            if (!isNonOverflow && OverflowPatterns.Any(p => p.IsMatch(error))) return true;
        }

        if (contextWindow is > 0 && message.StopReason == StopReason.Stop)
        {
            var inputTokens = message.Usage.Input + message.Usage.CacheRead;
            if (inputTokens > contextWindow) return true;
        }

        if (contextWindow is > 0 && message.StopReason == StopReason.Length && message.Usage.Output == 0)
        {
            var inputTokens = message.Usage.Input + message.Usage.CacheRead;
            if (inputTokens >= contextWindow.Value * 0.99) return true;
        }

        return false;
    }

    /// <summary>Whether a length stop ended below the intended output limit.</summary>
    public static bool IsRecoverableLength(AssistantMessage message, long desiredMaxOutput) =>
        message.StopReason == StopReason.Length && desiredMaxOutput > 0 && message.Usage.Output < desiredMaxOutput;

    public static IReadOnlyList<Regex> GetOverflowPatterns() => OverflowPatterns;
}
