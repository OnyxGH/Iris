using Iris.Tui.Layout;

namespace Iris.Tui.Components;

public enum ScrollViewScrollbar
{
    Hidden,
    Auto,
    Always,
}

public sealed class ScrollViewOptions
{
    public bool FollowEnd { get; init; }
    public bool Primary { get; init; }

    /// <summary>"chain" passes leftover wheel scrolling to outer views; "contain" stops it.</summary>
    public bool ContainOverscroll { get; init; }

    public ScrollViewScrollbar Scrollbar { get; init; } = ScrollViewScrollbar.Hidden;
    public Func<string, string>? ScrollbarTrackStyle { get; init; }
    public Func<string, string>? ScrollbarThumbStyle { get; init; }
    public int ScrollbarHideDelayMs { get; init; } = 1000;
}

/// <summary>Vertical scroll viewport for fullscreen layouts. Port of pi-tui ScrollView.</summary>
public sealed class ScrollView : Container, ILayoutComponent
{
    private readonly IComponent _child;
    private readonly int _scrollbarHideDelayMs;
    private int _contentHeight;
    private bool _followSuppressedAtEnd;
    private Action? _requestRender;
    private bool _transientScrollbarVisible;
    private IDisposable? _scrollbarHideTimer;

    public ScrollView(IComponent component, ScrollViewOptions? options = null)
    {
        options ??= new ScrollViewOptions();
        _child = component;
        Children.Add(component);
        FollowEnd = options.FollowEnd;
        IsFollowingEnd = FollowEnd;
        Primary = options.Primary;
        ContainOverscroll = options.ContainOverscroll;
        Scrollbar = options.Scrollbar;
        ScrollbarTrackStyle = options.ScrollbarTrackStyle ?? (text => $"\e[90m{text}\e[39m");
        ScrollbarThumbStyle = options.ScrollbarThumbStyle ?? (text => $"\e[37m{text}\e[39m");
        _scrollbarHideDelayMs = Math.Max(0, options.ScrollbarHideDelayMs);
    }

    public bool FollowEnd { get; }
    public bool Primary { get; }
    public bool ContainOverscroll { get; }
    public Func<string, string> ScrollbarTrackStyle { get; }
    public Func<string, string> ScrollbarThumbStyle { get; }
    public int ScrollTop { get; private set; }
    public bool IsFollowingEnd { get; private set; }
    public int ViewportHeight { get; private set; }
    public ScrollViewScrollbar Scrollbar { get; private set; }
    public bool IsScrollbarActive { get; private set; }

    public bool IsScrollbarVisible => Scrollbar switch
    {
        ScrollViewScrollbar.Always => ViewportHeight > 0,
        ScrollViewScrollbar.Auto => _contentHeight > ViewportHeight && _transientScrollbarVisible,
        _ => false,
    };

    public void SetScrollbar(ScrollViewScrollbar scrollbar)
    {
        if (scrollbar == Scrollbar) return;
        Scrollbar = scrollbar;
        if (scrollbar != ScrollViewScrollbar.Auto) HideTransientScrollbar();
        else if (IsScrollbarActive) MarkScrollbarActivity();
        _requestRender?.Invoke();
    }

    public int GetContentWidth(int width) => Scrollbar == ScrollViewScrollbar.Always && width > 1 ? width - 1 : width;

    private void MarkScrollbarActivity()
    {
        if (Scrollbar != ScrollViewScrollbar.Auto || _contentHeight <= ViewportHeight) return;
        _transientScrollbarVisible = true;
        _scrollbarHideTimer?.Dispose();
        _scrollbarHideTimer = null;
        if (IsScrollbarActive || UiDispatcher.Current is not { } dispatcher) return;
        _scrollbarHideTimer = dispatcher.SetTimeout(() =>
        {
            _scrollbarHideTimer = null;
            _transientScrollbarVisible = false;
            _requestRender?.Invoke();
        }, _scrollbarHideDelayMs);
    }

    private void HideTransientScrollbar()
    {
        _transientScrollbarVisible = false;
        _scrollbarHideTimer?.Dispose();
        _scrollbarHideTimer = null;
    }

    public void SetScrollbarActive(bool active)
    {
        if (active == IsScrollbarActive) return;
        IsScrollbarActive = active;
        MarkScrollbarActivity();
        _requestRender?.Invoke();
    }

    public void ScrollTo(int scrollTop, bool disableFollow = false)
    {
        var maxScrollTop = Math.Max(0, _contentHeight - ViewportHeight);
        var next = Math.Max(0, Math.Min(maxScrollTop, scrollTop));
        var nextSuppressed = disableFollow && next == maxScrollTop;
        var nextFollowing = !nextSuppressed && FollowEnd && next == maxScrollTop;
        if (next == ScrollTop && nextFollowing == IsFollowingEnd && nextSuppressed == _followSuppressedAtEnd) return;
        var moved = next != ScrollTop;
        ScrollTop = next;
        IsFollowingEnd = nextFollowing;
        _followSuppressedAtEnd = nextSuppressed;
        if (moved) MarkScrollbarActivity();
        _requestRender?.Invoke();
    }

    /// <summary>Scroll by lines; returns the part that could not be applied.</summary>
    public int ScrollBy(int lines)
    {
        if (lines == 0) return 0;
        var maxScrollTop = Math.Max(0, _contentHeight - ViewportHeight);
        var start = IsFollowingEnd ? maxScrollTop : ScrollTop;
        var next = Math.Max(0, Math.Min(maxScrollTop, start + lines));
        var moved = next - start;
        var wasFollowing = IsFollowingEnd;
        ScrollTop = next;
        IsFollowingEnd = FollowEnd && next == maxScrollTop;
        _followSuppressedAtEnd = false;
        if (moved != 0) MarkScrollbarActivity();
        if (moved != 0 || IsFollowingEnd != wasFollowing) _requestRender?.Invoke();
        return lines - moved;
    }

    public void ScrollToStart()
    {
        var following = FollowEnd && _contentHeight <= ViewportHeight;
        var changed = ScrollTop != 0 || IsFollowingEnd != following;
        ScrollTop = 0;
        IsFollowingEnd = following;
        _followSuppressedAtEnd = false;
        if (!changed) return;
        MarkScrollbarActivity();
        _requestRender?.Invoke();
    }

    public void ScrollToEnd()
    {
        var next = Math.Max(0, _contentHeight - ViewportHeight);
        var changed = ScrollTop != next || IsFollowingEnd != FollowEnd;
        ScrollTop = next;
        IsFollowingEnd = FollowEnd;
        _followSuppressedAtEnd = false;
        if (!changed) return;
        MarkScrollbarActivity();
        _requestRender?.Invoke();
    }

    public void UpdateLayout(int contentHeight, int viewportHeight, Action requestRender)
    {
        _contentHeight = Math.Max(0, contentHeight);
        ViewportHeight = Math.Max(0, viewportHeight);
        _requestRender = requestRender;
        var maxScrollTop = Math.Max(0, _contentHeight - ViewportHeight);
        ScrollTop = IsFollowingEnd ? maxScrollTop : Math.Max(0, Math.Min(ScrollTop, maxScrollTop));
        if (ScrollTop < maxScrollTop) _followSuppressedAtEnd = false;
        if (FollowEnd && ScrollTop == maxScrollTop && !_followSuppressedAtEnd) IsFollowingEnd = true;
        if (_contentHeight <= ViewportHeight) HideTransientScrollbar();
    }

    public override void AddChild(IComponent component) => throw new InvalidOperationException("ScrollView has exactly one child");

    public override void RemoveChild(IComponent component) => throw new InvalidOperationException("ScrollView child cannot be removed");

    public override void Clear() => throw new InvalidOperationException("ScrollView child cannot be cleared");

    public override List<string> Render(int width)
    {
        var contentWidth = GetContentWidth(width);
        var lines = _child.Render(contentWidth);
        return contentWidth == width ? lines : lines.Select(line => line + " ").ToList();
    }

    public LayoutNode GetLayoutNode() => new ScrollLayoutNode(_child, this);
}
