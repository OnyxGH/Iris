using PiSharp.Ai.Utils;

namespace PiSharp.CodingAgent.Utils;

/// <summary>Bounded immediate retry for idempotent management requests. Port of utils/management-http.ts.</summary>
public static class ManagementHttp
{
    private static readonly HashSet<int> RetryableStatusCodes = [408, 425, 429, 500, 502, 503, 504];

    public static async Task<HttpResponseMessage> FetchWithRetryAsync(
        Func<HttpRequestMessage> createRequest,
        CancellationToken cancellationToken,
        int maxRetries = 2,
        bool retryOnStatus = true,
        int? timeoutMs = null,
        int? attemptTimeoutMs = null,
        HttpClient? client = null)
    {
        using var overall = timeoutMs is > 0 ? new CancellationTokenSource(timeoutMs.Value) : null;
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            overall?.Token.ThrowIfCancellationRequested();
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, overall?.Token ?? CancellationToken.None);
            if (attemptTimeoutMs is > 0) attemptCts.CancelAfter(attemptTimeoutMs.Value);
            try
            {
                using var request = createRequest();
                var response = await (client ?? ProviderHttp.Client).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attemptCts.Token);
                var shouldRetry = retryOnStatus && RetryableStatusCodes.Contains((int)response.StatusCode) && attempt < maxRetries;
                if (!shouldRetry) return response;
                response.Dispose();
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested && overall?.IsCancellationRequested != true && attempt < maxRetries)
            {
                // Transport failure or attempt timeout: retry.
            }
        }
    }
}
