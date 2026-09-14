using PiSharp.Ai.Utils;

namespace PiSharp.Ai.Auth;

/// <summary>Per-key async serialization helper (promise chains in the TS source).</summary>
public sealed class KeyedSerialQueue
{
    private readonly Dictionary<string, SemaphoreSlim> _locks = new();
    private readonly object _gate = new();

    public async Task<T> RunAsync<T>(string key, Func<Task<T>> task, CancellationToken cancellationToken)
    {
        SemaphoreSlim sem;
        lock (_gate)
        {
            if (!_locks.TryGetValue(key, out sem!))
            {
                sem = new SemaphoreSlim(1, 1);
                _locks[key] = sem;
            }
        }
        await sem.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await task();
        }
        finally
        {
            sem.Release();
        }
    }

    public Task RunAsync(string key, Func<Task> task, CancellationToken cancellationToken) =>
        RunAsync(key, async () =>
        {
            await task();
            return true;
        }, cancellationToken);
}

/// <summary>Default in-memory credential store.</summary>
public sealed class InMemoryCredentialStore : ICredentialStore
{
    private readonly Dictionary<string, Credential> _credentials = new();
    private readonly KeyedSerialQueue _queue = new();

    public Task<Credential?> ReadAsync(string providerId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_credentials) return Task.FromResult(_credentials.GetValueOrDefault(providerId));
    }

    public Task<IReadOnlyList<CredentialInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_credentials)
        {
            return Task.FromResult<IReadOnlyList<CredentialInfo>>(_credentials.Select(kv => new CredentialInfo(kv.Key, kv.Value.Type)).ToList());
        }
    }

    public Task<Credential?> ModifyAsync(string providerId, Func<Credential?, Task<Credential?>> fn, CancellationToken cancellationToken = default) =>
        _queue.RunAsync(providerId, async () =>
        {
            Credential? current;
            lock (_credentials) current = _credentials.GetValueOrDefault(providerId);
            var next = await fn(current);
            cancellationToken.ThrowIfCancellationRequested();
            if (next is not null)
            {
                lock (_credentials) _credentials[providerId] = next;
            }
            return next ?? current;
        }, cancellationToken);

    public Task DeleteAsync(string providerId, CancellationToken cancellationToken = default) =>
        _queue.RunAsync(providerId, () =>
        {
            lock (_credentials) _credentials.Remove(providerId);
            return Task.CompletedTask;
        }, cancellationToken);
}

public static class AuthHelpers
{
    /// <summary>Standard api-key auth: a stored credential key wins, otherwise the first set env var resolves.</summary>
    public static ApiKeyAuth EnvApiKeyAuth(string name, IReadOnlyList<string> envVars) => new()
    {
        Name = name,
        Login = async interaction =>
        {
            interaction.CancellationToken.ThrowIfCancellationRequested();
            var key = await interaction.PromptAsync(new AuthPrompt { Type = "secret", Message = $"Enter {name}" });
            interaction.CancellationToken.ThrowIfCancellationRequested();
            return new ApiKeyCredential { Key = key };
        },
        Resolve = async input =>
        {
            input.CancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrEmpty(input.Credential?.Key))
            {
                return new AuthResult { Auth = new ModelAuth { ApiKey = input.Credential.Key }, Env = input.Credential.Env, Source = "stored credential" };
            }
            foreach (var envVar in envVars)
            {
                var value = await input.Ctx.EnvAsync(envVar);
                input.CancellationToken.ThrowIfCancellationRequested();
                if (!string.IsNullOrEmpty(value)) return new AuthResult { Auth = new ModelAuth { ApiKey = value }, Source = envVar };
            }
            return null;
        },
    };

    /// <summary>Wraps a lazily created OAuthAuth implementation.</summary>
    public static OAuthAuth LazyOAuth(string name, Func<Task<OAuthAuth>> load, bool isSubscription = false, string? loginLabel = null)
    {
        var lazy = new Lazy<Task<OAuthAuth>>(load);
        return new OAuthAuth
        {
            Name = name,
            IsSubscription = isSubscription,
            LoginLabel = loginLabel,
            Login = async interaction => await (await lazy.Value).Login(interaction),
            Refresh = async (credential, ct) => await (await lazy.Value).Refresh(credential, ct),
            ToAuth = async credential => await (await lazy.Value).ToAuth(credential),
        };
    }
}

public sealed class AuthResolutionOverrides
{
    public string? ApiKey { get; init; }
    public Dictionary<string, string>? Env { get; init; }
    /// <summary>Require this much remaining OAuth-token validity; defaults to five minutes.</summary>
    public long? MinOAuthValidityMs { get; init; }
    public CancellationToken CancellationToken { get; init; }
}

/// <summary>Shared auth resolution. A stored credential owns the provider; ambient env is only used when nothing is stored.</summary>
public static class AuthResolver
{
    private const long DefaultOAuthMinimumValidityMs = 5 * 60 * 1000;
    private const int DefaultOAuthRefreshTimeoutMs = 15_000;

    public static async Task<AuthResult?> ResolveProviderAuthAsync(
        string providerId,
        ProviderAuth auth,
        ICredentialStore credentials,
        IAuthContext authContext,
        AuthResolutionOverrides? overrides = null)
    {
        var ct = overrides?.CancellationToken ?? default;
        ct.ThrowIfCancellationRequested();
        var requestContext = overrides?.Env is not null ? new OverlayEnvAuthContext(authContext, overrides.Env) : authContext;

        if (overrides?.ApiKey is not null && auth.ApiKey is not null)
        {
            return await ResolveApiKeyAsync(requestContext, auth.ApiKey, providerId, new ApiKeyCredential { Key = overrides.ApiKey, Env = overrides.Env }, ct);
        }

        var stored = await ReadCredentialAsync(credentials, providerId, ct);
        if (stored is not null)
        {
            if (stored is OAuthCredential oauthCredential && auth.OAuth is not null)
            {
                return await ResolveStoredOAuthAsync(credentials, providerId, auth.OAuth, oauthCredential, ct, overrides?.MinOAuthValidityMs);
            }
            if (stored is ApiKeyCredential apiCredential && auth.ApiKey is not null)
            {
                var credential = apiCredential;
                if (overrides?.Env is not null)
                {
                    var env = new Dictionary<string, string>(apiCredential.Env ?? []);
                    foreach (var (k, v) in overrides.Env) env[k] = v;
                    credential = new ApiKeyCredential { Key = apiCredential.Key, Env = env };
                }
                return await ResolveApiKeyAsync(requestContext, auth.ApiKey, providerId, credential, ct);
            }
            return null;
        }

        return auth.ApiKey is not null
            ? await ResolveApiKeyAsync(requestContext, auth.ApiKey, providerId, null, ct)
            : null;
    }

    private static async Task<AuthResult?> ResolveStoredOAuthAsync(
        ICredentialStore credentials, string providerId, OAuthAuth oauth, OAuthCredential stored, CancellationToken ct, long? minOAuthValidityMs)
    {
        var minimumValidity = Math.Max(DefaultOAuthMinimumValidityMs, minOAuthValidityMs ?? 0);
        bool ExpiresSoon(OAuthCredential c) => TimeUtil.NowMs() + minimumValidity >= c.Expires;
        var credential = stored;

        if (ExpiresSoon(credential))
        {
            Credential? post;
            try
            {
                post = await credentials.ModifyAsync(providerId, async current =>
                {
                    if (current is not OAuthCredential currentOAuth) return null;
                    if (!ExpiresSoon(currentOAuth)) return null;
                    try
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        timeout.CancelAfter(DefaultOAuthRefreshTimeoutMs);
                        return await oauth.Refresh(currentOAuth, timeout.Token);
                    }
                    catch (Exception ex)
                    {
                        throw new ModelsException(ModelsErrorCode.OAuth, $"OAuth refresh failed for {providerId}", ex);
                    }
                }, ct);
            }
            catch (ModelsException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new ModelsException(ModelsErrorCode.Auth, $"Credential store modify failed for {providerId}", ex);
            }
            if (post is not OAuthCredential refreshed) return null;
            credential = refreshed;
            if (minOAuthValidityMs is not null && ExpiresSoon(credential))
            {
                throw new ModelsException(ModelsErrorCode.OAuth, $"OAuth refresh returned a token that expires too soon for {providerId}");
            }
        }

        try
        {
            return new AuthResult { Auth = await oauth.ToAuth(credential), Source = "OAuth" };
        }
        catch (Exception ex)
        {
            throw new ModelsException(ModelsErrorCode.OAuth, $"OAuth auth derivation failed for {providerId}", ex);
        }
    }

    private static async Task<AuthResult?> ResolveApiKeyAsync(IAuthContext ctx, ApiKeyAuth apiKey, string providerId, ApiKeyCredential? credential, CancellationToken ct)
    {
        try
        {
            return await apiKey.Resolve(new ApiKeyAuthInput(ctx, credential, ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ModelsException(ModelsErrorCode.Auth, $"API key auth failed for provider {providerId}", ex);
        }
    }

    public static async Task<Credential?> ReadCredentialAsync(ICredentialStore credentials, string providerId, CancellationToken ct)
    {
        try
        {
            return await credentials.ReadAsync(providerId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ModelsException(ModelsErrorCode.Auth, $"Credential store read failed for {providerId}", ex);
        }
    }
}

/// <summary>Known API key environment variables per provider. Port of env-api-keys.ts.</summary>
public static class EnvApiKeys
{
    public const string AnthropicAuthTokenEnv = "ANTHROPIC_AUTH_TOKEN";
    public const string AnthropicOAuthTokenEnv = "ANTHROPIC_OAUTH_TOKEN";
    public const string AnthropicApiKeyEnv = "ANTHROPIC_API_KEY";
    public const string AmbientAuthMarker = "<authenticated>";

    private static readonly Dictionary<string, string> EnvMap = new()
    {
        ["ant-ling"] = "ANT_LING_API_KEY",
        ["qwen-token-plan"] = "QWEN_TOKEN_PLAN_API_KEY",
        ["qwen-token-plan-cn"] = "QWEN_TOKEN_PLAN_CN_API_KEY",
        ["qwen-token-plan-individual"] = "QWEN_TOKEN_PLAN_API_KEY",
        ["openai"] = "OPENAI_API_KEY",
        ["azure-openai-responses"] = "AZURE_OPENAI_API_KEY",
        ["nvidia"] = "NVIDIA_API_KEY",
        ["deepseek"] = "DEEPSEEK_API_KEY",
        ["google"] = "GEMINI_API_KEY",
        ["google-vertex"] = "GOOGLE_CLOUD_API_KEY",
        ["groq"] = "GROQ_API_KEY",
        ["cerebras"] = "CEREBRAS_API_KEY",
        ["xai"] = "XAI_API_KEY",
        ["radius"] = "RADIUS_API_KEY",
        ["openrouter"] = "OPENROUTER_API_KEY",
        ["vercel-ai-gateway"] = "AI_GATEWAY_API_KEY",
        ["zai"] = "ZAI_API_KEY",
        ["zai-coding-cn"] = "ZAI_CODING_CN_API_KEY",
        ["mistral"] = "MISTRAL_API_KEY",
        ["minimax"] = "MINIMAX_API_KEY",
        ["minimax-cn"] = "MINIMAX_CN_API_KEY",
        ["moonshotai"] = "MOONSHOT_API_KEY",
        ["moonshotai-cn"] = "MOONSHOT_API_KEY",
        ["huggingface"] = "HF_TOKEN",
        ["fireworks"] = "FIREWORKS_API_KEY",
        ["together"] = "TOGETHER_API_KEY",
        ["baseten"] = "BASETEN_API_KEY",
        ["opencode"] = "OPENCODE_API_KEY",
        ["opencode-go"] = "OPENCODE_API_KEY",
        ["kimi-coding"] = "KIMI_API_KEY",
        ["cloudflare-workers-ai"] = "CLOUDFLARE_API_KEY",
        ["cloudflare-ai-gateway"] = "CLOUDFLARE_API_KEY",
        ["xiaomi"] = "XIAOMI_API_KEY",
        ["xiaomi-token-plan-cn"] = "XIAOMI_TOKEN_PLAN_CN_API_KEY",
        ["xiaomi-token-plan-ams"] = "XIAOMI_TOKEN_PLAN_AMS_API_KEY",
        ["xiaomi-token-plan-sgp"] = "XIAOMI_TOKEN_PLAN_SGP_API_KEY",
    };

    public static IReadOnlyList<string>? GetApiKeyEnvVars(string provider)
    {
        if (provider == "github-copilot") return ["COPILOT_GITHUB_TOKEN"];
        if (provider == "anthropic") return [AnthropicAuthTokenEnv, AnthropicOAuthTokenEnv, AnthropicApiKeyEnv];
        return EnvMap.TryGetValue(provider, out var envVar) ? [envVar] : null;
    }

    public static IReadOnlyList<string>? FindEnvKeys(string provider, IReadOnlyDictionary<string, string>? env = null)
    {
        var vars = GetApiKeyEnvVars(provider);
        if (vars is null) return null;
        var found = vars.Where(v => ProviderEnv.Get(v, env) is not null).ToList();
        return found.Count > 0 ? found : null;
    }

    public static string? GetEnvApiKey(string provider, IReadOnlyDictionary<string, string>? env = null)
    {
        var envKeys = FindEnvKeys(provider, env);
        if (envKeys is { Count: > 0 })
        {
            var apiKeyEnv = provider == "anthropic" ? envKeys.FirstOrDefault(k => k != AnthropicAuthTokenEnv) : envKeys[0];
            if (apiKeyEnv is not null) return ProviderEnv.Get(apiKeyEnv, env);
        }

        if (provider == "google-vertex")
        {
            var explicitPath = env?.GetValueOrDefault("GOOGLE_APPLICATION_CREDENTIALS") ?? ProviderEnv.Get("GOOGLE_APPLICATION_CREDENTIALS", env);
            var hasCredentials = explicitPath is not null
                ? File.Exists(explicitPath)
                : File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "gcloud", "application_default_credentials.json"));
            var hasProject = ProviderEnv.Get("GOOGLE_CLOUD_PROJECT", env) is not null || ProviderEnv.Get("GCLOUD_PROJECT", env) is not null;
            var hasLocation = ProviderEnv.Get("GOOGLE_CLOUD_LOCATION", env) is not null;
            if (hasCredentials && hasProject && hasLocation) return AmbientAuthMarker;
        }

        if (provider == "amazon-bedrock")
        {
            if (ProviderEnv.Get("AWS_PROFILE", env) is not null
                || (ProviderEnv.Get("AWS_ACCESS_KEY_ID", env) is not null && ProviderEnv.Get("AWS_SECRET_ACCESS_KEY", env) is not null)
                || ProviderEnv.Get("AWS_BEARER_TOKEN_BEDROCK", env) is not null
                || ProviderEnv.Get("AWS_CONTAINER_CREDENTIALS_RELATIVE_URI", env) is not null
                || ProviderEnv.Get("AWS_CONTAINER_CREDENTIALS_FULL_URI", env) is not null
                || ProviderEnv.Get("AWS_WEB_IDENTITY_TOKEN_FILE", env) is not null)
            {
                return AmbientAuthMarker;
            }
        }

        return null;
    }
}
