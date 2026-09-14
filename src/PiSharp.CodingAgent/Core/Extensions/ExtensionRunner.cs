using System.Text.Json.Nodes;
using PiSharp.Agent;
using PiSharp.Ai;
using PiSharp.CodingAgent.Core.Tools;

namespace PiSharp.CodingAgent.Core.Extensions;

/// <summary>
/// An extension event. pi's extension events are a large discriminated union; until the PiSharp extension model is
/// designed they are carried as a type name plus named payload values mirroring pi's field names.
/// </summary>
public sealed record ExtensionEvent(string Type, IReadOnlyDictionary<string, object?>? Data = null)
{
    public static ExtensionEvent Of(string type, params (string Key, object? Value)[] data) =>
        new(type, data.ToDictionary(d => d.Key, d => d.Value));
}

public sealed record ExtensionError(string ExtensionPath, string Event, string Error);

public sealed record RegisteredTool(ToolDefinition Definition, SourceInfo SourceInfo);

public sealed record RegisteredCommand(string InvocationName, string? Description, SourceInfo SourceInfo, Func<string, object?, Task> Handler);

/// <summary>Result of a "tool_call" hook: block execution with an optional reason.</summary>
public sealed record ToolCallHookResult(bool Block, string? Reason = null);

/// <summary>Result of a "tool_result" hook; null fields keep the original values.</summary>
public sealed record ToolResultHookResult(List<ContentBlock>? Content = null, JsonNode? Details = null, bool? IsError = null, Usage? Usage = null);

/// <summary>"continue" | "transform" | "handled".</summary>
public sealed record InputHookResult(string Action, string? Text = null, List<ImageContent>? Images = null);

public sealed record BeforeAgentStartCustomMessage(string CustomType, UserContent? Content, bool Display, JsonNode? Details);

public sealed record BeforeAgentStartResult(List<BeforeAgentStartCustomMessage>? Messages = null, string? SystemPrompt = null);

public sealed record DiscoveredResourcePath(string Path, string ExtensionPath);

public sealed record ResourcesDiscoverResult(List<DiscoveredResourcePath> SkillPaths, List<DiscoveredResourcePath> PromptPaths, List<DiscoveredResourcePath> ThemePaths);

/// <summary>
/// The surface AgentSession uses to talk to loaded extensions (port of the ExtensionRunner calls in agent-session.ts).
/// </summary>
public interface IExtensionRunner
{
    bool HasHandlers(string eventType);

    /// <summary>Emit an event; returns the last non-null handler result (e.g. session_before_compact results).</summary>
    Task<object?> EmitAsync(ExtensionEvent evt);

    Task<ToolCallHookResult?> EmitToolCallAsync(string toolName, string toolCallId, JsonObject input);

    Task<ToolResultHookResult?> EmitToolResultAsync(string toolName, string toolCallId, JsonObject input, List<ContentBlock> content, JsonNode? details, bool isError, Usage? usage);

    /// <summary>Returns a replacement message, or null to keep the original.</summary>
    Task<Message?> EmitMessageEndAsync(Message message);

    Task<InputHookResult> EmitInputAsync(string text, List<ImageContent>? images, string source, string? streamingBehavior);

    Task<BeforeAgentStartResult?> EmitBeforeAgentStartAsync(string prompt, List<ImageContent>? images, string systemPrompt, BuildSystemPromptOptions systemPromptOptions);

    Task<ResourcesDiscoverResult> EmitResourcesDiscoverAsync(string cwd, string reason);

    Task<List<Message>> EmitContextAsync(List<Message> messages);

    RegisteredCommand? GetCommand(string name);

    IReadOnlyList<RegisteredCommand> GetRegisteredCommands();

    IReadOnlyList<RegisteredTool> GetAllRegisteredTools();

    IReadOnlyDictionary<string, object> GetFlagValues();

    object? CreateCommandContext();

    void EmitError(ExtensionError error);

    IDisposable OnError(Action<ExtensionError> listener);

    /// <summary>Wrap tool definitions into agent tools bound to the extension context.</summary>
    List<AgentTool> WrapTools(IEnumerable<RegisteredTool> tools);

    void Invalidate(string? message = null);
}

/// <summary>Runner used while no extensions are loaded: no handlers, no commands, no extension tools.</summary>
public sealed class NullExtensionRunner(Func<ExtensionContext>? contextFactory = null) : IExtensionRunner
{
    private readonly List<Action<ExtensionError>> _errorListeners = [];

    public bool HasHandlers(string eventType) => false;

    public Task<object?> EmitAsync(ExtensionEvent evt) => Task.FromResult<object?>(null);

    public Task<ToolCallHookResult?> EmitToolCallAsync(string toolName, string toolCallId, JsonObject input) => Task.FromResult<ToolCallHookResult?>(null);

    public Task<ToolResultHookResult?> EmitToolResultAsync(string toolName, string toolCallId, JsonObject input, List<ContentBlock> content, JsonNode? details, bool isError, Usage? usage) =>
        Task.FromResult<ToolResultHookResult?>(null);

    public Task<Message?> EmitMessageEndAsync(Message message) => Task.FromResult<Message?>(null);

    public Task<InputHookResult> EmitInputAsync(string text, List<ImageContent>? images, string source, string? streamingBehavior) =>
        Task.FromResult(new InputHookResult("continue"));

    public Task<BeforeAgentStartResult?> EmitBeforeAgentStartAsync(string prompt, List<ImageContent>? images, string systemPrompt, BuildSystemPromptOptions systemPromptOptions) =>
        Task.FromResult<BeforeAgentStartResult?>(null);

    public Task<ResourcesDiscoverResult> EmitResourcesDiscoverAsync(string cwd, string reason) => Task.FromResult(new ResourcesDiscoverResult([], [], []));

    public Task<List<Message>> EmitContextAsync(List<Message> messages) => Task.FromResult(messages);

    public RegisteredCommand? GetCommand(string name) => null;

    public IReadOnlyList<RegisteredCommand> GetRegisteredCommands() => [];

    public IReadOnlyList<RegisteredTool> GetAllRegisteredTools() => [];

    public IReadOnlyDictionary<string, object> GetFlagValues() => new Dictionary<string, object>();

    public object? CreateCommandContext() => null;

    public void EmitError(ExtensionError error)
    {
        foreach (var listener in _errorListeners.ToArray()) listener(error);
    }

    public IDisposable OnError(Action<ExtensionError> listener)
    {
        _errorListeners.Add(listener);
        return new Unsubscriber(() => _errorListeners.Remove(listener));
    }

    public List<AgentTool> WrapTools(IEnumerable<RegisteredTool> tools) =>
        tools.Select(t => t.Definition.ToAgentTool(contextFactory)).ToList();

    public void Invalidate(string? message = null)
    {
    }

    private sealed class Unsubscriber(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
