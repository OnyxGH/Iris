using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Iris.Agent;
using Iris.Ai;
using Iris.Ai.Models;
using Iris.CodingAgent.Core.Tools;
using Iris.CodingAgent.Modes.Interactive.Components;
using Iris.Extensions;


namespace Iris.CodingAgent.Core.Extensions;

public sealed record RegisteredShortcut(string Key, string? Description, string ExtensionPath, Func<ExtensionContext, Task> Handler);

public sealed record RegisteredFlag(string Name, FlagOptions Options, string ExtensionPath);

/// <summary>Everything one extension registered while loading.</summary>
public sealed class LoadedExtension(string path, SourceInfo sourceInfo)
{
    public string Path { get; } = path;

    public SourceInfo SourceInfo { get; } = sourceInfo;

    internal Dictionary<string, List<Func<ExtensionEvent, ExtensionContext, Task<object?>>>> Handlers { get; } = [];

    internal Dictionary<string, RegisteredTool> Tools { get; } = [];

    internal List<(string Name, CommandOptions Options)> Commands { get; } = [];

    internal Dictionary<string, RegisteredShortcut> Shortcuts { get; } = new(StringComparer.OrdinalIgnoreCase);

    internal Dictionary<string, RegisteredFlag> Flags { get; } = [];

    internal Dictionary<string, MessageRenderer> MessageRenderers { get; } = [];
}

/// <summary>State shared by all extensions of one load: session actions (bound later), flag values, event bus.</summary>
public sealed class ExtensionRuntime
{
    internal const string DefaultStaleMessage =
        "This extension context is stale after session replacement or reload. Do not keep extension API or context references across /new, /resume, /fork or /reload.";

    private string? _staleMessage;

    internal AgentSession? Session { get; set; }

    internal Dictionary<string, object> FlagValues { get; } = [];

    internal List<(IProvider Provider, string ExtensionPath)> PendingProviders { get; } = [];

    internal ExtensionEventBus EventBus { get; } = new();

    internal void AssertActive()
    {
        if (_staleMessage is not null) throw new InvalidOperationException(_staleMessage);
    }

    internal AgentSession RequireSession()
    {
        AssertActive();
        return Session ?? throw new InvalidOperationException("Extension actions cannot be called while the extension is loading. Use them from events, tools or commands.");
    }

    internal void Invalidate(string? message = null)
    {
        _staleMessage ??= message ?? DefaultStaleMessage;
        EventBus.Clear();
    }
}

internal sealed class ExtensionEventBus : IEventBus
{
    private readonly Dictionary<string, List<Action<object?>>> _handlers = [];

    public void Emit(string channel, object? data)
    {
        Action<object?>[] handlers;
        lock (_handlers) handlers = _handlers.TryGetValue(channel, out var list) ? [.. list] : [];
        foreach (var handler in handlers)
        {
            try
            {
                handler(data);
            }
            catch
            {
                // One failing subscriber must not break the others.
            }
        }
    }

    public IDisposable On(string channel, Action<object?> handler)
    {
        lock (_handlers)
        {
            if (!_handlers.TryGetValue(channel, out var list)) _handlers[channel] = list = [];
            list.Add(handler);
        }
        return new Subscription(() =>
        {
            lock (_handlers)
            {
                if (_handlers.TryGetValue(channel, out var list)) list.Remove(handler);
            }
        });
    }

    public void Clear()
    {
        lock (_handlers) _handlers.Clear();
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}

/// <summary>Wire names of the typed extension events.</summary>
internal static class ExtensionEventNames
{
    private static readonly Dictionary<Type, string> Names = new()
    {
        [typeof(SessionStartEvent)] = "session_start",
        [typeof(Iris.Extensions.SessionInfoChangedEvent)] = "session_info_changed",
        [typeof(SessionShutdownEvent)] = "session_shutdown",
        [typeof(SessionBeforeSwitchEvent)] = "session_before_switch",
        [typeof(SessionBeforeForkEvent)] = "session_before_fork",
        [typeof(SessionBeforeCompactEvent)] = "session_before_compact",
        [typeof(SessionCompactEvent)] = "session_compact",
        [typeof(SessionCompactFailedEvent)] = "session_compact_failed",
        [typeof(SessionBeforeTreeEvent)] = "session_before_tree",
        [typeof(SessionTreeEvent)] = "session_tree",
        [typeof(ModelSelectEvent)] = "model_select",
        [typeof(ThinkingLevelSelectEvent)] = "thinking_level_select",
        [typeof(AgentStartedEvent)] = "agent_start",
        [typeof(AgentEndedEvent)] = "agent_end",
        [typeof(AgentIdleEvent)] = "agent_settled",
        [typeof(TurnStartedEvent)] = "turn_start",
        [typeof(TurnEndedEvent)] = "turn_end",
        [typeof(MessageStartedEvent)] = "message_start",
        [typeof(MessageUpdatedEvent)] = "message_update",
        [typeof(MessageEndedEvent)] = "message_end",
        [typeof(ToolExecutionStartedEvent)] = "tool_execution_start",
        [typeof(ToolExecutionUpdatedEvent)] = "tool_execution_update",
        [typeof(ToolExecutionEndedEvent)] = "tool_execution_end",
        [typeof(ToolCallEvent)] = "tool_call",
        [typeof(ToolResultEvent)] = "tool_result",
        [typeof(InputEvent)] = "input",
        [typeof(BeforeAgentStartEvent)] = "before_agent_start",
        [typeof(ContextEvent)] = "context",
        [typeof(ResourcesDiscoverEvent)] = "resources_discover",
    };

    public static string Of(Type type) =>
        Names.TryGetValue(type, out var name) ? name : throw new ArgumentException($"{type.Name} is not an extension event that handlers can subscribe to.");

    /// <summary>Convert an internal session event into its typed extension event, or null when it has no public type.</summary>
    public static ExtensionEvent? ToTyped(RunnerEvent evt)
    {
        var data = evt.Data ?? new Dictionary<string, object?>();
        T? Get<T>(string key) => data.TryGetValue(key, out var value) && value is T typed ? typed : default;
        return evt.Type switch
        {
            "session_start" => new SessionStartEvent(Get<string>("reason") ?? "startup"),
            "session_info_changed" => new Iris.Extensions.SessionInfoChangedEvent(Get<string>("name")),
            "session_shutdown" => new SessionShutdownEvent(Get<string>("reason") ?? "quit", Get<string>("targetSessionFile")),
            "session_before_switch" => new SessionBeforeSwitchEvent(Get<string>("reason") ?? "", Get<string>("targetSessionFile")),
            "session_before_fork" => new SessionBeforeForkEvent(Get<string>("entryId") ?? "", Get<string>("position") ?? ""),
            "session_before_compact" => new SessionBeforeCompactEvent(Get<string>("customInstructions"), Get<string>("reason") ?? "manual", Get<bool>("willRetry")),
            "session_compact" => new SessionCompactEvent(Get<CompactionEntry>("compactionEntry"), Get<bool>("fromExtension"), Get<string>("reason") ?? "manual"),
            "session_compact_failed" => new SessionCompactFailedEvent(Get<string>("reason") ?? "", Get<string>("errorMessage"), Get<bool>("aborted"), Get<bool>("willRetry"), Get<bool>("fromExtension")),
            "session_before_tree" => new SessionBeforeTreeEvent(
                (Get<IReadOnlyDictionary<string, object?>>("preparation") ?? Get<Dictionary<string, object?>>("preparation"))?.GetValueOrDefault("targetId") as string ?? "",
                (Get<IReadOnlyDictionary<string, object?>>("preparation") ?? Get<Dictionary<string, object?>>("preparation"))?.GetValueOrDefault("oldLeafId") as string),
            "session_tree" => new SessionTreeEvent(Get<string>("newLeafId"), Get<string>("oldLeafId"), Get<bool?>("fromExtension") == true),
            "model_select" when Get<Model>("model") is { } model => new ModelSelectEvent(model, Get<Model>("previousModel"), Get<string>("source") ?? "set"),
            "thinking_level_select" => new ThinkingLevelSelectEvent(Get<ThinkingLevel>("level"), Get<ThinkingLevel>("previousLevel")),
            "agent_start" => new AgentStartedEvent(),
            "agent_end" => new AgentEndedEvent(Get<IReadOnlyList<Message>>("messages") ?? []),
            "agent_settled" => new AgentIdleEvent(),
            "turn_start" => new TurnStartedEvent(Get<int>("turnIndex")),
            "turn_end" when Get<Message>("message") is { } message => new TurnEndedEvent(Get<int>("turnIndex"), message, Get<IReadOnlyList<ToolResultMessage>>("toolResults") ?? []),
            "message_start" when Get<Message>("message") is { } message => new MessageStartedEvent(message),
            "message_update" when Get<Message>("message") is { } message && Get<AssistantMessageEvent>("assistantMessageEvent") is { } update => new MessageUpdatedEvent(message, update),
            "tool_execution_start" => new ToolExecutionStartedEvent(Get<string>("toolCallId") ?? "", Get<string>("toolName") ?? "", Get<JsonObject>("args") ?? []),
            "tool_execution_update" when Get<AgentToolResult>("partialResult") is { } partial => new ToolExecutionUpdatedEvent(Get<string>("toolCallId") ?? "", Get<string>("toolName") ?? "", Get<JsonObject>("args") ?? [], partial),
            "tool_execution_end" when Get<AgentToolResult>("result") is { } result => new ToolExecutionEndedEvent(Get<string>("toolCallId") ?? "", Get<string>("toolName") ?? "", result, Get<bool>("isError")),
            _ => null,
        };
    }

    /// <summary>Convert a typed hook result back into what the session reads.</summary>
    public static object? ToSessionResult(object? result) => result switch
    {
        SessionBeforeCompactResult r => new Dictionary<string, object?> { ["cancel"] = r.Cancel },
        SessionBeforeTreeResult r => new Dictionary<string, object?> { ["cancel"] = r.Cancel },
        SessionBeforeSwitchResult r => new Dictionary<string, object?> { ["cancel"] = r.Cancel },
        SessionBeforeForkResult r => new Dictionary<string, object?> { ["cancel"] = r.Cancel },
        _ => null,
    };
}

/// <summary>Tool parameter schemas and argument binding for <see cref="Tool{TParams}"/>.</summary>
internal static class ToolParameterBinding
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        RespectNullableAnnotations = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static JsonObject SchemaFor(Type type)
    {
        var exporterOptions = new JsonSchemaExporterOptions
        {
            TreatNullObliviousAsNonNullable = true,
            TransformSchemaNode = (context, node) =>
            {
                if (node is not JsonObject obj) return node;
                ICustomAttributeProvider? provider = context.PropertyInfo is { } property ? property.AttributeProvider : context.TypeInfo.Type;
                if (provider?.GetCustomAttributes(typeof(DescriptionAttribute), inherit: true).FirstOrDefault() is DescriptionAttribute description)
                {
                    obj.Insert(0, "description", description.Description);
                }
                return obj;
            },
        };
        return Options.GetJsonSchemaAsNode(type, exporterOptions) as JsonObject
            ?? throw new InvalidOperationException($"Tool parameters type {type.Name} must be an object type.");
    }

    public static T Bind<T>(JsonObject args) =>
        args.Deserialize<T>(Options) ?? throw new InvalidOperationException($"Tool arguments could not be read as {typeof(T).Name}.");

    public static ToolDefinition ToDefinition<TParams>(Tool<TParams> tool)
    {
        var schema = SchemaFor(typeof(TParams));
        if (schema["type"]?.GetValue<string>() != "object") throw new InvalidOperationException($"Tool \"{tool.Name}\" parameters must be an object type.");
        return new ToolDefinition
        {
            Name = tool.Name,
            Label = tool.Label,
            Description = tool.Description,
            PromptSnippet = tool.PromptSnippet,
            PromptGuidelines = tool.PromptGuidelines,
            Parameters = schema,
            ExecutionMode = tool.ExecutionMode,
            Renderers = tool.RenderCall is null && tool.RenderResult is null && tool.RenderShell is null
                ? null
                : new ToolRenderers { RenderCall = tool.RenderCall, RenderResult = tool.RenderResult, RenderShell = tool.RenderShell },
            Execute = async (toolCallId, args, cancellationToken, onUpdate, ctx) =>
            {
                var call = new ToolCallContext
                {
                    ToolCallId = toolCallId,
                    Arguments = args,
                    CancellationToken = cancellationToken,
                    Update = partial => onUpdate?.Invoke(partial.ToAgentToolResult()),
                };
                var result = await tool.ExecuteAsync(call, Bind<TParams>(args), ctx ?? new ExtensionContext { Cwd = Directory.GetCurrentDirectory() });
                return result.ToAgentToolResult();
            },
        };
    }
}

/// <summary>The <see cref="IExtensionApi"/> handed to one extension.</summary>
internal sealed class ExtensionApi(LoadedExtension extension, ExtensionRuntime runtime, string cwd) : IExtensionApi
{
    private bool _loading = true;

    public string ExtensionPath => extension.Path;

    public string Cwd => cwd;

    public IEventBus Events => runtime.EventBus;

    internal void FinishLoading() => _loading = false;

    private void AddHandler(Type eventType, Func<ExtensionEvent, ExtensionContext, Task<object?>> handler)
    {
        runtime.AssertActive();
        var name = ExtensionEventNames.Of(eventType);
        if (!extension.Handlers.TryGetValue(name, out var list)) extension.Handlers[name] = list = [];
        list.Add(handler);
    }

    public void OnAsync<TEvent>(Func<TEvent, ExtensionContext, Task> handler) where TEvent : ExtensionEvent =>
        AddHandler(typeof(TEvent), async (evt, ctx) =>
        {
            await handler((TEvent)evt, ctx);
            return null;
        });

    public void On<TEvent>(Action<TEvent, ExtensionContext> handler) where TEvent : ExtensionEvent =>
        AddHandler(typeof(TEvent), (evt, ctx) =>
        {
            handler((TEvent)evt, ctx);
            return Task.FromResult<object?>(null);
        });

    public void OnAsync<TEvent, TResult>(Func<TEvent, ExtensionContext, Task<TResult?>> handler) where TEvent : ExtensionEvent<TResult> where TResult : class =>
        AddHandler(typeof(TEvent), async (evt, ctx) => await handler((TEvent)evt, ctx));

    public void On<TEvent, TResult>(Func<TEvent, ExtensionContext, TResult?> handler) where TEvent : ExtensionEvent<TResult> where TResult : class =>
        AddHandler(typeof(TEvent), (evt, ctx) => Task.FromResult<object?>(handler((TEvent)evt, ctx)));

    public void RegisterTool<TParams>(Tool<TParams> tool) => RegisterTool(ToolParameterBinding.ToDefinition(tool));

    public void RegisterTool(ToolDefinition tool)
    {
        runtime.AssertActive();
        if (tool.Parameters["type"]?.GetValue<string>() != "object")
        {
            throw new InvalidOperationException($"Tool \"{tool.Name}\" registered by extension \"{extension.Path}\" must define an object parameter schema.");
        }
        extension.Tools[tool.Name] = new RegisteredTool(tool, extension.SourceInfo);
        if (!_loading) runtime.Session?.RefreshToolRegistry();
    }

    public void RegisterCommand(string name, CommandOptions options)
    {
        runtime.AssertActive();
        extension.Commands.RemoveAll(c => c.Name == name);
        extension.Commands.Add((name.TrimStart('/'), options));
    }

    public void RegisterShortcut(string key, ShortcutOptions options)
    {
        runtime.AssertActive();
        extension.Shortcuts[key] = new RegisteredShortcut(key, options.Description, extension.Path, options.HandlerAsync);
    }

    public void RegisterFlag(string name, FlagOptions options)
    {
        runtime.AssertActive();
        if (options.Default is not null && (options.Type == FlagType.Boolean ? options.Default is not bool : options.Default is not string))
        {
            throw new ArgumentException($"Invalid default for flag \"{name}\": expected {(options.Type == FlagType.Boolean ? "bool" : "string")}.");
        }
        extension.Flags[name] = new RegisteredFlag(name, options, extension.Path);
        if (options.Default is not null) runtime.FlagValues.TryAdd(name, options.Default);
    }

    public object? GetFlag(string name)
    {
        runtime.AssertActive();
        return extension.Flags.ContainsKey(name) ? runtime.FlagValues.GetValueOrDefault(name) : null;
    }

    public void RegisterMessageRenderer(string customType, MessageRenderer renderer)
    {
        runtime.AssertActive();
        extension.MessageRenderers[customType] = renderer;
    }

    public void RegisterProvider(IProvider provider)
    {
        runtime.AssertActive();
        if (runtime.Session is { } session) session.ModelRuntime.RegisterNativeProvider(provider);
        else runtime.PendingProviders.Add((provider, extension.Path));
    }

    public void UnregisterProvider(string providerId)
    {
        runtime.AssertActive();
        runtime.PendingProviders.RemoveAll(p => p.Provider.Id == providerId);
        runtime.Session?.ModelRuntime.UnregisterProvider(providerId);
    }

    public void SendMessage(string customType, string content, bool display = true, JsonNode? details = null, SendMessageOptions? options = null)
    {
        var session = runtime.RequireSession();
        _ = RunReportingAsync("send_message", () => session.SendCustomMessageAsync(customType, UserContent.FromText(content), display, details, options?.TriggerTurn, options?.DeliverAs));
    }

    public void SendUserMessage(string content, string? deliverAs = null)
    {
        var session = runtime.RequireSession();
        _ = RunReportingAsync("send_user_message", () => session.SendUserMessageAsync(UserContent.FromText(content), deliverAs));
    }

    private async Task RunReportingAsync(string action, Func<Task> run)
    {
        try
        {
            await run();
        }
        catch (Exception ex)
        {
            runtime.Session?.ExtensionRunner.EmitError(new ExtensionError(extension.Path, action, ex.Message, ex.StackTrace));
        }
    }

    public void AppendEntry(string customType, JsonNode? data = null) => runtime.RequireSession().SessionManager.AppendCustomEntry(customType, data);

    public void SetSessionName(string name) => runtime.RequireSession().SetSessionName(name);

    public string? GetSessionName() => runtime.RequireSession().SessionManager.SessionName;

    public void SetLabel(string entryId, string? label) => runtime.RequireSession().SessionManager.AppendLabelChange(entryId, label);

    public async Task<ExecResult> ExecAsync(string command, IReadOnlyList<string> args, ExecOptions? options = null)
    {
        runtime.AssertActive();
        var startInfo = new ProcessStartInfo(command)
        {
            WorkingDirectory = options?.Cwd ?? cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args) startInfo.ArgumentList.Add(arg);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {command}");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = options?.TimeoutMs is { } ms ? new CancellationTokenSource(ms) : new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, options?.CancellationToken ?? CancellationToken.None);
        var killed = false;
        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            killed = true;
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Already exited.
            }
            await process.WaitForExitAsync(CancellationToken.None);
        }
        return new ExecResult(killed ? -1 : process.ExitCode, await stdout, await stderr, killed);
    }

    public IReadOnlyList<string> GetActiveTools() => runtime.RequireSession().GetActiveToolNames();

    public IReadOnlyList<Iris.Extensions.ToolInfo> GetAllTools() => runtime.RequireSession().GetAllToolInfos();

    public void SetActiveTools(IEnumerable<string> toolNames) => runtime.RequireSession().SetActiveToolsByName(toolNames);

    public IReadOnlyList<CommandInfo> GetCommands() =>
        runtime.RequireSession().ExtensionRunner.GetRegisteredCommands().Select(c => new CommandInfo(c.InvocationName, c.Description, c.SourceInfo.Path)).ToList();

    public async Task<bool> SetModelAsync(Model model)
    {
        var session = runtime.RequireSession();
        if (await session.ModelRuntime.CheckAuthAsync(model.Provider) is null) return false;
        await session.SetModelAsync(model);
        return true;
    }

    public ThinkingLevel GetThinkingLevel() => runtime.RequireSession().ThinkingLevel;

    public void SetThinkingLevel(ThinkingLevel level) => runtime.RequireSession().SetThinkingLevel(level);
}

/// <summary>Runs loaded extensions for one session: hooks, tools, commands, flags, shortcuts and UI binding.</summary>
public sealed class ExtensionRunner : IExtensionRunner
{
    private readonly IReadOnlyList<LoadedExtension> _extensions;
    private readonly ExtensionRuntime _runtime;
    private readonly AgentSession _session;
    private readonly List<Action<ExtensionError>> _errorListeners = [];
    private IExtensionUI _ui = NoOpExtensionUI.Instance;
    private string _mode = ExtensionModes.Print;
    private bool _hasUI;
    private string? _staleMessage;

    public ExtensionRunner(IReadOnlyList<LoadedExtension> extensions, ExtensionRuntime runtime, AgentSession session)
    {
        _extensions = extensions;
        _runtime = runtime;
        _session = session;
        runtime.Session = session;
        foreach (var (provider, path) in runtime.PendingProviders)
        {
            try
            {
                session.ModelRuntime.RegisterNativeProvider(provider);
            }
            catch (Exception ex)
            {
                EmitError(new ExtensionError(path, "register_provider", ex.Message, ex.StackTrace));
            }
        }
        runtime.PendingProviders.Clear();
    }

    public IReadOnlyList<string> ExtensionPaths => _extensions.Select(e => e.Path).ToList();

    public void SetUIContext(IExtensionUI? ui, string mode)
    {
        _ui = ui ?? NoOpExtensionUI.Instance;
        _hasUI = ui is not null;
        _mode = mode;
    }

    private void AssertActive()
    {
        if (_staleMessage is not null) throw new InvalidOperationException(_staleMessage);
    }

    public ExtensionContext CreateContext() => new()
    {
        GetUI = () => _ui,
        GetMode = () => _mode,
        GetHasUI = () => _hasUI,
        Cwd = _session.SessionManager.Cwd,
        SessionManager = _session.SessionManager,
        ModelRegistry = new ModelRegistry(_session.ModelRuntime),
        GetModel = () => _session.Model,
        GetScopedModels = () => _session.ScopedModels,
        GetThinkingLevel = () => _session.ThinkingLevel,
        IsIdle = () => _session.IsIdle,
        IsProjectTrusted = () => _session.SettingsManager.IsProjectTrusted,
        GetSignal = () => _session.Agent.Signal,
        Abort = () => _ = _session.AbortAsync(),
        HasPendingMessages = () => _session.PendingMessageCount > 0,
        Shutdown = () => ShutdownRequested?.Invoke(),
        GetContextUsage = () => _session.GetContextUsage(),
        Compact = instructions => _ = RunCompactAsync(instructions),
        GetSystemPrompt = () => _session.SystemPrompt,
    };

    private async Task RunCompactAsync(string? instructions)
    {
        try
        {
            await _session.CompactAsync(instructions);
        }
        catch (Exception ex)
        {
            EmitError(new ExtensionError("<compact>", "compact", ex.Message));
        }
    }

    /// <summary>Set by the mode: how a shutdown requested by an extension is carried out.</summary>
    public Action? ShutdownRequested { get; set; }

    /// <summary>Set by the mode: reload settings, resources and extensions.</summary>
    public Func<Task>? ReloadRequested { get; set; }

    public object? CreateCommandContext()
    {
        var ctx = CreateContext();
        return new CommandContext
        {
            GetUI = ctx.GetUI,
            GetMode = ctx.GetMode,
            GetHasUI = ctx.GetHasUI,
            Cwd = ctx.Cwd,
            SessionManager = ctx.SessionManager,
            ModelRegistry = ctx.ModelRegistry,
            GetModel = ctx.GetModel,
            GetScopedModels = ctx.GetScopedModels,
            GetThinkingLevel = ctx.GetThinkingLevel,
            IsIdle = ctx.IsIdle,
            IsProjectTrusted = ctx.IsProjectTrusted,
            GetSignal = ctx.GetSignal,
            Abort = ctx.Abort,
            HasPendingMessages = ctx.HasPendingMessages,
            Shutdown = ctx.Shutdown,
            GetContextUsage = ctx.GetContextUsage,
            Compact = ctx.Compact,
            GetSystemPrompt = ctx.GetSystemPrompt,
            WaitForIdleAsync = _session.WaitForIdleAsync,
            ReloadAsync = () => ReloadRequested?.Invoke() ?? _session.ReloadAsync(),
        };
    }

    public bool HasHandlers(string eventType) => _extensions.Any(e => e.Handlers.TryGetValue(eventType, out var list) && list.Count > 0);

    private IEnumerable<(LoadedExtension Extension, Func<ExtensionEvent, ExtensionContext, Task<object?>> Handler)> HandlersFor(string eventType) =>
        _extensions.SelectMany(e => e.Handlers.TryGetValue(eventType, out var list) ? list.Select(h => (e, h)) : []).ToList();

    private void ReportHandlerError(LoadedExtension extension, string eventType, Exception ex) =>
        EmitError(new ExtensionError(extension.Path, eventType, ex.Message, ex.StackTrace));

    public async Task<object?> EmitAsync(RunnerEvent evt)
    {
        if (!HasHandlers(evt.Type) || ExtensionEventNames.ToTyped(evt) is not { } typed) return null;
        var ctx = CreateContext();
        object? sessionResult = null;
        foreach (var (extension, handler) in HandlersFor(evt.Type))
        {
            try
            {
                var result = await handler(typed, ctx);
                if (ExtensionEventNames.ToSessionResult(result) is IReadOnlyDictionary<string, object?> converted)
                {
                    sessionResult = converted;
                    if (converted.GetValueOrDefault("cancel") is true) return converted;
                }
            }
            catch (Exception ex)
            {
                ReportHandlerError(extension, evt.Type, ex);
            }
        }
        return sessionResult;
    }

    public async Task<ToolCallHookResult?> EmitToolCallAsync(string toolName, string toolCallId, JsonObject input)
    {
        ToolCallHookResult? result = null;
        var evt = new ToolCallEvent(toolName, toolCallId, input);
        var ctx = CreateContext();
        foreach (var (_, handler) in HandlersFor("tool_call"))
        {
            // Errors propagate so a broken guard cannot silently let a tool run.
            if (await handler(evt, ctx) is ToolCallResult hookResult)
            {
                result = new ToolCallHookResult(hookResult.Block, hookResult.Reason);
                if (hookResult.Block) return result;
            }
        }
        return result;
    }

    public async Task<ToolResultHookResult?> EmitToolResultAsync(string toolName, string toolCallId, JsonObject input, List<ContentBlock> content, JsonNode? details, bool isError, Usage? usage)
    {
        var ctx = CreateContext();
        var current = new ToolResultEvent(toolName, toolCallId, input, content, details, isError);
        var modified = false;
        foreach (var (extension, handler) in HandlersFor("tool_result"))
        {
            try
            {
                if (await handler(current, ctx) is not ToolResultResult result) continue;
                if (result.Content is not null) current = current with { Content = result.Content };
                if (result.Details is not null) current = current with { Details = result.Details };
                if (result.IsError is { } error) current = current with { IsError = error };
                modified = true;
            }
            catch (Exception ex)
            {
                ReportHandlerError(extension, "tool_result", ex);
            }
        }
        return modified ? new ToolResultHookResult(current.Content, current.Details, current.IsError, usage) : null;
    }

    public async Task<Message?> EmitMessageEndAsync(Message message)
    {
        var ctx = CreateContext();
        var current = message;
        var modified = false;
        foreach (var (extension, handler) in HandlersFor("message_end"))
        {
            try
            {
                if (await handler(new MessageEndedEvent(current), ctx) is not MessageEndResult { Message: { } replacement }) continue;
                if (replacement.GetType() != current.GetType())
                {
                    EmitError(new ExtensionError(extension.Path, "message_end", "message_end handlers must return a message with the same role"));
                    continue;
                }
                current = replacement;
                modified = true;
            }
            catch (Exception ex)
            {
                ReportHandlerError(extension, "message_end", ex);
            }
        }
        return modified ? current : null;
    }

    public async Task<InputHookResult> EmitInputAsync(string text, List<ImageContent>? images, string source, string? streamingBehavior)
    {
        var ctx = CreateContext();
        var currentText = text;
        var currentImages = images;
        foreach (var (extension, handler) in HandlersFor("input"))
        {
            try
            {
                if (await handler(new InputEvent(currentText, currentImages, source), ctx) is not InputResult result) continue;
                if (result.Action == "handled") return new InputHookResult("handled");
                if (result.Action == "transform")
                {
                    currentText = result.Text ?? currentText;
                    currentImages = result.Images ?? currentImages;
                }
            }
            catch (Exception ex)
            {
                ReportHandlerError(extension, "input", ex);
            }
        }
        return currentText == text && ReferenceEquals(currentImages, images)
            ? new InputHookResult("continue")
            : new InputHookResult("transform", currentText, currentImages);
    }

    public async Task<RunnerBeforeAgentStartResult?> EmitBeforeAgentStartAsync(string prompt, List<ImageContent>? images, string systemPrompt, BuildSystemPromptOptions systemPromptOptions)
    {
        var ctx = CreateContext();
        var messages = new List<BeforeAgentStartCustomMessage>();
        var currentSystemPrompt = systemPrompt;
        var changed = false;
        foreach (var (extension, handler) in HandlersFor("before_agent_start"))
        {
            try
            {
                if (await handler(new BeforeAgentStartEvent(prompt, images, currentSystemPrompt), ctx) is not BeforeAgentStartResult result) continue;
                foreach (var message in result.Messages ?? [])
                {
                    messages.Add(new BeforeAgentStartCustomMessage(message.CustomType, UserContent.FromText(message.Content), message.Display, message.Details));
                }
                if (result.SystemPrompt is { } replacement)
                {
                    currentSystemPrompt = replacement;
                    changed = true;
                }
            }
            catch (Exception ex)
            {
                ReportHandlerError(extension, "before_agent_start", ex);
            }
        }
        return messages.Count == 0 && !changed ? null : new RunnerBeforeAgentStartResult(messages.Count > 0 ? messages : null, changed ? currentSystemPrompt : null);
    }

    public async Task<RunnerResourcesDiscoverResult> EmitResourcesDiscoverAsync(string cwd, string reason)
    {
        var ctx = CreateContext();
        List<DiscoveredResourcePath> skills = [], prompts = [], themes = [];
        foreach (var (extension, handler) in HandlersFor("resources_discover"))
        {
            try
            {
                if (await handler(new ResourcesDiscoverEvent(cwd, reason), ctx) is not ResourcesDiscoverResult result) continue;
                skills.AddRange((result.SkillPaths ?? []).Select(p => new DiscoveredResourcePath(p, extension.Path)));
                prompts.AddRange((result.PromptPaths ?? []).Select(p => new DiscoveredResourcePath(p, extension.Path)));
                themes.AddRange((result.ThemePaths ?? []).Select(p => new DiscoveredResourcePath(p, extension.Path)));
            }
            catch (Exception ex)
            {
                ReportHandlerError(extension, "resources_discover", ex);
            }
        }
        return new RunnerResourcesDiscoverResult(skills, prompts, themes);
    }

    public async Task<List<Message>> EmitContextAsync(List<Message> messages)
    {
        var ctx = CreateContext();
        var current = messages;
        foreach (var (extension, handler) in HandlersFor("context"))
        {
            try
            {
                if (await handler(new ContextEvent(current), ctx) is ContextResult result) current = result.Messages;
            }
            catch (Exception ex)
            {
                ReportHandlerError(extension, "context", ex);
            }
        }
        return current;
    }

    private List<RegisteredCommand> ResolveCommands()
    {
        var all = _extensions.SelectMany(e => e.Commands.Select(c => (Extension: e, c.Name, c.Options))).ToList();
        var counts = all.GroupBy(c => c.Name).ToDictionary(g => g.Key, g => g.Count());
        var seen = new Dictionary<string, int>();
        var taken = new HashSet<string>();
        var result = new List<RegisteredCommand>();
        foreach (var (extension, name, options) in all)
        {
            var occurrence = seen[name] = seen.GetValueOrDefault(name) + 1;
            var invocationName = counts[name] > 1 ? $"{name}:{occurrence}" : name;
            var suffix = occurrence;
            while (taken.Contains(invocationName)) invocationName = $"{name}:{++suffix}";
            taken.Add(invocationName);
            result.Add(new RegisteredCommand(invocationName, options.Description, extension.SourceInfo,
                (args, ctx) => options.HandlerAsync(args, ctx as CommandContext ?? (CommandContext)CreateCommandContext()!)));
        }
        return result;
    }

    public RegisteredCommand? GetCommand(string name) => ResolveCommands().FirstOrDefault(c => c.InvocationName == name);

    public IReadOnlyList<RegisteredCommand> GetRegisteredCommands() => ResolveCommands();

    public IReadOnlyList<RegisteredTool> GetAllRegisteredTools()
    {
        var byName = new Dictionary<string, RegisteredTool>();
        foreach (var extension in _extensions)
        {
            foreach (var tool in extension.Tools.Values) byName.TryAdd(tool.Definition.Name, tool);
        }
        return byName.Values.ToList();
    }

    public IReadOnlyDictionary<string, object> GetFlagValues() => new Dictionary<string, object>(_runtime.FlagValues);

    public IReadOnlyList<RegisteredFlag> GetFlags()
    {
        var flags = new Dictionary<string, RegisteredFlag>();
        foreach (var extension in _extensions)
        {
            foreach (var (name, flag) in extension.Flags) flags.TryAdd(name, flag);
        }
        return flags.Values.ToList();
    }

    /// <summary>Extension shortcuts by normalized key; later extensions win conflicts.</summary>
    public IReadOnlyDictionary<string, RegisteredShortcut> GetShortcuts()
    {
        var shortcuts = new Dictionary<string, RegisteredShortcut>(StringComparer.OrdinalIgnoreCase);
        foreach (var extension in _extensions)
        {
            foreach (var (key, shortcut) in extension.Shortcuts) shortcuts[key.ToLowerInvariant()] = shortcut;
        }
        return shortcuts;
    }

    public MessageRenderer? GetMessageRenderer(string customType)
    {
        foreach (var extension in _extensions)
        {
            if (extension.MessageRenderers.TryGetValue(customType, out var renderer)) return renderer;
        }
        return null;
    }

    public void EmitError(ExtensionError error)
    {
        Action<ExtensionError>[] listeners;
        lock (_errorListeners) listeners = [.. _errorListeners];
        foreach (var listener in listeners) listener(error);
    }

    public IDisposable OnError(Action<ExtensionError> listener)
    {
        lock (_errorListeners) _errorListeners.Add(listener);
        return new Unsubscriber(() =>
        {
            lock (_errorListeners) _errorListeners.Remove(listener);
        });
    }

    public List<AgentTool> WrapTools(IEnumerable<RegisteredTool> tools) => tools.Select(t => t.Definition.ToAgentTool(CreateContext)).ToList();

    public void Invalidate(string? message = null)
    {
        _staleMessage ??= message ?? ExtensionRuntime.DefaultStaleMessage;
        _runtime.Invalidate(_staleMessage);
    }

    private sealed class Unsubscriber(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
