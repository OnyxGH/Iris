using System.Text;
using System.Text.RegularExpressions;
using Iris.Tui.Components;
using Iris.Tui.Layout;

namespace Iris.Tui;

public sealed class TuiAltScreenOptions
{
    /// <summary>Number of logical lines moved for each mouse-wheel event.</summary>
    public int WheelScrollLines { get; init; } = 1;

    /// <summary>Capture mouse events for viewport scrolling and application-owned text selection.</summary>
    public bool Mouse { get; init; } = true;

    public Func<string, string>? SearchMatchStyle { get; init; }
    public Func<string, string>? SearchCurrentMatchStyle { get; init; }
    public Func<string, bool, string>? SearchNavigationButtonStyle { get; init; }

    /// <summary>Clickable jump-to-end label centered on the last row of a follow-end primary scroll view scrolled away from its end.</summary>
    public Func<string>? ScrollToEndIndicator { get; init; }

    /// <summary>Open an OSC 8 hyperlink activated with a primary-button click.</summary>
    public Action<string>? OpenUrl { get; init; }

    /// <summary>Handle an unmodified secondary-button press for clipboard paste (Windows only).</summary>
    public Action? OnRightClickPaste { get; init; }

    /// <summary>Automatically copy selected text to the clipboard on mouse release.</summary>
    public bool CopyOnSelect { get; init; } = true;

    /// <summary>Copy selected text to the system clipboard; returns success. When null, OSC 52 is used.</summary>
    public Func<string, Task<bool>>? CopySelection { get; init; }
}

/// <summary>
/// Alternate-screen TUI with a scrollable, application-owned viewport.
/// A selection that starts outside a scroll view stays inside the layout section (or overlay) where it started;
/// inline images are disabled while the alternate screen is active.
/// </summary>
public sealed partial class TuiAltScreen : TuiBase
{
    private const string EnterAltScreen = "\e[?1049h";
    private const string ExitAltScreen = "\e[?1049l";
    private const string DisableAutowrap = "\e[?7l";
    private const string EnableAutowrap = "\e[?7h";
    private const string EnableButtonMotionMouse = "\e[?1000h\e[?1002h\e[?1004h\e[?1006h";
    private const string EnableAllMotionMouse = "\e[?1000h\e[?1002h\e[?1003h\e[?1004h\e[?1006h";
    private const string DisableMouse = "\e[?1006l\e[?1004l\e[?1003l\e[?1002l\e[?1000l";
    private const string FocusIn = "\e[I";
    private const string FocusOut = "\e[O";
    private const string BeginSynchronizedOutput = "\e[?2026h";
    private const string EndSynchronizedOutput = "\e[?2026l";
    private const int PageScrollOverlap = 4;
    private const int AltWheelScrollMultiplier = 5;
    private const int DoubleClickIntervalMs = 500;

    [GeneratedRegex(@"^\e\]133;A(?:\a|\e\\)")]
    private static partial Regex Osc133PromptStart();

    [GeneratedRegex(@"^\e\[<(\d+);(\d+);(\d+)([Mm])$")]
    private static partial Regex SgrMouse();

    private sealed record SelectionPoint(int Row, int Col, ScrollView? ScrollView = null, bool Boundary = false, LayoutRect? Region = null);

    private sealed record SelectionRange(SelectionPoint Start, SelectionPoint End);

    private enum Granularity
    {
        Character,
        Word,
        Line,
    }

    private sealed record ClickTarget(long Timestamp, int Count, int Row, ScrollView? ScrollView, int WordStart, int WordEnd);

    private sealed record SgrMouseEvent(int Button, int X, int Y, bool Release);

    private sealed record WheelEvent(int Direction, int X, int Y, int Button);

    private sealed record ScrollbarDrag(ScrollView ScrollView, int GrabOffset);

    private sealed record ScrollbarTarget(ScrollView ScrollView, ScrollbarGeometry Geometry);

    private enum SearchSelectionMode
    {
        Query,
        Retain,
        Next,
        Previous,
    }

    private sealed class ActiveSearch
    {
        public required AltScreenSearchComponent Component { get; init; }
        public AltScreenSearchIndex Index { get; } = new();
        public IOverlayHandle? Overlay { get; set; }
        public string Query { get; set; } = "";
        public List<AltScreenSearchMatch> Matches { get; set; } = [];
        public int SelectedIndex { get; set; } = -1;
        public string? SelectedKey { get; set; }
        public int AnchorRow { get; set; }
        public SearchSelectionMode SelectionMode { get; set; } = SearchSelectionMode.Query;
    }

    private List<string> _previousScreen = [];
    private int _previousScreenWidth;
    private int _previousScreenHeight;
    private IComponent? _layoutRoot;
    private LayoutFrame? _currentLayout;
    private readonly ScrollView _implicitScrollView;
    private readonly AltScreenFlashContainer _flashes;
    private bool _altScreenActive;
    private TerminalCapabilities? _savedCapabilities;
    private SelectionPoint? _selectionAnchor;
    private SelectionPoint? _selectionFocus;
    private Granularity _selectionGranularity = Granularity.Character;
    private SelectionRange? _selectionInitialRange;
    private ClickTarget? _lastClick;
    private (int X, int Y)? _selectionDragPointer;
    private int _selectionAutoScrollDirection;
    private IDisposable? _selectionAutoScrollTimer;
    private bool _selectionPressActive;
    private ScrollbarDrag? _scrollbarDrag;
    private ScrollView? _scrollbarHover;
    private (int Row, int Column, int Width)? _scrollToEndIndicatorRect;
    private ActiveSearch? _activeSearch;
    private string? _pressedUrl;
    private bool _selectionDragged;
    private TuiMouseDispatchTarget? _mouseCapture;
    private TuiMouseDispatchTarget? _mousePressTarget;
    private (int X, int Y)? _mousePressPoint;
    private bool _mousePressMoved;
    private (long Timestamp, int Count, IComponent Component, int X, int Y)? _lastComponentClick;
    private readonly int _wheelScrollLines;
    private readonly bool _mouseEnabled;
    private readonly Func<string, string> _searchMatchStyle;
    private readonly Func<string, string> _searchCurrentMatchStyle;
    private readonly Func<string, bool, string> _searchNavigationButtonStyle;
    private readonly Func<string>? _scrollToEndIndicator;
    private readonly Action<string>? _openUrl;
    private readonly Action? _onRightClickPaste;
    private readonly Func<string, Task<bool>>? _copySelection;

    private sealed class ImplicitDocument(TuiAltScreen tui) : IMouseComponent
    {
        public List<string> Render(int width) => tui.RenderChildren(width);

        public TuiMouseEventResult? HandleMouse(TuiMouseEvent mouseEvent) => tui.HandleChildrenMouse(mouseEvent);

        public void Invalidate()
        {
            foreach (var child in tui.Children.ToList()) child.Invalidate();
        }
    }

    public TuiAltScreen(ITerminal terminal, UiDispatcher dispatcher, bool? showHardwareCursor = null, string? logDirectory = null, TuiAltScreenOptions? options = null)
        : base(terminal, dispatcher, showHardwareCursor, logDirectory)
    {
        options ??= new TuiAltScreenOptions();
        _implicitScrollView = new ScrollView(new ImplicitDocument(this), new ScrollViewOptions { FollowEnd = true, Primary = true });
        _flashes = new AltScreenFlashContainer(dispatcher, () => RequestRender());
        _wheelScrollLines = Math.Max(1, options.WheelScrollLines);
        _mouseEnabled = options.Mouse;
        _searchMatchStyle = options.SearchMatchStyle ?? (text => $"\e[4m{text}\e[24m");
        _searchCurrentMatchStyle = options.SearchCurrentMatchStyle ?? (text => $"\e[1;7m{text}\e[22;27m");
        _searchNavigationButtonStyle = options.SearchNavigationButtonStyle ?? ((text, _) => text);
        _scrollToEndIndicator = options.ScrollToEndIndicator;
        _openUrl = options.OpenUrl;
        _onRightClickPaste = options.OnRightClickPaste;
        CopyOnSelect = options.CopyOnSelect;
        _copySelection = options.CopySelection;
        AddInputListener(HandleViewportInput);
    }

    public override string Mode => "fullscreen";

    public int ViewportTop => GetPrimaryScrollView().ScrollTop;

    public bool IsFollowingOutput => GetPrimaryScrollView().IsFollowingEnd;

    public bool CopyOnSelect { get; set; }

    /// <summary>Whether the viewport has a non-empty active text selection.</summary>
    public bool HasActiveSelection() => GetActiveSelectionText() is not null;

    /// <summary>Copy the active text selection, if any.</summary>
    public async Task<bool> CopyActiveSelectionToClipboardAsync()
    {
        var text = GetActiveSelectionText();
        return text is not null && await CopyTextToClipboardAsync(text);
    }

    public void SetLayoutRoot(IComponent? component)
    {
        if (ReferenceEquals(_layoutRoot, component)) return;
        _layoutRoot = component;
        _currentLayout = null;
        RequestRender();
    }

    private List<string> RenderChildren(int width) => base.Render(width);

    private TuiMouseEventResult? HandleChildrenMouse(TuiMouseEvent mouseEvent) => base.HandleMouse(mouseEvent);

    public override List<string> Render(int width) => _layoutRoot?.Render(width) ?? base.Render(width);

    protected override IReadOnlyList<IComponent> GetMountedRoots() => _layoutRoot is not null ? [_layoutRoot] : Children;

    private ScrollView GetPrimaryScrollView() => _currentLayout?.PrimaryScrollView ?? _implicitScrollView;

    private static bool IsMultiplexer()
    {
        var term = Environment.GetEnvironmentVariable("TERM")?.ToLowerInvariant() ?? "";
        return Environment.GetEnvironmentVariable("TMUX") is not null || Environment.GetEnvironmentVariable("ZELLIJ") is not null
            || Environment.GetEnvironmentVariable("STY") is not null || term.StartsWith("tmux", StringComparison.Ordinal) || term.StartsWith("screen", StringComparison.Ordinal);
    }

    protected override void BeforeTerminalStart()
    {
        StopSelectionAutoScroll();
        _selectionPressActive = false;
        StopScrollbarHover();
        _scrollbarDrag = null;
        _flashes.Dispose();
        _altScreenActive = true;
        var capabilities = TerminalImage.GetCapabilities();
        if (capabilities.Images is not null)
        {
            _savedCapabilities = capabilities;
            TerminalImage.SetCapabilities(capabilities with { Images = null });
            Invalidate();
        }
        _selectionAnchor = null;
        _selectionFocus = null;
        _selectionGranularity = Granularity.Character;
        _selectionInitialRange = null;
        _lastClick = null;
        _pressedUrl = null;
        _selectionDragged = false;
        ClearComponentMouseGesture();
        _lastComponentClick = null;
        ResetRenderState();
        var mouseSequence = IsMultiplexer() ? EnableButtonMotionMouse : EnableAllMotionMouse;
        Terminal.Write($"{EnterAltScreen}{DisableAutowrap}{(_mouseEnabled ? mouseSequence : "")}\e[2J\e[H\e[?25l");
    }

    protected override void BeforeTerminalStop(bool preserveScreen)
    {
        CloseSearch();
        StopSelectionAutoScroll();
        _selectionPressActive = false;
        StopScrollbarHover();
        _scrollbarDrag = null;
        ClearComponentMouseGesture();
        _flashes.Dispose();
        if (!_altScreenActive) return;
        Terminal.Write($"{BeginSynchronizedOutput}{(_mouseEnabled ? DisableMouse : "")}{EnableAutowrap}{EndSynchronizedOutput}");
    }

    protected override void AfterTerminalStop(bool preserveScreen)
    {
        if (!_altScreenActive) return;
        _altScreenActive = false;
        if (_savedCapabilities is not null)
        {
            TerminalImage.SetCapabilities(_savedCapabilities);
            _savedCapabilities = null;
            Invalidate();
        }
        if (preserveScreen)
        {
            Terminal.Write($"{BeginSynchronizedOutput}{ExitAltScreen}\e[?25h{EndSynchronizedOutput}");
            return;
        }
        // Print the whole document to the main screen so it stays in the terminal scrollback after exit.
        var width = Math.Max(1, Terminal.Columns);
        var documentLines = Render(width).Select(line => LayoutEngine.Osc133ZonePrefix().Replace(line, "").Replace(CursorMarker, "", StringComparison.Ordinal)).ToList();
        var lastDocument = ApplyLineResets(documentLines)
            .Select(line => TerminalImage.IsImageLine(line) || TextUtils.VisibleWidth(line) <= width ? line : TextUtils.SliceByColumn(line, 0, width, true))
            .ToList();
        var buffer = new StringBuilder($"{BeginSynchronizedOutput}{ExitAltScreen}{DisableAutowrap}");
        for (var row = 0; row < lastDocument.Count; row++)
        {
            if (row > 0) buffer.Append("\r\n");
            buffer.Append("\r\e[2K").Append(lastDocument[row]);
        }
        buffer.Append($"\e[0m{EnableAutowrap}\r\n\e[?25h{EndSynchronizedOutput}");
        Terminal.Write(buffer.ToString());
    }

    protected override void ResetRenderState()
    {
        _previousScreen = [];
        _previousScreenWidth = 0;
        _previousScreenHeight = 0;
        _currentLayout = null;
    }

    public void ScrollBy(int lines)
    {
        GetPrimaryScrollView().ScrollBy(lines);
        RequestRender();
    }

    public void ScrollToTop()
    {
        GetPrimaryScrollView().ScrollToStart();
        RequestRender();
    }

    public void ScrollToBottom()
    {
        GetPrimaryScrollView().ScrollToEnd();
        RequestRender();
    }

    private void ScrollToPrompt(int direction)
    {
        if (_currentLayout is null) return;
        var scrollView = GetPrimaryScrollView();
        if (LayoutEngine.GetScrollViewBox(_currentLayout, scrollView)?.ScrollContentLines is not { } lines) return;
        for (var row = scrollView.ScrollTop + direction; row >= 0 && row < lines.Count; row += direction)
        {
            if (!Osc133PromptStart().IsMatch(lines[row])) continue;
            scrollView.ScrollTo(row);
            RequestRender();
            return;
        }
    }

    // ----- Search -----

    private void ToggleSearch()
    {
        if (_activeSearch is not null)
        {
            CloseSearch();
            return;
        }
        var search = new ActiveSearch
        {
            Component = new AltScreenSearchComponent(UpdateSearchQuery, _searchNavigationButtonStyle),
            AnchorRow = GetPrimaryScrollView().ScrollTop,
        };
        _activeSearch = search;
        search.Overlay = ShowOverlay(search.Component, new OverlayOptions
        {
            Anchor = OverlayAnchor.TopRight,
            Width = SizeValue.Percent(40),
            MinWidth = 32,
            Margin = new OverlayMargin(1, 1, 1, 1),
        });
    }

    private void CloseSearch()
    {
        if (_activeSearch is not { } search) return;
        _activeSearch = null;
        search.Overlay?.Hide();
        RequestRender();
    }

    private void UpdateSearchQuery(string query)
    {
        if (_activeSearch is not { } search || query == search.Query) return;
        var selected = search.SelectedIndex >= 0 && search.SelectedIndex < search.Matches.Count ? search.Matches[search.SelectedIndex] : null;
        search.AnchorRow = selected?.Segments.FirstOrDefault()?.Row ?? GetPrimaryScrollView().ScrollTop;
        search.Query = query;
        search.SelectionMode = SearchSelectionMode.Query;
        search.Component.SetResult(-1, 0);
        RequestRender();
    }

    private void NavigateSearch(int direction)
    {
        if (_activeSearch is not { Query.Length: > 0 } search) return;
        search.SelectionMode = direction < 0 ? SearchSelectionMode.Previous : SearchSelectionMode.Next;
        RequestRender();
    }

    private int? GetSearchNavigationDirectionAt(int x, int y)
    {
        if (_activeSearch?.Overlay?.GetBounds() is not { } bounds) return null;
        if (x < bounds.Col || x >= bounds.Col + bounds.Width || y < bounds.Row || y >= bounds.Row + bounds.Height) return null;
        return _activeSearch.Component.GetNavigationDirectionAt(y - bounds.Row, x - bounds.Col);
    }

    private bool HandleSearchMouseEvent(SgrMouseEvent mouseEvent)
    {
        if (_activeSearch is not { } search) return false;
        var direction = GetSearchNavigationDirectionAt(mouseEvent.X, mouseEvent.Y);
        if (search.Component.SetHoveredNavigationDirection(direction)) RequestRender();
        if (direction is null || mouseEvent.Release || (mouseEvent.Button & 32) != 0 || (mouseEvent.Button & 3) != 0) return false;
        NavigateSearch(direction.Value);
        return true;
    }

    private bool RefreshSearch(LayoutFrame layout)
    {
        if (_activeSearch is not { } search) return false;
        var scrollView = layout.PrimaryScrollView ?? _implicitScrollView;
        var box = LayoutEngine.GetScrollViewBox(layout, scrollView);
        if (box?.ScrollContentLines is not { } lines || search.Query.Trim().Length == 0)
        {
            search.Matches = [];
            search.SelectedIndex = -1;
            search.SelectedKey = null;
            search.SelectionMode = SearchSelectionMode.Retain;
            search.Component.SetResult(-1, 0);
            return false;
        }

        var shouldReveal = search.SelectionMode != SearchSelectionMode.Retain;
        var (matches, changed) = search.Index.Search(lines, search.Query);
        search.Matches = matches;
        if (!changed && search.SelectionMode == SearchSelectionMode.Retain) return false;

        var exactIndex = changed
            ? search.SelectedKey is { } key ? matches.FindIndex(m => m.Key == key) : -1
            : search.SelectedIndex;
        var selectedIndex = -1;
        if (matches.Count > 0)
        {
            switch (search.SelectionMode)
            {
                case SearchSelectionMode.Query:
                {
                    int low = 0, high = matches.Count;
                    while (low < high)
                    {
                        var middle = low + (high - low) / 2;
                        if (matches[middle].Segments[0].Row < search.AnchorRow) low = middle + 1;
                        else high = middle;
                    }
                    selectedIndex = low < matches.Count ? low : 0;
                    break;
                }
                case SearchSelectionMode.Next:
                {
                    var baseIndex = exactIndex >= 0 ? exactIndex : Math.Min(search.SelectedIndex, matches.Count - 1);
                    selectedIndex = baseIndex < 0 ? 0 : (baseIndex + 1) % matches.Count;
                    break;
                }
                case SearchSelectionMode.Previous:
                {
                    var baseIndex = exactIndex >= 0 ? exactIndex : Math.Min(search.SelectedIndex, matches.Count - 1);
                    selectedIndex = baseIndex < 0 ? matches.Count - 1 : (baseIndex - 1 + matches.Count) % matches.Count;
                    break;
                }
                default:
                    selectedIndex = exactIndex >= 0 ? exactIndex : Math.Min(Math.Max(0, search.SelectedIndex), matches.Count - 1);
                    break;
            }
        }

        search.SelectedIndex = selectedIndex;
        search.SelectedKey = selectedIndex >= 0 ? matches[selectedIndex].Key : null;
        search.SelectionMode = SearchSelectionMode.Retain;
        search.Component.SetResult(selectedIndex, matches.Count);
        if (!shouldReveal || selectedIndex < 0 || scrollView.ViewportHeight <= 0) return false;

        var selected = matches[selectedIndex];
        var before = scrollView.ScrollTop;
        var visibleBottom = before + scrollView.ViewportHeight - 1;
        var target = before;
        if (selected.Segments[0].Row < before || selected.Segments[^1].Row > visibleBottom) target = selected.Segments[0].Row - scrollView.ViewportHeight / 3;
        scrollView.ScrollTo(target, disableFollow: true);
        return scrollView.ScrollTop != before;
    }

    /// <summary>Show a transient message in the flash stack.</summary>
    public void Flash(string message, int durationMs = 1000) => _flashes.Flash(message, durationMs);

    private bool ShouldDeferViewportInputToOverlay() => IsOverlayFocused() && _activeSearch?.Overlay?.IsFocused() != true;

    private void ClearComponentMouseGesture()
    {
        _mouseCapture = null;
        _mousePressTarget = null;
        _mousePressPoint = null;
        _mousePressMoved = false;
    }

    // ----- Input -----

    private InputListenerResult? HandleViewportInput(string data)
    {
        if (data == FocusOut)
        {
            var hadActiveSelection = _selectionPressActive;
            var hadNonEmptySelection = hadActiveSelection && GetSelectionBounds() is not null;
            _selectionPressActive = false;
            StopSelectionAutoScroll();
            StopScrollbarHover();
            if (_activeSearch?.Component.SetHoveredNavigationDirection(null) == true) RequestRender();
            _scrollbarDrag = null;
            _pressedUrl = null;
            _selectionDragged = false;
            ClearComponentMouseGesture();
            _lastComponentClick = null;
            if (hadActiveSelection)
            {
                _selectionAnchor = null;
                _selectionFocus = null;
                _selectionGranularity = Granularity.Character;
                _selectionInitialRange = null;
                if (hadNonEmptySelection) RequestRender();
            }
            _lastClick = null;
            return new InputListenerResult(Consume: true);
        }
        if (data == FocusIn) return new InputListenerResult(Consume: true);

        if (ParseWheelEvent(data) is { } wheel)
        {
            var mouseEvent = CreateMouseEvent(TuiMouseEventType.Wheel, wheel.Button, wheel.X, wheel.Y, wheelDelta: wheel.Direction * GetWheelScrollLines(wheel.Button));
            var overlay = DispatchMouseToOverlay(mouseEvent);
            var result = overlay.Result ?? (overlay.Hit ? null : DispatchMouseToLayout(mouseEvent));
            if (result is not null)
            {
                if (ApplyMouseDispatchResult(mouseEvent, result)) RequestRender();
                return new InputListenerResult(Consume: true);
            }
            if (ShouldDeferViewportInputToOverlay()) return null;
            RouteWheel(wheel);
            return new InputListenerResult(Consume: true);
        }
        if (ParseSgrMouseEvent(data) is { } sgr)
        {
            HandleMouseEvent(sgr);
            return new InputListenerResult(Consume: true);
        }
        if (IsMouseSequence(data)) return new InputListenerResult(Consume: true);

        var keybindings = KeybindingsManager.Global;
        var isRelease = Keys.IsKeyRelease(data);
        bool Consume(Action action)
        {
            if (!isRelease) action();
            return true;
        }

        if (keybindings.Matches(data, "tui.altScreen.search") && Consume(ToggleSearch)) return new InputListenerResult(Consume: true);
        if (_activeSearch?.Overlay?.IsFocused() == true)
        {
            if (keybindings.Matches(data, "tui.altScreen.searchNext") && Consume(() => NavigateSearch(1))) return new InputListenerResult(Consume: true);
            if (keybindings.Matches(data, "tui.altScreen.searchPrevious") && Consume(() => NavigateSearch(-1))) return new InputListenerResult(Consume: true);
            if (keybindings.Matches(data, "tui.altScreen.searchClose") && Consume(CloseSearch)) return new InputListenerResult(Consume: true);
        }
        if (ShouldDeferViewportInputToOverlay()) return null;
        var viewportHeight = GetPrimaryScrollView().ViewportHeight;
        (string Binding, Action Action)[] scrollBindings =
        [
            ("tui.altScreen.pageUp", () => ScrollBy(-Math.Max(1, viewportHeight - PageScrollOverlap))),
            ("tui.altScreen.pageDown", () => ScrollBy(Math.Max(1, viewportHeight - PageScrollOverlap))),
            ("tui.altScreen.halfPageUp", () => ScrollBy(-Math.Max(1, viewportHeight / 2))),
            ("tui.altScreen.halfPageDown", () => ScrollBy(Math.Max(1, viewportHeight / 2))),
            ("tui.altScreen.lineUp", () => ScrollBy(-1)),
            ("tui.altScreen.lineDown", () => ScrollBy(1)),
            ("tui.altScreen.previousPrompt", () => ScrollToPrompt(-1)),
            ("tui.altScreen.nextPrompt", () => ScrollToPrompt(1)),
            ("tui.altScreen.top", ScrollToTop),
            ("tui.altScreen.bottom", ScrollToBottom),
        ];
        foreach (var (binding, action) in scrollBindings)
        {
            if (keybindings.Matches(data, binding) && Consume(action)) return new InputListenerResult(Consume: true);
        }
        return null;
    }

    private static TuiMouseButton DecodeMouseButton(int button) => (button & 3) switch
    {
        0 => TuiMouseButton.Left,
        1 => TuiMouseButton.Middle,
        2 => TuiMouseButton.Right,
        _ => TuiMouseButton.None,
    };

    private TuiMouseEvent CreateMouseEvent(TuiMouseEventType type, int button, int x, int y, int? wheelDelta = null, int? clickCount = null) => new()
    {
        Type = type,
        Button = type == TuiMouseEventType.Wheel ? TuiMouseButton.None : DecodeMouseButton(button),
        X = x,
        Y = y,
        ScreenX = x,
        ScreenY = y,
        Width = Math.Max(1, Terminal.Columns),
        Height = Math.Max(1, Terminal.Rows),
        Shift = (button & 4) != 0,
        Alt = (button & 8) != 0,
        Ctrl = (button & 16) != 0,
        WheelDelta = wheelDelta,
        ClickCount = clickCount,
    };

    private static readonly Dictionary<Type, bool> DefaultContainerMouse = [];

    private static bool UsesDefaultContainerMouse(IComponent component)
    {
        if (component is not Container) return false;
        var type = component.GetType();
        lock (DefaultContainerMouse)
        {
            if (!DefaultContainerMouse.TryGetValue(type, out var isDefault))
            {
                isDefault = type.GetMethod(nameof(Container.HandleMouse), [typeof(TuiMouseEvent)])?.DeclaringType == typeof(Container);
                DefaultContainerMouse[type] = isDefault;
            }
            return isDefault;
        }
    }

    private TuiMouseDispatchResult? DispatchMouseToLayout(TuiMouseEvent mouseEvent)
    {
        if (_currentLayout is null) return null;
        var visited = new HashSet<IComponent>(ReferenceEqualityComparer.Instance);
        foreach (var box in LayoutEngine.GetBoxesAt(_currentLayout, mouseEvent.ScreenX, mouseEvent.ScreenY))
        {
            if (visited.Contains(box.Component)) continue;
            if (box.Component is ILayoutComponent && UsesDefaultContainerMouse(box.Component)) continue;
            visited.Add(box.Component);
            var result = MouseDispatch.Dispatch(box.Component, mouseEvent with
            {
                X = mouseEvent.ScreenX - box.Rect.X,
                Y = mouseEvent.ScreenY - box.Rect.Y,
                Width = box.Rect.Width,
                Height = box.Rect.Height,
            });
            if (result is not null) return result;
        }
        return null;
    }

    private bool ApplyMouseDispatchResult(TuiMouseEvent mouseEvent, TuiMouseDispatchResult result)
    {
        var focusTarget = ResolveMouseFocusTarget(result.FocusTarget ?? result.Target.Component);
        var focusChanged = result.Focus && !ReferenceEquals(GetFocusedComponent(), focusTarget);
        if (result.Focus) SetFocus(focusTarget);
        if (result.Capture) _mouseCapture = result.Target;
        return result.Render ?? (focusChanged || mouseEvent.Type is TuiMouseEventType.Press or TuiMouseEventType.Click or TuiMouseEventType.Drag or TuiMouseEventType.Wheel);
    }

    private int GetComponentClickCount(TuiMouseDispatchTarget target, int x, int y)
    {
        var now = Environment.TickCount64;
        var count = _lastComponentClick is { } previous && now - previous.Timestamp <= DoubleClickIntervalMs
            && ReferenceEquals(previous.Component, target.Component) && previous.X == x && previous.Y == y
            ? previous.Count % 3 + 1
            : 1;
        _lastComponentClick = (now, count, target.Component, x, y);
        return count;
    }

    private void ClearTextSelection()
    {
        StopSelectionAutoScroll();
        _selectionPressActive = false;
        _selectionAnchor = null;
        _selectionFocus = null;
        _selectionGranularity = Granularity.Character;
        _selectionInitialRange = null;
        _pressedUrl = null;
        _selectionDragged = false;
    }

    private void HandleMouseEvent(SgrMouseEvent raw)
    {
        var isMotion = (raw.Button & 32) != 0;
        var type = raw.Release ? TuiMouseEventType.Release
            : isMotion ? DecodeMouseButton(raw.Button) == TuiMouseButton.None ? TuiMouseEventType.Move : TuiMouseEventType.Drag
            : TuiMouseEventType.Press;
        var mouseEvent = CreateMouseEvent(type, raw.Button, raw.X, raw.Y);

        if ((_mouseCapture ?? _mousePressTarget) is { } target)
        {
            if (_mousePressPoint is { } pressPoint && (raw.X != pressPoint.X || raw.Y != pressPoint.Y))
            {
                _mousePressMoved = true;
                _lastComponentClick = null;
            }
            var render = false;
            if (MouseDispatch.Dispatch(target.Component, MouseDispatch.Retarget(mouseEvent, target)) is { } targetResult) render = ApplyMouseDispatchResult(mouseEvent, targetResult);
            if (raw.Release)
            {
                if (!_mousePressMoved && _mousePressPoint == (raw.X, raw.Y))
                {
                    var clickEvent = CreateMouseEvent(TuiMouseEventType.Click, raw.Button, raw.X, raw.Y, clickCount: GetComponentClickCount(target, raw.X, raw.Y));
                    if (MouseDispatch.Dispatch(target.Component, MouseDispatch.Retarget(clickEvent, target)) is { } clickResult) render = ApplyMouseDispatchResult(clickEvent, clickResult) || render;
                }
                ClearComponentMouseGesture();
            }
            if (render) RequestRender();
            return;
        }

        if (HandleSearchMouseEvent(raw)) return;

        var overlay = DispatchMouseToOverlay(mouseEvent);
        if (!overlay.Hit)
        {
            if (HandleScrollToEndIndicatorMouseEvent(raw)) return;
            var scrollbarHandled = HandleScrollbarMouseEvent(raw);
            if (_scrollbarDrag is null) UpdateScrollbarHover(raw.X, raw.Y);
            if (scrollbarHandled) return;
        }
        else
        {
            StopScrollbarHover();
        }

        var result = overlay.Result ?? (overlay.Hit ? null : DispatchMouseToLayout(mouseEvent));
        if (result is not null)
        {
            var render = ApplyMouseDispatchResult(mouseEvent, result);
            if (type == TuiMouseEventType.Press)
            {
                ClearTextSelection();
                _mousePressTarget = result.Target;
                _mousePressPoint = (raw.X, raw.Y);
                _mousePressMoved = false;
            }
            if (render) RequestRender();
            return;
        }

        if (HandleRightClickPaste(raw)) return;
        HandleSelectionMouseEvent(raw);
    }

    private static WheelEvent? ParseWheelEvent(string data)
    {
        var sgr = SgrMouse().Match(data);
        if (sgr.Success)
        {
            var button = int.Parse(sgr.Groups[1].ValueSpan);
            if ((button & 64) == 0) return null;
            var direction = button & 3;
            if (direction is not (0 or 1)) return null;
            return new WheelEvent(direction == 0 ? -1 : 1, int.Parse(sgr.Groups[2].ValueSpan) - 1, int.Parse(sgr.Groups[3].ValueSpan) - 1, button);
        }
        if (data.Length == 6 && data.StartsWith("\e[M", StringComparison.Ordinal))
        {
            var button = data[3] - 32;
            if ((button & 64) == 0) return null;
            var direction = button & 3;
            if (direction is not (0 or 1)) return null;
            return new WheelEvent(direction == 0 ? -1 : 1, data[4] - 33, data[5] - 33, button);
        }
        return null;
    }

    private int GetWheelScrollLines(int button) => (button & 8) != 0 ? _wheelScrollLines * AltWheelScrollMultiplier : _wheelScrollLines;

    private void RouteWheel(WheelEvent wheel)
    {
        var remaining = wheel.Direction * GetWheelScrollLines(wheel.Button);
        var seen = new HashSet<ScrollView>(ReferenceEqualityComparer.Instance);
        foreach (var scrollView in _currentLayout is null ? [] : LayoutEngine.GetScrollViewsAt(_currentLayout, wheel.X, wheel.Y))
        {
            seen.Add(scrollView);
            remaining = scrollView.ScrollBy(remaining);
            if (remaining == 0 || scrollView.ContainOverscroll) break;
        }
        var primary = GetPrimaryScrollView();
        if (remaining != 0 && !seen.Contains(primary)) primary.ScrollBy(remaining);
        UpdateScrollbarHover(wheel.X, wheel.Y);
        RequestRender();
    }

    private static SgrMouseEvent? ParseSgrMouseEvent(string data)
    {
        var match = SgrMouse().Match(data);
        if (!match.Success) return null;
        return new SgrMouseEvent(int.Parse(match.Groups[1].ValueSpan), int.Parse(match.Groups[2].ValueSpan) - 1, int.Parse(match.Groups[3].ValueSpan) - 1, match.Groups[4].Value == "m");
    }

    private bool HandleRightClickPaste(SgrMouseEvent mouseEvent)
    {
        if (_onRightClickPaste is null || !OperatingSystem.IsWindows()
            || string.Equals(Environment.GetEnvironmentVariable("TERM_PROGRAM"), "vscode", StringComparison.OrdinalIgnoreCase)
            || mouseEvent.Release || mouseEvent.Button != 2)
        {
            return false;
        }
        try
        {
            _onRightClickPaste();
        }
        catch
        {
            // Clipboard paste is best-effort.
        }
        return true;
    }

    private bool HandleScrollToEndIndicatorMouseEvent(SgrMouseEvent mouseEvent)
    {
        if (_scrollToEndIndicatorRect is not { } rect || mouseEvent.Release || (mouseEvent.Button & 32) != 0 || (mouseEvent.Button & 3) != 0) return false;
        if (mouseEvent.Y != rect.Row || mouseEvent.X < rect.Column || mouseEvent.X >= rect.Column + rect.Width) return false;
        ScrollToBottom();
        return true;
    }

    // ----- Scrollbar -----

    private ScrollbarTarget? GetScrollbarTargetAt(int x, int y, bool includeHiddenAuto = false)
    {
        if (HasOverlay() || _currentLayout is null) return null;
        foreach (var scrollView in LayoutEngine.GetScrollViewsAt(_currentLayout, x, y))
        {
            var box = LayoutEngine.GetScrollViewBox(_currentLayout, scrollView);
            if (box is not null && LayoutEngine.GetScrollbarGeometry(box, includeHiddenAuto) is { } geometry
                && x == geometry.Column && y >= geometry.TrackTop && y < geometry.TrackTop + geometry.TrackHeight)
            {
                return new ScrollbarTarget(scrollView, geometry);
            }
        }
        return null;
    }

    private void SetScrollbarHover(ScrollView? scrollView)
    {
        if (ReferenceEquals(scrollView, _scrollbarHover)) return;
        _scrollbarHover?.SetScrollbarActive(false);
        _scrollbarHover = scrollView;
        _scrollbarHover?.SetScrollbarActive(true);
    }

    private void UpdateScrollbarHover(int x, int y) => SetScrollbarHover(GetScrollbarTargetAt(x, y, true)?.ScrollView);

    private void StopScrollbarHover() => SetScrollbarHover(null);

    private static void ScrollScrollbarToPointer(ScrollView scrollView, ScrollbarGeometry geometry, int pointerY, int grabOffset)
    {
        var maxThumbOffset = geometry.TrackHeight - geometry.ThumbHeight;
        var thumbOffset = Math.Max(0, Math.Min(maxThumbOffset, pointerY - geometry.TrackTop - grabOffset));
        var scrollTop = maxThumbOffset == 0 ? 0 : (int)Math.Round((double)thumbOffset / maxThumbOffset * geometry.MaxScrollTop, MidpointRounding.AwayFromZero);
        scrollView.ScrollTo(scrollTop);
    }

    private bool HandleScrollbarMouseEvent(SgrMouseEvent mouseEvent)
    {
        if (_scrollbarDrag is { } drag)
        {
            if (mouseEvent.Release)
            {
                _scrollbarDrag = null;
                return true;
            }
            var box = _currentLayout is null ? null : LayoutEngine.GetScrollViewBox(_currentLayout, drag.ScrollView);
            if (box is not null && LayoutEngine.GetScrollbarGeometry(box) is { } geometry) ScrollScrollbarToPointer(drag.ScrollView, geometry, mouseEvent.Y, drag.GrabOffset);
            return true;
        }

        if (mouseEvent.Release || (mouseEvent.Button & 32) != 0 || (mouseEvent.Button & 3) != 0) return false;
        if (GetScrollbarTargetAt(mouseEvent.X, mouseEvent.Y) is not { } target) return false;
        ClearTextSelection();
        _lastClick = null;
        SetScrollbarHover(target.ScrollView);
        var onThumb = mouseEvent.Y >= target.Geometry.ThumbTop && mouseEvent.Y < target.Geometry.ThumbTop + target.Geometry.ThumbHeight;
        var grabOffset = onThumb ? mouseEvent.Y - target.Geometry.ThumbTop : target.Geometry.ThumbHeight / 2;
        if (!onThumb) ScrollScrollbarToPointer(target.ScrollView, target.Geometry, mouseEvent.Y, grabOffset);
        _scrollbarDrag = new ScrollbarDrag(target.ScrollView, grabOffset);
        return true;
    }

    // ----- Selection -----

    private SelectionPoint? GetScrollSelectionPoint(ScrollView scrollView, int x, int y)
    {
        if (_currentLayout is null || LayoutEngine.GetScrollViewBox(_currentLayout, scrollView) is not { } box) return null;
        if (box.Rect.Height <= 0 || box.Clip.Height <= 0) return null;
        var visibleTop = Math.Max(0, Math.Max(box.Rect.Y, box.Clip.Y));
        var visibleBottom = Math.Min(Terminal.Rows - 1, Math.Min(box.Rect.Y + box.Rect.Height - 1, box.Clip.Y + box.Clip.Height - 1));
        if (visibleBottom < visibleTop) return null;
        var pointerRow = Math.Max(visibleTop, Math.Min(visibleBottom, y));
        var maxContentRow = Math.Max(0, (box.ScrollContentLines?.Count ?? 1) - 1);
        return new SelectionPoint(
            Math.Max(0, Math.Min(maxContentRow, scrollView.ScrollTop + pointerRow - box.Rect.Y)),
            Math.Max(0, Math.Min(box.Rect.Width - 1, x - box.Rect.X)),
            scrollView);
    }

    private SelectionPoint GetSelectionPoint(SgrMouseEvent mouseEvent, ScrollView? scrollView, LayoutRect? region)
    {
        if (scrollView is not null && GetScrollSelectionPoint(scrollView, mouseEvent.X, mouseEvent.Y) is { } point) return point;
        if (region is { Width: > 0, Height: > 0 } r)
        {
            // Iris: keep the selection inside the section where it started. Dragging past its top or bottom edge
            // extends the selection to the start or end of the section.
            if (mouseEvent.Y < r.Y) return new SelectionPoint(r.Y, r.X, Region: r);
            if (mouseEvent.Y >= r.Y + r.Height) return new SelectionPoint(r.Y + r.Height - 1, r.X + r.Width, Boundary: true, Region: r);
            return new SelectionPoint(mouseEvent.Y, Math.Max(r.X, Math.Min(r.X + r.Width - 1, mouseEvent.X)), Region: r);
        }
        return new SelectionPoint(Math.Max(0, Math.Min(Terminal.Rows - 1, mouseEvent.Y)), Math.Max(0, Math.Min(Terminal.Columns - 1, mouseEvent.X)));
    }

    /// <summary>Iris: the section (overlay or leaf layout box) a screen-level selection is confined to.</summary>
    private LayoutRect? GetSelectionRegionAt(int x, int y)
    {
        if (GetOverlayRectAt(x, y) is { } overlay) return new LayoutRect(overlay.Col, overlay.Row, overlay.Width, overlay.Height);
        if (_currentLayout is null) return null;
        var leaf = LayoutEngine.GetBoxesAt(_currentLayout, x, y).FirstOrDefault(box => box.Lines is not null);
        return leaf?.Clip;
    }

    private string GetSelectionSourceLine(SelectionPoint point)
    {
        if (point.ScrollView is not null && _currentLayout is not null
            && LayoutEngine.GetScrollViewBox(_currentLayout, point.ScrollView)?.ScrollContentLines is { } lines)
        {
            return point.Row < lines.Count ? lines[point.Row] : "";
        }
        return point.Row < _previousScreen.Count ? _previousScreen[point.Row] : "";
    }

    private SelectionRange? GetWordSelection(SelectionPoint point)
    {
        var line = TextUtils.StripTerminalSequences(GetSelectionSourceLine(point));
        var segments = new List<(int Start, int End, bool Selectable, bool Joiner)>();
        var start = 0;
        foreach (var segment in WordSegmenter.Segment(line))
        {
            var end = start + TextUtils.VisibleWidth(segment.Segment);
            var joiner = segment.Segment is "/" or "-";
            segments.Add((start, end, segment.IsWordLike || joiner, joiner));
            start = end;
        }
        var clicked = segments.FindIndex(s => point.Col >= s.Start && point.Col < s.End);
        if (clicked < 0) return null;
        static bool CanJoin((int, int, bool Selectable, bool Joiner) left, (int, int, bool Selectable, bool Joiner) right) =>
            left.Selectable && right.Selectable && (left.Joiner || right.Joiner);
        var selectionStart = segments[clicked].Start;
        var selectionEnd = segments[clicked].End;
        for (var index = clicked; index > 0 && CanJoin(segments[index - 1], segments[index]); index--) selectionStart = segments[index - 1].Start;
        for (var index = clicked; index < segments.Count - 1 && CanJoin(segments[index], segments[index + 1]); index++) selectionEnd = segments[index + 1].End;
        if (point.Region is { } region)
        {
            selectionStart = Math.Max(region.X, selectionStart);
            selectionEnd = Math.Min(region.X + region.Width, selectionEnd);
        }
        return new SelectionRange(point with { Col = selectionStart }, point with { Col = selectionEnd, Boundary = true });
    }

    private SelectionRange GetLineSelection(SelectionPoint point)
    {
        var lineWidth = TextUtils.VisibleWidth(GetSelectionSourceLine(point));
        return point.Region is { } region
            ? new SelectionRange(point with { Col = region.X }, point with { Col = Math.Min(lineWidth, region.X + region.Width), Boundary = true })
            : new SelectionRange(point with { Col = 0 }, point with { Col = lineWidth, Boundary = true });
    }

    private void UpdateSelectionFocus(SelectionPoint point)
    {
        if (_selectionGranularity == Granularity.Character || _selectionInitialRange is not { } initial)
        {
            _selectionFocus = point;
            return;
        }
        var range = _selectionGranularity == Granularity.Word ? GetWordSelection(point) : GetLineSelection(point);
        if (range is null) return;
        var targetBeforeInitial = range.Start.Row < initial.Start.Row || (range.Start.Row == initial.Start.Row && range.Start.Col < initial.Start.Col);
        if (targetBeforeInitial)
        {
            _selectionAnchor = initial.End;
            _selectionFocus = range.Start;
        }
        else
        {
            _selectionAnchor = initial.Start;
            _selectionFocus = range.End;
        }
    }

    private int GetClickCount(SelectionPoint point, SelectionRange? word)
    {
        var now = Environment.TickCount64;
        var count = word is not null && _lastClick is { } previous && now - previous.Timestamp <= DoubleClickIntervalMs
            && previous.Row == point.Row && ReferenceEquals(previous.ScrollView, point.ScrollView)
            && previous.WordStart == word.Start.Col && previous.WordEnd == word.End.Col
            ? previous.Count % 3 + 1
            : 1;
        _lastClick = word is null ? null : new ClickTarget(now, count, point.Row, point.ScrollView, word.Start.Col, word.End.Col);
        return count;
    }

    private void UpdateSelectionAutoScroll(SgrMouseEvent mouseEvent)
    {
        if (_selectionAnchor?.ScrollView is not { } scrollView || _currentLayout is null
            || LayoutEngine.GetScrollViewBox(_currentLayout, scrollView) is not { } box || box.Rect.Height <= 0 || box.Clip.Height <= 0)
        {
            StopSelectionAutoScroll();
            return;
        }
        var visibleTop = Math.Max(0, Math.Max(box.Rect.Y, box.Clip.Y));
        var visibleBottom = Math.Min(Terminal.Rows - 1, Math.Min(box.Rect.Y + box.Rect.Height - 1, box.Clip.Y + box.Clip.Height - 1));
        _selectionDragPointer = (mouseEvent.X, mouseEvent.Y);
        _selectionAutoScrollDirection = mouseEvent.Y <= visibleTop ? -1 : mouseEvent.Y >= visibleBottom ? 1 : 0;
        if (_selectionAutoScrollDirection == 0)
        {
            StopSelectionAutoScroll();
            return;
        }
        _selectionAutoScrollTimer ??= Dispatcher.SetInterval(AutoScrollSelection, 50);
    }

    private void AutoScrollSelection()
    {
        if (_selectionAnchor?.ScrollView is not { } scrollView || _selectionDragPointer is not { } pointer || _selectionAutoScrollDirection == 0)
        {
            StopSelectionAutoScroll();
            return;
        }
        var remaining = scrollView.ScrollBy(_selectionAutoScrollDirection);
        if (remaining == _selectionAutoScrollDirection)
        {
            StopSelectionAutoScroll();
            return;
        }
        if (GetScrollSelectionPoint(scrollView, pointer.X, pointer.Y) is { } point) UpdateSelectionFocus(point);
        RequestRender();
    }

    private void StopSelectionAutoScroll()
    {
        _selectionAutoScrollTimer?.Dispose();
        _selectionAutoScrollTimer = null;
        _selectionAutoScrollDirection = 0;
        _selectionDragPointer = null;
    }

    private void HandleSelectionMouseEvent(SgrMouseEvent mouseEvent)
    {
        var button = mouseEvent.Button & 3;
        if (button != 0 && !(mouseEvent.Release && button == 3)) return;
        var point = GetSelectionPoint(mouseEvent, _selectionAnchor?.ScrollView, _selectionAnchor?.Region);
        if (mouseEvent.Release)
        {
            if (!_selectionPressActive) return;
            _selectionPressActive = false;
            StopSelectionAutoScroll();
            if (_selectionAnchor is not { } anchor) return;
            UpdateSelectionFocus(point);
            var isClick = !_selectionDragged && ReferenceEquals(anchor.ScrollView, point.ScrollView) && anchor.Row == point.Row && anchor.Col == point.Col;
            var clickedUrl = isClick ? _pressedUrl : null;
            _pressedUrl = null;
            if (clickedUrl is not null && _openUrl is not null)
            {
                _selectionAnchor = null;
                _selectionFocus = null;
                try
                {
                    _openUrl(clickedUrl);
                }
                catch
                {
                    // URL activation is best-effort.
                }
                RequestRender();
                return;
            }
            if (isClick)
            {
                var clickEvent = CreateMouseEvent(TuiMouseEventType.Click, mouseEvent.Button, mouseEvent.X, mouseEvent.Y, clickCount: _lastClick?.Count ?? 1);
                var overlay = DispatchMouseToOverlay(clickEvent);
                var result = overlay.Result ?? (overlay.Hit ? null : DispatchMouseToLayout(clickEvent));
                if (result is not null)
                {
                    var render = ApplyMouseDispatchResult(clickEvent, result);
                    ClearTextSelection();
                    if (render) RequestRender();
                    return;
                }
            }
            if (CopyOnSelect) _ = CopySelectionToClipboardAsync();
            RequestRender();
            return;
        }
        if ((mouseEvent.Button & 32) != 0)
        {
            if (!_selectionPressActive || _selectionAnchor is null) return;
            _selectionDragged = true;
            _lastClick = null;
            _pressedUrl = null;
            UpdateSelectionFocus(point);
            UpdateSelectionAutoScroll(mouseEvent);
            RequestRender();
            return;
        }
        StopSelectionAutoScroll();
        _selectionPressActive = true;
        var scrollView = !HasOverlay() && _currentLayout is not null ? LayoutEngine.GetScrollViewsAt(_currentLayout, mouseEvent.X, mouseEvent.Y).FirstOrDefault() : null;
        var region = scrollView is null ? GetSelectionRegionAt(mouseEvent.X, mouseEvent.Y) : null;
        var anchorPoint = GetSelectionPoint(mouseEvent, scrollView, region);
        var word = GetWordSelection(anchorPoint);
        var clickCount = GetClickCount(anchorPoint, word);
        var range = clickCount == 2 ? word : clickCount == 3 ? GetLineSelection(anchorPoint) : null;
        _selectionGranularity = range is null ? Granularity.Character : clickCount == 2 ? Granularity.Word : Granularity.Line;
        _selectionInitialRange = range;
        _selectionAnchor = range?.Start ?? anchorPoint;
        _selectionFocus = range?.End ?? anchorPoint;
        _selectionDragged = false;
        var screenRow = Math.Max(0, Math.Min(Terminal.Rows - 1, mouseEvent.Y));
        _pressedUrl = range is null && screenRow < _previousScreen.Count
            ? TextUtils.GetOsc8LinkAtColumn(_previousScreen[screenRow], Math.Max(0, Math.Min(Terminal.Columns - 1, mouseEvent.X)))
            : null;
        RequestRender();
    }

    private SelectionRange? GetSelectionBounds()
    {
        if (_selectionAnchor is not { } anchor || _selectionFocus is not { } focus) return null;
        if (!ReferenceEquals(anchor.ScrollView, focus.ScrollView)) return null;
        if (anchor.Row == focus.Row && anchor.Col == focus.Col) return null;
        var anchorBeforeFocus = anchor.Row < focus.Row || (anchor.Row == focus.Row && anchor.Col < focus.Col);
        return anchorBeforeFocus ? new SelectionRange(anchor, focus) : new SelectionRange(focus, anchor);
    }

    private static (int Start, int End) GetSelectionColumns(string line, int row, SelectionRange selection, int minColumn = 0, int? maxColumn = null)
    {
        var lineWidth = TextUtils.VisibleWidth(line);
        var max = maxColumn ?? lineWidth;
        var start = Math.Max(0, minColumn);
        var end = Math.Min(lineWidth, max);
        if (row == selection.Start.Row) start = TextUtils.GetGraphemeCellRange(line, selection.Start.Col)?.Start ?? Math.Min(selection.Start.Col, lineWidth);
        if (row == selection.End.Row)
        {
            end = selection.End.Boundary
                ? Math.Min(selection.End.Col, lineWidth)
                : TextUtils.GetGraphemeCellRange(line, selection.End.Col)?.End ?? Math.Min(selection.End.Col + 1, lineWidth);
        }
        return (Math.Max(minColumn, start), Math.Min(max, end));
    }

    private string? GetActiveSelectionText()
    {
        if (GetSelectionBounds() is not { } selection) return null;
        IReadOnlyList<string> sourceLines = _previousScreen;
        if (selection.Start.ScrollView is { } scrollView)
        {
            if (_currentLayout is null || LayoutEngine.GetScrollViewBox(_currentLayout, scrollView)?.ScrollContentLines is not { } contentLines) return null;
            sourceLines = contentLines;
        }
        var region = selection.Start.Region;
        var lines = new List<string>();
        for (var row = selection.Start.Row; row <= selection.End.Row; row++)
        {
            var line = row < sourceLines.Count ? sourceLines[row] : "";
            var (start, end) = region is { } r ? GetSelectionColumns(line, row, selection, r.X, r.X + r.Width) : GetSelectionColumns(line, row, selection);
            lines.Add(TextUtils.StripTerminalSequences(TextUtils.SliceByColumn(line, start, Math.Max(0, end - start), true)).TrimEnd());
        }
        var text = string.Join("\n", lines);
        return text.Length == 0 ? null : text;
    }

    private async Task<bool> CopySelectionToClipboardAsync()
    {
        var text = GetActiveSelectionText();
        return text is not null && await CopyTextToClipboardAsync(text);
    }

    private async Task<bool> CopyTextToClipboardAsync(string text)
    {
        if (_copySelection is not null)
        {
            var ok = await _copySelection(text);
            Flash(ok ? "Copied!" : "Copy failed");
            return ok;
        }
        Terminal.Write($"\e]52;c;{Convert.ToBase64String(Encoding.UTF8.GetBytes(text))}\a");
        Flash("Copied!");
        return true;
    }

    // ----- Compositing -----

    private string ApplySearchTextHighlight(string text, bool current)
    {
        var style = current ? _searchCurrentMatchStyle : _searchMatchStyle;
        var result = new StringBuilder();
        var plainStart = 0;
        var index = 0;
        while (index < text.Length)
        {
            if (TextUtils.ExtractAnsiCode(text, index) is not { } ansi)
            {
                index++;
                continue;
            }
            if (index > plainStart) result.Append(style(text[plainStart..index]));
            result.Append(ansi.Code);
            index += ansi.Length;
            plainStart = index;
        }
        if (plainStart < text.Length) result.Append(style(text[plainStart..]));
        return result.ToString();
    }

    private List<string> ApplySearchHighlights(List<string> screen, LayoutFrame layout)
    {
        if (_activeSearch is not { SelectedIndex: >= 0 } search || search.Matches.Count == 0) return screen;
        var scrollView = layout.PrimaryScrollView ?? _implicitScrollView;
        if (LayoutEngine.GetScrollViewBox(layout, scrollView) is not { } box) return screen;

        var rangesByRow = new Dictionary<int, List<(int StartCol, int EndCol, bool Current)>>();
        var scrollbarColumn = LayoutEngine.GetScrollbarGeometry(box)?.Column;
        var minRow = Math.Max(0, Math.Max(box.Rect.Y, box.Clip.Y));
        var maxRow = Math.Min(screen.Count, Math.Min(box.Rect.Y + box.Rect.Height, box.Clip.Y + box.Clip.Height));
        var minColumn = Math.Max(0, Math.Max(box.Rect.X, box.Clip.X));
        var maxColumn = Math.Min(Math.Min(Terminal.Columns, box.Rect.X + box.Rect.Width), Math.Min(box.Clip.X + box.Clip.Width, scrollbarColumn ?? int.MaxValue));
        var minContentRow = scrollView.ScrollTop + minRow - box.Rect.Y;
        var maxContentRow = scrollView.ScrollTop + maxRow - box.Rect.Y - 1;
        int low = 0, high = search.Matches.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (search.Matches[middle].Segments[^1].Row < minContentRow) low = middle + 1;
            else high = middle;
        }
        for (var matchIndex = low; matchIndex < search.Matches.Count; matchIndex++)
        {
            var match = search.Matches[matchIndex];
            if (match.Segments[0].Row > maxContentRow) break;
            foreach (var segment in match.Segments)
            {
                var row = box.Rect.Y + segment.Row - scrollView.ScrollTop;
                if (row < minRow || row >= maxRow) continue;
                var startCol = Math.Max(minColumn, box.Rect.X + segment.StartCol);
                var endCol = Math.Min(maxColumn, box.Rect.X + segment.EndCol);
                if (endCol <= startCol) continue;
                if (!rangesByRow.TryGetValue(row, out var ranges)) rangesByRow[row] = ranges = [];
                ranges.Add((startCol, endCol, matchIndex == search.SelectedIndex));
            }
        }

        var result = new List<string>(screen);
        foreach (var (row, ranges) in rangesByRow)
        {
            var line = result[row];
            if (TerminalImage.IsImageLine(line)) continue;
            var lineWidth = TextUtils.VisibleWidth(line);
            foreach (var range in ranges.OrderByDescending(r => r.StartCol))
            {
                var startCol = Math.Min(range.StartCol, lineWidth);
                var endCol = Math.Min(range.EndCol, lineWidth);
                if (endCol <= startCol) continue;
                var before = TextUtils.SliceByColumn(line, 0, startCol, true);
                var highlighted = TextUtils.SliceByColumn(line, startCol, endCol - startCol, true);
                var after = TextUtils.SliceByColumn(line, endCol, Math.Max(0, lineWidth - endCol), true);
                line = before + ApplySearchTextHighlight(highlighted, range.Current) + after;
            }
            result[row] = line;
        }
        return result;
    }

    private static string ApplySelectionHighlight(string text)
    {
        var result = new StringBuilder("\e[7m");
        var index = 0;
        while (index < text.Length)
        {
            if (TextUtils.ExtractAnsiCode(text, index) is not { } ansi)
            {
                result.Append(text[index]);
                index++;
                continue;
            }
            result.Append(ansi.Code);
            if (ansi.Code.EndsWith('m')) result.Append("\e[7m");
            index += ansi.Length;
        }
        return result.Append("\e[27m").ToString();
    }

    private List<string> ApplySelection(List<string> screen, LayoutFrame? layout)
    {
        if (GetSelectionBounds() is not { } selection) return screen;
        var screenSelection = selection;
        var minRow = 0;
        var maxRow = screen.Count - 1;
        var minColumn = 0;
        var maxColumn = Terminal.Columns;
        if (selection.Start.ScrollView is { } scrollView)
        {
            if (layout is null || LayoutEngine.GetScrollViewBox(layout, scrollView) is not { } box) return screen;
            minRow = Math.Max(0, Math.Max(box.Rect.Y, box.Clip.Y));
            maxRow = Math.Min(screen.Count - 1, Math.Min(box.Rect.Y + box.Rect.Height - 1, box.Clip.Y + box.Clip.Height - 1));
            minColumn = Math.Max(0, Math.Max(box.Rect.X, box.Clip.X));
            maxColumn = Math.Min(Terminal.Columns, Math.Min(box.Rect.X + box.Rect.Width, box.Clip.X + box.Clip.Width));
            screenSelection = new SelectionRange(
                selection.Start with { Row = box.Rect.Y + selection.Start.Row - scrollView.ScrollTop, Col = box.Rect.X + selection.Start.Col },
                selection.End with { Row = box.Rect.Y + selection.End.Row - scrollView.ScrollTop, Col = box.Rect.X + selection.End.Col });
        }
        else if (selection.Start.Region is { } region)
        {
            minRow = Math.Max(0, region.Y);
            maxRow = Math.Min(screen.Count - 1, region.Y + region.Height - 1);
            minColumn = Math.Max(0, region.X);
            maxColumn = Math.Min(Terminal.Columns, region.X + region.Width);
        }
        var result = new List<string>(screen.Count);
        for (var row = 0; row < screen.Count; row++)
        {
            var line = screen[row];
            if (row < minRow || row > maxRow || row < screenSelection.Start.Row || row > screenSelection.End.Row || TerminalImage.IsImageLine(line))
            {
                result.Add(line);
                continue;
            }
            var lineWidth = TextUtils.VisibleWidth(line);
            var (start, end) = GetSelectionColumns(line, row, screenSelection, minColumn, maxColumn);
            if (end <= start)
            {
                result.Add(line);
                continue;
            }
            var before = TextUtils.SliceByColumn(line, 0, start, true);
            var selected = TextUtils.SliceByColumn(line, start, end - start, true);
            var after = TextUtils.SliceByColumn(line, end, Math.Max(0, lineWidth - end), true);
            result.Add(before + ApplySelectionHighlight(selected) + after);
        }
        return result;
    }

    private static bool IsMouseSequence(string data) => SgrMouse().IsMatch(data) || (data.Length == 6 && data.StartsWith("\e[M", StringComparison.Ordinal));

    private List<string> CompositeScrollToEndIndicator(List<string> screen, LayoutFrame layout, int width)
    {
        _scrollToEndIndicatorRect = null;
        var scrollView = layout.PrimaryScrollView ?? _implicitScrollView;
        if (_scrollToEndIndicator is null || !scrollView.FollowEnd || scrollView.IsFollowingEnd) return screen;
        var box = LayoutEngine.GetScrollViewBox(layout, scrollView);
        if (box?.Clip is not { Width: > 0, Height: > 0 } clip) return screen;
        var row = clip.Y + clip.Height - 1;
        if (row >= screen.Count || TerminalImage.IsImageLine(screen[row])) return screen;
        var scrollbarColumn = LayoutEngine.GetScrollbarGeometry(box)?.Column;
        var availableWidth = Math.Max(0, (scrollbarColumn ?? clip.X + clip.Width) - clip.X);
        var text = TextUtils.TruncateToWidth(_scrollToEndIndicator(), availableWidth, "");
        var textWidth = TextUtils.VisibleWidth(text);
        if (textWidth == 0) return screen;
        var column = clip.X + (availableWidth - textWidth) / 2;
        var result = new List<string>(screen);
        result[row] = CompositeLine(result[row], text, column, textWidth, width);
        _scrollToEndIndicatorRect = (row, column, textWidth);
        return result;
    }

    private List<string> CompositeFlashes(List<string> screen, int width, int height)
    {
        var flashLines = _flashes.Render(width);
        if (flashLines.Count == 0) return screen;
        if (flashLines.Count > height) flashLines = flashLines[^height..];
        var result = new List<string>(screen);
        while (result.Count < height) result.Add("");
        for (var row = 0; row < flashLines.Count; row++)
        {
            var flashWidth = TextUtils.VisibleWidth(flashLines[row]);
            if (flashWidth == 0) continue;
            result[row] = CompositeLine(result[row], flashLines[row], width - flashWidth, flashWidth, width);
        }
        return result;
    }

    protected override void DoRender()
    {
        if (Stopped || !_altScreenActive) return;
        var width = Math.Max(1, Terminal.Columns);
        var height = Math.Max(1, Terminal.Rows);
        IComponent root = _layoutRoot ?? _implicitScrollView;
        var nextLayout = LayoutEngine.RenderFrame(root, width, height, () => RequestRender());
        if (RefreshSearch(nextLayout)) nextLayout = LayoutEngine.RenderFrame(root, width, height, () => RequestRender());
        var screen = nextLayout.Lines.Select(line => LayoutEngine.Osc133ZonePrefix().Replace(line, "")).ToList();
        screen = ApplySearchHighlights(screen, nextLayout);
        screen = CompositeScrollToEndIndicator(screen, nextLayout, width);
        screen = CompositeOverlays(screen, width, height);
        if (screen.Count > height) screen = screen[^height..];
        screen = ApplySelection(screen, nextLayout);
        screen = CompositeFlashes(screen, width, height);

        var cursorPos = ExtractCursorPosition(screen, height);
        screen = ApplyLineResets(screen)
            .Select(line => TerminalImage.IsImageLine(line) || TextUtils.VisibleWidth(line) <= width ? line : TextUtils.SliceByColumn(line, 0, width, true))
            .ToList();

        var fullRedraw = _previousScreen.Count == 0 || _previousScreenWidth != width || _previousScreenHeight != height;
        var buffer = new StringBuilder(BeginSynchronizedOutput);
        if (fullRedraw)
        {
            FullRedrawCount++;
            buffer.Append("\e[2J");
        }
        for (var row = 0; row < height; row++)
        {
            var line = row < screen.Count ? screen[row] : "";
            if (!fullRedraw && row < _previousScreen.Count && line == _previousScreen[row]) continue;
            buffer.Append($"\e[{row + 1};1H\e[2K").Append(line);
        }
        if (cursorPos is { } cursor)
        {
            buffer.Append($"\e[{cursor.Row + 1};{Math.Min(width, cursor.Col) + 1}H");
            buffer.Append(GetShowHardwareCursor() ? "\e[?25h" : "\e[?25l");
        }
        else
        {
            buffer.Append("\e[?25l");
        }
        buffer.Append(EndSynchronizedOutput);
        Terminal.Write(buffer.ToString());

        _previousScreen = screen;
        _previousScreenWidth = width;
        _previousScreenHeight = height;
        _currentLayout = nextLayout;
    }
}
