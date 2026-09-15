using Iris.Ai;
using Iris.CodingAgent.Core;
using Iris.CodingAgent.Modes.Interactive;
using Iris.Tui;

namespace Iris.Extensions;

/// <summary>Where the agent is running: "tui" (interactive), "print", "json" or "rpc".</summary>
public static class ExtensionModes
{
    public const string Tui = "tui";
    public const string Print = "print";
    public const string Json = "json";
    public const string Rpc = "rpc";
}

/// <summary>Context passed to event handlers, tools, commands and shortcuts. Values are read at call time.</summary>
public class ExtensionContext
{
    /// <summary>User interface; a no-op implementation when <see cref="HasUI"/> is false.</summary>
    public IExtensionUI UI => GetUI();

    public Func<IExtensionUI> GetUI { get; init; } = () => NoOpExtensionUI.Instance;

    /// <summary>"tui" | "print" | "json" | "rpc".</summary>
    public string Mode => GetMode();

    public Func<string> GetMode { get; init; } = () => ExtensionModes.Print;

    public bool HasUI => GetHasUI();

    public Func<bool> GetHasUI { get; init; } = () => false;

    public required string Cwd { get; init; }

    public SessionManager? SessionManager { get; init; }

    public ModelRegistry? ModelRegistry { get; init; }

    public Func<Model?> GetModel { get; init; } = () => null;

    public Model? Model => GetModel();

    public Func<IReadOnlyList<ScopedModel>> GetScopedModels { get; init; } = () => [];

    public IReadOnlyList<ScopedModel> ScopedModels => GetScopedModels();

    public Func<ThinkingLevel?> GetThinkingLevel { get; init; } = () => null;

    public ThinkingLevel? ThinkingLevel => GetThinkingLevel();

    public Func<bool> IsIdle { get; init; } = () => true;

    public Func<bool> IsProjectTrusted { get; init; } = () => false;

    public Func<CancellationToken?> GetSignal { get; init; } = () => null;

    /// <summary>Cancellation of the current agent run, if one is active.</summary>
    public CancellationToken? Signal => GetSignal();

    public Action Abort { get; init; } = () => { };

    public Func<bool> HasPendingMessages { get; init; } = () => false;

    public Action Shutdown { get; init; } = () => { };

    public Func<ContextUsage?> GetContextUsage { get; init; } = () => null;

    /// <summary>Start a compaction with optional custom instructions (runs in the background).</summary>
    public Action<string?> Compact { get; init; } = _ => { };

    public Func<string> GetSystemPrompt { get; init; } = () => "";
}

/// <summary>Context for slash command handlers: adds waiting for idle and reloading.</summary>
public sealed class CommandContext : ExtensionContext
{
    public Func<Task> WaitForIdleAsync { get; init; } = () => Task.CompletedTask;

    /// <summary>Reload settings, resources and extensions. Do not use this context afterwards.</summary>
    public Func<Task> ReloadAsync { get; init; } = () => Task.CompletedTask;
}

public enum NotifyType
{
    Info,
    Warning,
    Error,
}

public enum WidgetPlacement
{
    AboveEditor,
    BelowEditor,
}

/// <summary>Options for <see cref="IExtensionUI.CustomAsync{T}"/>.</summary>
public sealed class CustomUIOptions
{
    /// <summary>Show the component as an overlay (modal) instead of replacing the editor.</summary>
    public bool Overlay { get; init; }

    public OverlayOptions? OverlayOptions { get; init; }
}

/// <summary>Handed to a custom component factory: call <see cref="Done"/> to close it with a result.</summary>
public sealed class CustomUIContext<T>(TuiBase tui, Theme theme, KeybindingsManager keybindings, Action<T?> done)
{
    public TuiBase Tui => tui;

    public Theme Theme => theme;

    public KeybindingsManager Keybindings => keybindings;

    public void Done(T? result) => done(result);
}

/// <summary>User interface available to extensions. Dialogs return null/false when there is no UI.</summary>
public interface IExtensionUI
{
    Task<string?> SelectAsync(string title, IReadOnlyList<string> options, CancellationToken cancellationToken = default);

    Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken = default);

    Task<string?> InputAsync(string title, string? placeholder = null, CancellationToken cancellationToken = default);

    /// <summary>Multi-line editor dialog.</summary>
    Task<string?> EditorAsync(string title, string? prefill = null);

    void Notify(string message, NotifyType type = NotifyType.Info);

    /// <summary>Set or clear (null) a status entry shown in the footer.</summary>
    void SetStatus(string key, string? text);

    /// <summary>Override the working indicator message while the agent runs; null restores the default.</summary>
    void SetWorkingMessage(string? message);

    /// <summary>Set or clear (null) a text widget above or below the editor.</summary>
    void SetWidget(string key, IReadOnlyList<string>? lines, WidgetPlacement placement = WidgetPlacement.AboveEditor);

    /// <summary>Set or clear (null) a component widget above or below the editor.</summary>
    void SetWidget(string key, Func<TuiBase, Theme, IComponent>? factory, WidgetPlacement placement = WidgetPlacement.AboveEditor);

    void SetTitle(string title);

    /// <summary>Show a custom component until it calls Done; returns the result (default when there is no UI).</summary>
    Task<T?> CustomAsync<T>(Func<CustomUIContext<T>, IComponent> factory, CustomUIOptions? options = null);

    string GetEditorText();

    void SetEditorText(string text);

    void PasteToEditor(string text);

    /// <summary>Observe raw terminal input before it reaches the focused component. Dispose to stop.</summary>
    IDisposable OnTerminalInput(Func<string, InputListenerResult?> handler);

    Theme Theme { get; }
}

/// <summary>UI used without an interactive terminal (print, json and rpc modes).</summary>
public sealed class NoOpExtensionUI : IExtensionUI
{
    public static NoOpExtensionUI Instance { get; } = new();

    public Task<string?> SelectAsync(string title, IReadOnlyList<string> options, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

    public Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task<string?> InputAsync(string title, string? placeholder = null, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

    public Task<string?> EditorAsync(string title, string? prefill = null) => Task.FromResult<string?>(null);

    public void Notify(string message, NotifyType type = NotifyType.Info)
    {
    }

    public void SetStatus(string key, string? text)
    {
    }

    public void SetWorkingMessage(string? message)
    {
    }

    public void SetWidget(string key, IReadOnlyList<string>? lines, WidgetPlacement placement = WidgetPlacement.AboveEditor)
    {
    }

    public void SetWidget(string key, Func<TuiBase, Theme, IComponent>? factory, WidgetPlacement placement = WidgetPlacement.AboveEditor)
    {
    }

    public void SetTitle(string title)
    {
    }

    public Task<T?> CustomAsync<T>(Func<CustomUIContext<T>, IComponent> factory, CustomUIOptions? options = null) => Task.FromResult<T?>(default);

    public string GetEditorText() => "";

    public void SetEditorText(string text)
    {
    }

    public void PasteToEditor(string text)
    {
    }

    public IDisposable OnTerminalInput(Func<string, InputListenerResult?> handler) => new EmptyDisposable();

    public Theme Theme => ThemeManager.Current;

    private sealed class EmptyDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
