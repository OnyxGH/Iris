namespace Iris.Ai.Providers;

/// <summary>
/// The uniform stream contract of an API implementation (ProviderStreams in pi-ai).
/// Direct StreamSimple calls may throw synchronously when request auth is missing. Once a stream is
/// returned, failures must be encoded in the stream as an error event.
/// </summary>
public interface IApiStreams
{
    AssistantMessageEventStream Stream(Model model, Context context, StreamOptions? options = null);

    AssistantMessageEventStream StreamSimple(Model model, Context context, SimpleStreamOptions? options = null);
}
