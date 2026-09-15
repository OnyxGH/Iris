using System.Net;
using System.Text.Json.Nodes;
using Iris.Ai;
using Iris.Ai.Auth;
using Iris.Ai.Json;
using Iris.Ai.Models;
using Iris.Ai.Providers;
using Iris.Ai.Utils;
using Iris.CodingAgent.Config;
using Iris.CodingAgent.Utils;

namespace Iris.CodingAgent.Core;

/// <summary>Adds a persisted pi.dev catalog overlay to a static built-in provider. Port of remote-catalog-provider.ts.</summary>
public sealed class RemoteCatalogProvider : IProvider
{
    public const long RefreshIntervalMs = 4 * 60 * 60 * 1000;
    private const int AttemptTimeoutMs = 4_000;

    private readonly IProvider _inner;
    private readonly string _catalogBaseUrl;
    private readonly long? _localGeneratedAt;
    private IReadOnlyList<Model> _dynamicModels = [];

    public RemoteCatalogProvider(IProvider inner, string? catalogBaseUrl, long? localGeneratedAt)
    {
        _inner = inner;
        _catalogBaseUrl = catalogBaseUrl ?? "https://pi.dev";
        _localGeneratedAt = localGeneratedAt;
        RefreshModels = RefreshAsync;
    }

    public string Id => _inner.Id;
    public string Name => _inner.Name;
    public string? BaseUrl => _inner.BaseUrl;
    public IReadOnlyDictionary<string, string?>? Headers => _inner.Headers;
    public ProviderAuth Auth => _inner.Auth;
    public Func<RefreshModelsContext, Task>? RefreshModels { get; }
    public Func<IReadOnlyList<Model>, Credential?, IReadOnlyList<Model>>? FilterModels => _inner.FilterModels;

    public IReadOnlyList<Model> GetModels()
    {
        var merged = _inner.GetModels().ToList();
        foreach (var model in _dynamicModels)
        {
            var index = merged.FindIndex(m => m.Id == model.Id);
            if (index >= 0) merged[index] = model;
            else merged.Add(model);
        }
        return merged;
    }

    public AssistantMessageEventStream Stream(Model model, Context context, StreamOptions? options = null) => _inner.Stream(model, context, options);

    public AssistantMessageEventStream StreamSimple(Model model, Context context, SimpleStreamOptions? options = null) => _inner.StreamSimple(model, context, options);

    private IReadOnlyList<Model> RemoteModels(ModelsStoreEntry? entry)
    {
        if (entry is null) return [];
        if (_localGeneratedAt is not null && (entry.LastModified is null || entry.LastModified <= _localGeneratedAt)) return [];
        return entry.Models;
    }

    private static List<Model> ParseCatalog(string providerId, JsonNode? value)
    {
        IEnumerable<JsonNode?>? entries = value switch
        {
            JsonArray arr => arr,
            JsonObject obj when obj["models"] is JsonArray models => models,
            JsonObject obj => obj.Select(kv => kv.Value),
            _ => null,
        };
        if (entries is null) throw new InvalidOperationException($"Invalid model catalog for provider \"{providerId}\"");
        return entries.OfType<JsonObject>().Where(o => o.ContainsKey("id")).Select(o =>
        {
            var model = PiJson.Deserialize<Model>(o)!;
            model.Provider = providerId;
            return model;
        }).ToList();
    }

    private async Task RefreshAsync(RefreshModelsContext context)
    {
        var stored = context.Stored;
        var restored = RemoteModels(stored).Where(m => m.Provider == Id).ToList();
        if (!await context.Publish(new ModelsPublication { Update = () => _dynamicModels = restored })) return;
        if (!context.AllowNetwork || context.CancellationToken.IsCancellationRequested) return;
        if (context.Force != true && stored?.CheckedAt is not null && stored.LastModified is not null && TimeUtil.NowMs() - stored.CheckedAt < RefreshIntervalMs) return;

        var validator = stored is { Models.Count: > 0 } ? stored.Etag : null;
        var url = new Uri(new Uri(_catalogBaseUrl), $"/api/models/providers/{Uri.EscapeDataString(Id)}");
        using var response = await ManagementHttp.FetchWithRetryAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("accept", "application/json");
            request.Headers.TryAddWithoutValidation("User-Agent", PiUserAgent.Get());
            if (validator is not null) request.Headers.TryAddWithoutValidation("if-none-match", validator);
            return request;
        }, context.CancellationToken, attemptTimeoutMs: AttemptTimeoutMs);
        if (context.CancellationToken.IsCancellationRequested) return;
        var checkedAt = TimeUtil.NowMs();
        if (response.StatusCode == HttpStatusCode.NotModified && stored is not null)
        {
            var next = stored.Clone();
            next.CheckedAt = checkedAt;
            await context.Publish(new ModelsPublication { HasPersist = true, Persist = next });
            return;
        }
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NotImplemented)
        {
            var next = stored?.Clone() ?? new ModelsStoreEntry();
            next.CheckedAt = checkedAt;
            next.LastModified = 0;
            next.Etag = null;
            await context.Publish(new ModelsPublication { HasPersist = true, Persist = next });
            return;
        }
        if (!response.IsSuccessStatusCode)
        {
            var next = stored?.Clone() ?? new ModelsStoreEntry();
            next.CheckedAt = checkedAt;
            await context.Publish(new ModelsPublication { HasPersist = true, Persist = next });
            throw new InvalidOperationException($"Model catalog request failed for {Id}: {(int)response.StatusCode}");
        }
        var body = await response.Content.ReadAsStringAsync(context.CancellationToken);
        var refreshed = ParseCatalog(Id, JsonNode.Parse(body));
        var lastModified = response.Content.Headers.LastModified?.ToUnixTimeMilliseconds() ?? 0;
        var entry = new ModelsStoreEntry
        {
            Models = refreshed,
            CheckedAt = checkedAt,
            LastModified = lastModified,
            Etag = response.Headers.ETag?.ToString(),
        };
        var published = RemoteModels(entry);
        await context.Publish(new ModelsPublication { HasPersist = true, Persist = entry, Update = () => _dynamicModels = published });
    }
}

public sealed class CreateModelRuntimeOptions
{
    public ICredentialStore? Credentials { get; init; }
    public string? AuthPath { get; init; }
    /// <summary>Path to models.json. Use <see cref="DisableModelsJson"/> to skip.</summary>
    public string? ModelsPath { get; init; }
    public bool DisableModelsJson { get; init; }
    public IModelsStore? ModelsStore { get; init; }
    public string? ModelsStorePath { get; init; }
    public bool AllowModelNetwork { get; init; }
    public int? ModelRefreshTimeoutMs { get; init; }
    public string? CatalogBaseUrl { get; init; }
    public bool RefreshOnCreate { get; init; } = true;
    public IEnumerable<IProvider>? BuiltinProviders { get; init; }
    public CancellationToken CancellationToken { get; init; }
}

/// <summary>Configured Models collection used by the coding agent. Port of core/model-runtime.ts.</summary>
public sealed class ModelRuntime
{
    private readonly ModelsCollection _models;
    private readonly RuntimeCredentials _credentials;
    private readonly Dictionary<string, IProvider> _builtins = new();
    private readonly Dictionary<string, IProvider> _nativeExtensionProviders = new();
    private readonly Dictionary<string, ProviderConfigInput> _extensionProviders = new();
    private readonly Dictionary<string, string> _compositionErrors = new();
    private readonly string? _modelsPath;
    private readonly object _gate = new();
    private ModelConfig _config;
    private IReadOnlyList<Model> _all = [];
    private IReadOnlyList<Model> _available = [];
    private HashSet<string> _configuredProviders = [];
    private HashSet<string> _storedProviders = [];
    private Dictionary<string, AuthCheck?> _auth = new();
    private string? _availabilityError;

    private ModelRuntime(RuntimeCredentials credentials, ModelConfig config, string? modelsPath, IModelsStore modelsStore, IEnumerable<IProvider> providers, bool networkEnabled)
    {
        _credentials = credentials;
        _config = config;
        _modelsPath = modelsPath;
        ModelNetworkEnabled = networkEnabled;
        foreach (var provider in providers) _builtins[provider.Id] = provider;
        _models = new ModelsCollection(new CreateModelsOptions { Credentials = credentials, ModelsStore = modelsStore });
        RebuildProviders();
    }

    public bool ModelNetworkEnabled { get; }

    public static async Task<ModelRuntime> CreateAsync(CreateModelRuntimeOptions? options = null)
    {
        options ??= new CreateModelRuntimeOptions();
        var credentials = new RuntimeCredentials(options.Credentials ?? AuthStorage.Create(options.AuthPath));
        var modelsPath = options.DisableModelsJson ? null : options.ModelsPath ?? Path.Combine(AppConfig.AgentDir, "models.json");
        var config = await ModelConfig.LoadAsync(modelsPath);
        var modelsStore = options.ModelsStore
            ?? (modelsPath is not null
                ? new FileModelsStore(options.ModelsStorePath ?? Path.Combine(Path.GetDirectoryName(modelsPath)!, "models-store.json"))
                : new InMemoryModelsStore());
        var generatedAt = BuiltinCatalog.GeneratedAt;
        var providers = (options.BuiltinProviders ?? BuiltinProviders.All())
            .Select(p => (IProvider)new RemoteCatalogProvider(p, options.CatalogBaseUrl, generatedAt))
            .ToList();
        var runtime = new ModelRuntime(credentials, config, modelsPath, modelsStore, providers, Environment.GetEnvironmentVariable("PI_OFFLINE") is null);

        var refreshFromNetwork = runtime.ModelNetworkEnabled && options.AllowModelNetwork;
        using var timeout = refreshFromNetwork && options.ModelRefreshTimeoutMs is not null ? new CancellationTokenSource(options.ModelRefreshTimeoutMs.Value) : null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(options.CancellationToken, timeout?.Token ?? CancellationToken.None);
        if (options.RefreshOnCreate)
        {
            await runtime.RefreshAsync(new ModelsRefreshOptions { AllowNetwork = refreshFromNetwork, CancellationToken = linked.Token });
        }
        return runtime;
    }

    private IEnumerable<string> ProviderIds()
    {
        lock (_gate) return _builtins.Keys.Concat(_nativeExtensionProviders.Keys).Concat(_config.ProviderIds).Concat(_extensionProviders.Keys).Distinct().ToList();
    }

    private void RecomposeProvider(string providerId)
    {
        IProvider? baseProvider;
        ProviderConfigInput? extension;
        JsonObject? config;
        lock (_gate)
        {
            baseProvider = _nativeExtensionProviders.GetValueOrDefault(providerId) ?? _builtins.GetValueOrDefault(providerId);
            extension = _extensionProviders.GetValueOrDefault(providerId);
            config = _config.GetProvider(providerId);
        }
        if (baseProvider is null && config is null && extension is null)
        {
            _models.DeleteProvider(providerId);
            lock (_gate) _compositionErrors.Remove(providerId);
            return;
        }
        if (baseProvider is not null && config is null && extension is null)
        {
            _models.SetProvider(baseProvider);
            lock (_gate) _compositionErrors.Remove(providerId);
            return;
        }
        try
        {
            _models.SetProvider(ProviderComposer.ComposeModelProvider(providerId, baseProvider, _config, extension));
            lock (_gate) _compositionErrors.Remove(providerId);
        }
        catch (Exception ex)
        {
            lock (_gate) _compositionErrors[providerId] = ex.Message;
            if (baseProvider is not null) _models.SetProvider(baseProvider);
            else _models.DeleteProvider(providerId);
        }
    }

    private void RebuildProviders()
    {
        _models.ClearProviders();
        lock (_gate) _compositionErrors.Clear();
        foreach (var id in ProviderIds()) RecomposeProvider(id);
        UpdateModelSnapshot();
    }

    private void UpdateModelSnapshot()
    {
        var all = _models.GetModels();
        lock (_gate)
        {
            _all = all;
            _available = all.Where(m => _configuredProviders.Contains(m.Provider)).ToList();
        }
    }

    private async Task RefreshAvailabilityAsync(CancellationToken ct)
    {
        try
        {
            var providers = _models.GetProviders();
            var available = await _models.GetAvailableAsync(null, ct);
            var checks = await Task.WhenAll(providers.Select(async p => (p.Id, Check: await SafeCheck(p.Id, ct))));
            var credentials = await _credentials.ListAsync(ct);
            lock (_gate)
            {
                _all = _models.GetModels();
                _available = available;
                _auth = checks.ToDictionary(c => c.Id, c => c.Check);
                _configuredProviders = checks.Where(c => c.Check is not null).Select(c => c.Id).ToHashSet();
                _storedProviders = credentials.Select(c => c.ProviderId).ToHashSet();
                _availabilityError = null;
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            lock (_gate) _availabilityError = ex.Message;
        }
    }

    private async Task<AuthCheck?> SafeCheck(string providerId, CancellationToken ct)
    {
        try
        {
            return await _models.CheckAuthAsync(providerId, ct);
        }
        catch
        {
            return null;
        }
    }

    public IReadOnlyList<IProvider> GetProviders() => _models.GetProviders();

    public IProvider? GetProvider(string providerId) => _models.GetProvider(providerId);

    public IReadOnlyList<Model> GetModels(string? providerId = null) => _models.GetModels(providerId);

    public Model? GetModel(string providerId, string modelId) => _models.GetModel(providerId, modelId);

    public Task<AuthCheck?> CheckAuthAsync(string providerId, CancellationToken ct = default) => _models.CheckAuthAsync(providerId, ct);

    public async Task<IReadOnlyList<Model>> GetAvailableAsync(string? providerId = null, CancellationToken ct = default)
    {
        if (providerId is not null) return await _models.GetAvailableAsync(providerId, ct);
        await RefreshAvailabilityAsync(ct);
        lock (_gate) return _available;
    }

    public IReadOnlyList<Model> AvailableSnapshot
    {
        get
        {
            lock (_gate) return _available;
        }
    }

    public string? GetError()
    {
        var errors = new List<string>();
        lock (_gate)
        {
            if (_config.Error is not null) errors.Add(_config.Error);
            foreach (var (id, error) in _compositionErrors) errors.Add($"Provider \"{id}\": {error}");
            if (_availabilityError is not null) errors.Add($"Availability refresh: {_availabilityError}");
        }
        return errors.Count > 0 ? string.Join("\n\n", errors) : null;
    }

    public ProviderConfigInput? GetRegisteredProviderConfig(string providerId)
    {
        lock (_gate) return _extensionProviders.GetValueOrDefault(providerId);
    }

    public IReadOnlyList<string> GetRegisteredProviderIds()
    {
        lock (_gate) return _extensionProviders.Keys.Concat(_nativeExtensionProviders.Keys).Distinct().ToList();
    }

    public ProviderComposer.CompatibilityRequestConfig GetCompatibilityRequestConfig(Model model)
    {
        lock (_gate) return ProviderComposer.ResolveCompatibilityRequestConfig(model, _config.GetProvider(model.Provider), _extensionProviders.GetValueOrDefault(model.Provider));
    }

    public bool IsUsingOAuth(string providerId)
    {
        lock (_gate) return _auth.GetValueOrDefault(providerId)?.Type == AuthTypes.OAuth;
    }

    public bool IsUsingSubscription(string providerId) => IsUsingOAuth(providerId) && _models.GetProvider(providerId)?.Auth.OAuth?.IsSubscription == true;

    public bool HasConfiguredAuth(string providerId)
    {
        lock (_gate) return _configuredProviders.Contains(providerId);
    }

    public Task<AuthResult?> GetAuthAsync(string providerId, AuthResolutionOverrides? overrides = null) => _models.GetAuthAsync(providerId, overrides);

    public async Task<AuthResult?> GetAuthAsync(Model model, AuthResolutionOverrides? overrides = null)
    {
        var resolution = await _models.GetAuthAsync(model, overrides);
        if (resolution is null) return null;
        JsonObject? config;
        ProviderConfigInput? extension;
        lock (_gate)
        {
            config = _config.GetProvider(model.Provider);
            extension = _extensionProviders.GetValueOrDefault(model.Provider);
        }
        var env = new Dictionary<string, string>(resolution.Env ?? []);
        foreach (var (k, v) in overrides?.Env ?? []) env[k] = v;
        var configured = ProviderComposer.ResolveConfiguredModelHeaders(model, config, extension, env);
        var auth = resolution.Auth.Clone();
        auth.Headers = HeaderUtils.Merge(auth.Headers, configured?.ToDictionary(kv => kv.Key, kv => (string?)kv.Value));
        return new AuthResult { Auth = auth, Env = resolution.Env, Source = resolution.Source };
    }

    public async Task SetRuntimeApiKeyAsync(string providerId, string apiKey, CancellationToken ct = default)
    {
        _credentials.SetRuntimeApiKey(providerId, apiKey);
        await SynchronizeCredentialStateAsync(providerId, ct);
    }

    public async Task RemoveRuntimeApiKeyAsync(string providerId, CancellationToken ct = default)
    {
        _credentials.RemoveRuntimeApiKey(providerId);
        await SynchronizeCredentialStateAsync(providerId, ct);
    }

    public Task<IReadOnlyList<CredentialInfo>> ListCredentialsAsync(CancellationToken ct = default) => _credentials.ListAsync(ct);

    public AuthStatus GetProviderAuthStatus(string providerId)
    {
        if (_credentials.HasRuntimeApiKey(providerId)) return new AuthStatus(true, "runtime");
        lock (_gate)
        {
            if (_storedProviders.Contains(providerId)) return new AuthStatus(true, "stored");
            var configured = ProviderComposer.ConfiguredRequestAuthStatus(_config.GetProvider(providerId), _extensionProviders.GetValueOrDefault(providerId));
            if (configured is not null) return configured;
            var check = _auth.GetValueOrDefault(providerId);
            return check is not null ? new AuthStatus(true, "environment", check.Source) : new AuthStatus(false);
        }
    }

    private async Task<(IProvider Provider, Model Model, T Options)> PrepareRequestAsync<T>(Model model, T options) where T : StreamOptions
    {
        var provider = _models.GetProvider(model.Provider) ?? throw new ModelsException(ModelsErrorCode.Provider, $"Unknown provider: {model.Provider}");
        var resolution = await GetAuthAsync(model, new AuthResolutionOverrides { ApiKey = options.ApiKey, Env = options.Env, CancellationToken = options.CancellationToken })
            ?? throw new ModelsException(ModelsErrorCode.Auth, $"Provider is not configured: {model.Provider}");
        var headers = HeaderUtils.Merge(resolution.Auth.Headers, options.Headers);
        if (options is IHeaderTransform { TransformHeaders: { } transform }) headers = await transform(headers ?? []);
        options.Headers = headers;
        if (resolution.Env is not null || options.Env is not null)
        {
            var env = new Dictionary<string, string>(resolution.Env ?? []);
            foreach (var (k, v) in options.Env ?? []) env[k] = v;
            options.Env = env;
        }
        options.ApiKey ??= resolution.Auth.ApiKey;
        var requestModel = resolution.Auth.BaseUrl is not null ? model.WithBaseUrl(resolution.Auth.BaseUrl) : model;
        return (provider, requestModel, options);
    }

    public AssistantMessageEventStream Stream(Model model, Context context, StreamOptions? options = null) =>
        AssistantMessageEventStream.Lazy(model, async () =>
        {
            var (provider, requestModel, requestOptions) = await PrepareRequestAsync(model, options?.ShallowClone() ?? new StreamOptions());
            return provider.Stream(requestModel, context, requestOptions);
        });

    public Task<AssistantMessage> CompleteAsync(Model model, Context context, StreamOptions? options = null) => Stream(model, context, options).Result();

    public AssistantMessageEventStream StreamSimple(Model model, Context context, SimpleStreamOptions? options = null) =>
        AssistantMessageEventStream.Lazy(model, async () =>
        {
            var (provider, requestModel, requestOptions) = await PrepareRequestAsync(model, options?.CloneSimple() ?? new SimpleStreamOptions());
            return provider.StreamSimple(requestModel, context, requestOptions);
        });

    public Task<AssistantMessage> CompleteSimpleAsync(Model model, Context context, SimpleStreamOptions? options = null) => StreamSimple(model, context, options).Result();

    public async Task<Credential> LoginAsync(string providerId, string type, IAuthInteraction interaction)
    {
        var credential = await _models.LoginAsync(providerId, type, interaction);
        await SynchronizeCredentialStateAsync(providerId, interaction.CancellationToken);
        return credential;
    }

    public async Task LogoutAsync(string providerId, CancellationToken ct = default)
    {
        await _models.LogoutAsync(providerId, ct);
        await SynchronizeCredentialStateAsync(providerId, ct);
    }

    private async Task SynchronizeCredentialStateAsync(string providerId, CancellationToken ct)
    {
        RecomposeProvider(providerId);
        await _models.RefreshAsync(new ModelsRefreshOptions { AllowNetwork = false, Providers = [providerId], CancellationToken = ct });
        UpdateModelSnapshot();
        await RefreshAvailabilityAsync(ct);
    }

    public async Task<ModelsRefreshResult> RefreshAsync(ModelsRefreshOptions? options = null)
    {
        options ??= new ModelsRefreshOptions();
        var config = await ModelConfig.LoadAsync(_modelsPath);
        lock (_gate) _config = config;
        if (options.Providers is not null)
        {
            foreach (var id in options.Providers.Distinct()) RecomposeProvider(id);
            UpdateModelSnapshot();
        }
        else
        {
            RebuildProviders();
        }
        var result = await _models.RefreshAsync(new ModelsRefreshOptions
        {
            AllowNetwork = options.AllowNetwork ?? ModelNetworkEnabled,
            Providers = options.Providers,
            Force = options.Force,
            CancellationToken = options.CancellationToken,
        });
        UpdateModelSnapshot();
        await RefreshAvailabilityAsync(options.CancellationToken);
        return result;
    }

    public void RegisterNativeProvider(IProvider provider)
    {
        if (string.IsNullOrWhiteSpace(provider.Id)) throw new ArgumentException("Provider id must not be empty.");
        lock (_gate)
        {
            _extensionProviders.Remove(provider.Id);
            _nativeExtensionProviders[provider.Id] = provider;
        }
        RecomposeProvider(provider.Id);
        UpdateModelSnapshot();
        _ = RefreshAsync(new ModelsRefreshOptions { AllowNetwork = false });
    }

    public void RegisterProvider(string providerId, ProviderConfigInput config)
    {
        IProvider? baseProvider;
        JsonObject? modelsConfig;
        lock (_gate)
        {
            baseProvider = _builtins.GetValueOrDefault(providerId);
            modelsConfig = _config.GetProvider(providerId);
        }
        ProviderComposer.ValidateExtensionProvider(providerId, baseProvider, modelsConfig, config);
        lock (_gate)
        {
            _nativeExtensionProviders.Remove(providerId);
            var effective = _extensionProviders.GetValueOrDefault(providerId)?.Clone() ?? new ProviderConfigInput();
            if (config.Name is not null) effective.Name = config.Name;
            if (config.BaseUrl is not null) effective.BaseUrl = config.BaseUrl;
            if (config.ApiKey is not null) effective.ApiKey = config.ApiKey;
            if (config.Api is not null) effective.Api = config.Api;
            if (config.StreamSimple is not null) effective.StreamSimple = config.StreamSimple;
            if (config.Headers is not null) effective.Headers = config.Headers;
            if (config.AuthHeader is not null) effective.AuthHeader = config.AuthHeader;
            if (config.OAuth is not null) effective.OAuth = config.OAuth;
            if (config.Models is not null) effective.Models = config.Models;
            if (config.RefreshModels is not null) effective.RefreshModels = config.RefreshModels;
            _extensionProviders[providerId] = effective;
        }
        RecomposeProvider(providerId);
        UpdateModelSnapshot();
        lock (_gate)
        {
            var effective = _extensionProviders[providerId];
            if (_storedProviders.Contains(providerId) || ProviderComposer.ConfiguredRequestAuthStatus(_config.GetProvider(providerId), effective)?.Configured == true)
            {
                _configuredProviders.Add(providerId);
                if (_auth.GetValueOrDefault(providerId) is null)
                {
                    _auth[providerId] = new AuthCheck(effective.OAuth is not null && effective.ApiKey is null ? AuthTypes.OAuth : AuthTypes.ApiKey, "configured provider");
                }
                _available = _all.Where(m => _configuredProviders.Contains(m.Provider)).ToList();
            }
        }
        _ = RefreshAsync(new ModelsRefreshOptions { AllowNetwork = false });
    }

    public void UnregisterProvider(string providerId)
    {
        lock (_gate)
        {
            _extensionProviders.Remove(providerId);
            _nativeExtensionProviders.Remove(providerId);
        }
        RecomposeProvider(providerId);
        UpdateModelSnapshot();
        _ = RefreshAsync(new ModelsRefreshOptions { AllowNetwork = false });
    }
}

/// <summary>Synchronous compatibility facade over <see cref="ModelRuntime"/>. Port of core/model-registry.ts.</summary>
public sealed class ModelRegistry(ModelRuntime runtime)
{
    public ModelRuntime Runtime => runtime;

    public Task<ModelsRefreshResult> RefreshAsync(ModelsRefreshOptions? options = null) => runtime.RefreshAsync(options);

    public string? GetError() => runtime.GetError();

    public List<Model> GetAll() => [.. runtime.GetModels()];

    public List<Model> GetAvailable() => [.. runtime.AvailableSnapshot];

    public Model? Find(string provider, string modelId) => runtime.GetModel(provider, modelId);

    public bool HasConfiguredAuth(Model model) => runtime.HasConfiguredAuth(model.Provider);

    public sealed record ResolvedRequestAuth(bool Ok, string? ApiKey = null, Dictionary<string, string?>? Headers = null, string? BaseUrl = null, Dictionary<string, string>? Env = null, string? Error = null);

    public async Task<ResolvedRequestAuth> GetApiKeyAndHeadersAsync(Model model)
    {
        try
        {
            var resolution = await runtime.GetAuthAsync(model);
            if (resolution is null)
            {
                var compatibility = runtime.GetCompatibilityRequestConfig(model);
                if (compatibility.AuthHeader) return new ResolvedRequestAuth(false, Error: $"No API key found for \"{model.Provider}\"");
                return new ResolvedRequestAuth(true, Headers: compatibility.Headers);
            }
            return new ResolvedRequestAuth(true, resolution.Auth.ApiKey, resolution.Auth.Headers, resolution.Auth.BaseUrl, resolution.Env);
        }
        catch (Exception ex)
        {
            var message = ex.InnerException?.Message ?? ex.Message;
            return new ResolvedRequestAuth(false, Error: message == "authHeader requires a resolved API key" ? $"No API key found for \"{model.Provider}\"" : message);
        }
    }

    public AuthStatus GetProviderAuthStatus(string provider) => runtime.GetProviderAuthStatus(provider);

    public IProvider? GetProvider(string provider) => runtime.GetProvider(provider);

    public AssistantMessageEventStream Stream(Model model, Context context, StreamOptions? options = null) => runtime.Stream(model, context, options);

    public AssistantMessageEventStream StreamSimple(Model model, Context context, SimpleStreamOptions? options = null) => runtime.StreamSimple(model, context, options);

    public Task<AssistantMessage> CompleteAsync(Model model, Context context, StreamOptions? options = null) => runtime.CompleteAsync(model, context, options);

    public string GetProviderDisplayName(string provider) => runtime.GetProvider(provider)?.Name ?? provider;

    public Task<AuthResult?> GetProviderAuthAsync(string provider) => runtime.GetAuthAsync(provider);

    public async Task<string?> GetApiKeyForProviderAsync(string provider)
    {
        try
        {
            return (await runtime.GetAuthAsync(provider))?.Auth.ApiKey;
        }
        catch
        {
            return null;
        }
    }

    public bool IsUsingOAuth(Model model) => runtime.IsUsingOAuth(model.Provider);

    public void RegisterProvider(IProvider provider) => runtime.RegisterNativeProvider(provider);

    public void RegisterProvider(string providerName, ProviderConfigInput config) => runtime.RegisterProvider(providerName, config);

    public void UnregisterProvider(string providerName) => runtime.UnregisterProvider(providerName);
}
