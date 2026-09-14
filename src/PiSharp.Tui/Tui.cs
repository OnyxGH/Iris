using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace PiSharp.Tui;

/// <summary>A renderable TUI component. Port of the pi-tui Component interface.</summary>
public interface IComponent
{
    /// <summary>Render to lines for the given viewport width. Lines must not exceed width.</summary>
    List<string> Render(int width);

    /// <summary>Invalidate cached rendering state (theme changes etc.).</summary>
    void Invalidate();
}

/// <summary>Component that accepts keyboard input while focused.</summary>
public interface IInputComponent : IComponent
{
    void HandleInput(string data);

    /// <summary>Whether Kitty key release events are delivered. Default false.</summary>
    bool WantsKeyRelease => false;
}

/// <summary>Component that renders CURSOR_MARKER at its cursor position while focused.</summary>
public interface IFocusable
{
    bool Focused { get; set; }
}

/// <summary>A component containing child components, rendered top to bottom.</summary>
public class Container : IComponent
{
    public List<IComponent> Children { get; set; } = [];

    public void AddChild(IComponent component) => Children.Add(component);

    public void RemoveChild(IComponent component) => Children.Remove(component);

    public void Clear() => Children = [];

    public virtual void Invalidate()
    {
        foreach (var child in Children.ToList()) child.Invalidate();
    }

    public virtual List<string> Render(int width)
    {
        var lines = new List<string>();
        foreach (var child in Children.ToList()) lines.AddRange(child.Render(width));
        return lines;
    }
}

public enum OverlayAnchor
{
    Center, TopLeft, TopRight, BottomLeft, BottomRight, TopCenter, BottomCenter, LeftCenter, RightCenter,
}

/// <summary>Absolute value or percentage ("50%").</summary>
public readonly record struct SizeValue(double Value, bool IsPercent)
{
    public static implicit operator SizeValue(int value) => new(value, false);

    public static SizeValue Percent(double percent) => new(percent, true);

    internal int Resolve(int reference) => IsPercent ? (int)Math.Floor(reference * Value / 100) : (int)Value;
}

public sealed record OverlayMargin(int Top = 0, int Right = 0, int Bottom = 0, int Left = 0);

public sealed class OverlayOptions
{
    public SizeValue? Width { get; init; }
    public int? MinWidth { get; init; }
    public SizeValue? MaxHeight { get; init; }
    public OverlayAnchor? Anchor { get; init; }
    public int? OffsetX { get; init; }
    public int? OffsetY { get; init; }
    public SizeValue? Row { get; init; }
    public SizeValue? Col { get; init; }
    public OverlayMargin? Margin { get; init; }
    public Func<int, int, bool>? Visible { get; init; }
    public bool NonCapturing { get; init; }
}

public sealed record OverlayBounds(int Row, int Col, int Width, int Height);

public interface IOverlayHandle
{
    void Hide();
    void SetHidden(bool hidden);
    bool IsHidden();
    void Focus();
    void Unfocus(IComponent? target = null, bool explicitTarget = false);
    bool IsFocused();
    OverlayBounds? GetBounds();
}

/// <summary>Input listener result: consume the input, or replace it.</summary>
public readonly record struct InputListenerResult(bool Consume = false, string? Data = null);

/// <summary>
/// Base TUI: component tree, focus, overlays, throttled rendering and input routing. Port of pi-tui TuiBase.
/// All members must be used on the <see cref="UiDispatcher"/> thread.
/// </summary>
public abstract partial class TuiBase : Container
{
    public const string CursorMarker = "\e_pi:c\a";
    internal const string SegmentReset = "\e[0m\e]8;;\a";
    private const int MinRenderIntervalMs = 16;

    private sealed class OverlayEntry
    {
        public required IComponent Component { get; init; }
        public OverlayOptions? Options { get; init; }
        public IComponent? PreFocus { get; set; }
        public bool Hidden { get; set; }
        public int FocusOrder { get; set; }
        public OverlayBounds? Bounds { get; set; }
    }

    private abstract record FocusRestore;

    private sealed record FocusRestoreInactive : FocusRestore;

    private sealed record FocusRestoreEligible(OverlayEntry Overlay) : FocusRestore;

    private sealed record FocusRestoreBlocked(OverlayEntry Overlay, IComponent BlockedBy, bool ResumeRestoreOverlay, IComponent? ResumeTarget) : FocusRestore;

    public ITerminal Terminal { get; }
    public UiDispatcher Dispatcher { get; }
    private IComponent? _focused;
    private readonly List<Func<string, InputListenerResult?>> _inputListeners = [];

    /// <summary>Debug key callback (Shift+Ctrl+D).</summary>
    public Action? OnDebug { get; set; }

    private bool _renderRequested;
    private bool _immediateRenderScheduled;
    private IDisposable? _renderTimer;
    private long _lastRenderAt;
    private bool _showHardwareCursor;
    private bool _clearOnShrink;
    protected int FullRedrawCount;
    protected bool Stopped;
    private int _pendingOsc11Replies;
    private readonly Queue<TaskCompletionSource<RgbColor?>> _pendingOsc11Queries = new();
    private readonly List<Action<string>> _colorSchemeListeners = [];
    private bool _colorSchemeNotifications;
    protected readonly string? LogDirectory;

    private int _focusOrderCounter;
    private readonly List<OverlayEntry> _overlayStack = [];
    private FocusRestore _focusRestore = new FocusRestoreInactive();

    protected TuiBase(ITerminal terminal, UiDispatcher dispatcher, bool? showHardwareCursor = null, string? logDirectory = null)
    {
        Terminal = terminal;
        Dispatcher = dispatcher;
        LogDirectory = logDirectory;
        if (showHardwareCursor is { } show) _showHardwareCursor = show;
    }

    public abstract string Mode { get; }

    public int FullRedraws => FullRedrawCount;

    protected bool HasOverlayEntries => _overlayStack.Count > 0;

    protected abstract void DoRender();

    protected virtual void ResetRenderState()
    {
    }

    protected virtual void BeforeTerminalStop(bool preserveScreen)
    {
    }

    public bool GetShowHardwareCursor() => _showHardwareCursor;

    public void SetShowHardwareCursor(bool enabled)
    {
        if (_showHardwareCursor == enabled) return;
        _showHardwareCursor = enabled;
        if (!enabled) Terminal.HideCursor();
        RequestRender();
    }

    public bool GetClearOnShrink() => _clearOnShrink;

    public void SetClearOnShrink(bool enabled) => _clearOnShrink = enabled;

    public IComponent? GetFocusedComponent() => _focused;

    public void SetFocus(IComponent? component) => SetFocusInternal(component, clearRestore: true);

    private void SetFocusInternal(IComponent? component, bool clearRestore)
    {
        var previous = _focused;
        var next = component;
        var previousOverlay = previous is null ? null : _overlayStack.FirstOrDefault(e => ReferenceEquals(e.Component, previous) && IsOverlayVisible(e));
        var nextIsOverlay = next is not null && _overlayStack.Any(e => ReferenceEquals(e.Component, next));
        var restore = GetVisibleFocusRestore();

        if (next is not null && !nextIsOverlay)
        {
            if (restore is FocusRestoreBlocked blocked && ReferenceEquals(blocked.BlockedBy, previous))
            {
                if (!blocked.ResumeRestoreOverlay || !IsComponentMounted(blocked.BlockedBy)) next = ResolveBlockedResume(blocked);
                else _focusRestore = blocked with { BlockedBy = next };
            }
            else if (previousOverlay is not null && restore is not FocusRestoreInactive && ReferenceEquals(RestoreOverlay(restore), previousOverlay)
                     && !IsOverlayFocusAncestor(previousOverlay, next))
            {
                _focusRestore = new FocusRestoreBlocked(previousOverlay, next, true, null);
            }
        }
        else if (next is null)
        {
            if (restore is FocusRestoreBlocked blocked && ReferenceEquals(blocked.BlockedBy, previous)) next = ResolveBlockedResume(blocked);
            else if (clearRestore) _focusRestore = new FocusRestoreInactive();
        }

        if (_focused is IFocusable oldFocusable) oldFocusable.Focused = false;
        _focused = next;
        if (next is IFocusable newFocusable) newFocusable.Focused = true;

        var focusedOverlay = next is null ? null : _overlayStack.FirstOrDefault(e => ReferenceEquals(e.Component, next) && IsOverlayVisible(e));
        if (focusedOverlay is not null) _focusRestore = new FocusRestoreEligible(focusedOverlay);
    }

    private static OverlayEntry? RestoreOverlay(FocusRestore restore) => restore switch
    {
        FocusRestoreEligible e => e.Overlay,
        FocusRestoreBlocked b => b.Overlay,
        _ => null,
    };

    private IComponent? ResolveBlockedResume(FocusRestoreBlocked blocked)
    {
        if (blocked.ResumeRestoreOverlay) return blocked.Overlay.Component;
        _focusRestore = new FocusRestoreInactive();
        return blocked.ResumeTarget;
    }

    private FocusRestore GetVisibleFocusRestore()
    {
        var overlay = RestoreOverlay(_focusRestore);
        if (overlay is null) return _focusRestore;
        return !_overlayStack.Contains(overlay) || !IsOverlayVisible(overlay) ? new FocusRestoreInactive() : _focusRestore;
    }

    private void ClearFocusRestoreFor(OverlayEntry overlay)
    {
        if (ReferenceEquals(RestoreOverlay(_focusRestore), overlay)) _focusRestore = new FocusRestoreInactive();
    }

    private bool IsOverlayFocusAncestor(OverlayEntry entry, IComponent component)
    {
        var visited = new HashSet<IComponent>(ReferenceEqualityComparer.Instance);
        var current = entry.PreFocus;
        while (current is not null && visited.Add(current))
        {
            if (ReferenceEquals(current, component)) return true;
            var c = current;
            current = _overlayStack.FirstOrDefault(o => ReferenceEquals(o.Component, c))?.PreFocus;
        }
        return false;
    }

    private void RetargetOverlayPreFocus(OverlayEntry removed)
    {
        foreach (var overlay in _overlayStack)
        {
            if (!ReferenceEquals(overlay, removed) && ReferenceEquals(overlay.PreFocus, removed.Component)) overlay.PreFocus = removed.PreFocus;
        }
    }

    protected virtual IReadOnlyList<IComponent> GetMountedRoots() => Children;

    private bool IsComponentMounted(IComponent component) => GetMountedRoots().Any(root => ContainsComponent(root, component));

    private static bool ContainsComponent(IComponent root, IComponent target)
    {
        if (ReferenceEquals(root, target)) return true;
        return root is Container container && container.Children.Any(child => ContainsComponent(child, target));
    }

    private sealed class OverlayHandle(TuiBase tui, OverlayEntry entry) : IOverlayHandle
    {
        public void Hide()
        {
            var index = tui._overlayStack.IndexOf(entry);
            if (index == -1) return;
            tui.ClearFocusRestoreFor(entry);
            tui.RetargetOverlayPreFocus(entry);
            tui._overlayStack.RemoveAt(index);
            if (ReferenceEquals(tui._focused, entry.Component)) tui.SetFocus(tui.GetTopmostVisibleOverlay()?.Component ?? entry.PreFocus);
            if (tui._overlayStack.Count == 0) tui.Terminal.HideCursor();
            tui.RequestRender();
        }

        public void SetHidden(bool hidden)
        {
            if (entry.Hidden == hidden) return;
            entry.Hidden = hidden;
            if (hidden)
            {
                tui.ClearFocusRestoreFor(entry);
                if (ReferenceEquals(tui._focused, entry.Component)) tui.SetFocus(tui.GetTopmostVisibleOverlay()?.Component ?? entry.PreFocus);
            }
            else if (entry.Options?.NonCapturing != true && tui.IsOverlayVisible(entry))
            {
                entry.FocusOrder = ++tui._focusOrderCounter;
                tui.SetFocus(entry.Component);
            }
            tui.RequestRender();
        }

        public bool IsHidden() => entry.Hidden;

        public void Focus()
        {
            if (!tui._overlayStack.Contains(entry) || !tui.IsOverlayVisible(entry)) return;
            entry.FocusOrder = ++tui._focusOrderCounter;
            tui.SetFocus(entry.Component);
            tui.RequestRender();
        }

        public void Unfocus(IComponent? target = null, bool explicitTarget = false)
        {
            var isFocused = ReferenceEquals(tui._focused, entry.Component);
            var restore = tui._focusRestore;
            var pendingRestore = ReferenceEquals(RestoreOverlay(restore), entry);
            if (!isFocused && !pendingRestore) return;
            if (restore is FocusRestoreBlocked blocked && ReferenceEquals(blocked.Overlay, entry) && ReferenceEquals(tui._focused, blocked.BlockedBy))
            {
                tui._focusRestore = explicitTarget ? blocked with { ResumeRestoreOverlay = false, ResumeTarget = target } : new FocusRestoreInactive();
                tui.RequestRender();
                return;
            }
            tui.ClearFocusRestoreFor(entry);
            if (isFocused || explicitTarget)
            {
                var top = tui.GetTopmostVisibleOverlay();
                var fallback = top is not null && !ReferenceEquals(top, entry) ? top.Component : entry.PreFocus;
                tui.SetFocus(explicitTarget ? target : fallback);
            }
            tui.RequestRender();
        }

        public bool IsFocused() => ReferenceEquals(tui._focused, entry.Component);

        public OverlayBounds? GetBounds() => tui._overlayStack.Contains(entry) && tui.IsOverlayVisible(entry) ? entry.Bounds : null;
    }

    /// <summary>Show an overlay component with positioning options. Returns a handle to control it.</summary>
    public IOverlayHandle ShowOverlay(IComponent component, OverlayOptions? options = null)
    {
        var entry = new OverlayEntry { Component = component, Options = options, PreFocus = _focused, FocusOrder = ++_focusOrderCounter };
        _overlayStack.Add(entry);
        if (options?.NonCapturing != true && IsOverlayVisible(entry)) SetFocus(component);
        Terminal.HideCursor();
        RequestRender();
        return new OverlayHandle(this, entry);
    }

    /// <summary>Hide the topmost overlay and restore previous focus.</summary>
    public void HideOverlay()
    {
        if (_overlayStack.Count == 0) return;
        var overlay = _overlayStack[^1];
        ClearFocusRestoreFor(overlay);
        RetargetOverlayPreFocus(overlay);
        _overlayStack.RemoveAt(_overlayStack.Count - 1);
        if (ReferenceEquals(_focused, overlay.Component)) SetFocus(GetTopmostVisibleOverlay()?.Component ?? overlay.PreFocus);
        if (_overlayStack.Count == 0) Terminal.HideCursor();
        RequestRender();
    }

    public bool HasOverlay() => _overlayStack.Any(IsOverlayVisible);

    private bool IsOverlayVisible(OverlayEntry entry)
    {
        if (entry.Hidden) return false;
        return entry.Options?.Visible is not { } visible || visible(Terminal.Columns, Terminal.Rows);
    }

    private OverlayEntry? GetTopmostVisibleOverlay()
    {
        OverlayEntry? top = null;
        foreach (var overlay in _overlayStack)
        {
            if (overlay.Options?.NonCapturing == true || !IsOverlayVisible(overlay)) continue;
            if (top is null || overlay.FocusOrder > top.FocusOrder) top = overlay;
        }
        return top;
    }

    public override void Invalidate()
    {
        foreach (var root in GetMountedRoots().ToList()) root.Invalidate();
        foreach (var overlay in _overlayStack.ToList()) overlay.Component.Invalidate();
    }

    public void Start()
    {
        Stopped = false;
        Terminal.Start(HandleTerminalInput, () => RequestRender());
        Terminal.HideCursor();
        if (_colorSchemeNotifications) Terminal.Write("\e[?2031h");
        if (TerminalImage.GetCapabilities().Images is not null) Terminal.Write("\e[16t");
        RequestRender();
    }

    public Action AddInputListener(Func<string, InputListenerResult?> listener)
    {
        _inputListeners.Add(listener);
        return () => _inputListeners.Remove(listener);
    }

    public void RemoveInputListener(Func<string, InputListenerResult?> listener) => _inputListeners.Remove(listener);

    public Action OnTerminalColorSchemeChange(Action<string> listener)
    {
        _colorSchemeListeners.Add(listener);
        return () => _colorSchemeListeners.Remove(listener);
    }

    public void SetTerminalColorSchemeNotifications(bool enabled)
    {
        if (_colorSchemeNotifications == enabled) return;
        _colorSchemeNotifications = enabled;
        if (!Stopped) Terminal.Write(enabled ? "\e[?2031h" : "\e[?2031l");
    }

    public void Stop(bool preserveScreen = false)
    {
        Stopped = true;
        CancelRenderTimer();
        if (_colorSchemeNotifications) Terminal.Write("\e[?2031l");
        BeforeTerminalStop(preserveScreen);
        Terminal.ShowCursor();
        Terminal.Stop();
    }

    public void RenderNow(bool force = false)
    {
        if (force) ResetRenderState();
        _renderRequested = false;
        CancelRenderTimer();
        _lastRenderAt = Stopwatch.GetTimestamp();
        DoRender();
    }

    public void RequestRender(bool force = false)
    {
        if (force)
        {
            ResetRenderState();
            RequestImmediateRender();
            return;
        }
        if (_renderRequested) return;
        _renderRequested = true;
        Dispatcher.Post(ScheduleRender);
    }

    private void RequestImmediateRender()
    {
        CancelRenderTimer();
        _renderRequested = true;
        if (_immediateRenderScheduled) return;
        _immediateRenderScheduled = true;
        Dispatcher.Post(() =>
        {
            _immediateRenderScheduled = false;
            if (Stopped || !_renderRequested) return;
            CancelRenderTimer();
            _renderRequested = false;
            _lastRenderAt = Stopwatch.GetTimestamp();
            DoRender();
        });
    }

    private void CancelRenderTimer()
    {
        _renderTimer?.Dispose();
        _renderTimer = null;
    }

    private void ScheduleRender()
    {
        if (Stopped || _renderTimer is not null || !_renderRequested) return;
        var elapsed = Stopwatch.GetElapsedTime(_lastRenderAt).TotalMilliseconds;
        var delay = (int)Math.Max(0, MinRenderIntervalMs - elapsed);
        _renderTimer = Dispatcher.SetTimeout(() =>
        {
            _renderTimer = null;
            if (Stopped || !_renderRequested) return;
            _renderRequested = false;
            _lastRenderAt = Stopwatch.GetTimestamp();
            DoRender();
            if (_renderRequested) ScheduleRender();
        }, delay);
    }

    [GeneratedRegex("^\\e\\[6;(\\d+);(\\d+)t$")]
    private static partial Regex CellSizeResponse();

    private void HandleTerminalInput(string data)
    {
        if (ConsumeOsc11Response(data)) return;
        if (TerminalImage.ParseTerminalColorSchemeReport(data) is { } scheme)
        {
            foreach (var listener in _colorSchemeListeners.ToList()) listener(scheme);
            return;
        }

        if (_inputListeners.Count > 0)
        {
            var current = data;
            foreach (var listener in _inputListeners.ToList())
            {
                var result = listener(current);
                if (result?.Consume == true) return;
                if (result?.Data is { } replaced) current = replaced;
            }
            if (current.Length == 0) return;
            data = current;
        }

        var cellSize = CellSizeResponse().Match(data);
        if (cellSize.Success)
        {
            var heightPx = int.Parse(cellSize.Groups[1].Value, CultureInfo.InvariantCulture);
            var widthPx = int.Parse(cellSize.Groups[2].Value, CultureInfo.InvariantCulture);
            if (heightPx > 0 && widthPx > 0)
            {
                TerminalImage.SetCellDimensions(new CellDimensions(widthPx, heightPx));
                Invalidate();
                RequestRender();
            }
            return;
        }

        if (Keys.Matches(data, "shift+ctrl+d") && OnDebug is not null)
        {
            OnDebug();
            return;
        }

        var focusedOverlay = _overlayStack.FirstOrDefault(o => ReferenceEquals(o.Component, _focused));
        if (focusedOverlay is not null && !IsOverlayVisible(focusedOverlay))
        {
            if (GetTopmostVisibleOverlay() is { } top) SetFocus(top.Component);
            else SetFocusInternal(focusedOverlay.PreFocus, clearRestore: false);
        }

        if (!_overlayStack.Any(o => ReferenceEquals(o.Component, _focused)))
        {
            switch (GetVisibleFocusRestore())
            {
                case FocusRestoreEligible eligible:
                    SetFocus(eligible.Overlay.Component);
                    break;
                case FocusRestoreBlocked blocked when !ReferenceEquals(blocked.BlockedBy, _focused):
                    if (blocked.ResumeRestoreOverlay)
                    {
                        SetFocus(blocked.Overlay.Component);
                    }
                    else
                    {
                        _focusRestore = new FocusRestoreInactive();
                        SetFocus(blocked.ResumeTarget);
                    }
                    break;
            }
        }

        if (_focused is IInputComponent input)
        {
            if (Keys.IsKeyRelease(data) && !input.WantsKeyRelease) return;
            input.HandleInput(data);
            RequestImmediateRender();
        }
    }

    private bool ConsumeOsc11Response(string data)
    {
        if (_pendingOsc11Replies <= 0 || !TerminalImage.IsOsc11BackgroundColorResponse(data)) return false;
        var rgb = TerminalImage.ParseOsc11BackgroundColor(data);
        _pendingOsc11Replies--;
        if (_pendingOsc11Queries.TryDequeue(out var query)) query.TrySetResult(rgb);
        return true;
    }

    private (int Width, int Row, int Col, int? MaxHeight) ResolveOverlayLayout(OverlayOptions? options, int overlayHeight, int termWidth, int termHeight)
    {
        var opt = options ?? new OverlayOptions();
        var margin = opt.Margin ?? new OverlayMargin();
        int marginTop = Math.Max(0, margin.Top), marginRight = Math.Max(0, margin.Right), marginBottom = Math.Max(0, margin.Bottom), marginLeft = Math.Max(0, margin.Left);
        var availWidth = Math.Max(1, termWidth - marginLeft - marginRight);
        var availHeight = Math.Max(1, termHeight - marginTop - marginBottom);

        var width = opt.Width?.Resolve(termWidth) ?? Math.Min(80, availWidth);
        if (opt.MinWidth is { } minWidth) width = Math.Max(width, minWidth);
        width = Math.Max(1, Math.Min(width, availWidth));

        int? maxHeight = opt.MaxHeight?.Resolve(termHeight);
        if (maxHeight is { } mh) maxHeight = Math.Max(1, Math.Min(mh, availHeight));
        var effectiveHeight = maxHeight is { } m ? Math.Min(overlayHeight, m) : overlayHeight;

        int row;
        if (opt.Row is { } r)
        {
            row = r.IsPercent ? marginTop + (int)Math.Floor(Math.Max(0, availHeight - effectiveHeight) * r.Value / 100) : (int)r.Value;
        }
        else
        {
            row = ResolveAnchorRow(opt.Anchor ?? OverlayAnchor.Center, effectiveHeight, availHeight, marginTop);
        }

        int col;
        if (opt.Col is { } c)
        {
            col = c.IsPercent ? marginLeft + (int)Math.Floor(Math.Max(0, availWidth - width) * c.Value / 100) : (int)c.Value;
        }
        else
        {
            col = ResolveAnchorCol(opt.Anchor ?? OverlayAnchor.Center, width, availWidth, marginLeft);
        }

        if (opt.OffsetY is { } oy) row += oy;
        if (opt.OffsetX is { } ox) col += ox;
        row = Math.Max(marginTop, Math.Min(row, termHeight - marginBottom - effectiveHeight));
        col = Math.Max(marginLeft, Math.Min(col, termWidth - marginRight - width));
        return (width, row, col, maxHeight);
    }

    private static int ResolveAnchorRow(OverlayAnchor anchor, int height, int availHeight, int marginTop) => anchor switch
    {
        OverlayAnchor.TopLeft or OverlayAnchor.TopCenter or OverlayAnchor.TopRight => marginTop,
        OverlayAnchor.BottomLeft or OverlayAnchor.BottomCenter or OverlayAnchor.BottomRight => marginTop + availHeight - height,
        _ => marginTop + (int)Math.Floor((availHeight - height) / 2.0),
    };

    private static int ResolveAnchorCol(OverlayAnchor anchor, int width, int availWidth, int marginLeft) => anchor switch
    {
        OverlayAnchor.TopLeft or OverlayAnchor.LeftCenter or OverlayAnchor.BottomLeft => marginLeft,
        OverlayAnchor.TopRight or OverlayAnchor.RightCenter or OverlayAnchor.BottomRight => marginLeft + availWidth - width,
        _ => marginLeft + (int)Math.Floor((availWidth - width) / 2.0),
    };

    /// <summary>Composite overlay content into a terminal line at a fixed column.</summary>
    public static string CompositeLine(string baseLine, string overlayLine, int startCol, int overlayWidth, int totalWidth)
    {
        if (TerminalImage.IsImageLine(baseLine)) return baseLine;
        var afterStart = startCol + overlayWidth;
        var segments = TextUtils.ExtractSegments(baseLine, startCol, afterStart, totalWidth - afterStart, true);
        var overlay = TextUtils.SliceWithWidth(overlayLine, 0, overlayWidth, true);
        var beforePad = Math.Max(0, startCol - segments.BeforeWidth);
        var overlayPad = Math.Max(0, overlayWidth - overlay.Width);
        var actualBefore = Math.Max(startCol, segments.BeforeWidth);
        var actualOverlay = Math.Max(overlayWidth, overlay.Width);
        var afterTarget = Math.Max(0, totalWidth - actualBefore - actualOverlay);
        var afterPad = Math.Max(0, afterTarget - segments.AfterWidth);
        var result = segments.Before + new string(' ', beforePad) + SegmentReset + overlay.Text + new string(' ', overlayPad) + SegmentReset + segments.After + new string(' ', afterPad);
        return TextUtils.VisibleWidth(result) <= totalWidth ? result : TextUtils.SliceByColumn(result, 0, totalWidth, true);
    }

    /// <summary>Composite visible overlays (by focus order) into rendered lines.</summary>
    protected List<string> CompositeOverlays(List<string> lines, int termWidth, int termHeight)
    {
        if (_overlayStack.Count == 0) return lines;
        var result = new List<string>(lines);
        foreach (var entry in _overlayStack) entry.Bounds = null;

        var rendered = new List<(List<string> Lines, int Row, int Col, int Width)>();
        var minLinesNeeded = result.Count;
        foreach (var entry in _overlayStack.Where(IsOverlayVisible).OrderBy(e => e.FocusOrder).ToList())
        {
            var (width, _, _, maxHeight) = ResolveOverlayLayout(entry.Options, 0, termWidth, termHeight);
            var overlayLines = entry.Component.Render(width);
            if (maxHeight is { } mh && overlayLines.Count > mh) overlayLines = overlayLines.Take(mh).ToList();
            var (_, row, col, _) = ResolveOverlayLayout(entry.Options, overlayLines.Count, termWidth, termHeight);
            entry.Bounds = new OverlayBounds(row, col, width, overlayLines.Count);
            rendered.Add((overlayLines, row, col, width));
            minLinesNeeded = Math.Max(minLinesNeeded, row + overlayLines.Count);
        }

        var workingHeight = Math.Max(result.Count, Math.Max(termHeight, minLinesNeeded));
        while (result.Count < workingHeight) result.Add("");
        var viewportStart = Math.Max(0, workingHeight - termHeight);

        foreach (var (overlayLines, row, col, w) in rendered)
        {
            for (var i = 0; i < overlayLines.Count; i++)
            {
                var idx = viewportStart + row + i;
                if (idx < 0 || idx >= result.Count) continue;
                var line = TextUtils.VisibleWidth(overlayLines[i]) > w ? TextUtils.SliceByColumn(overlayLines[i], 0, w, true) : overlayLines[i];
                result[idx] = CompositeLine(result[idx], line, col, w, termWidth);
            }
        }
        return result;
    }

    protected static List<string> ApplyLineResets(List<string> lines)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (!TerminalImage.IsImageLine(lines[i])) lines[i] = TextUtils.NormalizeTerminalOutput(lines[i]) + SegmentReset;
        }
        return lines;
    }

    /// <summary>Find, strip and return the cursor marker position within the visible viewport.</summary>
    protected static (int Row, int Col)? ExtractCursorPosition(List<string> lines, int height)
    {
        var viewportTop = Math.Max(0, lines.Count - height);
        for (var row = lines.Count - 1; row >= viewportTop; row--)
        {
            var line = lines[row];
            var markerIndex = line.IndexOf(CursorMarker, StringComparison.Ordinal);
            if (markerIndex == -1) continue;
            var col = TextUtils.VisibleWidth(line[..markerIndex]);
            lines[row] = line[..markerIndex] + line[(markerIndex + CursorMarker.Length)..];
            return (row, col);
        }
        return null;
    }

    /// <summary>Query the terminal default background color (OSC 11).</summary>
    public Task<RgbColor?> QueryTerminalBackgroundColorAsync(int timeoutMs)
    {
        var tcs = new TaskCompletionSource<RgbColor?>();
        _pendingOsc11Queries.Enqueue(tcs);
        _pendingOsc11Replies++;
        Dispatcher.SetTimeout(() => tcs.TrySetResult(null), timeoutMs);
        Terminal.Write("\e]11;?\a");
        return tcs.Task;
    }

    /// <summary>Query the terminal color scheme preference (CSI ? 996 n). Returns "dark", "light" or null.</summary>
    public Task<string?> QueryTerminalColorSchemeAsync(int timeoutMs)
    {
        var tcs = new TaskCompletionSource<string?>();
        Action? unsubscribe = null;
        IDisposable? timer = null;
        void Settle(string? scheme)
        {
            if (!tcs.TrySetResult(scheme)) return;
            timer?.Dispose();
            unsubscribe?.Invoke();
        }
        unsubscribe = OnTerminalColorSchemeChange(s => Settle(s));
        timer = Dispatcher.SetTimeout(() => Settle(null), timeoutMs);
        Terminal.Write("\e[?996n");
        return tcs.Task;
    }
}
