using Iris.CodingAgent.Core;
using Iris.Tui;
using Iris.Tui.Components;

namespace Iris.CodingAgent.Modes.Interactive.Components;

/// <summary>Horizontal border spanning the viewport.</summary>
public sealed class DynamicBorder(Func<string, string>? color = null) : IComponent
{
    private readonly Func<string, string> _color = color ?? (s => ThemeManager.Current.Fg("border", s));

    public void Invalidate()
    {
    }

    public List<string> Render(int width) => [_color(new string('─', Math.Max(1, width)))];
}

/// <summary>Keybinding hint formatting.</summary>
public static class KeyHints
{
    private static string FormatKeyPart(string part, bool capitalize)
    {
        var display = OperatingSystem.IsMacOS() && part.Equals("alt", StringComparison.OrdinalIgnoreCase) ? "option" : part;
        return capitalize && display.Length > 0 ? char.ToUpperInvariant(display[0]) + display[1..] : display;
    }

    public static string FormatKeyText(string key, bool capitalize = false) =>
        string.Join("/", key.Split('/').Select(k => string.Join("+", k.Split('+').Select(p => FormatKeyPart(p, capitalize)))));

    private static string FormatKeys(IReadOnlyList<string> keys, bool capitalize = false) => keys.Count == 0 ? "" : FormatKeyText(string.Join("/", keys), capitalize);

    public static string KeyText(string keybinding) => FormatKeys(KeybindingsManager.Global.GetKeys(keybinding));

    public static string KeyDisplayText(string keybinding) => FormatKeys(KeybindingsManager.Global.GetKeys(keybinding), true);

    public static string KeyHint(string keybinding, string description) =>
        ThemeManager.Current.Fg("dim", KeyText(keybinding)) + ThemeManager.Current.Fg("muted", $" {description}");

    public static string RawKeyHint(string key, string description) =>
        ThemeManager.Current.Fg("dim", FormatKeyText(key)) + ThemeManager.Current.Fg("muted", $" {description}");
}

/// <summary>Per-second countdown.</summary>
public sealed class CountdownTimer : IDisposable
{
    private IDisposable? _interval;
    private int _remainingSeconds;

    public CountdownTimer(int timeoutMs, TuiBase? tui, Action<int> onTick, Action onExpire)
    {
        _remainingSeconds = (int)Math.Ceiling(timeoutMs / 1000.0);
        onTick(_remainingSeconds);
        var dispatcher = tui?.Dispatcher ?? UiDispatcher.Current;
        _interval = dispatcher?.SetInterval(() =>
        {
            _remainingSeconds--;
            onTick(_remainingSeconds);
            tui?.RequestRender();
            if (_remainingSeconds <= 0)
            {
                Dispose();
                onExpire();
            }
        }, 1000);
    }

    public void Dispose()
    {
        _interval?.Dispose();
        _interval = null;
    }
}

/// <summary>Spinner status shown above the editor or in its border.</summary>
public class StatusIndicator(string kind, TuiBase ui, Func<string, string> spinnerColor, Func<string, string> messageColor, string message, LoaderIndicatorOptions? indicator = null)
    : Loader(ui, spinnerColor, messageColor, message, indicator), IDisposable
{
    public string Kind { get; } = kind;

    public string RenderInBorder(int width)
    {
        var lines = base.Render(width + 2);
        var line = lines.Count > 1 ? lines[1] : "";
        return TextUtils.TruncateToWidth(line.StartsWith(' ') ? line[1..].TrimEnd() : line.TrimEnd(), width, "");
    }

    public string RenderSpinnerInBorder(int width) => TextUtils.TruncateToWidth(GetRenderedIndicator(), width, "");

    public virtual void Dispose() => Stop();
}

public sealed class WorkingStatusIndicator(TuiBase ui, string message, LoaderIndicatorOptions? indicator = null, Func<string, string>? colorFn = null)
    : StatusIndicator("working", ui, colorFn ?? (t => ThemeManager.Current.Fg("accent", t)), colorFn ?? (t => ThemeManager.Current.Fg("muted", t)), message, indicator);

public sealed class RetryStatusIndicator : StatusIndicator
{
    private CountdownTimer? _countdown;

    public RetryStatusIndicator(TuiBase ui, int attempt, int maxAttempts, int delayMs)
        : base("retry", ui, s => ThemeManager.Current.Fg("warning", s), t => ThemeManager.Current.Fg("muted", t), RetryMessage(attempt, maxAttempts, (int)Math.Ceiling(delayMs / 1000.0)))
    {
        _countdown = new CountdownTimer(delayMs, ui, seconds => SetMessage(RetryMessage(attempt, maxAttempts, seconds)), () => _countdown = null);
    }

    private static string RetryMessage(int attempt, int maxAttempts, int seconds) =>
        $"Retrying ({attempt}/{maxAttempts}) in {seconds}s... ({KeyHints.KeyText("app.interrupt")} to cancel)";

    public override void Dispose()
    {
        _countdown?.Dispose();
        _countdown = null;
        base.Dispose();
    }
}

public sealed class CompactionStatusIndicator(TuiBase ui, string reason)
    : StatusIndicator("compaction", ui, s => ThemeManager.Current.Fg("accent", s), t => ThemeManager.Current.Fg("muted", t), Label(reason))
{
    private static string Label(string reason)
    {
        var cancelHint = $"({KeyHints.KeyText("app.interrupt")} to cancel)";
        return reason == "manual"
            ? $"Compacting context... {cancelHint}"
            : $"{(reason == "overflow" ? "Context overflow detected, " : "")}Auto-compacting... {cancelHint}";
    }
}

public sealed class BranchSummaryStatusIndicator(TuiBase ui)
    : StatusIndicator("branchSummary", ui, s => ThemeManager.Current.Fg("accent", s), t => ThemeManager.Current.Fg("muted", t), $"Summarizing branch... ({KeyHints.KeyText("app.interrupt")} to cancel)");

public sealed class IdleStatus : IComponent
{
    public void Invalidate()
    {
    }

    public List<string> Render(int width)
    {
        var empty = new string(' ', Math.Max(0, width));
        return [empty, empty];
    }
}

/// <summary>Editor with coding-agent app keybindings and an embedded working status.</summary>
public sealed class CustomEditor : Editor
{
    private readonly KeybindingsManager _keybindings;
    private StatusIndicator? _workingStatusIndicator;
    private readonly List<(string Action, Action Handler)> _actionHandlers = [];

    public bool EmbedWorkingStatus { get; }
    public Action? OnEscape { get; set; }
    public Action? OnCtrlD { get; set; }
    public Action? OnPasteImage { get; set; }
    public Func<string, bool>? OnExtensionShortcut { get; set; }

    public CustomEditor(TuiBase tui, EditorTheme theme, KeybindingsManager keybindings, EditorOptions? options = null, bool embedWorkingStatus = false)
        : base(tui, theme, options)
    {
        _keybindings = keybindings;
        EmbedWorkingStatus = embedWorkingStatus;
    }

    public void SetWorkingStatusIndicator(StatusIndicator? indicator) => _workingStatusIndicator = indicator;

    private string Border(string s) => (BorderColor ?? (x => x))(s);

    protected override string RenderTopBorder(int width, int hiddenLineCount)
    {
        if (!EmbedWorkingStatus || _workingStatusIndicator is null || width <= 0) return base.RenderTopBorder(width, hiddenLineCount);

        var status = _workingStatusIndicator.RenderInBorder(Math.Max(1, width - 5));
        var statusWidth = TextUtils.VisibleWidth(status);
        if (statusWidth == 0) return base.RenderTopBorder(width, hiddenLineCount);

        var overflowLabel = hiddenLineCount > 0 ? $" ↑ {hiddenLineCount} more " : null;
        var overflowLabelWidth = overflowLabel is null ? 0 : TextUtils.VisibleWidth(overflowLabel);
        var overflowStart = (int)Math.Floor((width - overflowLabelWidth) / 2.0);
        bool CanFitOverflow() => overflowLabel is not null && overflowLabelWidth + 2 <= width && overflowStart - (3 + statusWidth + 1) >= 1;

        if (overflowLabel is not null && !CanFitOverflow())
        {
            status = _workingStatusIndicator.RenderSpinnerInBorder(width);
            statusWidth = TextUtils.VisibleWidth(status);
        }

        if (CanFitOverflow())
        {
            var leftBlockWidth = 3 + statusWidth + 1;
            return Border("── ") + status + Border($" {new string('─', overflowStart - leftBlockWidth)}{overflowLabel}{new string('─', Math.Max(0, width - overflowStart - overflowLabelWidth))}");
        }

        if (width >= statusWidth + 5) return Border("── ") + status + Border($" {new string('─', width - statusWidth - 4)}");

        status = _workingStatusIndicator.RenderSpinnerInBorder(width);
        statusWidth = TextUtils.VisibleWidth(status);
        var prefixWidth = Math.Min(3, Math.Max(0, width - statusWidth));
        return Border(new string('─', prefixWidth)) + status + Border(new string('─', Math.Max(0, width - prefixWidth - statusWidth)));
    }

    public void OnAction(string action, Action handler)
    {
        var index = _actionHandlers.FindIndex(h => h.Action == action);
        if (index >= 0) _actionHandlers[index] = (action, handler);
        else _actionHandlers.Add((action, handler));
    }

    private Action? GetActionHandler(string action) => _actionHandlers.FirstOrDefault(h => h.Action == action).Handler;

    public override void HandleInput(string data)
    {
        if (OnExtensionShortcut?.Invoke(data) == true) return;

        if (_keybindings.Matches(data, "app.clipboard.pasteImage"))
        {
            OnPasteImage?.Invoke();
            return;
        }

        if (_keybindings.Matches(data, "app.interrupt"))
        {
            if (!IsShowingAutocomplete())
            {
                var handler = OnEscape ?? GetActionHandler("app.interrupt");
                if (handler is not null)
                {
                    handler();
                    return;
                }
            }
            base.HandleInput(data);
            return;
        }

        if (_keybindings.Matches(data, "app.exit"))
        {
            if (GetText().Length == 0)
            {
                (OnCtrlD ?? GetActionHandler("app.exit"))?.Invoke();
                return;
            }
        }

        if (_keybindings.Matches(data, "tui.editor.historyPrevious") || _keybindings.Matches(data, "tui.editor.historyNext"))
        {
            base.HandleInput(data);
            return;
        }

        foreach (var (action, handler) in _actionHandlers.ToList())
        {
            if (action != "app.interrupt" && action != "app.exit" && _keybindings.Matches(data, action))
            {
                handler();
                return;
            }
        }

        base.HandleInput(data);
    }
}

/// <summary>Truncate text to its last visual lines.</summary>
public static class VisualTruncate
{
    public static (List<string> VisualLines, int SkippedCount) TruncateToVisualLines(string text, int maxVisualLines, int width, int paddingX = 0)
    {
        if (string.IsNullOrEmpty(text)) return ([], 0);
        var all = new Text(text, paddingX, 0).Render(width);
        if (all.Count <= maxVisualLines) return (all, 0);
        return (all.Skip(all.Count - maxVisualLines).ToList(), all.Count - maxVisualLines);
    }
}

/// <summary>Context for markdown transformers (extensions).</summary>
public sealed record MarkdownTransformContext(string MessageType, bool IsStreaming, int AvailableWidth);

public delegate string? MarkdownTransformer(string markdown, MarkdownTransformContext context);

public static class MarkdownTransform
{
    public static Func<string, int, string> Create(string messageType, bool isStreaming, IReadOnlyList<MarkdownTransformer> transformers) =>
        (markdown, availableWidth) =>
        {
            var current = markdown;
            foreach (var transformer in transformers)
            {
                try
                {
                    if (transformer(current, new MarkdownTransformContext(messageType, isStreaming, availableWidth)) is { } transformed) current = transformed;
                }
                catch
                {
                    // Keep the current markdown.
                }
            }
            return current;
        };
}

/// <summary>Renders a user message.</summary>
public sealed class UserMessageComponent : Container
{
    internal const string Osc133ZoneStart = "\e]133;A\a";
    internal const string Osc133ZoneEnd = "\e]133;B\a";
    internal const string Osc133ZoneFinal = "\e]133;C\a";

    private readonly string _text;
    private readonly MarkdownTheme _markdownTheme;
    private int _outputPad;
    private readonly IReadOnlyList<MarkdownTransformer> _transformers;

    public UserMessageComponent(string text, MarkdownTheme? markdownTheme = null, int outputPad = 1, IReadOnlyList<MarkdownTransformer>? transformers = null)
    {
        _text = text;
        _markdownTheme = markdownTheme ?? ThemeManager.GetMarkdownTheme();
        _outputPad = outputPad;
        _transformers = transformers ?? [];
        Rebuild();
    }

    public void SetOutputPad(int padding)
    {
        _outputPad = padding;
        Rebuild();
    }

    private void Rebuild()
    {
        Clear();
        var box = new Box(_outputPad, 1, c => ThemeManager.Current.Bg("userMessageBg", c));
        box.AddChild(new MarkdownComponent(_text, 0, 0, _markdownTheme, new DefaultTextStyle { Color = c => ThemeManager.Current.Fg("userMessageText", c) },
            new MarkdownOptions { PreserveOrderedListMarkers = true, PreserveBackslashEscapes = true, Transform = MarkdownTransform.Create("user", false, _transformers) }));
        AddChild(box);
    }

    public override List<string> Render(int width)
    {
        var lines = base.Render(width);
        if (lines.Count == 0) return lines;
        lines[0] = Osc133ZoneStart + lines[0];
        lines[^1] = Osc133ZoneEnd + Osc133ZoneFinal + lines[^1];
        return lines;
    }
}

/// <summary>Renders an assistant message (text and thinking blocks).</summary>
public sealed class AssistantMessageComponent : Container
{
    private readonly Container _contentContainer = new();
    private bool _hideThinkingBlock;
    private readonly MarkdownTheme _markdownTheme;
    private string _hiddenThinkingLabel;
    private int _outputPad;
    private readonly IReadOnlyList<MarkdownTransformer> _transformers;
    private Iris.Ai.AssistantMessage? _lastMessage;
    private bool _hasToolCalls;
    private bool _isStreaming;

    public AssistantMessageComponent(Iris.Ai.AssistantMessage? message = null, bool hideThinkingBlock = false, MarkdownTheme? markdownTheme = null, string hiddenThinkingLabel = "Thinking...", int outputPad = 1, IReadOnlyList<MarkdownTransformer>? transformers = null)
    {
        _hideThinkingBlock = hideThinkingBlock;
        _markdownTheme = markdownTheme ?? ThemeManager.GetMarkdownTheme();
        _hiddenThinkingLabel = hiddenThinkingLabel;
        _outputPad = outputPad;
        _transformers = transformers ?? [];
        AddChild(_contentContainer);
        if (message is not null) UpdateContent(message);
    }

    public override void Invalidate()
    {
        base.Invalidate();
        if (_lastMessage is not null) UpdateContent(_lastMessage);
    }

    public void SetHideThinkingBlock(bool hide)
    {
        _hideThinkingBlock = hide;
        if (_lastMessage is not null) UpdateContent(_lastMessage);
    }

    public void SetHiddenThinkingLabel(string label)
    {
        _hiddenThinkingLabel = label;
        if (_lastMessage is not null) UpdateContent(_lastMessage);
    }

    public void SetOutputPad(int padding)
    {
        _outputPad = padding;
        if (_lastMessage is not null) UpdateContent(_lastMessage);
    }

    public override List<string> Render(int width)
    {
        var lines = base.Render(width);
        if (_hasToolCalls || lines.Count == 0) return lines;
        lines[0] = UserMessageComponent.Osc133ZoneStart + lines[0];
        lines[^1] = UserMessageComponent.Osc133ZoneEnd + UserMessageComponent.Osc133ZoneFinal + lines[^1];
        return lines;
    }

    private static bool IsVisible(Iris.Ai.ContentBlock c) =>
        (c is Iris.Ai.TextContent t && t.Text.Trim().Length > 0) || (c is Iris.Ai.ThinkingContent th && th.Thinking.Trim().Length > 0);

    public void UpdateContent(Iris.Ai.AssistantMessage message, bool? isStreaming = null)
    {
        _lastMessage = message;
        _isStreaming = isStreaming ?? _isStreaming;
        _contentContainer.Clear();
        var theme = ThemeManager.Current;
        var content = message.Content;

        if (content.Any(IsVisible)) _contentContainer.AddChild(new Spacer(1));

        for (var i = 0; i < content.Count; i++)
        {
            if (content[i] is Iris.Ai.TextContent text && text.Text.Trim().Length > 0)
            {
                _contentContainer.AddChild(new MarkdownComponent(text.Text.Trim(), _outputPad, 0, _markdownTheme, null,
                    new MarkdownOptions { Transform = MarkdownTransform.Create("assistant", _isStreaming, _transformers) }));
            }
            else if (content[i] is Iris.Ai.ThinkingContent)
            {
                var blocks = new List<string>();
                for (; i < content.Count; i++)
                {
                    if (content[i] is not Iris.Ai.ThinkingContent thinkingContent) break;
                    var thinking = thinkingContent.Thinking.Trim();
                    if (thinking.Length > 0) blocks.Add(thinking);
                }
                i--;
                if (blocks.Count == 0) continue;

                var hasVisibleAfter = content.Skip(i + 1).Any(IsVisible);
                IComponent component = _hideThinkingBlock
                    ? new Text(theme.Italic(theme.Fg("thinkingText", _hiddenThinkingLabel)), _outputPad, 0)
                    : new MarkdownComponent(string.Join("\n\n", blocks), _outputPad, 0, _markdownTheme,
                        new DefaultTextStyle { Color = t => ThemeManager.Current.Fg("thinkingText", t), Italic = true },
                        new MarkdownOptions { Transform = MarkdownTransform.Create("assistant-thinking", _isStreaming, _transformers) });
                _contentContainer.AddChild(component);
                if (hasVisibleAfter) _contentContainer.AddChild(new Spacer(1));
            }
        }

        _hasToolCalls = content.Any(c => c is Iris.Ai.ToolCall);
        if (message.StopReason == Iris.Ai.StopReason.Length)
        {
            _contentContainer.AddChild(new Spacer(1));
            _contentContainer.AddChild(new Text(theme.Fg("error", "Response was truncated before completion."), _outputPad, 0));
        }
        else if (!_hasToolCalls)
        {
            if (message.StopReason == Iris.Ai.StopReason.Aborted)
            {
                var abortMessage = !string.IsNullOrEmpty(message.ErrorMessage) && message.ErrorMessage != "Request was aborted" ? message.ErrorMessage : "Operation aborted";
                _contentContainer.AddChild(new Spacer(1));
                _contentContainer.AddChild(new Text(theme.Fg("error", abortMessage), _outputPad, 0));
            }
            else if (message.StopReason == Iris.Ai.StopReason.Error)
            {
                var errorMessage = string.IsNullOrEmpty(message.ErrorMessage) ? "Unknown error" : message.ErrorMessage;
                _contentContainer.AddChild(new Spacer(1));
                _contentContainer.AddChild(new Text(theme.Fg("error", $"Error: {errorMessage}"), _outputPad, 0));
            }
        }
    }
}
