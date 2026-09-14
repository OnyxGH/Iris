using System.Text.Json.Nodes;
using PiSharp.Ai;
using PiSharp.Ai.Auth;
using PiSharp.Ai.Json;
using PiSharp.Ai.Models;
using PiSharp.Ai.Providers;

namespace PiSharp.CodingAgent.Core;

/// <summary>OAuth configuration registered by extensions.</summary>
public sealed class ExtensionOAuthConfig
{
    public required string Name { get; init; }
    public bool IsSubscription { get; init; }
    public required Func<IAuthInteraction, Task<OAuthCredential>> Login { get; init; }
    public required Func<OAuthCredential, CancellationToken, Task<OAuthCredential>> RefreshToken { get; init; }
    public required Func<OAuthCredential, string> GetApiKey { get; init; }
    public Func<List<Model>, OAuthCredential, List<Model>>? ModifyModels { get; init; }
}

/// <summary>Input for registering a provider programmatically (extension registerProvider API).</summary>
public sealed class ProviderConfigInput
{
    public string? Name { get; set; }
    public string? BaseUrl { get; set; }
    public string? ApiKey { get; set; }
    public string? Api { get; set; }
    public Func<Model, Context, SimpleStreamOptions?, AssistantMessageEventStream>? StreamSimple { get; set; }
    public Dictionary<string, string>? Headers { get; set; }
    public bool? AuthHeader { get; set; }
    public ExtensionOAuthConfig? OAuth { get; set; }
    /// <summary>Model definitions (model.Provider is ignored and replaced).</summary>
    public List<Model>? Models { get; set; }
    public Func<RefreshModelsContext, Task<List<Model>>>? RefreshModels { get; set; }

    public ProviderConfigInput Clone() => (ProviderConfigInput)MemberwiseClone();
}

public sealed record AuthStatus(bool Configured, string? Source = null, string? Label = null);

/// <summary>Compose built-in, models.json and extension provider layers. Port of core/provider-composer.ts.</summary>
public static class ProviderComposer
{
    private static readonly string[] NestedCompatKeys = ["openRouterRouting", "vercelGatewayRouting", "chatTemplateKwargs", "chatTemplateArgs"];

    public static JsonObject? MergeCompat(JsonObject? baseCompat, JsonObject? overrideCompat)
    {
        if (overrideCompat is null) return (JsonObject?)baseCompat?.DeepClone();
        var merged = (JsonObject?)baseCompat?.DeepClone() ?? new JsonObject();
        foreach (var (key, value) in overrideCompat) merged[key] = value?.DeepClone();
        foreach (var key in NestedCompatKeys)
        {
            var b = baseCompat?[key] as JsonObject;
            var o = overrideCompat[key] as JsonObject;
            if (b is null && o is null) continue;
            var nested = (JsonObject?)b?.DeepClone() ?? new JsonObject();
            foreach (var (k, v) in o ?? []) nested[k] = v?.DeepClone();
            merged[key] = nested;
        }
        return merged;
    }

    private static Dictionary<string, string?>? ThinkingMapFromJson(JsonNode? node)
    {
        if (node is not JsonObject obj) return null;
        return obj.ToDictionary(kv => kv.Key, kv => PiJson.GetString(kv.Value));
    }

    private static List<string>? StringList(JsonNode? node) =>
        node is JsonArray arr ? arr.Select(PiJson.GetString).Where(s => s is not null).Select(s => s!).ToList() : null;

    public static Model ApplyModelOverride(Model model, JsonObject over)
    {
        var result = model.Clone();
        if (PiJson.GetString(over["name"]) is { } name) result.Name = name;
        if (PiJson.GetBool(over["reasoning"]) is { } reasoning) result.Reasoning = reasoning;
        if (ThinkingMapFromJson(over["thinkingLevelMap"]) is { } map)
        {
            var mergedMap = new Dictionary<string, string?>(model.ThinkingLevelMap ?? []);
            foreach (var (k, v) in map) mergedMap[k] = v;
            result.ThinkingLevelMap = mergedMap;
        }
        if (StringList(over["input"]) is { } input) result.Input = input;
        if (over["cost"] is JsonObject cost)
        {
            result.Cost = new ModelCost
            {
                Input = PiJson.GetNumber(cost["input"]) ?? model.Cost.Input,
                Output = PiJson.GetNumber(cost["output"]) ?? model.Cost.Output,
                CacheRead = PiJson.GetNumber(cost["cacheRead"]) ?? model.Cost.CacheRead,
                CacheWrite = PiJson.GetNumber(cost["cacheWrite"]) ?? model.Cost.CacheWrite,
                Tiers = cost["tiers"] is JsonArray tiers ? PiJson.Deserialize<List<ModelCostTier>>(tiers) : model.Cost.Tiers,
            };
        }
        if (PiJson.GetLong(over["contextWindow"]) is { } contextWindow) result.ContextWindow = contextWindow;
        if (PiJson.GetLong(over["maxTokens"]) is { } maxTokens) result.MaxTokens = maxTokens;
        if (over["samplingParams"] is JsonObject sampling)
        {
            var merged = (JsonObject?)model.SamplingParams?.DeepClone() ?? new JsonObject();
            foreach (var (k, v) in sampling) merged[k] = v?.DeepClone();
            result.SamplingParams = merged;
        }
        result.Compat = MergeCompat(model.Compat, over["compat"] as JsonObject);
        return result;
    }

    private static Model ModelFromJson(string providerId, JsonObject definition, JsonObject providerConfig, Model? defaults)
    {
        var id = PiJson.GetString(definition["id"]) ?? "";
        var api = PiJson.GetString(definition["api"]) ?? PiJson.GetString(providerConfig["api"]) ?? defaults?.Api
            ?? throw new InvalidOperationException($"Provider {providerId}, model {id}: no \"api\" specified. Set at provider or model level.");
        var baseUrl = PiJson.GetString(definition["baseUrl"]) ?? PiJson.GetString(providerConfig["baseUrl"]) ?? defaults?.BaseUrl
            ?? throw new InvalidOperationException($"Provider {providerId}: \"baseUrl\" is required when defining custom models.");
        if (PiJson.GetNumber(definition["contextWindow"]) is <= 0) throw new InvalidOperationException($"Provider {providerId}, model {id}: invalid contextWindow");
        if (PiJson.GetNumber(definition["maxTokens"]) is <= 0) throw new InvalidOperationException($"Provider {providerId}, model {id}: invalid maxTokens");
        return new Model
        {
            Id = id,
            Name = PiJson.GetString(definition["name"]) ?? id,
            Api = api,
            Provider = providerId,
            BaseUrl = baseUrl,
            Reasoning = PiJson.GetBool(definition["reasoning"]) ?? false,
            ThinkingLevelMap = ThinkingMapFromJson(definition["thinkingLevelMap"]),
            Input = StringList(definition["input"]) ?? ["text"],
            Cost = definition["cost"] is JsonObject cost ? PiJson.Deserialize<ModelCost>(cost)! : new ModelCost(),
            ContextWindow = PiJson.GetLong(definition["contextWindow"]) ?? 128000,
            MaxTokens = PiJson.GetLong(definition["maxTokens"]) ?? 16384,
            SamplingParams = (JsonObject?)(definition["samplingParams"] as JsonObject)?.DeepClone(),
            Headers = null,
            Compat = MergeCompat(providerConfig["compat"] as JsonObject, definition["compat"] as JsonObject),
        };
    }

    private static Model? FindModelDefaults(IReadOnlyList<Model> models, string modelId, string? api) =>
        models.FirstOrDefault(m => m.Id == modelId)
        ?? (api is not null ? models.FirstOrDefault(m => m.Api == api) : null)
        ?? models.FirstOrDefault(m => m.Api == KnownApis.OpenAICompletions)
        ?? models.FirstOrDefault();

    public static List<Model> ApplyModelsJson(string providerId, IReadOnlyList<Model> baseModels, JsonObject? config)
    {
        if (config is null) return baseModels.ToList();
        var oauth = PiJson.GetString(config["oauth"]);
        var baseUrl = PiJson.GetString(config["baseUrl"]);
        if (oauth is not null && baseUrl is null) throw new InvalidOperationException($"Provider {providerId}: \"baseUrl\" is required when \"oauth\" is set.");
        var hasOverrides = config["modelOverrides"] is JsonObject { Count: > 0 };
        var hasModels = config["models"] is JsonArray { Count: > 0 };
        if (!hasModels && baseUrl is null && config["headers"] is null && config["compat"] is null && !hasOverrides
            && config["apiKey"] is null && oauth is null && config["authHeader"] is null)
        {
            throw new InvalidOperationException($"Provider {providerId}: must specify \"baseUrl\", \"headers\", \"compat\", \"modelOverrides\", or \"models\".");
        }

        var models = baseModels.Select(model =>
        {
            var copy = model.Clone();
            copy.BaseUrl = oauth == "radius" ? model.BaseUrl : baseUrl ?? model.BaseUrl;
            copy.Compat = MergeCompat(model.Compat, config["compat"] as JsonObject);
            return copy;
        }).ToList();

        foreach (var definition in (config["models"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            var id = PiJson.GetString(definition["id"]) ?? "";
            var existingIndex = models.FindIndex(m => m.Id == id);
            var defaults = FindModelDefaults(models, id, PiJson.GetString(definition["api"]) ?? PiJson.GetString(config["api"]));
            var model = ModelFromJson(providerId, definition, config, defaults);
            if (existingIndex >= 0) models[existingIndex] = model;
            else models.Add(model);
        }
        return models;
    }

    private static List<Model> ApplyExtension(string providerId, IReadOnlyList<Model> models, ProviderConfigInput? config)
    {
        if (config is null) return models.ToList();
        if (config.Models is null)
        {
            return config.BaseUrl is not null ? models.Select(m => m.WithBaseUrl(config.BaseUrl)).ToList() : models.ToList();
        }
        return config.Models.Select(definition =>
        {
            var defaults = FindModelDefaults(models, definition.Id, string.IsNullOrEmpty(definition.Api) ? config.Api : definition.Api);
            var api = (string.IsNullOrEmpty(definition.Api) ? null : definition.Api) ?? config.Api ?? defaults?.Api
                ?? throw new InvalidOperationException($"Provider {providerId}, model {definition.Id}: no \"api\" specified. Set at provider or model level.");
            var baseUrl = (string.IsNullOrEmpty(definition.BaseUrl) ? null : definition.BaseUrl) ?? config.BaseUrl ?? defaults?.BaseUrl
                ?? throw new InvalidOperationException($"Provider {providerId}: \"baseUrl\" is required when defining custom models.");
            var model = definition.Clone();
            model.Api = api;
            model.Provider = providerId;
            model.BaseUrl = baseUrl;
            model.Headers = null;
            return model;
        }).ToList();
    }

    private static OAuthAuth AdaptOAuth(ExtensionOAuthConfig config) => new()
    {
        Name = config.Name,
        IsSubscription = config.IsSubscription,
        Login = interaction => config.Login(interaction),
        Refresh = (credential, ct) => config.RefreshToken(credential, ct),
        ToAuth = credential => Task.FromResult(new ModelAuth { ApiKey = config.GetApiKey(credential) }),
    };

    private static ModelAuth WithConfiguredAuth(ModelAuth auth, IReadOnlyDictionary<string, string>? headers, bool authHeader)
    {
        Dictionary<string, string?>? merged = auth.Headers is not null || headers is not null
            ? new Dictionary<string, string?>(auth.Headers ?? [])
            : null;
        if (headers is not null)
        {
            foreach (var (k, v) in headers) merged![k] = v;
        }
        if (authHeader)
        {
            if (string.IsNullOrEmpty(auth.ApiKey)) throw new InvalidOperationException("authHeader requires a resolved API key");
            merged ??= [];
            merged["Authorization"] = $"Bearer {auth.ApiKey}";
        }
        var result = auth.Clone();
        result.Headers = merged;
        return result;
    }

    private static string? ConfiguredApiKey(JsonObject? config, ProviderConfigInput? extension) =>
        extension?.ApiKey ?? PiJson.GetString(config?["apiKey"]);

    private static Dictionary<string, string>? ConfiguredHeaders(JsonObject? config, ProviderConfigInput? extension)
    {
        var configHeaders = config?["headers"] as JsonObject;
        if (configHeaders is null && extension?.Headers is null) return null;
        var result = new Dictionary<string, string>();
        foreach (var (k, v) in configHeaders ?? []) if (PiJson.GetString(v) is { } s) result[k] = s;
        foreach (var (k, v) in extension?.Headers ?? []) result[k] = v;
        return result;
    }

    private static async Task<Dictionary<string, string>?> ConfigContextEnv(IEnumerable<string> values, IAuthContext ctx, IReadOnlyDictionary<string, string>? explicitEnv = null)
    {
        var env = new Dictionary<string, string>(explicitEnv ?? new Dictionary<string, string>());
        foreach (var name in values.SelectMany(ConfigValueResolver.GetConfigValueEnvVarNames).Distinct())
        {
            if (env.ContainsKey(name)) continue;
            var value = await ctx.EnvAsync(name);
            if (value is not null) env[name] = value;
        }
        return env.Count > 0 ? env : null;
    }

    private static ApiKeyAuth? ComposeApiKeyAuth(string providerId, IProvider? baseProvider, JsonObject? config, ProviderConfigInput? extension)
    {
        var inherited = baseProvider?.Auth.ApiKey;
        var rawKey = ConfiguredApiKey(config, extension);
        var oauth = extension?.OAuth is not null ? (object)extension.OAuth : baseProvider?.Auth.OAuth;
        if (inherited is null && rawKey is null && oauth is not null) return null;
        var rawHeaders = ConfiguredHeaders(config, extension);
        var authHeader = extension?.AuthHeader ?? PiJson.GetBool(config?["authHeader"]) ?? false;

        return new ApiKeyAuth
        {
            Name = inherited?.Name ?? "API key",
            Login = inherited?.Login ?? (async interaction => new ApiKeyCredential
            {
                Key = await interaction.PromptAsync(new AuthPrompt { Type = "secret", Message = "Enter API key" }),
            }),
            Check = async input =>
            {
                if (input.Credential is not null)
                {
                    if (inherited?.Check is not null) return await inherited.Check(input);
                    if (!string.IsNullOrEmpty(input.Credential.Key)) return new AuthCheck(AuthTypes.ApiKey, "stored credential");
                    var resolved = inherited is null ? null : await inherited.Resolve(input);
                    return resolved is not null ? new AuthCheck(AuthTypes.ApiKey, resolved.Source) : null;
                }
                if (rawKey is not null)
                {
                    if (ConfigValueResolver.IsCommandConfigValue(rawKey)) return new AuthCheck(AuthTypes.ApiKey, "configured API key");
                    foreach (var name in ConfigValueResolver.GetConfigValueEnvVarNames(rawKey))
                    {
                        if (await input.Ctx.EnvAsync(name) is null) return null;
                    }
                    return new AuthCheck(AuthTypes.ApiKey, "configured API key");
                }
                if (inherited?.Check is not null) return await inherited.Check(input);
                var fallback = inherited is null ? null : await inherited.Resolve(input);
                return fallback is not null ? new AuthCheck(AuthTypes.ApiKey, fallback.Source) : null;
            },
            Resolve = async input =>
            {
                AuthResult? result;
                if (input.Credential is not null)
                {
                    result = inherited is not null
                        ? await inherited.Resolve(input)
                        : !string.IsNullOrEmpty(input.Credential.Key)
                            ? new AuthResult { Auth = new ModelAuth { ApiKey = input.Credential.Key }, Env = input.Credential.Env, Source = "stored credential" }
                            : null;
                }
                else if (rawKey is not null)
                {
                    var env = await ConfigContextEnv([rawKey], input.Ctx);
                    var key = ConfigValueResolver.ResolveOrThrow(rawKey, $"API key for provider \"{providerId}\"", env);
                    result = inherited is not null
                        ? await inherited.Resolve(input with { Credential = new ApiKeyCredential { Key = key } })
                        : new AuthResult { Auth = new ModelAuth { ApiKey = key }, Source = "configured API key" };
                }
                else
                {
                    result = inherited is null ? null : await inherited.Resolve(input);
                }
                if (result is null) return null;
                var explicitEnv = new Dictionary<string, string>(input.Credential?.Env ?? []);
                foreach (var (k, v) in result.Env ?? []) explicitEnv[k] = v;
                var headerEnv = await ConfigContextEnv(rawHeaders?.Values ?? Enumerable.Empty<string>(), input.Ctx, explicitEnv);
                var headers = ConfigValueResolver.ResolveHeadersOrThrow(rawHeaders, $"provider \"{providerId}\"", headerEnv);
                return new AuthResult { Auth = WithConfiguredAuth(result.Auth, headers, authHeader), Env = result.Env, Source = result.Source };
            },
        };
    }

    private static OAuthAuth? ComposeOAuthAuth(string providerId, IProvider? baseProvider, JsonObject? config, ProviderConfigInput? extension)
    {
        var oauth = extension?.OAuth is not null ? AdaptOAuth(extension.OAuth) : baseProvider?.Auth.OAuth;
        if (oauth is null) return null;
        var rawHeaders = ConfiguredHeaders(config, extension);
        var authHeader = extension?.AuthHeader ?? PiJson.GetBool(config?["authHeader"]) ?? false;
        return new OAuthAuth
        {
            Name = oauth.Name,
            IsSubscription = oauth.IsSubscription,
            LoginLabel = oauth.LoginLabel,
            Login = oauth.Login,
            Refresh = oauth.Refresh,
            ToAuth = async credential =>
            {
                var auth = await oauth.ToAuth(credential);
                var env = credential.Extra["env"] is JsonObject envObj
                    ? envObj.Where(kv => PiJson.GetString(kv.Value) is not null).ToDictionary(kv => kv.Key, kv => PiJson.GetString(kv.Value)!)
                    : null;
                var headers = ConfigValueResolver.ResolveHeadersOrThrow(rawHeaders, $"provider \"{providerId}\"", env);
                return WithConfiguredAuth(auth, headers, authHeader);
            },
        };
    }

    private static Dictionary<string, string>? RawModelHeaders(Model model, JsonObject? config, ProviderConfigInput? extension)
    {
        var result = new Dictionary<string, string>();
        void Add(JsonNode? node)
        {
            foreach (var (k, v) in node as JsonObject ?? []) if (PiJson.GetString(v) is { } s) result[k] = s;
        }
        Add((config?["modelOverrides"] as JsonObject)?[model.Id]?["headers"]);
        Add((config?["models"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(m => PiJson.GetString(m["id"]) == model.Id)?["headers"]);
        foreach (var (k, v) in extension?.Models?.FirstOrDefault(m => m.Id == model.Id)?.Headers ?? []) result[k] = v;
        return result.Count > 0 ? result : null;
    }

    public static void ValidateExtensionProvider(string providerId, IProvider? baseProvider, JsonObject? modelsConfig, ProviderConfigInput extension)
    {
        if (extension.StreamSimple is not null && extension.Api is null)
            throw new InvalidOperationException($"Provider {providerId}: \"api\" is required when registering streamSimple.");
        ApplyExtension(providerId, ApplyModelsJson(providerId, baseProvider?.GetModels() ?? [], modelsConfig), extension);
    }

    private sealed class ComposedModelProvider : IProvider
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public string? BaseUrl { get; init; }
        public IReadOnlyDictionary<string, string?>? Headers { get; init; }
        public required ProviderAuth Auth { get; init; }
        public required Func<IReadOnlyList<Model>> ModelsFactory { get; init; }
        public Func<RefreshModelsContext, Task>? RefreshModels { get; init; }
        public Func<IReadOnlyList<Model>, Credential?, IReadOnlyList<Model>>? FilterModels { get; init; }
        public required Func<Model, Context, StreamOptions?, bool, AssistantMessageEventStream> StreamWith { get; init; }

        public IReadOnlyList<Model> GetModels() => ModelsFactory();

        public AssistantMessageEventStream Stream(Model model, Context context, StreamOptions? options = null) => StreamWith(model, context, options, false);

        public AssistantMessageEventStream StreamSimple(Model model, Context context, SimpleStreamOptions? options = null) => StreamWith(model, context, options, true);
    }

    /// <summary>Compose built-in, models.json and extension layers without reading credentials.</summary>
    public static IProvider ComposeModelProvider(string providerId, IProvider? baseProvider, ModelConfig modelConfig, ProviderConfigInput? extension)
    {
        var config = modelConfig.GetProvider(providerId);
        OAuthCredential? extensionOAuthCredential = null;
        List<Model>? refreshedExtensionModels = null;
        ProviderConfigInput? CurrentExtension()
        {
            if (extension is null || refreshedExtensionModels is null) return extension;
            var copy = extension.Clone();
            copy.Models = refreshedExtensionModels;
            return copy;
        }

        IReadOnlyList<Model> GetModels()
        {
            var models = ApplyExtension(providerId, ApplyModelsJson(providerId, baseProvider?.GetModels() ?? [], config), CurrentExtension());
            if (extensionOAuthCredential is not null && extension?.OAuth?.ModifyModels is { } modify) models = modify(models, extensionOAuthCredential);
            var overrides = config?["modelOverrides"] as JsonObject;
            return models.Select(m => overrides?[m.Id] is JsonObject over ? ApplyModelOverride(m, over) : m).ToList();
        }

        GetModels();
        var apiKey = ComposeApiKeyAuth(providerId, baseProvider, config, extension);
        var oauth = ComposeOAuthAuth(providerId, baseProvider, config, extension);
        if (apiKey is null && oauth is null) throw new InvalidOperationException($"Provider {providerId}: no authentication method configured.");

        bool SupportsBaseApi(Model model) => baseProvider?.GetModels().Any(e => e.Api == model.Api) ?? false;

        AssistantMessageEventStream StreamWith(Model model, Context context, StreamOptions? options, bool simple) =>
            AssistantMessageEventStream.Lazy(model, () =>
            {
                if (extension?.StreamSimple is not null && model.Api == extension.Api)
                    return Task.FromResult(extension.StreamSimple(model, context, options as SimpleStreamOptions));
                if (baseProvider is not null && SupportsBaseApi(model))
                {
                    return Task.FromResult(simple
                        ? baseProvider.StreamSimple(model, context, options as SimpleStreamOptions ?? options?.CopyTo(new SimpleStreamOptions()))
                        : baseProvider.Stream(model, context, options));
                }
                var api = ApiRegistry.Get(model.Api) ?? throw new InvalidOperationException($"No API provider registered for api: {model.Api}");
                return Task.FromResult(simple
                    ? api.StreamSimple(model, context, options as SimpleStreamOptions ?? options?.CopyTo(new SimpleStreamOptions()))
                    : api.Stream(model, context, options));
            });

        Func<RefreshModelsContext, Task>? refresh = null;
        if (baseProvider?.RefreshModels is not null || extension?.RefreshModels is not null || extension?.OAuth?.ModifyModels is not null)
        {
            refresh = async context =>
            {
                if (baseProvider?.RefreshModels is { } baseRefresh) await baseRefresh(context);
                List<Model>? refreshed = null;
                if (extension?.RefreshModels is { } extRefresh) refreshed = await extRefresh(context);
                if (context.CancellationToken.IsCancellationRequested) return;
                var oauthCredential = context.Credential as OAuthCredential;
                await context.Publish(new ModelsPublication
                {
                    Update = () =>
                    {
                        if (refreshed is not null)
                        {
                            var validation = extension!.Clone();
                            validation.Models = refreshed;
                            ApplyExtension(providerId, ApplyModelsJson(providerId, baseProvider?.GetModels() ?? [], config), validation);
                            refreshedExtensionModels = refreshed;
                        }
                        extensionOAuthCredential = oauthCredential;
                    },
                });
            };
        }

        return new ComposedModelProvider
        {
            Id = providerId,
            Name = extension?.Name ?? PiJson.GetString(config?["name"]) ?? baseProvider?.Name ?? extension?.OAuth?.Name ?? providerId,
            BaseUrl = extension?.BaseUrl ?? PiJson.GetString(config?["baseUrl"]) ?? baseProvider?.BaseUrl,
            Headers = baseProvider?.Headers,
            Auth = new ProviderAuth { ApiKey = apiKey, OAuth = oauth },
            ModelsFactory = GetModels,
            RefreshModels = refresh,
            FilterModels = baseProvider?.FilterModels,
            StreamWith = StreamWith,
        };
    }

    public static Dictionary<string, string>? ResolveConfiguredModelHeaders(Model model, JsonObject? config, ProviderConfigInput? extension, IReadOnlyDictionary<string, string>? env = null) =>
        ConfigValueResolver.ResolveHeadersOrThrow(RawModelHeaders(model, config, extension), $"model \"{model.Provider}/{model.Id}\"", env);

    public sealed record CompatibilityRequestConfig(Dictionary<string, string?>? Headers, bool AuthHeader);

    public static CompatibilityRequestConfig ResolveCompatibilityRequestConfig(Model model, JsonObject? config, ProviderConfigInput? extension)
    {
        var raw = new Dictionary<string, string>(ConfiguredHeaders(config, extension) ?? []);
        foreach (var (k, v) in RawModelHeaders(model, config, extension) ?? []) raw[k] = v;
        var configured = ConfigValueResolver.ResolveHeadersOrThrow(raw, $"model \"{model.Provider}/{model.Id}\"");
        Dictionary<string, string?>? headers = null;
        if (model.Headers is not null || configured is not null)
        {
            headers = new Dictionary<string, string?>();
            foreach (var (k, v) in model.Headers ?? []) headers[k] = v;
            foreach (var (k, v) in configured ?? []) headers[k] = v;
        }
        return new CompatibilityRequestConfig(headers, extension?.AuthHeader ?? PiJson.GetBool(config?["authHeader"]) ?? false);
    }

    public static AuthStatus? ConfiguredRequestAuthStatus(JsonObject? config, ProviderConfigInput? extension)
    {
        var value = ConfiguredApiKey(config, extension);
        if (value is null) return null;
        if (ConfigValueResolver.IsCommandConfigValue(value)) return new AuthStatus(true, "models_json_command");
        var names = ConfigValueResolver.GetConfigValueEnvVarNames(value);
        if (names.Count > 0)
        {
            return ConfigValueResolver.IsConfigValueConfigured(value) ? new AuthStatus(true, "environment", string.Join(", ", names)) : new AuthStatus(false);
        }
        return new AuthStatus(true, extension?.ApiKey is not null ? "fallback" : "models_json_key");
    }
}
