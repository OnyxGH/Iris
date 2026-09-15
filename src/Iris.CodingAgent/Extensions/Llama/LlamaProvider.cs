using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Iris.Ai;
using Iris.Ai.Auth;
using Iris.Ai.Models;
using Iris.Ai.Providers;
using Iris.Ai.Utils;
using Iris.CodingAgent.Core;

namespace Iris.CodingAgent.Extensions.Llama;

/// <summary>
/// Built-in llama.cpp provider: dynamic catalog from a router-mode llama-server. Port of extensions/llama/provider.ts.
/// pi registers it through its hidden built-in extension; Iris registers it directly on each model runtime.
/// </summary>
public sealed class LlamaProvider : IProvider
{
    public const string ProviderId = "llama.cpp";
    public const string DefaultServerUrl = "http://127.0.0.1:8080";

    private static readonly ConditionalWeakTable<ModelRuntime, LlamaProvider> Registered = new();
    private IReadOnlyList<Model> _models = [];

    private LlamaProvider()
    {
        Auth = new ProviderAuth
        {
            ApiKey = new ApiKeyAuth
            {
                Name = "llama.cpp server",
                Login = async interaction =>
                {
                    var envUrl = Environment.GetEnvironmentVariable("LLAMA_BASE_URL");
                    var enteredUrl = await interaction.PromptAsync(new AuthPrompt
                    {
                        Type = "text",
                        Message = "llama.cpp server URL",
                        Placeholder = string.IsNullOrEmpty(envUrl) ? DefaultServerUrl : envUrl,
                        CancellationToken = interaction.CancellationToken,
                    });
                    var serverUrl = LlamaClient.NormalizeServerUrl(
                        enteredUrl.Trim() is { Length: > 0 } entered ? entered : !string.IsNullOrEmpty(envUrl) ? envUrl : DefaultServerUrl);
                    var apiKey = (await interaction.PromptAsync(new AuthPrompt
                    {
                        Type = "secret",
                        Message = "API key (optional)",
                        CancellationToken = interaction.CancellationToken,
                    })).Trim();
                    await new LlamaClient(serverUrl, apiKey).ListAsync(ct: interaction.CancellationToken);
                    return new ApiKeyCredential
                    {
                        Key = apiKey.Length > 0 ? apiKey : null,
                        Env = new Dictionary<string, string> { ["LLAMA_BASE_URL"] = serverUrl },
                    };
                },
                Check = async input =>
                {
                    var serverUrl = await ResolveServerUrlAsync(input.Ctx, input.Credential);
                    return serverUrl is null ? null : new AuthCheck(AuthTypes.ApiKey, input.Credential is not null ? "stored credential" : "LLAMA_BASE_URL");
                },
                Resolve = async input =>
                {
                    if (await ResolveServerUrlAsync(input.Ctx, input.Credential) is not { } serverUrl) return null;
                    var apiKey = input.Credential?.Key ?? await input.Ctx.EnvAsync("LLAMA_API_KEY") ?? "local";
                    var env = new Dictionary<string, string>(input.Credential?.Env ?? []) { ["LLAMA_BASE_URL"] = serverUrl };
                    return new AuthResult
                    {
                        Auth = new ModelAuth { ApiKey = apiKey, BaseUrl = LlamaClient.InferenceUrl(serverUrl) },
                        Env = env,
                        Source = input.Credential is not null ? "stored credential" : "LLAMA_BASE_URL",
                    };
                },
            },
        };
    }

    public string Id => ProviderId;
    public string Name => "llama.cpp";
    public string? BaseUrl => LlamaClient.InferenceUrl(DefaultServerUrl);
    public IReadOnlyDictionary<string, string?>? Headers => null;
    public ProviderAuth Auth { get; }
    public Func<IReadOnlyList<Model>, Credential?, IReadOnlyList<Model>>? FilterModels => null;
    public Func<RefreshModelsContext, Task>? RefreshModels => RefreshModelsAsync;

    /// <summary>Register the provider on a model runtime (once) and return it.</summary>
    public static LlamaProvider EnsureRegistered(ModelRuntime runtime)
    {
        lock (Registered)
        {
            if (Registered.TryGetValue(runtime, out var existing)) return existing;
            var provider = new LlamaProvider();
            Registered.Add(runtime, provider);
            runtime.RegisterNativeProvider(provider);
            return provider;
        }
    }

    public static LlamaProvider? Get(ModelRuntime runtime)
    {
        lock (Registered) return Registered.TryGetValue(runtime, out var provider) ? provider : null;
    }

    private static string? CredentialServerUrl(ApiKeyCredential? credential) =>
        credential?.Env?.GetValueOrDefault("LLAMA_BASE_URL") is { } value && value.Trim().Length > 0 ? LlamaClient.NormalizeServerUrl(value) : null;

    private static async Task<string?> ResolveServerUrlAsync(IAuthContext ctx, ApiKeyCredential? credential)
    {
        var configured = CredentialServerUrl(credential) ?? (await ctx.EnvAsync("LLAMA_BASE_URL"))?.Trim();
        return string.IsNullOrEmpty(configured) ? null : LlamaClient.NormalizeServerUrl(configured);
    }

    private static bool IsSelectable(LlamaModelInfo model, bool routerAutoload) =>
        model.Status.Value is "loaded" or "sleeping"
        || (routerAutoload && model.Status.Value == "unloaded" && !model.Status.Failed && model.Source == "preset");

    private static async Task<bool> RouterAutoloadEnabledAsync(LlamaClient client, IReadOnlyList<LlamaModelInfo> catalog, CancellationToken ct)
    {
        if (!catalog.Any(m => m.Status.Value == "unloaded" && m.Source == "preset")) return false;
        try
        {
            return await client.GetModelsAutoloadAsync(ct) == true;
        }
        catch
        {
            return false;
        }
    }

    private static Model ToModel(LlamaModelInfo model, string serverUrl)
    {
        var reported = model.ContextSize ?? model.TrainContextSize;
        var contextWindow = reported is > 0 ? reported.Value : 128000;
        return new Model
        {
            Id = model.Id,
            Name = model.Id,
            Api = "openai-completions",
            Provider = ProviderId,
            BaseUrl = LlamaClient.InferenceUrl(serverUrl),
            Reasoning = false,
            Input = model.InputModalities?.Contains("image") == true ? ["text", "image"] : ["text"],
            Cost = new ModelCost(),
            ContextWindow = contextWindow,
            MaxTokens = contextWindow,
            Compat = new JsonObject
            {
                ["supportsStore"] = false,
                ["supportsDeveloperRole"] = false,
                ["supportsReasoningEffort"] = false,
                ["supportsUsageInStreaming"] = true,
                ["supportsStrictMode"] = false,
                ["maxTokensField"] = "max_tokens",
            },
        };
    }

    /// <summary>Replace the in-memory catalog with models from a /llama catalog read.</summary>
    public void SetCatalog(IReadOnlyList<LlamaModelInfo> catalog, string serverUrl, bool routerAutoload = false) =>
        _models = catalog.Where(m => IsSelectable(m, routerAutoload)).Select(m => ToModel(m, serverUrl)).ToList();

    public IReadOnlyList<Model> GetModels() => _models;

    private async Task RefreshModelsAsync(RefreshModelsContext context)
    {
        if (context.Stored is not null)
        {
            var restored = context.Stored.Models.Where(m => m.Provider == ProviderId && m.Api == "openai-completions").ToList();
            if (!await context.Publish(new ModelsPublication { Update = () => _models = restored })) return;
        }
        if (!context.AllowNetwork || context.CancellationToken.IsCancellationRequested || context.Credential is not ApiKeyCredential credential) return;
        if (CredentialServerUrl(credential) is not { } serverUrl) return;
        var client = new LlamaClient(serverUrl, credential.Key);
        var catalog = await client.ListAsync(ct: context.CancellationToken);
        if (context.CancellationToken.IsCancellationRequested) return;
        var routerAutoload = await RouterAutoloadEnabledAsync(client, catalog, context.CancellationToken);
        if (context.CancellationToken.IsCancellationRequested) return;
        var refreshed = catalog.Where(m => IsSelectable(m, routerAutoload)).Select(m => ToModel(m, serverUrl)).ToList();
        await context.Publish(new ModelsPublication
        {
            HasPersist = true,
            Persist = new ModelsStoreEntry { Models = refreshed, CheckedAt = TimeUtil.NowMs() },
            Update = () => _models = refreshed,
        });
    }

    private static IApiStreams Api => ApiRegistry.BuiltinApis["openai-completions"];

    public AssistantMessageEventStream Stream(Model model, Context context, StreamOptions? options = null) => Api.Stream(model, context, options);

    public AssistantMessageEventStream StreamSimple(Model model, Context context, SimpleStreamOptions? options = null) => Api.StreamSimple(model, context, options);
}
