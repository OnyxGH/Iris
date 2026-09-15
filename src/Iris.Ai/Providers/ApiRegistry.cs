using System.Collections.Concurrent;

namespace Iris.Ai.Providers;

/// <summary>
/// Global registry of API implementations keyed by API id (the api-registry of pi-ai's compat module).
/// Built-in APIs are registered lazily; extensions and tests can override entries.
/// </summary>
public static class ApiRegistry
{
    private sealed record Entry(IApiStreams Streams, string? SourceId);

    private static readonly ConcurrentDictionary<string, Entry> Entries = new();
    private static readonly ConcurrentDictionary<string, IApiStreams> BuiltinInstances = new();

    static ApiRegistry() => RegisterBuiltIns();

    /// <summary>Built-in API implementations. Unported APIs fail with a clear stream error.</summary>
    public static IReadOnlyDictionary<string, IApiStreams> BuiltinApis { get; } = new Dictionary<string, IApiStreams>
    {
        [KnownApis.AnthropicMessages] = new LazyApi(KnownApis.AnthropicMessages, () => AnthropicMessagesApi.Instance),
        [KnownApis.OpenAICompletions] = OpenAICompletionsApi.Instance,
        [KnownApis.OpenAIResponses] = new LazyApi(KnownApis.OpenAIResponses, () => OpenAIResponsesApi.Instance),
        [KnownApis.OpenAICodexResponses] = new UnsupportedApi(KnownApis.OpenAICodexResponses),
        [KnownApis.AzureOpenAIResponses] = new UnsupportedApi(KnownApis.AzureOpenAIResponses),
        [KnownApis.GoogleGenerativeAI] = new LazyApi(KnownApis.GoogleGenerativeAI, () => GoogleGenerativeAIApi.Instance),
        [KnownApis.GoogleVertex] = new UnsupportedApi(KnownApis.GoogleVertex),
        [KnownApis.MistralConversations] = new UnsupportedApi(KnownApis.MistralConversations),
        [KnownApis.BedrockConverseStream] = new UnsupportedApi(KnownApis.BedrockConverseStream),
        [KnownApis.PiMessages] = new UnsupportedApi(KnownApis.PiMessages),
    };

    public static void Register(string api, IApiStreams streams, string? sourceId = null) => Entries[api] = new Entry(streams, sourceId);

    public static IApiStreams? Get(string api) => Entries.TryGetValue(api, out var entry) ? entry.Streams : null;

    public static IReadOnlyList<string> GetApis() => Entries.Keys.ToList();

    public static void UnregisterSource(string sourceId)
    {
        foreach (var (api, entry) in Entries)
        {
            if (entry.SourceId == sourceId) Entries.TryRemove(api, out _);
        }
    }

    /// <summary>Register built-ins without clobbering existing overrides.</summary>
    public static void RegisterBuiltIns()
    {
        foreach (var (api, streams) in BuiltinApis)
        {
            Entries.TryAdd(api, new Entry(streams, null));
            BuiltinInstances[api] = Entries[api].Streams;
        }
    }

    public static void Reset()
    {
        Entries.Clear();
        BuiltinInstances.Clear();
        RegisterBuiltIns();
    }

    /// <summary>True when the registered implementation for the API is still the built-in one.</summary>
    public static bool IsBuiltinRegistered(string api) =>
        Entries.TryGetValue(api, out var entry) && BuiltinInstances.TryGetValue(api, out var builtin) && ReferenceEquals(entry.Streams, builtin);
}

/// <summary>Defers creation of an API implementation until first use.</summary>
public sealed class LazyApi(string api, Func<IApiStreams> factory) : IApiStreams
{
    private readonly Lazy<IApiStreams> _impl = new(factory);

    public string ApiId => api;

    public AssistantMessageEventStream Stream(Model model, Context context, StreamOptions? options = null) => _impl.Value.Stream(model, context, options);

    public AssistantMessageEventStream StreamSimple(Model model, Context context, SimpleStreamOptions? options = null) => _impl.Value.StreamSimple(model, context, options);
}

/// <summary>Placeholder for APIs that have not been ported yet.</summary>
public sealed class UnsupportedApi(string api) : IApiStreams
{
    private string Message => $"API \"{api}\" is not yet supported by Iris";

    public AssistantMessageEventStream Stream(Model model, Context context, StreamOptions? options = null) =>
        AssistantMessageEventStream.FromError(model, Message);

    public AssistantMessageEventStream StreamSimple(Model model, Context context, SimpleStreamOptions? options = null) =>
        AssistantMessageEventStream.FromError(model, Message);
}
