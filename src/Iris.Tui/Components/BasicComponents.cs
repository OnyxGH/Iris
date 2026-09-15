namespace Iris.Tui.Components;

/// <summary>Multi-line text with word wrapping, padding and optional background. Port of pi-tui Text.</summary>
public class Text : IComponent
{
    private string _text;
    private readonly int _paddingX;
    private readonly int _paddingY;
    private Func<string, string>? _bg;
    private string? _cachedText;
    private int? _cachedWidth;
    private List<string>? _cachedLines;

    public Text(string text = "", int paddingX = 1, int paddingY = 1, Func<string, string>? customBg = null)
    {
        _text = text;
        _paddingX = paddingX;
        _paddingY = paddingY;
        _bg = customBg;
    }

    public void SetText(string text)
    {
        _text = text;
        ClearCache();
    }

    public void SetCustomBg(Func<string, string>? bg)
    {
        _bg = bg;
        ClearCache();
    }

    private void ClearCache()
    {
        _cachedText = null;
        _cachedWidth = null;
        _cachedLines = null;
    }

    public virtual void Invalidate() => ClearCache();

    public virtual List<string> Render(int width)
    {
        if (_cachedLines is not null && _cachedText == _text && _cachedWidth == width) return _cachedLines;

        if (string.IsNullOrEmpty(_text) || _text.Trim().Length == 0)
        {
            _cachedText = _text;
            _cachedWidth = width;
            _cachedLines = [];
            return _cachedLines;
        }

        var normalized = _text.Replace("\t", "   ");
        var paddingX = Math.Min(_paddingX, Math.Max(0, (width - 1) / 2));
        var contentWidth = Math.Max(1, width - paddingX * 2);
        var margin = new string(' ', paddingX);

        var contentLines = new List<string>();
        foreach (var line in TextUtils.WrapTextWithAnsi(normalized, contentWidth))
        {
            var withMargins = margin + line + margin;
            contentLines.Add(_bg is not null
                ? TextUtils.ApplyBackgroundToLine(withMargins, width, _bg)
                : withMargins + new string(' ', Math.Max(0, width - TextUtils.VisibleWidth(withMargins))));
        }

        var emptyLine = new string(' ', Math.Max(0, width));
        var emptyLines = Enumerable.Range(0, _paddingY).Select(_ => _bg is not null ? TextUtils.ApplyBackgroundToLine(emptyLine, width, _bg) : emptyLine).ToList();
        var result = new List<string>(emptyLines);
        result.AddRange(contentLines);
        result.AddRange(emptyLines);

        _cachedText = _text;
        _cachedWidth = width;
        _cachedLines = result;
        return result.Count > 0 ? result : [""];
    }
}

/// <summary>Single-line text truncated to the viewport width. Port of pi-tui TruncatedText.</summary>
public sealed class TruncatedText(string text, int paddingX = 0, int paddingY = 0) : IComponent
{
    public void Invalidate()
    {
    }

    public List<string> Render(int width)
    {
        var result = new List<string>();
        var emptyLine = new string(' ', Math.Max(0, width));
        for (var i = 0; i < paddingY; i++) result.Add(emptyLine);

        var available = Math.Max(1, width - paddingX * 2);
        var newline = text.IndexOf('\n');
        var single = newline == -1 ? text : text[..newline];
        var display = TextUtils.TruncateToWidth(single, available);
        var pad = new string(' ', paddingX);
        var line = pad + display + pad;
        result.Add(line + new string(' ', Math.Max(0, width - TextUtils.VisibleWidth(line))));

        for (var i = 0; i < paddingY; i++) result.Add(emptyLine);
        return result;
    }
}

/// <summary>Renders empty lines. Port of pi-tui Spacer.</summary>
public sealed class Spacer(int lines = 1) : IComponent
{
    private int _lines = lines;

    public void SetLines(int lines) => _lines = lines;

    public void Invalidate()
    {
    }

    public List<string> Render(int width) => Enumerable.Repeat("", _lines).ToList();
}

/// <summary>Container applying padding and background to all children. Port of pi-tui Box.</summary>
public class Box(int paddingX = 1, int paddingY = 1, Func<string, string>? bg = null) : IComponent
{
    private Func<string, string>? _bg = bg;
    private (List<string> ChildLines, int Width, string? BgSample, List<string> Lines)? _cache;

    public List<IComponent> Children { get; private set; } = [];

    public void AddChild(IComponent component)
    {
        Children.Add(component);
        _cache = null;
    }

    public void RemoveChild(IComponent component)
    {
        if (Children.Remove(component)) _cache = null;
    }

    public void Clear()
    {
        Children = [];
        _cache = null;
    }

    public void SetBg(Func<string, string>? bg) => _bg = bg;

    public virtual void Invalidate()
    {
        _cache = null;
        foreach (var child in Children) child.Invalidate();
    }

    public virtual List<string> Render(int width)
    {
        if (Children.Count == 0) return [];
        var contentWidth = Math.Max(1, width - paddingX * 2);
        var leftPad = new string(' ', paddingX);
        var childLines = new List<string>();
        foreach (var child in Children)
        {
            foreach (var line in child.Render(contentWidth)) childLines.Add(leftPad + line);
        }
        if (childLines.Count == 0) return [];

        var bgSample = _bg?.Invoke("test");
        if (_cache is { } c && c.Width == width && c.BgSample == bgSample && c.ChildLines.SequenceEqual(childLines)) return c.Lines;

        var result = new List<string>();
        for (var i = 0; i < paddingY; i++) result.Add(ApplyBg("", width));
        foreach (var line in childLines) result.Add(ApplyBg(line, width));
        for (var i = 0; i < paddingY; i++) result.Add(ApplyBg("", width));
        _cache = (childLines, width, bgSample, result);
        return result;
    }

    private string ApplyBg(string line, int width)
    {
        var padded = line + new string(' ', Math.Max(0, width - TextUtils.VisibleWidth(line)));
        return _bg is not null ? TextUtils.ApplyBackgroundToLine(padded, width, _bg) : padded;
    }
}

public sealed record LoaderIndicatorOptions(IReadOnlyList<string>? Frames = null, int? IntervalMs = null);

/// <summary>Text with an optional spinner animation. Port of pi-tui Loader.</summary>
public class Loader : Text
{
    private static readonly string[] DefaultFrames = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];
    private const int DefaultIntervalMs = 80;

    private List<string> _frames = [.. DefaultFrames];
    private int _intervalMs = DefaultIntervalMs;
    private int _currentFrame;
    private IDisposable? _interval;
    private readonly TuiBase? _ui;
    private bool _renderIndicatorVerbatim;
    private readonly Func<string, string> _spinnerColor;
    private readonly Func<string, string> _messageColor;
    private string _message;

    public Loader(TuiBase ui, Func<string, string> spinnerColor, Func<string, string> messageColor, string message = "Loading...", LoaderIndicatorOptions? indicator = null)
        : base("", 1, 0)
    {
        _ui = ui;
        _spinnerColor = spinnerColor;
        _messageColor = messageColor;
        _message = message;
        SetIndicator(indicator);
    }

    public override List<string> Render(int width) => ["", .. base.Render(width)];

    public void Start()
    {
        UpdateDisplay();
        RestartAnimation();
    }

    public void Stop()
    {
        _interval?.Dispose();
        _interval = null;
    }

    public void SetMessage(string message)
    {
        _message = message;
        UpdateDisplay();
    }

    public override void Invalidate()
    {
        base.Invalidate();
        UpdateDisplay();
    }

    public void SetIndicator(LoaderIndicatorOptions? indicator)
    {
        _renderIndicatorVerbatim = indicator is not null;
        _frames = indicator?.Frames is { } frames ? [.. frames] : [.. DefaultFrames];
        _intervalMs = indicator?.IntervalMs is > 0 ? indicator.IntervalMs.Value : DefaultIntervalMs;
        _currentFrame = 0;
        Start();
    }

    private void RestartAnimation()
    {
        Stop();
        if (_frames.Count <= 1 || _ui is null) return;
        _interval = _ui.Dispatcher.SetInterval(() =>
        {
            _currentFrame = (_currentFrame + 1) % _frames.Count;
            UpdateDisplay();
        }, _intervalMs);
    }

    protected virtual string GetRenderedIndicator()
    {
        var frame = _currentFrame < _frames.Count ? _frames[_currentFrame] : "";
        return _renderIndicatorVerbatim ? frame : _spinnerColor(frame);
    }

    private void UpdateDisplay()
    {
        var rendered = GetRenderedIndicator();
        SetText($"{(rendered.Length > 0 ? rendered + " " : "")}{_messageColor(_message)}");
        _ui?.RequestRender();
    }
}

/// <summary>Loader cancellable with the select-cancel keybinding. Port of pi-tui CancellableLoader.</summary>
public sealed class CancellableLoader(TuiBase ui, Func<string, string> spinnerColor, Func<string, string> messageColor, string message = "Loading...", LoaderIndicatorOptions? indicator = null)
    : Loader(ui, spinnerColor, messageColor, message, indicator), IInputComponent, IDisposable
{
    private readonly CancellationTokenSource _cts = new();

    public Action? OnAbort { get; set; }

    public CancellationToken Token => _cts.Token;

    public bool Aborted => _cts.IsCancellationRequested;

    public void HandleInput(string data)
    {
        if (!KeybindingsManager.Global.Matches(data, "tui.select.cancel")) return;
        _cts.Cancel();
        OnAbort?.Invoke();
    }

    public void Dispose() => Stop();
}
