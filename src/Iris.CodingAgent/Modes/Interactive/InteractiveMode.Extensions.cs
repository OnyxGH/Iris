using Iris.CodingAgent.Core.Extensions;
using Iris.CodingAgent.Modes.Interactive.Components;
using Iris.Extensions;
using Iris.Tui;
using Iris.Tui.Components;

namespace Iris.CodingAgent.Modes.Interactive;

public sealed partial class InteractiveMode
{
    /// <summary>Maximum lines of a text widget, so widgets cannot push the editor off screen.</summary>
    private const int MaxWidgetLines = 10;

    private readonly OrderedDictionary<string, IComponent> _extensionWidgetsAbove = [];
    private readonly OrderedDictionary<string, IComponent> _extensionWidgetsBelow = [];
    private readonly List<TerminalInputSubscription> _extensionTerminalInputSubscriptions = [];
    private InteractiveExtensionUI? _extensionUI;

    private sealed class TerminalInputSubscription(Func<string, InputListenerResult?> handler)
    {
        public Func<string, InputListenerResult?> Handler => handler;

        public Action? Unsubscribe { get; set; }
    }

    private ExtensionBindings CreateExtensionBindings() => new()
    {
        UI = _extensionUI ??= new InteractiveExtensionUI(this),
        Mode = ExtensionModes.Tui,
        ShutdownHandler = () => _dispatcher.Invoke(() =>
        {
            _shutdownRequested = true;
            if (Session.IsIdle) _ = ShutdownAsync();
        }),
        ReloadHandler = () => _dispatcher.InvokeAsync(HandleReloadCommandAsync),
        OnError = error => _dispatcher.Invoke(() => ShowExtensionError(error.ExtensionPath, error.Error, null)),
    };

    private void SetupExtensionShortcuts()
    {
        if (Session.ExtensionRunner is not ExtensionRunner runner) return;
        var shortcuts = runner.GetShortcuts();
        if (shortcuts.Count == 0) return;
        _defaultEditor.OnExtensionShortcut = data =>
        {
            foreach (var (key, shortcut) in shortcuts)
            {
                if (!Keys.Matches(data, key)) continue;
                _ = RunShortcutAsync(runner, shortcut);
                return true;
            }
            return false;
        };
    }

    private async Task RunShortcutAsync(ExtensionRunner runner, RegisteredShortcut shortcut)
    {
        try
        {
            await shortcut.Handler(runner.CreateContext());
        }
        catch (Exception ex)
        {
            _dispatcher.Invoke(() => ShowError($"Shortcut handler error: {ex.Message}"));
        }
    }

    private MessageRenderer? GetExtensionMessageRenderer(string customType) =>
        (Session.ExtensionRunner as ExtensionRunner)?.GetMessageRenderer(customType);

    // ----- Widgets -----

    private void SetExtensionWidget(string key, IComponent? component, WidgetPlacement placement)
    {
        RemoveExtensionWidget(_extensionWidgetsAbove, key);
        RemoveExtensionWidget(_extensionWidgetsBelow, key);
        if (component is not null) (placement == WidgetPlacement.BelowEditor ? _extensionWidgetsBelow : _extensionWidgetsAbove)[key] = component;
        RenderWidgets();
    }

    private static IComponent CreateTextWidget(IReadOnlyList<string> lines)
    {
        var container = new Container();
        foreach (var line in lines.Take(MaxWidgetLines)) container.AddChild(new Text(line, 1, 0));
        if (lines.Count > MaxWidgetLines) container.AddChild(new Text(Theme.Fg("muted", "... (widget truncated)"), 1, 0));
        return container;
    }

    private static void RemoveExtensionWidget(OrderedDictionary<string, IComponent> widgets, string key)
    {
        if (!widgets.Remove(key, out var existing)) return;
        (existing as IDisposable)?.Dispose();
    }

    private void ClearExtensionWidgets()
    {
        foreach (var widget in _extensionWidgetsAbove.Values.Concat(_extensionWidgetsBelow.Values)) (widget as IDisposable)?.Dispose();
        _extensionWidgetsAbove.Clear();
        _extensionWidgetsBelow.Clear();
        RenderWidgets();
    }

    private void RenderWidgets()
    {
        RenderWidgetContainer(_widgetContainerAbove, _extensionWidgetsAbove, spacerWhenEmpty: true, leadingSpacer: true);
        RenderWidgetContainer(_widgetContainerBelow, _extensionWidgetsBelow, spacerWhenEmpty: false, leadingSpacer: false);
        _ui.RequestRender();
    }

    private static void RenderWidgetContainer(Container container, OrderedDictionary<string, IComponent> widgets, bool spacerWhenEmpty, bool leadingSpacer)
    {
        container.Clear();
        if (widgets.Count == 0)
        {
            if (spacerWhenEmpty) container.AddChild(new Spacer(1));
            return;
        }
        if (leadingSpacer) container.AddChild(new Spacer(1));
        foreach (var component in widgets.Values) container.AddChild(component);
    }

    // ----- Terminal input -----

    private IDisposable AddExtensionTerminalInputListener(Func<string, InputListenerResult?> handler)
    {
        var subscription = new TerminalInputSubscription(handler) { Unsubscribe = _ui.AddInputListener(handler) };
        _extensionTerminalInputSubscriptions.Add(subscription);
        return new CallbackDisposable(() => _dispatcher.Invoke(() =>
        {
            subscription.Unsubscribe?.Invoke();
            _extensionTerminalInputSubscriptions.Remove(subscription);
        }));
    }

    private void ClearExtensionTerminalInputListeners()
    {
        foreach (var subscription in _extensionTerminalInputSubscriptions) subscription.Unsubscribe?.Invoke();
        _extensionTerminalInputSubscriptions.Clear();
    }

    private sealed class CallbackDisposable(Action dispose) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) dispose();
        }
    }

    // ----- Custom components -----

    private Task<T?> ShowExtensionCustomAsync<T>(Func<CustomUIContext<T>, IComponent> factory, CustomUIOptions? options)
    {
        var tcs = new TaskCompletionSource<T?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var isOverlay = options?.Overlay ?? false;
        var savedText = _editor.GetText();
        IComponent? component = null;
        IOverlayHandle? overlay = null;
        var closed = false;

        void Close(T? result) => _dispatcher.Invoke(() =>
        {
            if (closed) return;
            closed = true;
            if (isOverlay)
            {
                overlay?.Hide();
            }
            else
            {
                _editorContainer.Clear();
                _editorContainer.AddChild(_editor);
                _editor.SetText(savedText);
                _ui.SetFocus(_editor);
                _ui.RequestRender();
            }
            tcs.TrySetResult(result);
            try
            {
                (component as IDisposable)?.Dispose();
            }
            catch
            {
                // A failing dispose must not break the dialog result.
            }
        });

        try
        {
            component = factory(new CustomUIContext<T>(_ui, Theme, _keybindings, Close));
        }
        catch (Exception ex)
        {
            tcs.TrySetException(ex);
            return tcs.Task;
        }
        if (closed) return tcs.Task;

        if (isOverlay)
        {
            overlay = _ui.ShowOverlay(component, options?.OverlayOptions);
        }
        else
        {
            DisposeActiveSelector();
            _editorContainer.Clear();
            _editorContainer.AddChild(component);
            _ui.SetFocus(component);
            _ui.RequestRender();
        }
        return tcs.Task;
    }

    private void SetExtensionWorkingMessage(string? message)
    {
        _workingMessage = message;
        if (_activeStatusIndicator?.Kind == "working") _activeStatusIndicator.SetMessage(message ?? DefaultWorkingMessage);
    }

    private void ShowExtensionNotify(string message, NotifyType type)
    {
        switch (type)
        {
            case NotifyType.Error:
                ShowError(message);
                break;
            case NotifyType.Warning:
                ShowWarning(message);
                break;
            default:
                ShowStatus(message);
                break;
        }
    }

    /// <summary>
    /// The UI handed to extensions in interactive mode. Extension code may call it from any thread, so every member
    /// runs on the UI dispatcher.
    /// </summary>
    private sealed class InteractiveExtensionUI(InteractiveMode mode) : IExtensionUI
    {
        private readonly UiDispatcher _dispatcher = mode._dispatcher;

        public Theme Theme => ThemeManager.Current;

        public Task<string?> SelectAsync(string title, IReadOnlyList<string> options, CancellationToken cancellationToken = default) =>
            OnDispatcherAsync(() => mode.ShowExtensionSelectorAsync(title, [.. options], cancellationToken));

        public async Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken = default) =>
            await SelectAsync($"{title}\n{message}", ["Yes", "No"], cancellationToken) == "Yes";

        public Task<string?> InputAsync(string title, string? placeholder = null, CancellationToken cancellationToken = default) =>
            OnDispatcherAsync(() => mode.ShowExtensionInputAsync(title, cancellationToken));

        public Task<string?> EditorAsync(string title, string? prefill = null) =>
            OnDispatcherAsync(() => mode.ShowExtensionEditorAsync(title, prefill));

        public void Notify(string message, NotifyType type = NotifyType.Info) => _dispatcher.Invoke(() => mode.ShowExtensionNotify(message, type));

        public void SetStatus(string key, string? text) => _dispatcher.Invoke(() =>
        {
            mode._footerDataProvider.SetExtensionStatus(key, text);
            mode._ui.RequestRender();
        });

        public void SetWorkingMessage(string? message) => _dispatcher.Invoke(() => mode.SetExtensionWorkingMessage(message));

        public void SetWidget(string key, IReadOnlyList<string> lines, WidgetPlacement placement = WidgetPlacement.AboveEditor) =>
            _dispatcher.Invoke(() => mode.SetExtensionWidget(key, CreateTextWidget(lines), placement));

        public void SetWidget(string key, Func<TuiBase, Theme, IComponent> factory, WidgetPlacement placement = WidgetPlacement.AboveEditor) =>
            _dispatcher.Invoke(() => mode.SetExtensionWidget(key, factory(mode._ui, ThemeManager.Current), placement));

        public void ClearWidget(string key) => _dispatcher.Invoke(() => mode.SetExtensionWidget(key, null, WidgetPlacement.AboveEditor));

        public void SetTitle(string title) => _dispatcher.Invoke(() => mode._ui.Terminal.SetTitle(title));

        public Task<T?> CustomAsync<T>(Func<CustomUIContext<T>, IComponent> factory, CustomUIOptions? options = null) =>
            OnDispatcherAsync(() => mode.ShowExtensionCustomAsync(factory, options));

        public string GetEditorText() => OnDispatcher(() => mode._editor.GetExpandedText());

        public void SetEditorText(string text) => _dispatcher.Invoke(() => mode._editor.SetText(text));

        public void PasteToEditor(string text) => _dispatcher.Invoke(() => mode._editor.HandleInput($"\e[200~{text}\e[201~"));

        public IDisposable OnTerminalInput(Func<string, InputListenerResult?> handler) => OnDispatcher(() => mode.AddExtensionTerminalInputListener(handler));

        private async Task<T> OnDispatcherAsync<T>(Func<Task<T>> action)
        {
            if (_dispatcher.IsOnDispatcherThread) return await action();
            T result = default!;
            await _dispatcher.InvokeAsync(async () => result = await action());
            return result;
        }

        private T OnDispatcher<T>(Func<T> action)
        {
            if (_dispatcher.IsOnDispatcherThread) return action();
            T result = default!;
            _dispatcher.Send(_ => result = action(), null);
            return result;
        }
    }
}
