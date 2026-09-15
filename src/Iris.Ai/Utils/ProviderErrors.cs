using System.Text.Json;
using System.Text.Json.Nodes;
using Iris.Ai.Json;

namespace Iris.Ai.Utils;

/// <summary>
/// HTTP error from a provider endpoint. Message formatting follows the OpenAI/Anthropic SDK conventions that
/// pi's error handling (overflow/retry detection) is written against.
/// </summary>
public class ProviderHttpException : Exception
{
    public ProviderHttpException(int? status, IReadOnlyDictionary<string, string>? headers, string message, JsonNode? error = null, string? rawBody = null)
        : base(message)
    {
        Status = status;
        Headers = headers;
        Error = error;
        RawBody = rawBody;
    }

    public int? Status { get; }

    public IReadOnlyDictionary<string, string>? Headers { get; }

    /// <summary>Parsed error body object (SDK "error" field).</summary>
    public JsonNode? Error { get; }

    public string? RawBody { get; }

    public string? GetHeader(string name)
    {
        if (Headers is null) return null;
        foreach (var (key, value) in Headers)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) return value;
        }
        return null;
    }

    /// <summary>
    /// Build an error the way the OpenAI SDK does: parse JSON body, use its "error" field, and compose
    /// "&lt;status&gt; &lt;message&gt;" or "&lt;status&gt; status code (no body)".
    /// </summary>
    public static ProviderHttpException FromOpenAIStyle(int status, IReadOnlyDictionary<string, string> headers, string body)
    {
        JsonNode? parsed = null;
        try
        {
            parsed = string.IsNullOrWhiteSpace(body) ? null : JsonNode.Parse(body);
        }
        catch (JsonException)
        {
        }

        JsonNode? error = parsed is JsonObject obj ? obj["error"] : null;
        string? msg;
        if (error is JsonObject errObj && errObj["message"] is { } m)
        {
            msg = m is JsonValue mv && mv.TryGetValue<string>(out var s) ? s : PiJson.Stringify(m);
        }
        else if (error is not null)
        {
            msg = PiJson.Stringify(error);
        }
        else if (parsed is not null)
        {
            // No "error" field: SDK passes the raw text as message.
            msg = string.IsNullOrWhiteSpace(body) ? null : body;
        }
        else
        {
            msg = string.IsNullOrWhiteSpace(body) ? null : body;
        }

        var message = msg is not null ? $"{status} {msg}" : $"{status} status code (no body)";
        return new ProviderHttpException(status, headers, message, error, body);
    }

    /// <summary>Anthropic SDK style: "&lt;status&gt; &lt;JSON body&gt;".</summary>
    public static ProviderHttpException FromAnthropicStyle(int status, IReadOnlyDictionary<string, string> headers, string body)
    {
        JsonNode? parsed = null;
        try
        {
            parsed = string.IsNullOrWhiteSpace(body) ? null : JsonNode.Parse(body);
        }
        catch (JsonException)
        {
        }

        string? msg;
        if (parsed is JsonObject obj && obj["message"] is { } topMessage)
        {
            msg = topMessage is JsonValue mv && mv.TryGetValue<string>(out var ms) ? ms : PiJson.Stringify(topMessage);
        }
        else if (parsed is not null)
        {
            msg = PiJson.Stringify(parsed);
        }
        else
        {
            msg = string.IsNullOrWhiteSpace(body) ? null : body;
        }
        var message = msg is not null ? $"{status} {msg}" : $"{status} status code (no body)";
        // Anthropic's SDK stores the whole body as `error`; its message already carries it.
        return new ProviderHttpException(status, headers, message, parsed, body);
    }
}

public sealed record NormalizedProviderError(int? Status, string? Body, string Message, bool MessageCarriesBody);

public static class ProviderErrors
{
    public const int MaxProviderErrorBodyChars = 4000;

    public static NormalizedProviderError Normalize(Exception error)
    {
        if (error is ProviderHttpException http)
        {
            string? body = null;
            if (http.Error is JsonObject o && o.Count > 0) body = PiJson.Stringify(o);
            if (body is not null)
            {
                body = body.Trim();
                body = body.Length == 0 ? null : TruncateErrorText(body, MaxProviderErrorBodyChars);
            }
            var carries = body is null || http.Message.Contains(body, StringComparison.Ordinal);
            return new NormalizedProviderError(http.Status, body, http.Message, carries);
        }
        return new NormalizedProviderError(null, null, DescribeException(error), true);
    }

    public static string Format(NormalizedProviderError norm, string? prefix = null)
    {
        if (norm.MessageCarriesBody || norm.Status is null || norm.Body is null)
        {
            return prefix is not null && norm.Status is not null ? $"{prefix} ({norm.Status}): {norm.Message}" : norm.Message;
        }
        return prefix is not null ? $"{prefix} ({norm.Status}): {norm.Body}" : $"{norm.Status}: {norm.Body}";
    }

    public static string FormatException(Exception error) => Format(Normalize(error));

    public static string TruncateErrorText(string text, int maxChars) =>
        text.Length <= maxChars ? text : $"{text[..maxChars]}... [truncated {text.Length - maxChars} chars]";

    /// <summary>Message for non-HTTP failures, using fetch-like wording for transport errors.</summary>
    public static string DescribeException(Exception error) => error switch
    {
        OperationCanceledException => "Request was aborted",
        HttpRequestException http when http.InnerException is not null => $"Connection error. {http.InnerException.Message}",
        HttpRequestException http => $"Connection error. {http.Message}",
        IOException io => $"Connection error. {io.Message}",
        _ => string.IsNullOrEmpty(error.Message) ? error.GetType().Name : error.Message,
    };
}
