using System.Text.Json;
using System.Text.Json.Nodes;
using Iris.CodingAgent.Config;

namespace Iris.WebAccess;

/// <summary>
/// web-search.json in the agent directory. Re-read when the file changes, so edits apply without a restart.
/// </summary>
internal static class WebConfig
{
    private static readonly Lock Gate = new();
    private static (string Path, DateTime Modified, long Size, JsonObject Root)? _cache;

    public static string Path => System.IO.Path.Combine(AppConfig.AgentDir, "web-search.json");

    /// <summary>The parsed root object; empty when the file does not exist. Throws when it is not valid JSON.</summary>
    public static JsonObject Root
    {
        get
        {
            var path = Path;
            var info = new FileInfo(path);
            if (!info.Exists) return [];
            lock (Gate)
            {
                if (_cache is { } cached && cached.Path == path && cached.Modified == info.LastWriteTimeUtc && cached.Size == info.Length) return cached.Root;
                JsonNode? parsed;
                try
                {
                    parsed = JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                }
                catch (JsonException ex)
                {
                    throw new ConfigParseException($"Failed to parse {path}: {ex.Message}");
                }
                if (parsed is not JsonObject root) throw new ConfigParseException($"Failed to parse {path}: expected a JSON object");
                _cache = (path, info.LastWriteTimeUtc, info.Length, root);
                return root;
            }
        }
    }

    public static JsonNode? Get(string key) => Root[key];

    public static JsonObject? GetObject(string key) => Root[key] as JsonObject;

    public static string? GetString(string key) =>
        Root[key] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;

    public static int? GetInt(JsonObject? obj, string key) =>
        obj?[key] is JsonValue value && value.TryGetValue<double>(out var number) && double.IsFinite(number) ? (int)Math.Floor(number) : null;

    public static bool? GetBool(JsonObject? obj, string key) =>
        obj?[key] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;

    /// <summary>Resolve an API base URL from an environment variable or config key, falling back to the default.</summary>
    public static string ResolveBaseUrl(string configKey, string environmentKey, string defaultValue)
    {
        var fromEnvironment = Environment.GetEnvironmentVariable(environmentKey);
        var (value, source) = fromEnvironment is not null
            ? (fromEnvironment, environmentKey)
            : Get(configKey) is { } configured ? (configured.GetValueKind() == JsonValueKind.String ? configured.GetValue<string>() : null, $"{configKey} in {Path}") : (defaultValue, "");
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
        {
            throw new InvalidOperationException($"{source} must be an absolute HTTP(S) URL");
        }
        return uri.ToString().TrimEnd('/');
    }
}

/// <summary>web-search.json could not be parsed; such errors are reported as-is instead of falling back.</summary>
internal sealed class ConfigParseException(string message) : Exception(message);
