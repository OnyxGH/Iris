using System.Text.Json.Nodes;
using Iris.Agent;
using Iris.Ai;
using Iris.Ai.Models;
using Iris.CodingAgent.Core;
using Iris.CodingAgent.Core.Tools;
using Iris.CodingAgent.Modes.Interactive;
using Iris.CodingAgent.Modes.Interactive.Components;
using Iris.Tui;

namespace Iris.Extensions;

/// <summary>
/// An Iris extension. Public, non-abstract classes implementing this interface with a parameterless constructor are
/// discovered in extension assemblies; <see cref="Register"/> runs once per load.
/// </summary>
public interface IExtension
{
    void Register(IExtensionApi iris);
}

public sealed class CommandOptions
{
    public string? Description { get; init; }

    /// <summary>Receives the text after the command name.</summary>
    public required Func<string, CommandContext, Task> HandlerAsync { get; init; }
}

public sealed class ShortcutOptions
{
    public string? Description { get; init; }

    public required Func<ExtensionContext, Task> HandlerAsync { get; init; }
}

public enum FlagType
{
    Boolean,
    String,
}

public sealed class FlagOptions
{
    public string? Description { get; init; }

    public FlagType Type { get; init; } = FlagType.Boolean;

    /// <summary>A bool for boolean flags or a string for string flags.</summary>
    public object? Default { get; init; }
}

/// <summary>DeliverAs: "steer" | "followUp" | "nextTurn".</summary>
public sealed class SendMessageOptions
{
    public bool? TriggerTurn { get; init; }

    public string? DeliverAs { get; init; }
}

public sealed class ExecOptions
{
    public string? Cwd { get; init; }

    public int? TimeoutMs { get; init; }

    public CancellationToken CancellationToken { get; init; }
}

public sealed record ExecResult(int ExitCode, string Stdout, string Stderr, bool Killed);

public sealed record ToolInfo(string Name, string Description, SourceInfo SourceInfo);

public sealed record CommandInfo(string Name, string? Description, string Source);

/// <summary>Publish/subscribe channel shared by all loaded extensions.</summary>
public interface IEventBus
{
    void Emit(string channel, object? data);

    IDisposable On(string channel, Action<object?> handler);
}

/// <summary>Registration and actions available to an extension. Mirrors the agent's hook model.</summary>
public interface IExtensionApi
{
    /// <summary>Path (or "&lt;builtin:id&gt;") the extension was loaded from.</summary>
    string ExtensionPath { get; }

    string Cwd { get; }

    // ----- Events (handlers run in load order; On for synchronous handlers, OnAsync for asynchronous ones) -----

    void OnAsync<TEvent>(Func<TEvent, ExtensionContext, Task> handler) where TEvent : ExtensionEvent;

    void On<TEvent>(Action<TEvent, ExtensionContext> handler) where TEvent : ExtensionEvent;

    void OnAsync<TEvent, TResult>(Func<TEvent, ExtensionContext, Task<TResult?>> handler) where TEvent : ExtensionEvent<TResult> where TResult : class;

    void On<TEvent, TResult>(Func<TEvent, ExtensionContext, TResult?> handler) where TEvent : ExtensionEvent<TResult> where TResult : class;

    // ----- Registration -----

    void RegisterTool<TParams>(Tool<TParams> tool);

    /// <summary>Register a tool with a raw JSON parameter schema.</summary>
    void RegisterTool(ToolDefinition tool);

    void RegisterCommand(string name, CommandOptions options);

    /// <summary>Register a keyboard shortcut (e.g. "ctrl+shift+w") active while the editor has focus.</summary>
    void RegisterShortcut(string key, ShortcutOptions options);

    /// <summary>Register a command line flag (<c>--name</c>).</summary>
    void RegisterFlag(string name, FlagOptions options);

    /// <summary>bool, string, or null when the flag is unknown or unset without a default.</summary>
    object? GetFlag(string name);

    void RegisterMessageRenderer(string customType, MessageRenderer renderer);

    /// <summary>Register a model provider (see <c>Iris.Ai.Models.IProvider</c>).</summary>
    void RegisterProvider(IProvider provider);

    void UnregisterProvider(string providerId);

    // ----- Actions (available once the session is bound, i.e. from events, tools and commands) -----

    /// <summary>Add a custom message to the conversation (visible to the model).</summary>
    void SendMessage(string customType, string content, bool display = true, JsonNode? details = null, SendMessageOptions? options = null);

    /// <summary>Send a user message; always triggers a turn. DeliverAs "steer" or "followUp" while streaming.</summary>
    void SendUserMessage(string content, string? deliverAs = null);

    /// <summary>Store extension data in the session file (not sent to the model).</summary>
    void AppendEntry(string customType, JsonNode? data = null);

    void SetSessionName(string name);

    string? GetSessionName();

    void SetLabel(string entryId, string? label);

    Task<ExecResult> ExecAsync(string command, IReadOnlyList<string> args, ExecOptions? options = null);

    IReadOnlyList<string> GetActiveTools();

    IReadOnlyList<ToolInfo> GetAllTools();

    void SetActiveTools(IEnumerable<string> toolNames);

    IReadOnlyList<CommandInfo> GetCommands();

    /// <summary>Switch to a model; false when it has no configured authentication.</summary>
    Task<bool> SetModelAsync(Model model);

    ThinkingLevel GetThinkingLevel();

    void SetThinkingLevel(ThinkingLevel level);

    IEventBus Events { get; }
}

/// <summary>Content and details returned by a tool.</summary>
public sealed class ToolResult
{
    public List<ContentBlock> Content { get; init; } = [];

    public JsonNode? Details { get; init; }

    public static ToolResult Text(string text, JsonNode? details = null) => new() { Content = [new TextContent(text)], Details = details };

    internal AgentToolResult ToAgentToolResult() => new() { Content = Content, Details = Details };
}

/// <summary>Per-call information for <see cref="Tool{TParams}.ExecuteAsync"/>.</summary>
public sealed class ToolCallContext
{
    public required string ToolCallId { get; init; }

    /// <summary>The validated raw arguments.</summary>
    public required JsonObject Arguments { get; init; }

    public CancellationToken CancellationToken { get; init; }

    /// <summary>Report partial output while the tool runs (shown live in the UI).</summary>
    public Action<ToolResult> Update { get; init; } = _ => { };
}

/// <summary>
/// A tool whose parameters are a C# type. The JSON schema is generated from <typeparamref name="TParams"/>
/// (camelCase property names, <c>[Description]</c> attributes become descriptions) and arguments are deserialized into it.
/// </summary>
public sealed class Tool<TParams>
{
    public required string Name { get; init; }

    public required string Label { get; init; }

    public required string Description { get; init; }

    /// <summary>One-line entry for the "Available tools" section of the default system prompt.</summary>
    public string? PromptSnippet { get; init; }

    /// <summary>Guidelines appended to the default system prompt while the tool is active.</summary>
    public IReadOnlyList<string>? PromptGuidelines { get; init; }

    public ToolExecutionMode? ExecutionMode { get; init; }

    public required Func<ToolCallContext, TParams, ExtensionContext, Task<ToolResult>> ExecuteAsync { get; init; }

    /// <summary>Custom rendering of the call in the interactive transcript.</summary>
    public ToolRenderCall? RenderCall { get; init; }

    /// <summary>Custom rendering of the result in the interactive transcript.</summary>
    public ToolRenderResult? RenderResult { get; init; }

    /// <summary>"default" draws the standard tool box; "self" leaves all framing to the renderers.</summary>
    public string? RenderShell { get; init; }
}
