using Iris.Ai.Auth;

namespace Iris.Ai.Models;

public sealed class ModelsRefreshOptions
{
    public bool? AllowNetwork { get; init; }
    /// <summary>Restrict refresh to these provider ids.</summary>
    public IReadOnlyList<string>? Providers { get; init; }
    public bool? Force { get; init; }
    public CancellationToken CancellationToken { get; init; }
}

public sealed record ModelsRefreshResult(bool Aborted, IReadOnlyDictionary<string, Exception> Errors);

/// <summary>Request option extension used by Models: transform assembled headers before dispatch.</summary>
public interface IHeaderTransform
{
    Func<Dictionary<string, string?>, Task<Dictionary<string, string?>>>? TransformHeaders { get; }
}

/// <summary>Runtime collection of providers plus auth application and stream convenience.</summary>
public interface IModels
{
    IReadOnlyList<IProvider> GetProviders();
    IProvider? GetProvider(string id);
    IReadOnlyList<Model> GetModels(string? provider = null);
    Model? GetModel(string provider, string id);
    Task<ModelsRefreshResult> RefreshAsync(ModelsRefreshOptions? options = null);
    Task<AuthCheck?> CheckAuthAsync(string providerId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Model>> GetAvailableAsync(string? providerId = null, CancellationToken cancellationToken = default);
    Task<AuthResult?> GetAuthAsync(string providerId, AuthResolutionOverrides? overrides = null);
    Task<AuthResult?> GetAuthAsync(Model model, AuthResolutionOverrides? overrides = null);
    Task<Credential> LoginAsync(string providerId, string type, IAuthInteraction interaction);
    Task LogoutAsync(string providerId, CancellationToken cancellationToken = default);
    AssistantMessageEventStream Stream(Model model, Context context, StreamOptions? options = null);
    Task<AssistantMessage> CompleteAsync(Model model, Context context, StreamOptions? options = null);
    AssistantMessageEventStream StreamSimple(Model model, Context context, SimpleStreamOptions? options = null);
    Task<AssistantMessage> CompleteSimpleAsync(Model model, Context context, SimpleStreamOptions? options = null);
}

public interface IMutableModels : IModels
{
    void SetProvider(IProvider provider);
    void DeleteProvider(string id);
    void ClearProviders();
}

public sealed class CreateModelsOptions
{
    public ICredentialStore? Credentials { get; init; }
    public IModelsStore? ModelsStore { get; init; }
    public IAuthContext? AuthContext { get; init; }
}

public static class HeaderUtils
{
    /// <summary>Case-insensitive header merge where override names replace base names.</summary>
    public static Dictionary<string, string?>? Merge(IReadOnlyDictionary<string, string?>? baseHeaders, IReadOnlyDictionary<string, string?>? overrideHeaders)
    {
        if (baseHeaders is null && overrideHeaders is null) return null;
        var merged = new Dictionary<string, string?>(baseHeaders ?? new Dictionary<string, string?>());
        foreach (var (name, value) in overrideHeaders ?? new Dictionary<string, string?>())
        {
            foreach (var existing in merged.Keys.Where(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                merged.Remove(existing);
            }
            merged[name] = value;
        }
        return merged;
    }

    public static Dictionary<string, string?> FromModelHeaders(IReadOnlyDictionary<string, string>? headers) =>
        headers?.ToDictionary(kv => kv.Key, kv => (string?)kv.Value) ?? new Dictionary<string, string?>();
}

/// <summary>Default <see cref="IMutableModels"/> implementation (ModelsImpl in pi-ai).</summary>
public sealed class ModelsCollection : IMutableModels
{
    private readonly Dictionary<string, IProvider> _providers = new();
    private readonly ICredentialStore _credentials;
    private readonly IModelsStore _modelsStore;
    private readonly IAuthContext _authContext;
    private readonly Dictionary<string, int> _refreshGenerations = new();
    private readonly Dictionary<string, CancellationTokenSource> _refreshControllers = new();
    private readonly KeyedSerialQueue _publicationQueue = new();
    private readonly object _gate = new();

    public ModelsCollection(CreateModelsOptions? options = null)
    {
        _credentials = options?.Credentials ?? new InMemoryCredentialStore();
        _modelsStore = options?.ModelsStore ?? new InMemoryModelsStore();
        _authContext = options?.AuthContext ?? DefaultAuthContext.Instance;
    }

    public void SetProvider(IProvider provider)
    {
        lock (_gate)
        {
            SupersedeProviderRefresh(provider.Id);
            _providers[provider.Id] = provider;
        }
    }

    public void DeleteProvider(string id)
    {
        lock (_gate)
        {
            SupersedeProviderRefresh(id);
            _providers.Remove(id);
        }
    }

    public void ClearProviders()
    {
        lock (_gate)
        {
            foreach (var id in _providers.Keys.Concat(_refreshControllers.Keys).Distinct().ToList()) SupersedeProviderRefresh(id);
            _providers.Clear();
        }
    }

    public IReadOnlyList<IProvider> GetProviders()
    {
        lock (_gate) return _providers.Values.ToList();
    }

    public IProvider? GetProvider(string id)
    {
        lock (_gate) return _providers.GetValueOrDefault(id);
    }

    public IReadOnlyList<Model> GetModels(string? provider = null)
    {
        if (provider is not null)
        {
            var entry = GetProvider(provider);
            if (entry is null) return [];
            try
            {
                return entry.GetModels();
            }
            catch
            {
                return [];
            }
        }
        var models = new List<Model>();
        foreach (var entry in GetProviders())
        {
            try
            {
                models.AddRange(entry.GetModels());
            }
            catch
            {
                // Best-effort: ill-behaved providers yield no models.
            }
        }
        return models;
    }

    public Model? GetModel(string provider, string id) => GetModels(provider).FirstOrDefault(m => m.Id == id);

    private int SupersedeProviderRefresh(string providerId)
    {
        var generation = _refreshGenerations.GetValueOrDefault(providerId) + 1;
        _refreshGenerations[providerId] = generation;
        if (_refreshControllers.Remove(providerId, out var previous)) previous.Cancel();
        return generation;
    }

    private async Task<bool> PublishProviderModelsAsync(string providerId, int generation, CancellationToken ct, ModelsPublication publication)
    {
        return await _publicationQueue.RunAsync(providerId, async () =>
        {
            bool Stale()
            {
                lock (_gate) return ct.IsCancellationRequested || _refreshGenerations.GetValueOrDefault(providerId) != generation;
            }
            if (Stale()) return false;
            if (publication.HasPersist)
            {
                if (publication.Persist is null) await _modelsStore.DeleteAsync(providerId, ct);
                else await _modelsStore.WriteAsync(providerId, publication.Persist.Clone(), ct);
            }
            if (Stale()) return false;
            publication.Update?.Invoke();
            return true;
        }, ct);
    }

    private async Task RunProviderRefreshPhaseAsync(IProvider provider, Credential? credential, bool allowNetwork, bool? force, int generation, CancellationToken ct)
    {
        var stored = await _modelsStore.ReadAsync(provider.Id, ct);
        await provider.RefreshModels!(new RefreshModelsContext
        {
            Credential = credential,
            Stored = stored?.Clone(),
            Publish = publication => PublishProviderModelsAsync(provider.Id, generation, ct, publication),
            AllowNetwork = allowNetwork,
            Force = allowNetwork ? force : null,
            CancellationToken = ct,
        });
    }

    public async Task<ModelsRefreshResult> RefreshAsync(ModelsRefreshOptions? options = null)
    {
        options ??= new ModelsRefreshOptions();
        var allowNetwork = options.AllowNetwork ?? true;
        var callerToken = options.CancellationToken;
        var errors = new Dictionary<string, Exception>();
        if (callerToken.IsCancellationRequested) return new ModelsRefreshResult(true, errors);
        var selected = options.Providers?.ToHashSet();
        var refreshable = GetProviders().Where(p => p.RefreshModels is not null && (selected is null || selected.Contains(p.Id))).ToList();

        var tasks = refreshable.Select(async provider =>
        {
            int generation;
            CancellationTokenSource controller;
            lock (_gate)
            {
                generation = SupersedeProviderRefresh(provider.Id);
                controller = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
                _refreshControllers[provider.Id] = controller;
            }
            var ct = controller.Token;
            try
            {
                Credential? storedCredential = null;
                Exception? credentialError = null;
                try
                {
                    storedCredential = await AuthResolver.ReadCredentialAsync(_credentials, provider.Id, ct);
                }
                catch (Exception ex)
                {
                    credentialError = ex;
                }

                await RunProviderRefreshPhaseAsync(provider, storedCredential, false, null, generation, ct);
                if (credentialError is not null) throw credentialError;
                if (!allowNetwork || ct.IsCancellationRequested) return;

                var credential = await ResolveRefreshCredentialAsync(provider, storedCredential, ct);
                if (credential is null) return;
                await RunProviderRefreshPhaseAsync(provider, credential, true, options.Force, generation, ct);
            }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                {
                    lock (errors)
                    {
                        errors[provider.Id] = ex is OperationCanceledException
                            ? new ModelsException(ModelsErrorCode.ModelSource, $"Model refresh failed for {provider.Id}", ex)
                            : ex;
                    }
                }
            }
            finally
            {
                lock (_gate)
                {
                    if (_refreshControllers.TryGetValue(provider.Id, out var current) && ReferenceEquals(current, controller))
                    {
                        _refreshControllers.Remove(provider.Id);
                    }
                }
                controller.Dispose();
            }
        }).ToList();

        try
        {
            await Task.WhenAll(tasks).WaitAsync(callerToken);
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
        }

        Dictionary<string, Exception> snapshot;
        lock (errors) snapshot = new Dictionary<string, Exception>(errors);
        return new ModelsRefreshResult(callerToken.IsCancellationRequested, snapshot);
    }

    private async Task<Credential?> ResolveRefreshCredentialAsync(IProvider provider, Credential? stored, CancellationToken ct)
    {
        if (stored is OAuthCredential oauthStored)
        {
            var oauth = provider.Auth.OAuth;
            if (oauth is null) return null;
            if (TimeUtil.NowMs() < oauthStored.Expires) return stored;
            if (ct.IsCancellationRequested) return null;
            var post = await _credentials.ModifyAsync(provider.Id, async current =>
            {
                if (current is not OAuthCredential c || TimeUtil.NowMs() < c.Expires) return null;
                return await oauth.Refresh(c, ct);
            }, ct);
            return post as OAuthCredential;
        }

        var apiKey = provider.Auth.ApiKey;
        if (apiKey is null) return null;
        var credential = stored as ApiKeyCredential;
        var result = await apiKey.Resolve(new ApiKeyAuthInput(_authContext, credential, ct));
        if (result is null) return null;
        return new ApiKeyCredential { Key = result.Auth.ApiKey, Env = result.Env };
    }

    private async Task<AuthCheck?> CheckProviderAuthAsync(IProvider provider, Credential? credential, CancellationToken ct)
    {
        if (credential is OAuthCredential)
        {
            return provider.Auth.OAuth is not null ? new AuthCheck(AuthTypes.OAuth, "OAuth") : null;
        }
        var apiKey = provider.Auth.ApiKey;
        if (apiKey is null) return null;
        if (apiKey.Check is not null)
        {
            try
            {
                return await apiKey.Check(new ApiKeyAuthInput(_authContext, credential as ApiKeyCredential, ct));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new ModelsException(ModelsErrorCode.Auth, $"API key auth check failed for provider {provider.Id}", ex);
            }
        }
        var resolution = await AuthResolver.ResolveProviderAuthAsync(provider.Id, provider.Auth, _credentials, _authContext, new AuthResolutionOverrides { CancellationToken = ct });
        return resolution is not null ? new AuthCheck(AuthTypes.ApiKey, resolution.Source) : null;
    }

    public async Task<AuthCheck?> CheckAuthAsync(string providerId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var provider = GetProvider(providerId);
        if (provider is null) return null;
        var credential = await AuthResolver.ReadCredentialAsync(_credentials, providerId, cancellationToken);
        return await CheckProviderAuthAsync(provider, credential, cancellationToken);
    }

    public async Task<IReadOnlyList<Model>> GetAvailableAsync(string? providerId = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var providers = providerId is not null
            ? (GetProvider(providerId) is { } p ? [p] : [])
            : GetProviders();
        var checks = await Task.WhenAll(providers.Select(async provider =>
        {
            var credential = await AuthResolver.ReadCredentialAsync(_credentials, provider.Id, cancellationToken);
            var auth = await CheckProviderAuthAsync(provider, credential, cancellationToken);
            return (provider, credential, auth);
        }));

        var result = new List<Model>();
        foreach (var (provider, credential, auth) in checks)
        {
            if (auth is null) continue;
            var models = provider.GetModels();
            result.AddRange(provider.FilterModels?.Invoke(models, credential) ?? models);
        }
        return result;
    }

    public Task<AuthResult?> GetAuthAsync(string providerId, AuthResolutionOverrides? overrides = null)
    {
        var provider = GetProvider(providerId);
        if (provider is null) return Task.FromResult<AuthResult?>(null);
        return AuthResolver.ResolveProviderAuthAsync(provider.Id, provider.Auth, _credentials, _authContext, overrides);
    }

    public async Task<AuthResult?> GetAuthAsync(Model model, AuthResolutionOverrides? overrides = null)
    {
        var result = await GetAuthAsync(model.Provider, overrides);
        if (result is null || model.Headers is null) return result;
        var auth = result.Auth.Clone();
        auth.Headers = HeaderUtils.Merge(auth.Headers, HeaderUtils.FromModelHeaders(model.Headers));
        return new AuthResult { Auth = auth, Env = result.Env, Source = result.Source };
    }

    public async Task<Credential> LoginAsync(string providerId, string type, IAuthInteraction interaction)
    {
        var ct = interaction.CancellationToken;
        ct.ThrowIfCancellationRequested();
        var provider = GetProvider(providerId) ?? throw new ModelsException(ModelsErrorCode.Provider, $"Unknown provider: {providerId}");
        Credential credential;
        if (type == AuthTypes.OAuth)
        {
            if (provider.Auth.OAuth is null) throw new ModelsException(ModelsErrorCode.Auth, $"{provider.Name} does not support {type} login");
            credential = await provider.Auth.OAuth.Login(interaction);
        }
        else
        {
            if (provider.Auth.ApiKey?.Login is null) throw new ModelsException(ModelsErrorCode.Auth, $"{provider.Name} does not support {type} login");
            credential = await provider.Auth.ApiKey.Login(interaction);
        }
        ct.ThrowIfCancellationRequested();
        try
        {
            await _credentials.ModifyAsync(providerId, _ => Task.FromResult<Credential?>(credential), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ModelsException(ModelsErrorCode.Auth, $"Credential store modify failed for {providerId}", ex);
        }
        return credential;
    }

    public async Task LogoutAsync(string providerId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await _credentials.DeleteAsync(providerId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ModelsException(ModelsErrorCode.Auth, $"Credential store delete failed for {providerId}", ex);
        }
    }

    private IProvider RequireProvider(Model model) =>
        GetProvider(model.Provider) ?? throw new ModelsException(ModelsErrorCode.Provider, $"Unknown provider: {model.Provider}");

    private async Task<(Model Model, T Options)> ApplyAuthAsync<T>(Model model, T options) where T : StreamOptions
    {
        RequireProvider(model);
        var resolution = await GetAuthAsync(model, new AuthResolutionOverrides
        {
            ApiKey = options.ApiKey,
            Env = options.Env,
            CancellationToken = options.CancellationToken,
        }) ?? throw new ModelsException(ModelsErrorCode.Auth, $"Provider is not configured: {model.Provider}");

        var auth = resolution.Auth;
        options.ApiKey ??= auth.ApiKey;
        var headers = HeaderUtils.Merge(auth.Headers, options.Headers);
        if (options is IHeaderTransform { TransformHeaders: { } transform }) headers = await transform(headers ?? new());
        options.Headers = headers;
        if (resolution.Env is not null || options.Env is not null)
        {
            var env = new Dictionary<string, string>(resolution.Env ?? []);
            foreach (var (k, v) in options.Env ?? []) env[k] = v;
            options.Env = env;
        }
        var requestModel = auth.BaseUrl is not null ? model.WithBaseUrl(auth.BaseUrl) : model;
        return (requestModel, options);
    }

    public AssistantMessageEventStream Stream(Model model, Context context, StreamOptions? options = null) =>
        AssistantMessageEventStream.Lazy(model, async () =>
        {
            var provider = RequireProvider(model);
            var copy = options is null ? new StreamOptions() : options.ShallowClone();
            var (requestModel, requestOptions) = await ApplyAuthAsync(model, copy);
            return provider.Stream(requestModel, context, requestOptions);
        });

    public Task<AssistantMessage> CompleteAsync(Model model, Context context, StreamOptions? options = null) =>
        Stream(model, context, options).Result();

    public AssistantMessageEventStream StreamSimple(Model model, Context context, SimpleStreamOptions? options = null) =>
        AssistantMessageEventStream.Lazy(model, async () =>
        {
            var provider = RequireProvider(model);
            var copy = options?.CloneSimple() ?? new SimpleStreamOptions();
            var (requestModel, requestOptions) = await ApplyAuthAsync(model, copy);
            return provider.StreamSimple(requestModel, context, requestOptions);
        });

    public Task<AssistantMessage> CompleteSimpleAsync(Model model, Context context, SimpleStreamOptions? options = null) =>
        StreamSimple(model, context, options).Result();
}
