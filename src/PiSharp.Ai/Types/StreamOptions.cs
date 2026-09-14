using System.Text.Json.Nodes;

namespace PiSharp.Ai;

public enum CacheRetention
{
    None,
    Short,
    Long,
}

public enum Transport
{
    Sse,
    WebSocket,
    WebSocketCached,
    Auto,
}

public enum ToolChoice
{
    Auto,
    None,
}

public sealed record ProviderResponse(int Status, IReadOnlyDictionary<string, string> Headers);

/// <summary>Authentication, HTTP transport and lifecycle options shared by provider requests.</summary>
public class ProviderRequestOptions
{
    public CancellationToken CancellationToken { get; set; }

    public string? ApiKey { get; set; }

    /// <summary>Optional HttpClient used for provider requests. Defaults to a shared client.</summary>
    public HttpClient? HttpClient { get; set; }

    /// <summary>Provider-scoped environment values that take precedence over process environment.</summary>
    public Dictionary<string, string>? Env { get; set; }

    /// <summary>Inspect or replace the provider payload before sending. Return null to keep it unchanged.</summary>
    public Func<JsonNode, Model, Task<JsonNode?>>? OnPayload { get; set; }

    /// <summary>Invoked after an HTTP response is received, before its body is consumed.</summary>
    public Func<ProviderResponse, Model, Task>? OnResponse { get; set; }

    /// <summary>Custom HTTP headers. A null value suppresses a default header with the same name.</summary>
    public Dictionary<string, string?>? Headers { get; set; }

    /// <summary>HTTP request timeout in milliseconds.</summary>
    public int? TimeoutMs { get; set; }

    /// <summary>Maximum client-side retry attempts.</summary>
    public int? MaxRetries { get; set; }

    /// <summary>Maximum server-requested retry delay (default 60000; 0 disables the cap).</summary>
    public int? MaxRetryDelayMs { get; set; }
}

public class StreamOptions : ProviderRequestOptions
{
    public double? Temperature { get; set; }

    /// <summary>Arbitrary sampling parameters merged into the request body (OpenAI-compatible adapters only).</summary>
    public JsonObject? SamplingParams { get; set; }

    public int? MaxTokens { get; set; }

    public Transport? Transport { get; set; }

    public CacheRetention? CacheRetention { get; set; }

    public string? SessionId { get; set; }

    public int? WebSocketConnectTimeoutMs { get; set; }

    public JsonObject? Metadata { get; set; }

    /// <summary>Copy the shared base fields into another options instance.</summary>
    public T CopyTo<T>(T target) where T : StreamOptions
    {
        target.CancellationToken = CancellationToken;
        target.ApiKey = ApiKey;
        target.HttpClient = HttpClient;
        target.Env = Env;
        target.OnPayload = OnPayload;
        target.OnResponse = OnResponse;
        target.Headers = Headers;
        target.TimeoutMs = TimeoutMs;
        target.MaxRetries = MaxRetries;
        target.MaxRetryDelayMs = MaxRetryDelayMs;
        target.Temperature = Temperature;
        target.SamplingParams = SamplingParams;
        target.MaxTokens = MaxTokens;
        target.Transport = Transport;
        target.CacheRetention = CacheRetention;
        target.SessionId = SessionId;
        target.WebSocketConnectTimeoutMs = WebSocketConnectTimeoutMs;
        target.Metadata = Metadata;
        return target;
    }

    /// <summary>Provider-specific extra options (the TS "ProviderStreamOptions & Record&lt;string, unknown&gt;").</summary>
    public Dictionary<string, object?>? Extra { get; set; }

    /// <summary>Shallow copy preserving the runtime type (object spread in TS).</summary>
    public StreamOptions ShallowClone() => (StreamOptions)MemberwiseClone();
}

/// <summary>Unified options with reasoning, passed to StreamSimple/CompleteSimple.</summary>
public class SimpleStreamOptions : StreamOptions
{
    public ToolChoice? ToolChoice { get; set; }

    /// <summary>Reasoning level (never Off; null means reasoning disabled).</summary>
    public ThinkingLevel? Reasoning { get; set; }

    public ThinkingBudgets? ThinkingBudgets { get; set; }

    public SimpleStreamOptions CloneSimple() => (SimpleStreamOptions)ShallowClone();
}
