using System.Text.RegularExpressions;

namespace Iris.Ai.Utils;

/// <summary>Retry policy: bounded attempts with exponential backoff (baseDelayMs * 2^(attempt-1)).</summary>
public sealed class RetryPolicy
{
    public bool Enabled { get; set; }

    /// <summary>Max retry attempts (0 = no retries). The initial call never counts as a retry.</summary>
    public int MaxRetries { get; set; }

    public long BaseDelayMs { get; set; }

    /// <summary>Cap for agent-level retry delays in ms. Defaults to 60 seconds.</summary>
    public long? MaxAgentDelayMs { get; set; }
}

public sealed class RetryCallbacks
{
    public Func<int, int, long, string, Task>? OnRetryScheduled { get; set; }
    public Func<Task>? OnRetryAttemptStart { get; set; }
    public Func<bool, int, string?, Task>? OnRetryFinished { get; set; }
}

/// <summary>Transient-error classification and retry loop. Port of pi-ai utils/retry.ts.</summary>
public static class AssistantRetry
{
    public const long DefaultMaxAgentRetryDelayMs = 60_000;

    private static readonly Regex NonRetryableProviderLimitErrorPattern = Build(
    [
        "GoUsageLimitError",
        "FreeUsageLimitError",
        "Monthly usage limit reached",
        "available balance",
        "insufficient_quota",
        "out of budget",
        "quota exceeded",
        "billing",
    ]);

    private static readonly Regex RetryableProviderErrorPattern = Build(
    [
        "overloaded", "rate.?limit", "too many requests", "429", "500", "502", "503", "504", "524",
        "service.?unavailable", "server.?error", "internal.?error",
        "provider.?returned.?error", "exceeded request buffer limit while retrying upstream",
        "network.?error", "connection.?error", "connection.?refused", "connection.?lost", "other side closed",
        "fetch failed", "getaddrinfo", "ENOTFOUND", "EAI_AGAIN", "upstream.?connect", "reset before headers",
        "socket hang up", "socket connection was closed", "timed? out", "timeout", "terminated",
        "websocket.?closed", "websocket.?error",
        "ended without", "stream ended before message_stop", "stream ended before a terminal response event",
        "http2 request did not get a response",
        "retry delay",
        "you can retry your request", "try your request again", "please retry your request",
        "ResourceExhausted",
    ]);

    private static Regex Build(IEnumerable<string> patterns) =>
        new(string.Join("|", patterns), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static long RetryDelayMs(RetryPolicy policy, int attempt)
    {
        var delay = policy.BaseDelayMs * Math.Pow(2, Math.Max(0, attempt - 1));
        var safe = delay > 9007199254740991 ? 9007199254740991 : (long)delay;
        return Math.Min(safe, policy.MaxAgentDelayMs ?? DefaultMaxAgentRetryDelayMs);
    }

    public static bool IsRetryableAssistantError(AssistantMessage message)
    {
        if (message.StopReason != StopReason.Error || string.IsNullOrEmpty(message.ErrorMessage)) return false;
        if (NonRetryableProviderLimitErrorPattern.IsMatch(message.ErrorMessage)) return false;
        return RetryableProviderErrorPattern.IsMatch(message.ErrorMessage);
    }

    public static async Task<AssistantMessage> RetryAssistantCallAsync(
        Func<Task<AssistantMessage>> produce,
        RetryPolicy? policy,
        CancellationToken cancellationToken,
        RetryCallbacks? callbacks = null)
    {
        var maxAttempts = policy?.Enabled == true ? policy.MaxRetries : 0;
        var attempt = 0;
        (int Attempt, string ErrorMessage)? lastRetry = null;

        while (true)
        {
            var response = await produce();

            if (response.StopReason == StopReason.Aborted)
            {
                if (lastRetry is { } lr && callbacks?.OnRetryFinished is { } f1) await f1(false, lr.Attempt, null);
                return response;
            }

            if (response.StopReason != StopReason.Error)
            {
                if (lastRetry is { } lr && callbacks?.OnRetryFinished is { } f2) await f2(true, lr.Attempt, null);
                return response;
            }

            if (attempt >= maxAttempts || !IsRetryableAssistantError(response))
            {
                if (lastRetry is { } lr && callbacks?.OnRetryFinished is { } f3) await f3(false, lr.Attempt, response.ErrorMessage);
                return response;
            }

            attempt++;
            lastRetry = (attempt, response.ErrorMessage ?? "Unknown error");
            var delayMs = RetryDelayMs(policy!, attempt);
            if (callbacks?.OnRetryScheduled is { } scheduled) await scheduled(attempt, maxAttempts, delayMs, lastRetry.Value.ErrorMessage);

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(delayMs), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                if (callbacks?.OnRetryFinished is { } f4) await f4(false, attempt, lastRetry.Value.ErrorMessage);
                var aborted = response.Clone();
                aborted.ErrorMessage = null;
                aborted.StopReason = StopReason.Aborted;
                return aborted;
            }
            if (callbacks?.OnRetryAttemptStart is { } start) await start();
        }
    }
}
