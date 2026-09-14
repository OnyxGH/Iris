using PiSharp.Ai.Auth;
using PiSharp.Ai.Models;
using PiSharp.Ai.Providers;

namespace PiSharp.Ai;

/// <summary>
/// Global API surface equivalent to "@earendil-works/pi-ai/compat": api-registry dispatch with env API key
/// injection and built-in catalog reads.
/// </summary>
public static class PiAi
{
    private static readonly Lazy<ModelsCollection> CompatModels = new(() => BuiltinProviders.CreateModels());

    public static Model? GetModel(string provider, string modelId) => BuiltinCatalog.GetModel(provider, modelId);

    public static IReadOnlyList<Model> GetModels(string provider) => BuiltinCatalog.GetModels(provider);

    public static IReadOnlyList<string> GetProviders() => BuiltinCatalog.GetProviders();

    private static T? WithEnvApiKey<T>(Model model, T? options) where T : StreamOptions
    {
        if (!string.IsNullOrWhiteSpace(options?.ApiKey)) return options;
        var apiKey = EnvApiKeys.GetEnvApiKey(model.Provider, options?.Env);
        if (apiKey is null || apiKey == EnvApiKeys.AmbientAuthMarker) return options;
        var clone = options is null ? (T)Activator.CreateInstance(typeof(T))! : (T)options.ShallowClone();
        clone.ApiKey = apiKey;
        return clone;
    }

    private static IProvider? GetBuiltinProviderForModel(Model model)
    {
        if (!ApiRegistry.IsBuiltinRegistered(model.Api)) return null;
        var provider = CompatModels.Value.GetProvider(model.Provider);
        return provider is not null && provider.GetModels().Any(m => m.Api == model.Api) ? provider : null;
    }

    private static bool HasResolvedCloudflareAuth(StreamOptions? options) =>
        !string.IsNullOrWhiteSpace(options?.ApiKey) || options?.Headers?.GetValueOrDefault("cf-aig-authorization") is not null;

    private static IApiStreams ResolveApi(string api) =>
        ApiRegistry.Get(api) ?? throw new InvalidOperationException($"No API provider registered for api: {api}");

    public static AssistantMessageEventStream Stream(Model model, Context context, StreamOptions? options = null)
    {
        var builtin = GetBuiltinProviderForModel(model);
        if (builtin is not null)
        {
            if (model.Provider.StartsWith("cloudflare-", StringComparison.Ordinal) && !HasResolvedCloudflareAuth(options))
                return CompatModels.Value.Stream(model, context, options);
            return builtin.Stream(model, context, WithEnvApiKey(model, options));
        }
        return ResolveApi(model.Api).Stream(model, context, WithEnvApiKey(model, options));
    }

    public static Task<AssistantMessage> CompleteAsync(Model model, Context context, StreamOptions? options = null) =>
        Stream(model, context, options).Result();

    public static AssistantMessageEventStream StreamSimple(Model model, Context context, SimpleStreamOptions? options = null)
    {
        var builtin = GetBuiltinProviderForModel(model);
        if (builtin is not null)
        {
            if (model.Provider.StartsWith("cloudflare-", StringComparison.Ordinal) && !HasResolvedCloudflareAuth(options))
                return CompatModels.Value.StreamSimple(model, context, options);
            return builtin.StreamSimple(model, context, WithEnvApiKey(model, options));
        }
        return ResolveApi(model.Api).StreamSimple(model, context, WithEnvApiKey(model, options));
    }

    public static Task<AssistantMessage> CompleteSimpleAsync(Model model, Context context, SimpleStreamOptions? options = null) =>
        StreamSimple(model, context, options).Result();
}
