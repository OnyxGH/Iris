using System.Reflection;
using System.Text.Json.Nodes;
using Iris.Ai.Auth;
using Iris.Ai.Json;
using Iris.Ai.Models;

namespace Iris.Ai.Providers;

/// <summary>Generated built-in model catalog (embedded copies of pi-ai's providers/data/*.json).</summary>
public static class BuiltinCatalog
{
    private static readonly Lazy<Dictionary<string, IReadOnlyList<Model>>> Catalog = new(Load);
    private static readonly Lazy<long?> GeneratedAtValue = new(LoadGeneratedAt);

    private const string ResourcePrefix = "Iris.Ai.Providers.Data.";

    private static Dictionary<string, IReadOnlyList<Model>> Load()
    {
        var assembly = typeof(BuiltinCatalog).Assembly;
        var result = new Dictionary<string, IReadOnlyList<Model>>();
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal)))
        {
            var provider = name[ResourcePrefix.Length..^".json".Length];
            if (provider == "manifest") continue;
            using var stream = assembly.GetManifestResourceStream(name)!;
            var root = JsonNode.Parse(stream) as JsonObject;
            var models = new List<Model>();
            foreach (var (_, group) in root ?? [])
            {
                if (group is not JsonObject groupObj) continue;
                foreach (var (_, modelNode) in groupObj)
                {
                    if (modelNode is JsonObject) models.Add(PiJson.Deserialize<Model>(modelNode)!);
                }
            }
            result[provider] = models;
        }
        return result;
    }

    private static long? LoadGeneratedAt()
    {
        var assembly = typeof(BuiltinCatalog).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourcePrefix + "manifest.json");
        if (stream is null) return null;
        var node = JsonNode.Parse(stream);
        return node?["generatedAt"] is JsonValue v && v.TryGetValue<string>(out var s) && DateTimeOffset.TryParse(s, out var date)
            ? date.ToUnixTimeMilliseconds()
            : null;
    }

    public static long? GeneratedAt => GeneratedAtValue.Value;

    public static IReadOnlyList<string> GetProviders() => Catalog.Value.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();

    /// <summary>Fresh copies of a provider's built-in models.</summary>
    public static IReadOnlyList<Model> GetModels(string provider) =>
        Catalog.Value.TryGetValue(provider, out var models) ? models.Select(m => m.Clone()).ToList() : [];

    public static Model? GetModel(string provider, string modelId) =>
        Catalog.Value.TryGetValue(provider, out var models) ? models.FirstOrDefault(m => m.Id == modelId)?.Clone() : null;
}

/// <summary>Built-in provider factories. Port of pi-ai providers/*.ts.</summary>
public static class BuiltinProviders
{
    private static IApiStreams Api(string api) => ApiRegistry.BuiltinApis[api];

    private static OAuthAuth NotYetSupportedOAuth(string name, bool isSubscription = false, string? loginLabel = null) => new()
    {
        Name = name,
        IsSubscription = isSubscription,
        LoginLabel = loginLabel,
        Login = _ => throw new NotSupportedException($"{name} login is not yet supported by Iris"),
        Refresh = (_, _) => throw new NotSupportedException($"{name} token refresh is not yet supported by Iris"),
        ToAuth = credential => Task.FromResult(new ModelAuth { ApiKey = credential.Access }),
    };

    private static IProvider Simple(string id, string name, string? baseUrl, string keyName, string envVar, string api) =>
        ProviderFactory.Create(new CreateProviderOptions
        {
            Id = id,
            Name = name,
            BaseUrl = baseUrl,
            Auth = new ProviderAuth { ApiKey = AuthHelpers.EnvApiKeyAuth(keyName, [envVar]) },
            Models = BuiltinCatalog.GetModels(id),
            Api = Api(api),
        });

    public static IProvider Anthropic() => ProviderFactory.Create(new CreateProviderOptions
    {
        Id = "anthropic",
        Name = "Anthropic",
        BaseUrl = "https://api.anthropic.com",
        Auth = new ProviderAuth
        {
            ApiKey = new ApiKeyAuth
            {
                Name = "Anthropic API key",
                Login = async interaction =>
                {
                    interaction.CancellationToken.ThrowIfCancellationRequested();
                    var key = await interaction.PromptAsync(new AuthPrompt { Type = "secret", Message = "Enter Anthropic API key" });
                    return new ApiKeyCredential { Key = key };
                },
                Resolve = async input =>
                {
                    input.CancellationToken.ThrowIfCancellationRequested();
                    if (!string.IsNullOrEmpty(input.Credential?.Key))
                        return new AuthResult { Auth = new ModelAuth { ApiKey = input.Credential.Key }, Env = input.Credential.Env, Source = "stored credential" };
                    var authToken = await input.Ctx.EnvAsync(EnvApiKeys.AnthropicAuthTokenEnv);
                    if (!string.IsNullOrEmpty(authToken))
                    {
                        return new AuthResult
                        {
                            Auth = new ModelAuth { Headers = new Dictionary<string, string?> { ["Authorization"] = $"Bearer {authToken}" } },
                            Source = EnvApiKeys.AnthropicAuthTokenEnv,
                        };
                    }
                    foreach (var envVar in new[] { EnvApiKeys.AnthropicOAuthTokenEnv, EnvApiKeys.AnthropicApiKeyEnv })
                    {
                        var apiKey = await input.Ctx.EnvAsync(envVar);
                        if (!string.IsNullOrEmpty(apiKey)) return new AuthResult { Auth = new ModelAuth { ApiKey = apiKey }, Source = envVar };
                    }
                    return null;
                },
            },
            OAuth = NotYetSupportedOAuth("Anthropic (Claude Pro/Max)", isSubscription: true),
        },
        Models = BuiltinCatalog.GetModels("anthropic"),
        Api = Api(KnownApis.AnthropicMessages),
    });

    public static IProvider OpenAI() => Simple("openai", "OpenAI", "https://api.openai.com/v1", "OpenAI API key", "OPENAI_API_KEY", KnownApis.OpenAIResponses);

    public static IProvider Google() => Simple("google", "Google", "https://generativelanguage.googleapis.com/v1beta", "Gemini API key", "GEMINI_API_KEY", KnownApis.GoogleGenerativeAI);

    private static IProvider Mixed(string id, string name, string? baseUrl, ProviderAuth auth, params string[] apis) =>
        ProviderFactory.Create(new CreateProviderOptions
        {
            Id = id,
            Name = name,
            BaseUrl = baseUrl,
            Auth = auth,
            Models = BuiltinCatalog.GetModels(id),
            ApiByName = apis.ToDictionary(a => a, Api),
        });

    private static ProviderAuth EnvAuth(string keyName, string envVar) => new() { ApiKey = AuthHelpers.EnvApiKeyAuth(keyName, [envVar]) };

    /// <summary>All built-in providers, freshly constructed.</summary>
    public static IReadOnlyList<IProvider> All() =>
    [
        ProviderFactory.Create(new CreateProviderOptions
        {
            Id = "amazon-bedrock",
            Name = "Amazon Bedrock",
            Auth = new ProviderAuth { ApiKey = BedrockAuth() },
            Models = BuiltinCatalog.GetModels("amazon-bedrock"),
            Api = Api(KnownApis.BedrockConverseStream),
        }),
        Simple("ant-ling", "Ant Ling", "https://api.ant-ling.com/v1", "Ant Ling API key", "ANT_LING_API_KEY", KnownApis.OpenAICompletions),
        Anthropic(),
        Simple("azure-openai-responses", "Azure OpenAI", null, "Azure OpenAI API key", "AZURE_OPENAI_API_KEY", KnownApis.AzureOpenAIResponses),
        Simple("baseten", "Baseten", "https://inference.baseten.co/v1", "Baseten API key", "BASETEN_API_KEY", KnownApis.OpenAICompletions),
        Simple("cerebras", "Cerebras", "https://api.cerebras.ai/v1", "Cerebras API key", "CEREBRAS_API_KEY", KnownApis.OpenAICompletions),
        ProviderFactory.Create(new CreateProviderOptions
        {
            Id = "cloudflare-ai-gateway",
            Name = "Cloudflare AI Gateway",
            Auth = new ProviderAuth { ApiKey = CloudflareAuth(gateway: true) },
            Models = BuiltinCatalog.GetModels("cloudflare-ai-gateway"),
            ApiByName = new Dictionary<string, IApiStreams>
            {
                [KnownApis.AnthropicMessages] = new CloudflareStreams(Api(KnownApis.AnthropicMessages)),
                [KnownApis.OpenAICompletions] = new CloudflareStreams(Api(KnownApis.OpenAICompletions)),
                [KnownApis.OpenAIResponses] = new CloudflareStreams(Api(KnownApis.OpenAIResponses)),
            },
        }),
        ProviderFactory.Create(new CreateProviderOptions
        {
            Id = "cloudflare-workers-ai",
            Name = "Cloudflare Workers AI",
            Auth = new ProviderAuth { ApiKey = CloudflareAuth(gateway: false) },
            Models = BuiltinCatalog.GetModels("cloudflare-workers-ai"),
            Api = new CloudflareStreams(Api(KnownApis.OpenAICompletions)),
        }),
        Simple("deepseek", "DeepSeek", "https://api.deepseek.com", "DeepSeek API key", "DEEPSEEK_API_KEY", KnownApis.OpenAICompletions),
        Mixed("fireworks", "Fireworks", "https://api.fireworks.ai/inference", EnvAuth("Fireworks API key", "FIREWORKS_API_KEY"), KnownApis.AnthropicMessages, KnownApis.OpenAICompletions),
        ProviderFactory.Create(new CreateProviderOptions
        {
            Id = "github-copilot",
            Name = "GitHub Copilot",
            BaseUrl = "https://api.individual.githubcopilot.com",
            Auth = new ProviderAuth
            {
                ApiKey = AuthHelpers.EnvApiKeyAuth("GitHub Copilot token", ["COPILOT_GITHUB_TOKEN"]),
                OAuth = NotYetSupportedOAuth("GitHub Copilot", isSubscription: true),
            },
            Models = BuiltinCatalog.GetModels("github-copilot"),
            FilterModels = (models, credential) =>
            {
                if (credential is not OAuthCredential oauth) return models;
                if (oauth.Extra["availableModelIds"] is not JsonArray ids || !ids.All(i => i is JsonValue v && v.TryGetValue<string>(out _))) return models;
                var available = ids.Select(i => i!.GetValue<string>()).ToHashSet();
                return models.Where(m => available.Contains(m.Id)).ToList();
            },
            ApiByName = new Dictionary<string, IApiStreams>
            {
                [KnownApis.AnthropicMessages] = Api(KnownApis.AnthropicMessages),
                [KnownApis.OpenAICompletions] = Api(KnownApis.OpenAICompletions),
                [KnownApis.OpenAIResponses] = Api(KnownApis.OpenAIResponses),
            },
        }),
        Google(),
        ProviderFactory.Create(new CreateProviderOptions
        {
            Id = "google-vertex",
            Name = "Google Vertex AI",
            Auth = new ProviderAuth { ApiKey = VertexAuth() },
            Models = BuiltinCatalog.GetModels("google-vertex"),
            Api = Api(KnownApis.GoogleVertex),
        }),
        Simple("groq", "Groq", "https://api.groq.com/openai/v1", "Groq API key", "GROQ_API_KEY", KnownApis.OpenAICompletions),
        Simple("huggingface", "Hugging Face", "https://router.huggingface.co/v1", "Hugging Face token", "HF_TOKEN", KnownApis.OpenAICompletions),
        Mixed("kimi-coding", "Kimi For Coding", "https://api.kimi.com/coding",
            new ProviderAuth
            {
                ApiKey = AuthHelpers.EnvApiKeyAuth("Kimi API key", ["KIMI_API_KEY"]),
                OAuth = NotYetSupportedOAuth("Kimi Code (subscription)", isSubscription: true, loginLabel: "Sign in with Kimi Code"),
            },
            KnownApis.AnthropicMessages),
        Simple("minimax", "MiniMax", "https://api.minimax.io/anthropic", "MiniMax API key", "MINIMAX_API_KEY", KnownApis.AnthropicMessages),
        Simple("minimax-cn", "MiniMax CN", "https://api.minimaxi.com/anthropic", "MiniMax CN API key", "MINIMAX_CN_API_KEY", KnownApis.AnthropicMessages),
        Simple("mistral", "Mistral", "https://api.mistral.ai", "Mistral API key", "MISTRAL_API_KEY", KnownApis.MistralConversations),
        Simple("moonshotai", "Moonshot AI", "https://api.moonshot.ai/v1", "Moonshot AI API key", "MOONSHOT_API_KEY", KnownApis.OpenAICompletions),
        Simple("moonshotai-cn", "Moonshot AI CN", "https://api.moonshot.cn/v1", "Moonshot AI API key", "MOONSHOT_API_KEY", KnownApis.OpenAICompletions),
        Simple("nvidia", "NVIDIA", "https://integrate.api.nvidia.com/v1", "NVIDIA API key", "NVIDIA_API_KEY", KnownApis.OpenAICompletions),
        OpenAI(),
        ProviderFactory.Create(new CreateProviderOptions
        {
            Id = "openai-codex",
            Name = "OpenAI Codex",
            BaseUrl = "https://chatgpt.com/backend-api",
            Auth = new ProviderAuth { OAuth = NotYetSupportedOAuth("OpenAI (ChatGPT Plus/Pro)", isSubscription: true) },
            Models = BuiltinCatalog.GetModels("openai-codex"),
            Api = Api(KnownApis.OpenAICodexResponses),
        }),
        ProviderFactory.Create(new CreateProviderOptions
        {
            Id = "opencode",
            Name = "OpenCode Zen",
            Auth = EnvAuth("OpenCode API key", "OPENCODE_API_KEY"),
            Models = BuiltinCatalog.GetModels("opencode"),
            ApiByName = new Dictionary<string, IApiStreams>
            {
                [KnownApis.AnthropicMessages] = new OpenCodeSessionHeaderStreams(Api(KnownApis.AnthropicMessages)),
                [KnownApis.GoogleGenerativeAI] = new OpenCodeSessionHeaderStreams(Api(KnownApis.GoogleGenerativeAI)),
                [KnownApis.OpenAICompletions] = new OpenCodeSessionHeaderStreams(Api(KnownApis.OpenAICompletions)),
                [KnownApis.OpenAIResponses] = new OpenCodeSessionHeaderStreams(Api(KnownApis.OpenAIResponses)),
            },
        }),
        ProviderFactory.Create(new CreateProviderOptions
        {
            Id = "opencode-go",
            Name = "OpenCode Go",
            Auth = EnvAuth("OpenCode API key", "OPENCODE_API_KEY"),
            Models = BuiltinCatalog.GetModels("opencode-go"),
            ApiByName = new Dictionary<string, IApiStreams>
            {
                [KnownApis.AnthropicMessages] = new OpenCodeSessionHeaderStreams(Api(KnownApis.AnthropicMessages)),
                [KnownApis.OpenAICompletions] = new OpenCodeSessionHeaderStreams(Api(KnownApis.OpenAICompletions)),
                [KnownApis.OpenAIResponses] = new OpenCodeSessionHeaderStreams(Api(KnownApis.OpenAIResponses)),
            },
        }),
        Mixed("openrouter", "OpenRouter", "https://openrouter.ai/api/v1",
            new ProviderAuth
            {
                ApiKey = AuthHelpers.EnvApiKeyAuth("OpenRouter API key", ["OPENROUTER_API_KEY"]),
                OAuth = NotYetSupportedOAuth("OpenRouter OAuth", loginLabel: "Sign in with OpenRouter"),
            },
            KnownApis.AnthropicMessages, KnownApis.OpenAICompletions),
        Simple("qwen-token-plan", "Qwen Token Plan", "https://token-plan.ap-southeast-1.maas.aliyuncs.com/compatible-mode/v1", "Qwen Token Plan API key", "QWEN_TOKEN_PLAN_API_KEY", KnownApis.OpenAICompletions),
        Simple("qwen-token-plan-cn", "Qwen Token Plan CN", "https://token-plan.cn-beijing.maas.aliyuncs.com/compatible-mode/v1", "Qwen Token Plan CN API key", "QWEN_TOKEN_PLAN_CN_API_KEY", KnownApis.OpenAICompletions),
        Simple("qwen-token-plan-individual", "Qwen Token Plan Individual", "https://token-plan.ap-southeast-1.maas.aliyuncs.com/compatible-mode/v1", "Qwen Token Plan Individual API key", "QWEN_TOKEN_PLAN_API_KEY", KnownApis.OpenAICompletions),
        Simple("together", "Together", "https://api.together.ai/v1", "Together API key", "TOGETHER_API_KEY", KnownApis.OpenAICompletions),
        Simple("vercel-ai-gateway", "Vercel AI Gateway", "https://ai-gateway.vercel.sh", "Vercel AI Gateway API key", "AI_GATEWAY_API_KEY", KnownApis.AnthropicMessages),
        Mixed("xai", "xAI", "https://api.x.ai/v1",
            new ProviderAuth
            {
                ApiKey = AuthHelpers.EnvApiKeyAuth("xAI API key", ["XAI_API_KEY"]),
                OAuth = NotYetSupportedOAuth("xAI (Grok/X subscription)", isSubscription: true, loginLabel: "Sign in with SuperGrok or X Premium"),
            },
            KnownApis.OpenAIResponses),
        Simple("xiaomi", "Xiaomi", "https://api.xiaomimimo.com/v1", "Xiaomi API key", "XIAOMI_API_KEY", KnownApis.OpenAICompletions),
        Simple("xiaomi-token-plan-ams", "Xiaomi Token Plan AMS", "https://token-plan-ams.xiaomimimo.com/v1", "Xiaomi Token Plan AMS API key", "XIAOMI_TOKEN_PLAN_AMS_API_KEY", KnownApis.OpenAICompletions),
        Simple("xiaomi-token-plan-cn", "Xiaomi Token Plan CN", "https://token-plan-cn.xiaomimimo.com/v1", "Xiaomi Token Plan CN API key", "XIAOMI_TOKEN_PLAN_CN_API_KEY", KnownApis.OpenAICompletions),
        Simple("xiaomi-token-plan-sgp", "Xiaomi Token Plan SGP", "https://token-plan-sgp.xiaomimimo.com/v1", "Xiaomi Token Plan SGP API key", "XIAOMI_TOKEN_PLAN_SGP_API_KEY", KnownApis.OpenAICompletions),
        Simple("zai", "Z.AI", "https://api.z.ai/api/coding/paas/v4", "Z.AI API key", "ZAI_API_KEY", KnownApis.OpenAICompletions),
        Simple("zai-coding-cn", "Z.AI Coding CN", "https://open.bigmodel.cn/api/coding/paas/v4", "Z.AI Coding CN API key", "ZAI_CODING_CN_API_KEY", KnownApis.OpenAICompletions),
    ];

    /// <summary>A Models collection with every built-in provider registered.</summary>
    public static ModelsCollection CreateModels(CreateModelsOptions? options = null)
    {
        var models = new ModelsCollection(options);
        foreach (var provider in All()) models.SetProvider(provider);
        return models;
    }

    private static ApiKeyAuth BedrockAuth() => new()
    {
        Name = "AWS credentials or bearer token",
        Login = async interaction =>
        {
            var method = await interaction.PromptAsync(new AuthPrompt
            {
                Type = "select",
                Message = "Select Amazon Bedrock authentication method:",
                Options = [new("bearer-token", "Bearer token"), new("aws-profile", "AWS profile"), new("credential-chain", "Existing AWS credential chain")],
            });
            if (method == "bearer-token")
                return new ApiKeyCredential { Key = await interaction.PromptAsync(new AuthPrompt { Type = "secret", Message = "Enter Amazon Bedrock bearer token" }) };
            interaction.Notify(new AuthInfoEvent("Amazon Bedrock supports AWS profiles, IAM credentials, and role-based credentials.",
                [new AuthInfoLink("https://docs.aws.amazon.com/sdkref/latest/guide/standardized-credentials.html", "AWS credential provider chain")]));
            if (method == "aws-profile")
                return new ApiKeyCredential { Env = new() { ["AWS_PROFILE"] = await interaction.PromptAsync(new AuthPrompt { Type = "text", Message = "Enter AWS profile name" }) } };
            if (method != "credential-chain") throw new InvalidOperationException($"Unknown Amazon Bedrock auth method: {method}");
            await interaction.PromptAsync(new AuthPrompt { Type = "text", Message = "Configure AWS credentials, then press Enter to continue" });
            return new ApiKeyCredential();
        },
        Resolve = async input =>
        {
            async Task<string?> Env(string name) => await input.Ctx.EnvAsync(name);
            var credential = input.Credential;
            if (!string.IsNullOrEmpty(credential?.Key)) return new AuthResult { Auth = new ModelAuth { ApiKey = credential.Key }, Env = credential.Env, Source = "stored credential" };
            if (await Env("AWS_BEARER_TOKEN_BEDROCK") is not null) return new AuthResult { Source = "AWS_BEARER_TOKEN_BEDROCK" };
            var storedProfile = credential?.Env?.GetValueOrDefault("AWS_PROFILE");
            if (storedProfile is not null || await Env("AWS_PROFILE") is not null)
                return new AuthResult { Env = credential?.Env, Source = storedProfile is not null ? "stored credential" : "AWS_PROFILE" };
            if (await Env("AWS_ACCESS_KEY_ID") is not null && await Env("AWS_SECRET_ACCESS_KEY") is not null) return new AuthResult { Source = "AWS access keys" };
            if (await Env("AWS_CONTAINER_CREDENTIALS_RELATIVE_URI") is not null) return new AuthResult { Source = "ECS task role" };
            if (await Env("AWS_CONTAINER_CREDENTIALS_FULL_URI") is not null) return new AuthResult { Source = "ECS task role" };
            if (await Env("AWS_WEB_IDENTITY_TOKEN_FILE") is not null) return new AuthResult { Source = "web identity token" };
            return null;
        },
    };

    private static ApiKeyAuth VertexAuth() => new()
    {
        Name = "Google Cloud credentials",
        Resolve = async input =>
        {
            var credential = input.Credential;
            var key = credential?.Key ?? await input.Ctx.EnvAsync("GOOGLE_CLOUD_API_KEY");
            if (!string.IsNullOrEmpty(key))
                return new AuthResult { Auth = new ModelAuth { ApiKey = key }, Source = credential?.Key is not null ? "stored credential" : "GOOGLE_CLOUD_API_KEY" };
            var adcPath = credential?.Env?.GetValueOrDefault("GOOGLE_APPLICATION_CREDENTIALS") ?? await input.Ctx.EnvAsync("GOOGLE_APPLICATION_CREDENTIALS");
            var hasCredentials = await input.Ctx.FileExistsAsync(adcPath ?? "~/.config/gcloud/application_default_credentials.json");
            var project = credential?.Env?.GetValueOrDefault("GOOGLE_CLOUD_PROJECT") ?? await input.Ctx.EnvAsync("GOOGLE_CLOUD_PROJECT") ?? await input.Ctx.EnvAsync("GCLOUD_PROJECT");
            var location = credential?.Env?.GetValueOrDefault("GOOGLE_CLOUD_LOCATION") ?? await input.Ctx.EnvAsync("GOOGLE_CLOUD_LOCATION");
            if (hasCredentials && project is not null && location is not null)
                return new AuthResult { Env = credential?.Env, Source = credential is not null ? "stored credential" : "gcloud application default credentials" };
            return null;
        },
    };

    private static ApiKeyAuth CloudflareAuth(bool gateway)
    {
        async Task<string?> ResolveValue(string name, ApiKeyAuthInput input)
        {
            string? fromCredential = input.Credential is null ? null : name == "CLOUDFLARE_API_KEY" ? input.Credential.Key : input.Credential.Env?.GetValueOrDefault(name);
            return fromCredential ?? await input.Ctx.EnvAsync(name);
        }

        return new ApiKeyAuth
        {
            Name = "Cloudflare API key",
            Login = async interaction =>
            {
                var key = await interaction.PromptAsync(new AuthPrompt { Type = "secret", Message = "Enter Cloudflare API key" });
                var accountId = await interaction.PromptAsync(new AuthPrompt { Type = "text", Message = "Enter Cloudflare account ID" });
                var env = new Dictionary<string, string> { ["CLOUDFLARE_ACCOUNT_ID"] = accountId };
                if (gateway) env["CLOUDFLARE_GATEWAY_ID"] = await interaction.PromptAsync(new AuthPrompt { Type = "text", Message = "Enter Cloudflare AI Gateway ID" });
                return new ApiKeyCredential { Key = key, Env = env };
            },
            Resolve = async input =>
            {
                var apiKey = await ResolveValue("CLOUDFLARE_API_KEY", input);
                var accountId = await ResolveValue("CLOUDFLARE_ACCOUNT_ID", input);
                var gatewayId = gateway ? await ResolveValue("CLOUDFLARE_GATEWAY_ID", input) : null;
                if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(accountId) || (gateway && string.IsNullOrEmpty(gatewayId))) return null;
                var env = new Dictionary<string, string> { ["CLOUDFLARE_ACCOUNT_ID"] = accountId };
                if (!string.IsNullOrEmpty(gatewayId)) env["CLOUDFLARE_GATEWAY_ID"] = gatewayId;
                var source = input.Credential is not null ? "stored credential" : "CLOUDFLARE_API_KEY";
                var auth = gateway
                    ? new ModelAuth { Headers = new Dictionary<string, string?> { ["cf-aig-authorization"] = $"Bearer {apiKey}", ["Authorization"] = null, ["x-api-key"] = null } }
                    : new ModelAuth { ApiKey = apiKey };
                return new AuthResult { Auth = auth, Env = env, Source = source };
            },
        };
    }
}

/// <summary>Materializes Cloudflare account/gateway placeholders in the base URL from resolved env.</summary>
public sealed class CloudflareStreams(IApiStreams inner) : IApiStreams
{
    public static Model ResolveModel(Model model, IReadOnlyDictionary<string, string>? env)
    {
        if (env is null) return model;
        var baseUrl = model.BaseUrl
            .Replace("{CLOUDFLARE_ACCOUNT_ID}", env.GetValueOrDefault("CLOUDFLARE_ACCOUNT_ID") ?? "{CLOUDFLARE_ACCOUNT_ID}")
            .Replace("{CLOUDFLARE_GATEWAY_ID}", env.GetValueOrDefault("CLOUDFLARE_GATEWAY_ID") ?? "{CLOUDFLARE_GATEWAY_ID}");
        return baseUrl == model.BaseUrl ? model : model.WithBaseUrl(baseUrl);
    }

    public AssistantMessageEventStream Stream(Model model, Context context, StreamOptions? options = null) =>
        inner.Stream(ResolveModel(model, options?.Env), context, options);

    public AssistantMessageEventStream StreamSimple(Model model, Context context, SimpleStreamOptions? options = null) =>
        inner.StreamSimple(ResolveModel(model, options?.Env), context, options);
}

/// <summary>Adds OpenCode's per-conversation routing header.</summary>
public sealed class OpenCodeSessionHeaderStreams(IApiStreams inner) : IApiStreams
{
    private const string Header = "x-opencode-session";

    private static T? WithSessionHeader<T>(T? options) where T : StreamOptions
    {
        if (options?.SessionId is null || (options.Headers?.Keys.Any(k => string.Equals(k, Header, StringComparison.OrdinalIgnoreCase)) ?? false)) return options;
        var clone = (T)options.ShallowClone();
        clone.Headers = new Dictionary<string, string?>(options.Headers ?? []) { [Header] = options.SessionId };
        return clone;
    }

    public AssistantMessageEventStream Stream(Model model, Context context, StreamOptions? options = null) =>
        inner.Stream(model, context, WithSessionHeader(options));

    public AssistantMessageEventStream StreamSimple(Model model, Context context, SimpleStreamOptions? options = null) =>
        inner.StreamSimple(model, context, WithSessionHeader(options));
}
