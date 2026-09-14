using PiSharp.Agent;
using PiSharp.Ai;
using PiSharp.Ai.Models;
using PiSharp.CodingAgent.Config;
using PiSharp.CodingAgent.Core.Extensions;
using PiSharp.CodingAgent.Core.Tools;
using PiSharp.CodingAgent.Utils;

namespace PiSharp.CodingAgent.Core;

public sealed class CreateAgentSessionOptions
{
    public string? Cwd { get; init; }
    public string? AgentDir { get; init; }
    public ModelRuntime? ModelRuntime { get; init; }
    public Model? Model { get; init; }
    public ThinkingLevel? ThinkingLevel { get; init; }
    public List<ScopedModel>? ScopedModels { get; init; }

    /// <summary>"all" starts with no tools; "builtin" disables the default built-in tools but keeps custom tools.</summary>
    public string? NoTools { get; init; }

    /// <summary>Allowlist of tool names.</summary>
    public List<string>? Tools { get; init; }

    public List<string>? ExcludeTools { get; init; }
    public List<ToolDefinition>? CustomTools { get; init; }
    public IResourceLoader? ResourceLoader { get; init; }
    public SessionManager? SessionManager { get; init; }
    public SettingsManager? SettingsManager { get; init; }
    public string SessionStartReason { get; init; } = "startup";
    public Func<AgentSession, IExtensionRunner>? ExtensionRunnerFactory { get; init; }
}

public sealed record CreateAgentSessionResult(AgentSession Session, string? ModelFallbackMessage);

/// <summary>Stream options carrying the per-request header transform used for provider attribution and extensions.</summary>
internal sealed class SessionStreamOptions : SimpleStreamOptions, IHeaderTransform
{
    public Func<Dictionary<string, string?>, Task<Dictionary<string, string?>>>? TransformHeaders { get; init; }
}

/// <summary>Provider attribution headers. Port of core/provider-attribution.ts.</summary>
public static class ProviderAttribution
{
    private static bool MatchesHost(string baseUrl, string host) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) && uri.Host == host;

    public static bool IsInstallTelemetryEnabled(SettingsManager settings, string? telemetryEnv = null)
    {
        telemetryEnv ??= Environment.GetEnvironmentVariable("PI_TELEMETRY");
        if (telemetryEnv is null) return settings.EnableInstallTelemetry;
        return telemetryEnv == "1" || telemetryEnv.Equals("true", StringComparison.OrdinalIgnoreCase) || telemetryEnv.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string?>? DefaultAttributionHeaders(Model model, SettingsManager settings)
    {
        if (!IsInstallTelemetryEnabled(settings)) return null;
        if (model.Provider == "openrouter" || model.BaseUrl.Contains("openrouter.ai"))
        {
            return new() { ["HTTP-Referer"] = "https://pi.dev", ["X-OpenRouter-Title"] = "pi", ["X-OpenRouter-Categories"] = "cli-agent" };
        }
        if (model.Provider == "nvidia" || MatchesHost(model.BaseUrl, "integrate.api.nvidia.com"))
        {
            return new() { ["X-BILLING-INVOKE-ORIGIN"] = "Pi" };
        }
        if (model.Provider is "cloudflare-workers-ai" or "cloudflare-ai-gateway" || MatchesHost(model.BaseUrl, "api.cloudflare.com") || MatchesHost(model.BaseUrl, "gateway.ai.cloudflare.com"))
        {
            return new() { ["User-Agent"] = "pi-coding-agent" };
        }
        return null;
    }

    private static Dictionary<string, string?>? SessionHeaders(Model model, string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return null;
        if (model.Provider is not "opencode" and not "opencode-go" && !MatchesHost(model.BaseUrl, "opencode.ai")) return null;
        return new() { ["x-opencode-session"] = sessionId, ["x-opencode-client"] = "pi" };
    }

    public static Dictionary<string, string?>? Merge(Model model, SettingsManager settings, string? sessionId, params Dictionary<string, string?>?[] sources)
    {
        var merged = new Dictionary<string, string?>();
        foreach (var source in new[] { SessionHeaders(model, sessionId), DefaultAttributionHeaders(model, settings) }.Concat(sources))
        {
            if (source is null) continue;
            foreach (var (key, value) in source) merged[key] = value;
        }
        return merged.Count > 0 ? merged : null;
    }
}

/// <summary>Session factory. Port of core/sdk.ts (createAgentSession).</summary>
public static class Sdk
{
    private const string ImageReadingDisabled = "Image reading is disabled.";

    public static async Task<CreateAgentSessionResult> CreateAgentSessionAsync(CreateAgentSessionOptions? options = null)
    {
        options ??= new CreateAgentSessionOptions();
        CodingAgentMessages.Register();

        var cwd = PathUtils.ResolvePath(options.Cwd ?? options.SessionManager?.Cwd ?? Directory.GetCurrentDirectory());
        var agentDir = options.AgentDir is not null ? PathUtils.ResolvePath(options.AgentDir) : AppConfig.AgentDir;

        var modelRuntime = options.ModelRuntime ?? await ModelRuntime.CreateAsync(new CreateModelRuntimeOptions
        {
            AuthPath = options.AgentDir is not null ? Path.Combine(agentDir, "auth.json") : null,
            ModelsPath = options.AgentDir is not null ? Path.Combine(agentDir, "models.json") : null,
        });

        global::PiSharp.CodingAgent.Extensions.Llama.LlamaProvider.EnsureRegistered(modelRuntime);
        var settingsManager = options.SettingsManager ?? SettingsManager.Create(cwd, agentDir);
        var sessionManager = options.SessionManager ?? SessionManager.Create(cwd, SessionManager.GetDefaultSessionDir(cwd, agentDir));

        var resourceLoader = options.ResourceLoader;
        if (resourceLoader is null)
        {
            resourceLoader = new DefaultResourceLoader(new DefaultResourceLoaderOptions { Cwd = cwd, AgentDir = agentDir, SettingsManager = settingsManager });
            await resourceLoader.ReloadAsync();
        }

        var existingSession = sessionManager.BuildSessionContext();
        var hasExistingSession = existingSession.Messages.Count > 0;
        var hasThinkingEntry = sessionManager.GetBranch().Any(e => e is ThinkingLevelChangeEntry);

        var model = options.Model;
        string? modelFallbackMessage = null;

        if (model is null && hasExistingSession && existingSession.Model is { } savedModel)
        {
            var restored = modelRuntime.GetModel(savedModel.Provider, savedModel.ModelId);
            if (restored is not null && modelRuntime.HasConfiguredAuth(restored.Provider)) model = restored;
            if (model is null) modelFallbackMessage = $"Could not restore model {savedModel.Provider}/{savedModel.ModelId}";
        }

        if (model is null)
        {
            var result = ModelResolver.FindInitialModel(null, null, [], hasExistingSession, settingsManager.DefaultProvider, settingsManager.DefaultModel,
                settingsManager.DefaultThinkingLevel, settingsManager.GetAllModelThinkingLevels(), modelRuntime);
            model = result.Model;
            if (model is null) modelFallbackMessage = AuthGuidance.FormatNoModelsAvailableMessage();
            else if (modelFallbackMessage is not null) modelFallbackMessage += $". Using {model.Provider}/{model.Id}";
        }

        var thinkingLevel = options.ThinkingLevel;
        if (thinkingLevel is null && hasExistingSession)
        {
            thinkingLevel = hasThinkingEntry
                ? ThinkingLevels.Parse(existingSession.ThinkingLevel) ?? ModelResolver.DefaultThinkingLevel
                : settingsManager.DefaultThinkingLevel ?? ModelResolver.DefaultThinkingLevel;
        }
        if (thinkingLevel is null && model is not null && settingsManager.GetModelThinkingLevel(model.Provider, model.Id) is { } perModel) thinkingLevel = perModel;
        thinkingLevel ??= settingsManager.DefaultThinkingLevel ?? ModelResolver.DefaultThinkingLevel;
        thinkingLevel = model is null ? ThinkingLevel.Off : ModelUtils.ClampThinkingLevel(model, thinkingLevel.Value);

        var excluded = options.ExcludeTools?.ToHashSet();
        var allowedToolNames = options.Tools ?? (options.NoTools == "all" ? [] : null);
        var initialActiveToolNames = (options.Tools ?? (options.NoTools is not null ? [] : settingsManager.DefaultTools ?? ["read", "bash", "edit", "write"]))
            .Where(name => excluded?.Contains(name) != true)
            .ToList();

        var extensionRunnerRef = new ExtensionRunnerRef();

        var agent = new Agent.Agent(new AgentOptions
        {
            SystemPrompt = "",
            Model = model,
            ThinkingLevel = thinkingLevel,
            Tools = [],
            ConvertToLlm = messages => Task.FromResult(ConvertToLlmWithBlockImages(messages, settingsManager)),
            StreamFn = (streamModel, context, streamOptions) =>
            {
                var retry = settingsManager.ProviderRetrySettings;
                var httpIdleTimeoutMs = settingsManager.HttpIdleTimeoutMs;
                // SDKs treat timeout=0 as an immediate timeout; use max int32 to disable it.
                var effectiveTimeoutMs = httpIdleTimeoutMs == 0 ? int.MaxValue : (int)Math.Min(int.MaxValue, httpIdleTimeoutMs);
                var requestOptions = new SessionStreamOptions
                {
                    TransformHeaders = async requestHeaders =>
                    {
                        var headers = ProviderAttribution.Merge(streamModel, settingsManager, streamOptions?.SessionId, requestHeaders) ?? [];
                        var runner = extensionRunnerRef.Current;
                        return runner?.HasHandlers("before_provider_headers") == true
                            ? (await runner.EmitAsync(ExtensionEvent.Of("before_provider_headers", ("headers", headers)))) as Dictionary<string, string?> ?? headers
                            : headers;
                    },
                };
                if (streamOptions is not null)
                {
                    streamOptions.CopyTo(requestOptions);
                    requestOptions.ToolChoice = streamOptions.ToolChoice;
                    requestOptions.Reasoning = streamOptions.Reasoning;
                    requestOptions.ThinkingBudgets = streamOptions.ThinkingBudgets;
                }
                requestOptions.TimeoutMs = streamOptions?.TimeoutMs ?? retry.TimeoutMs ?? effectiveTimeoutMs;
                requestOptions.WebSocketConnectTimeoutMs = streamOptions?.WebSocketConnectTimeoutMs ?? settingsManager.WebSocketConnectTimeoutMs;
                requestOptions.MaxRetries = streamOptions?.MaxRetries ?? retry.MaxRetries;
                requestOptions.MaxRetryDelayMs = streamOptions?.MaxRetryDelayMs ?? retry.MaxRetryDelayMs;
                return Task.FromResult(modelRuntime.StreamSimple(streamModel, context, requestOptions));
            },
            OnPayload = async (payload, _) =>
            {
                var runner = extensionRunnerRef.Current;
                if (runner?.HasHandlers("before_provider_request") != true) return payload;
                return await runner.EmitAsync(ExtensionEvent.Of("before_provider_request", ("payload", payload))) as System.Text.Json.Nodes.JsonNode ?? payload;
            },
            OnResponse = async (response, _) =>
            {
                var runner = extensionRunnerRef.Current;
                if (runner?.HasHandlers("after_provider_response") != true) return;
                await runner.EmitAsync(ExtensionEvent.Of("after_provider_response", ("status", response.Status), ("headers", response.Headers)));
            },
            SessionId = sessionManager.SessionId,
            TransformContext = async (messages, _) =>
            {
                var runner = extensionRunnerRef.Current;
                return runner is null ? messages : await runner.EmitContextAsync(messages);
            },
            SteeringMode = QueueModes.Parse(settingsManager.SteeringMode),
            FollowUpMode = QueueModes.Parse(settingsManager.FollowUpMode),
            Transport = settingsManager.Transport,
            ThinkingBudgets = settingsManager.ThinkingBudgets,
            MaxRetryDelayMs = settingsManager.ProviderRetrySettings.MaxRetryDelayMs,
        });

        if (hasExistingSession)
        {
            agent.State.Messages = existingSession.Messages;
            if (!hasThinkingEntry) sessionManager.AppendThinkingLevelChange(thinkingLevel.Value.ToWire());
        }
        else
        {
            if (model is not null) sessionManager.AppendModelChange(model.Provider, model.Id);
            sessionManager.AppendThinkingLevelChange(thinkingLevel.Value.ToWire());
        }

        var session = new AgentSession(new AgentSessionConfig
        {
            Agent = agent,
            SessionManager = sessionManager,
            SettingsManager = settingsManager,
            Cwd = cwd,
            ScopedModels = options.ScopedModels,
            ResourceLoader = resourceLoader,
            CustomTools = options.CustomTools,
            ModelRuntime = modelRuntime,
            InitialActiveToolNames = initialActiveToolNames,
            AllowedToolNames = allowedToolNames,
            ExcludedToolNames = options.ExcludeTools,
            ExtensionRunnerRef = extensionRunnerRef,
            ExtensionRunnerFactory = options.ExtensionRunnerFactory,
            SessionStartReason = options.SessionStartReason,
        });

        return new CreateAgentSessionResult(session, modelFallbackMessage);
    }

    /// <summary>convertToLlm that replaces images with a placeholder when images.blockImages is enabled.</summary>
    private static List<Message> ConvertToLlmWithBlockImages(IReadOnlyList<Message> messages, SettingsManager settings)
    {
        var converted = CodingAgentMessages.ConvertToLlm(messages);
        if (!settings.BlockImages) return converted;

        List<ContentBlock> Filter(List<ContentBlock> content)
        {
            var result = new List<ContentBlock>();
            foreach (var block in content)
            {
                var next = block is ImageContent ? new TextContent(ImageReadingDisabled) : block;
                if (next is TextContent { Text: ImageReadingDisabled } && result.Count > 0 && result[^1] is TextContent { Text: ImageReadingDisabled }) continue;
                result.Add(next);
            }
            return result;
        }

        return converted.Select(message =>
        {
            switch (message)
            {
                case UserMessage { Content.Blocks: { } blocks } user when blocks.Any(b => b is ImageContent):
                    var userCopy = (UserMessage)user.ShallowCopy();
                    userCopy.Content = UserContent.FromBlocks(Filter(blocks));
                    return userCopy;
                case ToolResultMessage toolResult when toolResult.Content.Any(b => b is ImageContent):
                    var resultCopy = (ToolResultMessage)toolResult.ShallowCopy();
                    resultCopy.Content = Filter(toolResult.Content);
                    return resultCopy;
                default:
                    return message;
            }
        }).ToList();
    }
}
