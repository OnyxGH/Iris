using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Iris.Agent;
using Iris.Ai;
using Iris.Ai.Auth;
using Iris.Ai.Utils;
using Iris.CodingAgent.Core.Compaction;
using Iris.CodingAgent.Core.Extensions;
using Iris.CodingAgent.Core.Tools;
using Iris.CodingAgent.Utils;

namespace Iris.CodingAgent.Core;

/// <summary>Mutable holder through which the agent's stream hooks reach the current extension runner.</summary>
public sealed class ExtensionRunnerRef
{
    public IExtensionRunner? Current { get; set; }
}

public sealed class AgentSessionConfig
{
    public required Agent.Agent Agent { get; init; }
    public required SessionManager SessionManager { get; init; }
    public required SettingsManager SettingsManager { get; init; }
    public required string Cwd { get; init; }

    /// <summary>Models to cycle through (from --models).</summary>
    public List<ScopedModel>? ScopedModels { get; init; }

    public required IResourceLoader ResourceLoader { get; init; }

    /// <summary>SDK custom tools registered outside extensions.</summary>
    public List<ToolDefinition>? CustomTools { get; init; }

    public required ModelRuntime ModelRuntime { get; init; }

    /// <summary>Initial active built-in tool names. Default: read, bash, edit, write.</summary>
    public List<string>? InitialActiveToolNames { get; init; }

    public List<string>? AllowedToolNames { get; init; }
    public List<string>? ExcludedToolNames { get; init; }

    /// <summary>Override base tools (synthesized into minimal definitions).</summary>
    public Dictionary<string, AgentTool>? BaseToolsOverride { get; init; }

    public ExtensionRunnerRef? ExtensionRunnerRef { get; init; }

    /// <summary>Creates the extension runner for this session. Default: no extensions.</summary>
    public Func<AgentSession, IExtensionRunner>? ExtensionRunnerFactory { get; init; }

    /// <summary>"startup" | "reload" | "new" | "resume" | "fork".</summary>
    public string SessionStartReason { get; init; } = "startup";
}

/// <summary>
/// Core abstraction for agent lifecycle and session management, shared by all run modes.
/// Session HTML/JSONL export arrives with the export milestone.
/// </summary>
public sealed partial class AgentSession : IDisposable
{
    private sealed record ToolDefinitionEntry(ToolDefinition Definition, SourceInfo SourceInfo);

    private static readonly string[] DefaultActiveToolNames = ["read", "bash", "edit", "write"];

    public Agent.Agent Agent { get; }
    public SessionManager SessionManager { get; }
    public SettingsManager SettingsManager { get; }

    private List<ScopedModel> _scopedModels;
    private IDisposable? _agentSubscription;
    private readonly List<Action<AgentSessionEvent>> _eventListeners = [];
    private readonly object _listenerLock = new();
    private bool _isAgentRunActive;
    private TaskCompletionSource? _idleWait;

    private List<string> _steeringMessages = [];
    private List<string> _followUpMessages = [];
    private List<CustomMessage> _pendingNextTurnMessages = [];
    private List<CustomMessage> _pendingCustomMessages = [];

    private CancellationTokenSource? _compactionCts;
    private CancellationTokenSource? _autoCompactionCts;
    private bool _overflowRecoveryAttempted;
    private CancellationTokenSource? _branchSummaryCts;
    private CancellationTokenSource? _retryCts;
    private int _retryAttempt;
    private readonly HashSet<CancellationTokenSource> _bashCts = [];
    private List<BashExecutionMessage> _pendingBashMessages = [];

    private IExtensionRunner _extensionRunner = new NullExtensionRunner();
    private int _turnIndex;

    private readonly IResourceLoader _resourceLoader;
    private readonly List<ToolDefinition> _customTools;
    private Dictionary<string, ToolDefinition> _baseToolDefinitions = [];
    private readonly string _cwd;
    private readonly AgentSessionConfig _config;
    private readonly HashSet<string>? _allowedToolNames;
    private readonly HashSet<string>? _excludedToolNames;
    private IDisposable? _extensionErrorSubscription;
    private ExtensionBindings? _extensionBindings;

    private readonly ModelRuntime _modelRuntime;

    private Dictionary<string, AgentTool> _toolRegistry = [];
    private Dictionary<string, ToolDefinitionEntry> _toolDefinitions = [];
    private Dictionary<string, string> _toolPromptSnippets = [];
    private Dictionary<string, List<string>> _toolPromptGuidelines = [];

    private string _baseSystemPrompt = "";
    private BuildSystemPromptOptions _baseSystemPromptOptions;
    private string? _systemPromptOverride;

    private AssistantMessage? _lastAssistantMessage;

    public AgentSession(AgentSessionConfig config)
    {
        _config = config;
        Agent = config.Agent;
        SessionManager = config.SessionManager;
        SettingsManager = config.SettingsManager;
        _scopedModels = config.ScopedModels ?? [];
        _resourceLoader = config.ResourceLoader;
        _customTools = config.CustomTools ?? [];
        _cwd = config.Cwd;
        _modelRuntime = config.ModelRuntime;
        _allowedToolNames = config.AllowedToolNames is null ? null : [.. config.AllowedToolNames];
        _excludedToolNames = config.ExcludedToolNames is null ? null : [.. config.ExcludedToolNames];
        _baseSystemPromptOptions = new BuildSystemPromptOptions { Cwd = _cwd };

        // Always subscribe for internal handling (persistence, extensions, auto-compaction, retry).
        _agentSubscription = Agent.Subscribe((evt, _) => HandleAgentEventAsync(evt));
        InstallAgentToolHooks();
        InstallAgentNextTurnRefresh();

        BuildRuntime(config.InitialActiveToolNames, includeAllExtensionTools: true);
    }

    public ModelRuntime ModelRuntime => _modelRuntime;

    public IResourceLoader ResourceLoader => _resourceLoader;

    public IExtensionRunner ExtensionRunner => _extensionRunner;

    // =========================================================================
    // Auth
    // =========================================================================

    private sealed record RequestAuth(Model Model, string? ApiKey = null, Dictionary<string, string?>? Headers = null, Dictionary<string, string>? Env = null);

    private static Dictionary<string, string?>? WithoutDeletedHeaders(Dictionary<string, string?>? headers) =>
        headers?.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value);

    private async Task<RequestAuth> GetRequiredRequestAuthAsync(Model model)
    {
        AuthResult? result;
        try
        {
            result = await _modelRuntime.GetAuthAsync(model);
        }
        catch (Exception ex) when (ex.InnerException?.Message == "authHeader requires a resolved API key")
        {
            throw new InvalidOperationException(AuthGuidance.FormatNoApiKeyFoundMessage(model.Provider));
        }

        if (result is not null && (!string.IsNullOrEmpty(result.Auth.ApiKey) || result.Auth.Headers is not null))
        {
            var requestModel = string.IsNullOrEmpty(result.Auth.BaseUrl) ? model : model.WithBaseUrl(result.Auth.BaseUrl);
            return new RequestAuth(requestModel, result.Auth.ApiKey, WithoutDeletedHeaders(result.Auth.Headers), result.Env);
        }

        if (_modelRuntime.IsUsingOAuth(model.Provider))
        {
            throw new InvalidOperationException(
                $"Authentication failed for \"{model.Provider}\". Credentials may have expired or network is unavailable. Run '/login {model.Provider}' to re-authenticate.");
        }
        throw new InvalidOperationException(AuthGuidance.FormatNoApiKeyFoundMessage(model.Provider));
    }

    private async Task<RequestAuth> GetSummarizationRequestAuthAsync(Model model)
    {
        if (ReferenceEquals(Agent.StreamFunction, DefaultStreamFn.Get())) return await GetRequiredRequestAuthAsync(model);
        try
        {
            var result = await _modelRuntime.GetAuthAsync(model);
            if (result is null) return new RequestAuth(model);
            var requestModel = string.IsNullOrEmpty(result.Auth.BaseUrl) ? model : model.WithBaseUrl(result.Auth.BaseUrl);
            return new RequestAuth(requestModel, result.Auth.ApiKey, WithoutDeletedHeaders(result.Auth.Headers), result.Env);
        }
        catch
        {
            return new RequestAuth(model);
        }
    }

    // =========================================================================
    // Agent hooks
    // =========================================================================

    private void InstallAgentToolHooks()
    {
        Agent.BeforeToolCall = async (ctx, _) =>
        {
            var runner = _extensionRunner;
            if (!runner.HasHandlers("tool_call")) return null;
            var result = await runner.EmitToolCallAsync(ctx.ToolCall.Name, ctx.ToolCall.Id, ctx.Args);
            return result is null ? null : new BeforeToolCallResult { Block = result.Block, Reason = result.Reason };
        };

        Agent.AfterToolCall = async (ctx, _) =>
        {
            var runner = _extensionRunner;
            var hookResult = runner.HasHandlers("tool_result")
                ? await runner.EmitToolResultAsync(ctx.ToolCall.Name, ctx.ToolCall.Id, ctx.Args, ctx.Result.Content, ctx.Result.Details, ctx.IsError, ctx.Result.Usage)
                : null;

            var content = hookResult?.Content ?? ctx.Result.Content;
            // Runs after the extension hook so images injected or replaced by extensions are normalized too.
            var normalized = await ImageProcessor.NormalizeToolResultImagesAsync(content, SettingsManager.ImageAutoResize);
            if (hookResult is null && ReferenceEquals(normalized, content)) return null;

            return new AfterToolCallResult
            {
                Content = normalized,
                Details = hookResult?.Details,
                IsError = hookResult?.IsError ?? ctx.IsError,
                Usage = hookResult?.Usage,
            };
        };
    }

    private static CompactionSettings ToCompactionSettings((bool Enabled, long ReserveTokens, long KeepRecentTokens) s) =>
        new() { Enabled = s.Enabled, ReserveTokens = s.ReserveTokens, KeepRecentTokens = s.KeepRecentTokens };

    private async Task<AgentContext> CompactBeforeNextAssistantResponseAsync(AgentContext context)
    {
        var model = Model;
        var settings = ToCompactionSettings(SettingsManager.GetCompactionSettings(model));
        if (model is null || model.ContextWindow <= 0
            || !Compactor.ShouldCompact(Compactor.EstimateContextTokens(context.Messages).Tokens, model.ContextWindow, settings))
        {
            return context;
        }

        await RunAutoCompactionAsync("threshold", willRetry: false);
        return new AgentContext { SystemPrompt = context.SystemPrompt, Messages = [.. Agent.State.Messages], Tools = context.Tools };
    }

    private void InstallAgentNextTurnRefresh()
    {
        var previous = Agent.PrepareNextTurnWithContext;
        if (previous is null && Agent.PrepareNextTurn is { } prepareNextTurn) previous = (_, ct) => prepareNextTurn(ct);

        Agent.PrepareNextTurnWithContext = async (turn, ct) =>
        {
            var context = await CompactBeforeNextAssistantResponseAsync(turn.Context);
            var previousSnapshot = previous is null ? null : await previous(turn with { Context = context }, ct);
            var nextContext = previousSnapshot?.Context ?? context;
            return new AgentLoopTurnUpdate
            {
                Context = new AgentContext
                {
                    SystemPrompt = _systemPromptOverride ?? _baseSystemPrompt,
                    Messages = nextContext.Messages,
                    Tools = [.. Agent.State.Tools],
                },
                Model = Agent.State.Model,
                ThinkingLevel = Agent.State.ThinkingLevel,
            };
        };
    }

    // =========================================================================
    // Event subscription
    // =========================================================================

    private void Emit(AgentSessionEvent evt)
    {
        Action<AgentSessionEvent>[] listeners;
        lock (_listenerLock) listeners = [.. _eventListeners];
        foreach (var listener in listeners) listener(evt);
    }

    private void EmitQueueUpdate() => Emit(new QueueUpdateEvent([.. _steeringMessages], [.. _followUpMessages]));

    private async Task EmitSessionCompactFailedAsync(string reason, string? errorMessage, bool aborted, bool willRetry, bool fromExtension)
    {
        if (!_extensionRunner.HasHandlers("session_compact_failed")) return;
        await _extensionRunner.EmitAsync(RunnerEvent.Of("session_compact_failed",
            ("reason", reason), ("errorMessage", errorMessage), ("aborted", aborted), ("willRetry", willRetry), ("fromExtension", fromExtension)));
    }

    private void ResolveIdleWaitIfIdle()
    {
        if (!IsIdle) return;
        Interlocked.Exchange(ref _idleWait, null)?.TrySetResult();
    }

    private async Task EmitAgentSettledAsync()
    {
        _isAgentRunActive = false;
        try
        {
            await _extensionRunner.EmitAsync(new RunnerEvent("agent_settled"));
            Emit(new AgentSettledEvent());
        }
        finally
        {
            ResolveIdleWaitIfIdle();
        }
    }

    private async Task HandleAgentEventAsync(AgentEvent evt)
    {
        // Remove a delivered queued message before emitting so listeners see the updated queue.
        if (evt is MessageStartEvent { Message: UserMessage user })
        {
            _overflowRecoveryAttempted = false;
            var text = TextUtils.ContentText(user.Content, "");
            if (text.Length > 0)
            {
                var steeringIndex = _steeringMessages.IndexOf(text);
                if (steeringIndex != -1)
                {
                    _steeringMessages.RemoveAt(steeringIndex);
                    EmitQueueUpdate();
                }
                else
                {
                    var followUpIndex = _followUpMessages.IndexOf(text);
                    if (followUpIndex != -1)
                    {
                        _followUpMessages.RemoveAt(followUpIndex);
                        EmitQueueUpdate();
                    }
                }
            }
        }

        evt = await EmitExtensionEventAsync(evt);

        Emit(evt is AgentEndEvent end ? new SessionAgentEndEvent(end.Messages, WillRetryAfterAgentEnd(end)) : new AgentCoreSessionEvent(evt));

        if (evt is MessageEndEvent messageEnd)
        {
            switch (messageEnd.Message)
            {
                case CustomMessage custom:
                    SessionManager.AppendCustomMessageEntry(custom.CustomType, custom.Content, custom.Display, custom.Details);
                    break;
                case UserMessage or AssistantMessage or ToolResultMessage:
                    SessionManager.AppendMessage(messageEnd.Message);
                    break;
                // bashExecution, compactionSummary and branchSummary messages are persisted elsewhere.
            }

            if (messageEnd.Message is AssistantMessage assistant)
            {
                _lastAssistantMessage = assistant;
                if (assistant.StopReason is not StopReason.Error and not StopReason.Length) _overflowRecoveryAttempted = false;
                if (assistant.StopReason != StopReason.Error && _retryAttempt > 0)
                {
                    Emit(new AutoRetryEndEvent(true, _retryAttempt));
                    _retryAttempt = 0;
                }
            }
        }

        // A turn ends after its assistant message and all tool results are appended: the first safe point to insert
        // context-only custom messages without splitting a tool call from its result.
        if (evt is TurnEndEvent) FlushPendingCustomMessages();
    }

    private bool WillRetryAfterAgentEnd(AgentEndEvent evt)
    {
        var settings = SettingsManager.RetrySettings;
        if (!settings.Enabled || _retryAttempt >= settings.MaxRetries) return false;
        for (var i = evt.Messages.Count - 1; i >= 0; i--)
        {
            if (evt.Messages[i] is AssistantMessage assistant) return IsRetryableError(assistant);
        }
        return false;
    }

    private AssistantMessage? FindLastAssistantMessage() => Agent.State.Messages.OfType<AssistantMessage>().LastOrDefault();

    /// <summary>Forward agent events to extensions; message_end may be replaced by an extension.</summary>
    private async Task<AgentEvent> EmitExtensionEventAsync(AgentEvent evt)
    {
        var runner = _extensionRunner;
        switch (evt)
        {
            case AgentStartEvent:
                _turnIndex = 0;
                await runner.EmitAsync(new RunnerEvent("agent_start"));
                break;
            case AgentEndEvent end:
                await runner.EmitAsync(RunnerEvent.Of("agent_end", ("messages", end.Messages)));
                break;
            case TurnStartEvent:
                await runner.EmitAsync(RunnerEvent.Of("turn_start", ("turnIndex", _turnIndex), ("timestamp", TimeUtil.NowMs())));
                break;
            case TurnEndEvent turnEnd:
                await runner.EmitAsync(RunnerEvent.Of("turn_end", ("turnIndex", _turnIndex), ("message", turnEnd.Message), ("toolResults", turnEnd.ToolResults)));
                _turnIndex++;
                break;
            case MessageStartEvent start:
                await runner.EmitAsync(RunnerEvent.Of("message_start", ("message", start.Message)));
                break;
            case MessageUpdateEvent update:
                await runner.EmitAsync(RunnerEvent.Of("message_update", ("message", update.Message), ("assistantMessageEvent", update.AssistantMessageEvent)));
                break;
            case MessageEndEvent messageEnd:
            {
                var replacement = await runner.EmitMessageEndAsync(messageEnd.Message);
                if (replacement is not null && !ReferenceEquals(replacement, messageEnd.Message))
                {
                    // Agent state already holds the finalized message; swap it so state, listeners and persistence agree.
                    var messages = Agent.State.Messages;
                    var index = messages.FindLastIndex(m => ReferenceEquals(m, messageEnd.Message));
                    if (index >= 0) messages[index] = replacement;
                    return new MessageEndEvent(replacement);
                }
                break;
            }
            case ToolExecutionStartEvent toolStart:
                await runner.EmitAsync(RunnerEvent.Of("tool_execution_start", ("toolCallId", toolStart.ToolCallId), ("toolName", toolStart.ToolName), ("args", toolStart.Args)));
                break;
            case ToolExecutionUpdateEvent toolUpdate:
                await runner.EmitAsync(RunnerEvent.Of("tool_execution_update", ("toolCallId", toolUpdate.ToolCallId), ("toolName", toolUpdate.ToolName), ("args", toolUpdate.Args), ("partialResult", toolUpdate.PartialResult)));
                break;
            case ToolExecutionEndEvent toolEnd:
                await runner.EmitAsync(RunnerEvent.Of("tool_execution_end", ("toolCallId", toolEnd.ToolCallId), ("toolName", toolEnd.ToolName), ("result", toolEnd.Result), ("isError", toolEnd.IsError)));
                break;
        }
        return evt;
    }

    /// <summary>Subscribe to session events. Dispose the result to unsubscribe.</summary>
    public IDisposable Subscribe(Action<AgentSessionEvent> listener)
    {
        lock (_listenerLock) _eventListeners.Add(listener);
        return new Unsubscriber(() =>
        {
            lock (_listenerLock) _eventListeners.Remove(listener);
        });
    }

    private sealed class Unsubscriber(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }

    public void Dispose()
    {
        try
        {
            AbortRetry();
            AbortCompaction();
            AbortBranchSummary();
            AbortBash();
            Agent.Abort();
        }
        catch
        {
            // Dispose must succeed even if an abort hook throws.
        }

        _extensionRunner.Invalidate(
            "This extension ctx is stale after session replacement or reload. Do not use a captured extension API or command ctx after ctx.newSession(), ctx.fork(), ctx.switchSession(), or ctx.reload(). For newSession, fork, and switchSession, move post-replacement work into withSession and use the ctx passed to withSession. For reload, do not use the old ctx after await ctx.reload().");
        _agentSubscription?.Dispose();
        _agentSubscription = null;
        _extensionErrorSubscription?.Dispose();
        lock (_listenerLock) _eventListeners.Clear();
    }

    // =========================================================================
    // Read-only state
    // =========================================================================

    public AgentState State => Agent.State;

    /// <summary>Current model, or null when none is selected.</summary>
    public Model? Model => ReferenceEquals(Agent.State.Model, Iris.Agent.Agent.DefaultModel) ? null : Agent.State.Model;

    public ThinkingLevel ThinkingLevel => Agent.State.ThinkingLevel;

    /// <summary>Whether an agent run or post-run continuation is in progress.</summary>
    public bool IsStreaming => _isAgentRunActive;

    /// <summary>No active run, compaction, branch summary, or queued continuation.</summary>
    public bool IsIdle => !_isAgentRunActive && !IsCompacting;

    public string SystemPrompt => Agent.State.SystemPrompt;

    public int RetryAttempt => _retryAttempt;

    public List<string> GetActiveToolNames() => Agent.State.Tools.Select(t => t.Name).ToList();

    /// <summary>All registered tools (built-in, extension and SDK) with their sources.</summary>
    public List<Iris.Extensions.ToolInfo> GetAllToolInfos() =>
        _toolDefinitions.Values.Select(e => new Iris.Extensions.ToolInfo(e.Definition.Name, e.Definition.Description, e.SourceInfo)).ToList();

    public List<ToolInfo> GetAllTools() =>
        _toolDefinitions.Values.Select(e => new ToolInfo(e.Definition.Name, e.Definition.Description, e.Definition.Parameters, e.Definition.PromptGuidelines, e.SourceInfo)).ToList();

    public ToolDefinition? GetToolDefinition(string name) => _toolDefinitions.GetValueOrDefault(name)?.Definition;

    /// <summary>Set active tools by name (unknown names are ignored) and rebuild the system prompt.</summary>
    public void SetActiveToolsByName(IEnumerable<string> toolNames)
    {
        var tools = new List<AgentTool>();
        var validToolNames = new List<string>();
        foreach (var name in toolNames)
        {
            if (!_toolRegistry.TryGetValue(name, out var tool)) continue;
            tools.Add(tool);
            validToolNames.Add(name);
        }
        Agent.State.Tools = tools;
        _baseSystemPrompt = RebuildSystemPrompt(validToolNames);
        Agent.State.SystemPrompt = _systemPromptOverride ?? _baseSystemPrompt;
    }

    public bool IsCompacting => _autoCompactionCts is not null || _compactionCts is not null || _branchSummaryCts is not null;

    /// <summary>All messages including custom types like BashExecutionMessage.</summary>
    public List<Message> Messages => Agent.State.Messages;

    public QueueMode SteeringMode => Agent.SteeringMode;

    public QueueMode FollowUpMode => Agent.FollowUpMode;

    public string? SessionFile => SessionManager.SessionFile;

    public string SessionId => SessionManager.SessionId;

    public string? SessionName => SessionManager.SessionName;

    public IReadOnlyList<ScopedModel> ScopedModels => _scopedModels;

    public void SetScopedModels(List<ScopedModel> scopedModels) => _scopedModels = scopedModels;

    public IReadOnlyList<PromptTemplate> PromptTemplates => _resourceLoader.GetPrompts().Prompts;

    public BuildSystemPromptOptions SystemPromptOptions => _baseSystemPromptOptions;

    [GeneratedRegex(@"[\r\n]+")]
    private static partial Regex LineBreaks();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    private static string? NormalizePromptSnippet(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var oneLine = Whitespace().Replace(LineBreaks().Replace(text, " "), " ").Trim();
        return oneLine.Length > 0 ? oneLine : null;
    }

    private static List<string> NormalizePromptGuidelines(IReadOnlyList<string>? guidelines)
    {
        var unique = new List<string>();
        foreach (var guideline in guidelines ?? [])
        {
            var normalized = guideline.Trim();
            if (normalized.Length > 0 && !unique.Contains(normalized)) unique.Add(normalized);
        }
        return unique;
    }

    private string RebuildSystemPrompt(IEnumerable<string> toolNames)
    {
        var validToolNames = toolNames.Where(_toolRegistry.ContainsKey).ToList();
        var toolSnippets = new Dictionary<string, string>();
        var promptGuidelines = new List<string>();
        foreach (var name in validToolNames)
        {
            if (_toolPromptSnippets.TryGetValue(name, out var snippet)) toolSnippets[name] = snippet;
            if (_toolPromptGuidelines.TryGetValue(name, out var guidelines)) promptGuidelines.AddRange(guidelines);
        }

        var append = _resourceLoader.GetAppendSystemPrompt();
        _baseSystemPromptOptions = new BuildSystemPromptOptions
        {
            Cwd = _cwd,
            Skills = _resourceLoader.GetSkills().Skills,
            ContextFiles = _resourceLoader.GetAgentsFiles(),
            CustomPrompt = _resourceLoader.GetSystemPrompt(),
            AppendSystemPrompt = append.Count > 0 ? string.Join("\n\n", append) : null,
            SelectedTools = validToolNames,
            ToolSnippets = toolSnippets,
            PromptGuidelines = promptGuidelines,
        };
        return Core.SystemPrompt.Build(_baseSystemPromptOptions);
    }

    // =========================================================================
    // Prompting
    // =========================================================================

    private async Task RunAgentPromptAsync(IReadOnlyList<Message> messages)
    {
        _isAgentRunActive = true;
        try
        {
            await Agent.PromptAsync(messages);
            while (await HandlePostAgentRunAsync())
            {
                await Agent.ContinueAsync();
            }
        }
        finally
        {
            _systemPromptOverride = null;
            FlushPendingBashMessages();
            FlushPendingCustomMessages();
            await EmitAgentSettledAsync();
        }
    }

    private async Task<bool> HandlePostAgentRunAsync()
    {
        var msg = _lastAssistantMessage;
        _lastAssistantMessage = null;
        if (msg is null) return false;

        if (IsRetryableError(msg) && await PrepareRetryAsync(msg)) return true;

        if (msg.StopReason == StopReason.Error && _retryAttempt > 0)
        {
            Emit(new AutoRetryEndEvent(false, _retryAttempt, msg.ErrorMessage));
            _retryAttempt = 0;
        }

        if (await CheckCompactionAsync(msg)) return true;

        // Messages queued by agent_end extension handlers need a continuation.
        return Agent.HasQueuedMessages();
    }

    private async Task<(string Text, List<ImageContent>? Images)?> RunInputHandlersAsync(string text, List<ImageContent>? images, string source, string? streamingBehavior)
    {
        if (!_extensionRunner.HasHandlers("input")) return (text, images);
        var result = await _extensionRunner.EmitInputAsync(text, images, source, streamingBehavior);
        return result.Action switch
        {
            "handled" => null,
            "transform" => (result.Text ?? "", result.Images ?? images),
            _ => (text, images),
        };
    }

    /// <summary>
    /// Send a prompt: runs extension commands immediately, expands skills and prompt templates, queues via
    /// steer/followUp while streaming, and validates model and auth before starting a run.
    /// </summary>
    public async Task PromptAsync(string text, PromptOptions? options = null)
    {
        options ??= new PromptOptions();
        List<Message>? messages;
        try
        {
            if (options.ExpandPromptTemplates && text.StartsWith('/') && await TryExecuteExtensionCommandAsync(text))
            {
                options.PreflightResult?.Invoke(true);
                return;
            }

            if (_compactionCts is not null)
            {
                throw new InvalidOperationException("Cannot submit a prompt while compaction is in progress. Wait for compaction to finish and retry.");
            }

            var processed = await RunInputHandlersAsync(text, options.Images, options.Source, IsStreaming ? options.StreamingBehavior : null);
            if (processed is null)
            {
                options.PreflightResult?.Invoke(true);
                return;
            }
            var (currentText, currentImages) = processed.Value;

            var expandedText = currentText;
            if (options.ExpandPromptTemplates)
            {
                expandedText = ExpandSkillCommand(expandedText);
                expandedText = Core.PromptTemplates.ExpandPromptTemplate(expandedText, [.. PromptTemplates]);
            }

            if (IsStreaming)
            {
                if (options.StreamingBehavior is null)
                {
                    throw new InvalidOperationException("Agent is already processing. Specify streamingBehavior ('steer' or 'followUp') to queue the message.");
                }
                if (options.StreamingBehavior == "followUp") QueueFollowUp(expandedText, currentImages);
                else QueueSteer(expandedText, currentImages);
                options.PreflightResult?.Invoke(true);
                return;
            }

            FlushPendingBashMessages();
            FlushPendingCustomMessages();

            var model = Model ?? throw new InvalidOperationException(AuthGuidance.FormatNoModelSelectedMessage());
            var hasConfiguredAuth = _modelRuntime.HasConfiguredAuth(model.Provider)
                || await _modelRuntime.CheckAuthAsync(model.Provider) is not null;
            if (!hasConfiguredAuth)
            {
                if (_modelRuntime.IsUsingOAuth(model.Provider))
                {
                    throw new InvalidOperationException(
                        $"Authentication failed for \"{model.Provider}\". Credentials may have expired or network is unavailable. Run '/login {model.Provider}' to re-authenticate.");
                }
                throw new InvalidOperationException(AuthGuidance.FormatNoApiKeyFoundMessage(model.Provider));
            }

            // Compact before sending when needed (catches aborted responses). The new prompt is sent below.
            if (FindLastAssistantMessage() is { } lastAssistant) await CheckCompactionAsync(lastAssistant, skipAbortedCheck: false);

            var userContent = new List<ContentBlock> { new TextContent(expandedText) };
            if (currentImages is not null) userContent.AddRange(currentImages);
            messages = [new UserMessage { Content = UserContent.FromBlocks(userContent), Timestamp = TimeUtil.NowMs() }];

            messages.AddRange(_pendingNextTurnMessages);
            _pendingNextTurnMessages = [];

            var result = await _extensionRunner.EmitBeforeAgentStartAsync(expandedText, currentImages, _baseSystemPrompt, _baseSystemPromptOptions);
            foreach (var custom in result?.Messages ?? [])
            {
                messages.Add(new CustomMessage
                {
                    CustomType = custom.CustomType,
                    Content = custom.Content ?? UserContent.FromBlocks([]),
                    Display = custom.Display,
                    Details = custom.Details,
                    Timestamp = TimeUtil.NowMs(),
                });
            }
            if (result?.SystemPrompt is not null)
            {
                _systemPromptOverride = result.SystemPrompt;
                Agent.State.SystemPrompt = result.SystemPrompt;
            }
            else
            {
                _systemPromptOverride = null;
                Agent.State.SystemPrompt = _baseSystemPrompt;
            }
        }
        catch
        {
            options.PreflightResult?.Invoke(false);
            throw;
        }

        options.PreflightResult?.Invoke(true);
        await RunAgentPromptAsync(messages);
    }

    private async Task<bool> TryExecuteExtensionCommandAsync(string text)
    {
        var spaceIndex = text.IndexOf(' ');
        var commandName = spaceIndex == -1 ? text[1..] : text[1..spaceIndex];
        var args = spaceIndex == -1 ? "" : text[(spaceIndex + 1)..];
        var command = _extensionRunner.GetCommand(commandName);
        if (command is null) return false;
        try
        {
            await command.Handler(args, _extensionRunner.CreateCommandContext());
        }
        catch (Exception ex)
        {
            _extensionRunner.EmitError(new ExtensionError($"command:{commandName}", "command", ex.Message));
        }
        return true;
    }

    [GeneratedRegex("^<skill name=\"([^\"]+)\" location=\"([^\"]+)\">\\n([\\s\\S]*?)\\n</skill>(?:\\n\\n([\\s\\S]+))?$")]
    private static partial Regex SkillBlock();

    public static ParsedSkillBlock? ParseSkillBlock(string text)
    {
        var match = SkillBlock().Match(text);
        if (!match.Success) return null;
        var userMessage = match.Groups[4].Success ? match.Groups[4].Value.Trim() : "";
        return new ParsedSkillBlock(match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value, userMessage.Length > 0 ? userMessage : null);
    }

    /// <summary>Expand "/skill:name args" into a skill block; unknown skills pass through.</summary>
    private string ExpandSkillCommand(string text)
    {
        if (!text.StartsWith("/skill:", StringComparison.Ordinal)) return text;
        var spaceIndex = text.IndexOf(' ');
        var skillName = spaceIndex == -1 ? text[7..] : text[7..spaceIndex];
        var args = spaceIndex == -1 ? "" : text[(spaceIndex + 1)..].Trim();

        var skill = _resourceLoader.GetSkills().Skills.FirstOrDefault(s => s.Name == skillName);
        if (skill is null) return text;
        try
        {
            var body = Frontmatter.Strip(File.ReadAllText(skill.FilePath)).Trim();
            var skillBlock = $"<skill name=\"{skill.Name}\" location=\"{skill.FilePath}\">\nReferences are relative to {skill.BaseDir}.\n\n{body}\n</skill>";
            return args.Length > 0 ? $"{skillBlock}\n\n{args}" : skillBlock;
        }
        catch (Exception ex)
        {
            _extensionRunner.EmitError(new ExtensionError(skill.FilePath, "skill_expansion", ex.Message));
            return text;
        }
    }

    private async Task QueueUserInputAsync(string text, List<ImageContent>? images, string behavior, string source)
    {
        if (text.StartsWith('/')) ThrowIfExtensionCommand(text);
        var processed = await RunInputHandlersAsync(text, images, source, IsStreaming ? behavior : null);
        if (processed is null) return;
        var expandedText = Core.PromptTemplates.ExpandPromptTemplate(ExpandSkillCommand(processed.Value.Text), [.. PromptTemplates]);
        if (behavior == "steer") QueueSteer(expandedText, processed.Value.Images);
        else QueueFollowUp(expandedText, processed.Value.Images);
    }

    /// <summary>Queue a steering message, delivered after the current tool calls and before the next LLM call.</summary>
    public Task SteerAsync(string text, List<ImageContent>? images = null, string source = "interactive") =>
        QueueUserInputAsync(text, images, "steer", source);

    /// <summary>Queue a follow-up message, delivered once the agent has no more tool calls or steering messages.</summary>
    public Task FollowUpAsync(string text, List<ImageContent>? images = null, string source = "interactive") =>
        QueueUserInputAsync(text, images, "followUp", source);

    private static UserMessage CreateUserMessage(string text, List<ImageContent>? images)
    {
        var content = new List<ContentBlock> { new TextContent(text) };
        if (images is not null) content.AddRange(images);
        return new UserMessage { Content = UserContent.FromBlocks(content), Timestamp = TimeUtil.NowMs() };
    }

    private void QueueSteer(string text, List<ImageContent>? images)
    {
        _steeringMessages.Add(text);
        EmitQueueUpdate();
        Agent.Steer(CreateUserMessage(text, images));
    }

    private void QueueFollowUp(string text, List<ImageContent>? images)
    {
        _followUpMessages.Add(text);
        EmitQueueUpdate();
        Agent.FollowUp(CreateUserMessage(text, images));
    }

    private void ThrowIfExtensionCommand(string text)
    {
        var spaceIndex = text.IndexOf(' ');
        var commandName = spaceIndex == -1 ? text[1..] : text[1..spaceIndex];
        if (_extensionRunner.GetCommand(commandName) is not null)
        {
            throw new InvalidOperationException($"Extension command \"/{commandName}\" cannot be queued. Use prompt() or execute the command when not streaming.");
        }
    }

    /// <summary>
    /// Send a custom message. deliverAs: "steer" | "followUp" | "nextTurn". While streaming without triggerTurn=false it
    /// is queued; with triggerTurn it starts a turn; otherwise it is appended (deferred to the end of a running turn).
    /// </summary>
    public async Task SendCustomMessageAsync(string customType, UserContent? content, bool display, JsonNode? details, bool? triggerTurn = null, string? deliverAs = null)
    {
        var message = new CustomMessage
        {
            CustomType = customType,
            Content = content ?? UserContent.FromBlocks([]),
            Display = display,
            Details = details,
            Timestamp = TimeUtil.NowMs(),
        };
        if (deliverAs == "nextTurn")
        {
            _pendingNextTurnMessages.Add(message);
        }
        else if (IsStreaming && triggerTurn != false)
        {
            if (deliverAs == "followUp") Agent.FollowUp(message);
            else Agent.Steer(message);
        }
        else if (triggerTurn == true)
        {
            await RunAgentPromptAsync([message]);
        }
        else if (IsStreaming)
        {
            // Appending now could land between a tool call and its result; defer to the end of the turn.
            _pendingCustomMessages.Add(message);
        }
        else
        {
            AppendCustomMessage(message);
        }
    }

    private void AppendCustomMessage(CustomMessage message)
    {
        Agent.State.Messages.Add(message);
        SessionManager.AppendCustomMessageEntry(message.CustomType, message.Content, message.Display, message.Details);
        Emit(new AgentCoreSessionEvent(new MessageStartEvent(message)));
        Emit(new AgentCoreSessionEvent(new MessageEndEvent(message)));
    }

    private void FlushPendingCustomMessages()
    {
        if (_pendingCustomMessages.Count == 0) return;
        var pending = _pendingCustomMessages;
        _pendingCustomMessages = [];
        foreach (var message in pending) AppendCustomMessage(message);
    }

    /// <summary>Send a user message (always triggers a turn). Used by extensions.</summary>
    public Task SendUserMessageAsync(UserContent content, string? deliverAs = null, bool expandPromptTemplates = false)
    {
        string text;
        List<ImageContent>? images = null;
        if (content.Text is { } plain)
        {
            text = plain;
        }
        else
        {
            var blocks = content.Blocks ?? [];
            text = string.Join("\n", blocks.OfType<TextContent>().Select(t => t.Text));
            images = blocks.OfType<ImageContent>().ToList();
            if (images.Count == 0) images = null;
        }
        return PromptAsync(text, new PromptOptions { ExpandPromptTemplates = expandPromptTemplates, StreamingBehavior = deliverAs, Images = images, Source = "extension" });
    }

    /// <summary>Clear queued messages and return them (e.g. to restore into the editor on abort).</summary>
    public (List<string> Steering, List<string> FollowUp) ClearQueue()
    {
        var steering = _steeringMessages;
        var followUp = _followUpMessages;
        _steeringMessages = [];
        _followUpMessages = [];
        Agent.ClearAllQueues();
        EmitQueueUpdate();
        return (steering, followUp);
    }

    public int PendingMessageCount => _steeringMessages.Count + _followUpMessages.Count;

    public IReadOnlyList<string> GetSteeringMessages() => _steeringMessages;

    public IReadOnlyList<string> GetFollowUpMessages() => _followUpMessages;

    /// <summary>Abort the current operation and wait for the session to become idle.</summary>
    public async Task AbortAsync()
    {
        AbortRetry();
        AbortCompaction();
        AbortBranchSummary();
        Agent.Abort();
        await WaitForIdleAsync();
    }

    public Task WaitForIdleAsync()
    {
        if (IsIdle) return Task.CompletedTask;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var existing = Interlocked.CompareExchange(ref _idleWait, tcs, null);
        var wait = (existing ?? tcs).Task;
        // Re-check in case the session became idle between the check and registration.
        ResolveIdleWaitIfIdle();
        return wait;
    }
}
