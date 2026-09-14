using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using PiSharp.Ai.Json;

namespace PiSharp.Ai.Utils;

public enum ProviderErrorStyle
{
    OpenAI,
    Anthropic,
}

public sealed record SseEvent(string? Event, string Data, string? Id);

/// <summary>HTTP plumbing shared by provider adapters: client, headers, retries, SSE parsing.</summary>
public static class ProviderHttp
{
    private const int DefaultTimeoutMs = 10 * 60 * 1000;
    private const int DefaultMaxRetryDelayMs = 60_000;

    private static readonly Lazy<HttpClient> Shared = new(() =>
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            UseProxy = true,
            Proxy = EnvHttpProxy.Instance,
        };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    });

    public static HttpClient Client => Shared.Value;

    /// <summary>Merge header dictionaries case-insensitively; later values win and null removes.</summary>
    public static Dictionary<string, string> MergeHeaders(params IEnumerable<KeyValuePair<string, string?>>?[] sources)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var originalNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        {
            if (source is null) continue;
            foreach (var (key, value) in source)
            {
                if (value is null)
                {
                    result.Remove(key);
                    originalNames.Remove(key);
                }
                else
                {
                    result[key] = value;
                    originalNames[key] = key;
                }
            }
        }
        return result;
    }

    public static IEnumerable<KeyValuePair<string, string?>>? AsNullable(IReadOnlyDictionary<string, string>? headers) =>
        headers?.Select(kv => new KeyValuePair<string, string?>(kv.Key, kv.Value));

    public static Dictionary<string, string> HeadersToRecord(HttpResponseMessage response)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in response.Headers) result[header.Key.ToLowerInvariant()] = string.Join(", ", header.Value);
        foreach (var header in response.Content.Headers) result[header.Key.ToLowerInvariant()] = string.Join(", ", header.Value);
        return result;
    }

    /// <summary>
    /// POST a JSON body and return the successful response (headers read, body not consumed).
    /// Retries transient failures like the OpenAI/Anthropic SDKs, with interruptible backoff.
    /// </summary>
    public static async Task<HttpResponseMessage> PostJsonAsync(
        string url,
        JsonNode body,
        IReadOnlyDictionary<string, string> headers,
        ProviderRequestOptions? options,
        ProviderErrorStyle errorStyle,
        CancellationToken cancellationToken)
    {
        var payload = PiJson.Stringify(body);
        var maxRetries = options?.MaxRetries ?? 0;
        var retriesRemaining = maxRetries;
        var client = options?.HttpClient ?? Client;

        while (true)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json"),
                };
                ApplyHeaders(request, headers);

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(options?.TimeoutMs ?? DefaultTimeoutMs);
                HttpResponseMessage response;
                try
                {
                    response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new ProviderHttpException(null, null, "Request timed out.");
                }

                if (response.IsSuccessStatusCode) return response;

                var responseHeaders = HeadersToRecord(response);
                string text;
                try
                {
                    text = await response.Content.ReadAsStringAsync(cancellationToken);
                }
                finally
                {
                    response.Dispose();
                }
                throw errorStyle == ProviderErrorStyle.Anthropic
                    ? ProviderHttpException.FromAnthropicStyle((int)response.StatusCode, responseHeaders, text)
                    : ProviderHttpException.FromOpenAIStyle((int)response.StatusCode, responseHeaders, text);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException("Request aborted", ex, cancellationToken);
                if (retriesRemaining <= 0 || !IsRetryable(ex)) throw;
                var retryIndex = maxRetries - retriesRemaining;
                retriesRemaining--;
                var delay = GetRetryDelayMs(ex, retryIndex, options?.MaxRetryDelayMs);
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(0, delay)), cancellationToken);
            }
        }
    }

    public static void ApplyHeaders(HttpRequestMessage request, IReadOnlyDictionary<string, string> headers)
    {
        foreach (var (key, value) in headers)
        {
            if (string.Equals(key, "content-type", StringComparison.OrdinalIgnoreCase))
            {
                if (request.Content is not null && MediaTypeHeaderValue.TryParse(value, out var mediaType))
                    request.Content.Headers.ContentType = mediaType;
                continue;
            }
            if (!request.Headers.TryAddWithoutValidation(key, value))
            {
                request.Content?.Headers.TryAddWithoutValidation(key, value);
            }
        }
    }

    private static bool IsRetryable(Exception ex)
    {
        if (ex is ProviderHttpException http)
        {
            var shouldRetry = http.GetHeader("x-should-retry");
            if (shouldRetry == "true") return true;
            if (shouldRetry == "false") return false;
            if (http.Status is null) return true;
            return http.Status is 408 or 409 or 429 || http.Status >= 500;
        }
        return ex is HttpRequestException or IOException;
    }

    private static double GetRetryDelayMs(Exception ex, int retryIndex, int? maxRetryDelayMs)
    {
        if (ex is ProviderHttpException http)
        {
            var retryAfterMs = http.GetHeader("retry-after-ms");
            if (retryAfterMs is not null && double.TryParse(retryAfterMs, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ms))
            {
                return ValidateServerRetryDelay(ms, maxRetryDelayMs, http.Message);
            }
            var retryAfter = http.GetHeader("retry-after");
            if (retryAfter is not null)
            {
                double delayMs;
                if (double.TryParse(retryAfter, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds))
                    delayMs = seconds * 1000;
                else if (DateTimeOffset.TryParse(retryAfter, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var date))
                    delayMs = (date - DateTimeOffset.UtcNow).TotalMilliseconds;
                else
                    delayMs = double.NaN;
                if (!double.IsNaN(delayMs)) return ValidateServerRetryDelay(delayMs, maxRetryDelayMs, http.Message);
            }
        }
        var exponential = Math.Min(0.5 * Math.Pow(2, retryIndex), 8) * 1000;
        return exponential * (1 - Random.Shared.NextDouble() * 0.25);
    }

    private static double ValidateServerRetryDelay(double delayMs, int? maxRetryDelayMs, string providerErrorMessage)
    {
        var max = maxRetryDelayMs ?? DefaultMaxRetryDelayMs;
        if (max > 0 && delayMs > max)
        {
            throw new InvalidOperationException(
                $"Server requested {Math.Ceiling(delayMs / 1000)}s retry delay (max: {Math.Ceiling(max / 1000.0)}s). {providerErrorMessage}");
        }
        return delayMs;
    }

    /// <summary>Parse a text/event-stream body into events.</summary>
    public static async IAsyncEnumerable<SseEvent> ReadSseAsync(Stream stream, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 16 * 1024);
        string? eventName = null;
        string? id = null;
        var data = new StringBuilder();
        var hasData = false;

        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                if (hasData || eventName is not null) yield return new SseEvent(eventName, data.ToString(), id);
                yield break;
            }

            if (line.Length == 0)
            {
                if (hasData || eventName is not null)
                {
                    yield return new SseEvent(eventName, data.ToString(), id);
                }
                eventName = null;
                data.Clear();
                hasData = false;
                continue;
            }

            if (line[0] == ':') continue;

            var colon = line.IndexOf(':');
            string field;
            string value;
            if (colon < 0)
            {
                field = line;
                value = "";
            }
            else
            {
                field = line[..colon];
                value = line[(colon + 1)..];
                if (value.StartsWith(' ')) value = value[1..];
            }

            switch (field)
            {
                case "event":
                    eventName = value;
                    break;
                case "data":
                    if (hasData) data.Append('\n');
                    data.Append(value);
                    hasData = true;
                    break;
                case "id":
                    id = value;
                    break;
            }
        }
    }
}
