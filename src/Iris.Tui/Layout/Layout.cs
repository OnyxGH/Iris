using System.Text.RegularExpressions;
using Iris.Tui.Components;

namespace Iris.Tui.Layout;

public readonly record struct LayoutRect(int X, int Y, int Width, int Height)
{
    public bool Contains(int x, int y) => x >= X && x < X + Width && y >= Y && y < Y + Height;

    public static LayoutRect Intersect(LayoutRect a, LayoutRect b)
    {
        var x = Math.Max(a.X, b.X);
        var y = Math.Max(a.Y, b.Y);
        var right = Math.Min(a.X + a.Width, b.X + b.Width);
        var bottom = Math.Min(a.Y + a.Height, b.Y + b.Height);
        return new LayoutRect(x, y, Math.Max(0, right - x), Math.Max(0, bottom - y));
    }
}

public sealed class LayoutBox
{
    public required IComponent Component { get; init; }
    public LayoutRect Rect { get; set; }
    public LayoutRect Clip { get; set; }
    public List<LayoutBox> Children { get; } = [];
    public LayoutBox? Parent { get; set; }
    public IReadOnlyList<string>? Lines { get; init; }
    public int LineOffset { get; init; }
    public ScrollView? ScrollView { get; init; }
    public IReadOnlyList<string>? ScrollContentLines { get; init; }
    public int Layer { get; init; }
}

public sealed class LayoutFrame
{
    public required LayoutBox Root { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required List<string> Lines { get; init; }
    public ScrollView? PrimaryScrollView { get; init; }
}

public sealed record ScrollbarGeometry(int Column, int TrackTop, int TrackHeight, int ThumbTop, int ThumbHeight, int MaxScrollTop);

/// <summary>
/// Fullscreen layout: stacks, scroll views and leaf components rendered into a fixed-size frame. Port of pi-tui layout.ts.
/// Iris: Kitty image cropping at clip edges is not ported (images are disabled while the alternate screen is active).
/// </summary>
public static partial class LayoutEngine
{
    [GeneratedRegex(@"^(?:\e\]133;[ABC](?:\a|\e\\))+")]
    internal static partial Regex Osc133ZonePrefix();

    private sealed class Context
    {
        public required LayoutViewport Viewport { get; init; }
        public Dictionary<IComponent, Dictionary<int, List<string>>> RenderCache { get; } = new(ReferenceEqualityComparer.Instance);
        public required Action RequestRender { get; init; }
        public ScrollView? PrimaryScrollView { get; set; }
    }

    private static List<string> RenderCached(Context context, IComponent component, int width)
    {
        var safeWidth = Math.Max(1, width);
        if (!context.RenderCache.TryGetValue(component, out var widths))
        {
            widths = [];
            context.RenderCache[component] = widths;
        }
        if (!widths.TryGetValue(safeWidth, out var lines))
        {
            lines = component.Render(safeWidth);
            widths[safeWidth] = lines;
        }
        return lines;
    }

    private static int MeasureHeight(Context context, IComponent component, int width) => RenderCached(context, component, width).Count;

    private static int MeasureWidth(Context context, IComponent component, int width) =>
        RenderCached(context, component, width).Select(TextUtils.VisibleWidth).DefaultIfEmpty(0).Max();

    private static void TranslateBox(LayoutBox box, int deltaY)
    {
        box.Rect = box.Rect with { Y = box.Rect.Y + deltaY };
        foreach (var child in box.Children) TranslateBox(child, deltaY);
    }

    private static void UpdateClips(LayoutBox box, LayoutRect parentClip)
    {
        box.Clip = LayoutRect.Intersect(parentClip, box.Rect);
        foreach (var child in box.Children) UpdateClips(child, box.Clip);
    }

    private static LayoutBox LayoutComponent(Context context, IComponent component, int x, int y, int width, int? height, LayoutRect clip)
    {
        var safeWidth = Math.Max(1, width);
        var node = (component as ILayoutComponent)?.GetLayoutNode();
        if (node is null)
        {
            var lines = RenderCached(context, component, safeWidth);
            var allocatedHeight = height is { } h ? Math.Max(0, h) : lines.Count;
            var lineOffset = 0;
            if (lines.Count > allocatedHeight && allocatedHeight > 0)
            {
                var cursorLine = lines.FindIndex(line => line.Contains(TuiBase.CursorMarker, StringComparison.Ordinal));
                if (cursorLine >= allocatedHeight) lineOffset = cursorLine - allocatedHeight + 1;
            }
            var rect = new LayoutRect(x, y, safeWidth, allocatedHeight);
            return new LayoutBox { Component = component, Rect = rect, Clip = LayoutRect.Intersect(clip, rect), Lines = lines, LineOffset = lineOffset };
        }

        if (node is ScrollLayoutNode scroll)
        {
            var state = scroll.State;
            var previousScrollTop = state.ScrollTop;
            var contentWidth = state.GetContentWidth(safeWidth);
            var childBox = LayoutComponent(context, scroll.Component, x, y - previousScrollTop, contentWidth, null, clip);
            var contentHeight = childBox.Rect.Height;
            var viewportHeight = height is { } h ? Math.Max(0, h) : contentHeight;
            state.UpdateLayout(contentHeight, viewportHeight, context.RequestRender);
            TranslateBox(childBox, previousScrollTop - state.ScrollTop);
            if (state.Primary || context.PrimaryScrollView is null) context.PrimaryScrollView = state;
            var rect = new LayoutRect(x, y, safeWidth, viewportHeight);
            var childClip = LayoutRect.Intersect(clip, rect);
            var box = new LayoutBox
            {
                Component = component,
                Rect = rect,
                Clip = childClip,
                ScrollView = state,
                ScrollContentLines = RenderCached(context, scroll.Component, contentWidth),
            };
            box.Children.Add(childBox);
            childBox.Parent = box;
            UpdateClips(childBox, childClip);
            return box;
        }

        var stack = (StackLayoutNode)node;
        var entries = Stack.VisibleEntries(stack.Entries, context.Viewport);
        var gapTotal = Math.Max(0, entries.Count - 1) * stack.Gap;
        if (!stack.Horizontal)
        {
            var intrinsicHeights = entries.Select(e => e.Basis ?? MeasureHeight(context, e.Component, safeWidth)).ToList();
            var sizes = Stack.AllocateSizes(entries, intrinsicHeights, height, stack.Gap);
            var naturalHeight = sizes.Sum() + gapTotal;
            var allocatedHeight = height is { } h ? Math.Max(0, h) : naturalHeight;
            var rect = new LayoutRect(x, y, safeWidth, allocatedHeight);
            var box = new LayoutBox { Component = component, Rect = rect, Clip = LayoutRect.Intersect(clip, rect) };
            var childY = y;
            for (var index = 0; index < entries.Count; index++)
            {
                var child = LayoutComponent(context, entries[index].Component, x, childY, safeWidth, sizes[index], box.Clip);
                child.Parent = box;
                box.Children.Add(child);
                childY += sizes[index] + stack.Gap;
            }
            return box;
        }

        var intrinsicWidths = entries.Select(e => e.Basis ?? MeasureWidth(context, e.Component, safeWidth)).ToList();
        var widths = Stack.AllocateSizes(entries, intrinsicWidths, safeWidth, stack.Gap);
        var childHeights = entries.Select((e, index) => MeasureHeight(context, e.Component, Math.Max(1, widths[index]))).ToList();
        var hAllocated = height is { } hh ? Math.Max(0, hh) : childHeights.DefaultIfEmpty(0).Max();
        var hRect = new LayoutRect(x, y, safeWidth, hAllocated);
        var hBox = new LayoutBox { Component = component, Rect = hRect, Clip = LayoutRect.Intersect(clip, hRect) };
        var childX = x;
        for (var index = 0; index < entries.Count; index++)
        {
            var natural = childHeights[index];
            var childHeight = stack.Align == StackAlign.Stretch ? hAllocated : Math.Min(hAllocated, natural);
            var childY = y + stack.Align switch
            {
                StackAlign.Center => (hAllocated - childHeight) / 2,
                StackAlign.End => hAllocated - childHeight,
                _ => 0,
            };
            var childWidth = widths[index];
            if (childWidth == 0)
            {
                hBox.Children.Add(new LayoutBox
                {
                    Component = entries[index].Component,
                    Rect = new LayoutRect(childX, childY, 0, childHeight),
                    Clip = new LayoutRect(childX, childY, 0, 0),
                    Parent = hBox,
                });
            }
            else
            {
                var child = LayoutComponent(context, entries[index].Component, childX, childY, childWidth, childHeight, hBox.Clip);
                child.Parent = hBox;
                hBox.Children.Add(child);
            }
            childX += childWidth + stack.Gap;
        }
        return hBox;
    }

    private static string ReplaceScrollbarCell(string line, int column, int totalWidth, string replacement, bool preserveTargetBackground)
    {
        if (TerminalImage.IsImageLine(line)) return line;
        var range = TextUtils.GetGraphemeCellRange(line, column);
        var start = range?.Start ?? column;
        var end = range?.End ?? column + 1;
        var before = TextUtils.SliceByColumn(line, 0, start, true);
        var target = TextUtils.SliceByColumn(line, start, end - start, true);
        var after = TextUtils.SliceByColumn(line, end, Math.Max(0, totalWidth - end), true);

        var targetPrefix = "";
        var targetIndex = 0;
        while (targetIndex < target.Length && TextUtils.ExtractAnsiCode(target, targetIndex) is { } ansi)
        {
            targetPrefix += ansi.Code;
            targetIndex += ansi.Length;
        }
        var beforePadding = new string(' ', Math.Max(0, start - TextUtils.VisibleWidth(before)));
        var cellPaddingBefore = new string(' ', Math.Max(0, column - start));
        var cellPaddingAfter = new string(' ', Math.Max(0, end - column - 1));
        var targetStyle = TuiBase.SegmentReset + (preserveTargetBackground ? TextUtils.GetActiveBackgroundAnsi(targetPrefix) : "");
        return before + beforePadding + targetStyle + cellPaddingBefore + replacement + cellPaddingAfter + after;
    }

    public static ScrollbarGeometry? GetScrollbarGeometry(LayoutBox box, bool includeHiddenAuto = false)
    {
        if (box.ScrollView is not { } scrollView || box.Rect.Width <= 0 || box.Rect.Height <= 0) return null;
        var contentHeight = box.Children.Count > 0 ? box.Children[0].Rect.Height : box.ScrollContentLines?.Count ?? 0;
        var trackHeight = box.Rect.Height;
        var canRevealHiddenAuto = includeHiddenAuto && scrollView.Scrollbar == ScrollViewScrollbar.Auto && contentHeight > trackHeight;
        if (!scrollView.IsScrollbarVisible && !canRevealHiddenAuto) return null;

        var minThumbHeight = Math.Min(2, trackHeight);
        var thumbHeight = Math.Max(minThumbHeight, Math.Min(trackHeight, (int)Math.Round((double)trackHeight * trackHeight / Math.Max(1, contentHeight), MidpointRounding.AwayFromZero)));
        var maxScrollTop = Math.Max(0, contentHeight - trackHeight);
        var maxThumbTop = trackHeight - thumbHeight;
        var thumbOffset = maxScrollTop == 0 ? 0 : (int)Math.Round((double)scrollView.ScrollTop / maxScrollTop * maxThumbTop, MidpointRounding.AwayFromZero);
        var column = box.Rect.X + box.Rect.Width - 1;
        if (column < box.Clip.X || column >= box.Clip.X + box.Clip.Width) return null;
        return new ScrollbarGeometry(column, box.Rect.Y, trackHeight, box.Rect.Y + thumbOffset, thumbHeight, maxScrollTop);
    }

    private static void PaintScrollbar(LayoutBox box, List<string> screen, int totalWidth)
    {
        if (GetScrollbarGeometry(box) is not { } geometry || box.ScrollView is not { } scrollView) return;
        for (var offset = 0; offset < geometry.TrackHeight; offset++)
        {
            var row = geometry.TrackTop + offset;
            if (row < box.Clip.Y || row >= box.Clip.Y + box.Clip.Height || row < 0 || row >= screen.Count) continue;
            var isThumb = row >= geometry.ThumbTop && row < geometry.ThumbTop + geometry.ThumbHeight;
            var replacement = isThumb ? scrollView.ScrollbarThumbStyle(scrollView.IsScrollbarActive ? "█" : "┃") : scrollView.ScrollbarTrackStyle("│");
            screen[row] = ReplaceScrollbarCell(screen[row], geometry.Column, totalWidth, replacement, scrollView.Scrollbar != ScrollViewScrollbar.Always);
        }
    }

    private static void PaintBox(LayoutBox box, List<string> screen, int totalWidth)
    {
        if (box.Lines is { } lines)
        {
            var firstRow = Math.Max(Math.Max(box.Rect.Y, box.Clip.Y), 0);
            var lastRow = Math.Min(Math.Min(box.Rect.Y + box.Rect.Height, box.Clip.Y + box.Clip.Height), screen.Count);
            for (var row = firstRow; row < lastRow; row++)
            {
                var sourceIndex = box.LineOffset + row - box.Rect.Y;
                if (sourceIndex < 0 || sourceIndex >= lines.Count) continue;
                var line = Osc133ZonePrefix().Replace(lines[sourceIndex], "");
                // A full-width box painting onto an untouched row can use the source line directly.
                if (box.Rect.X == 0 && box.Rect.Width >= totalWidth && (TerminalImage.IsImageLine(line) || screen[row].Length == 0))
                {
                    screen[row] = line;
                }
                else
                {
                    screen[row] = TuiBase.CompositeLine(screen[row], line, box.Rect.X, box.Rect.Width, totalWidth);
                }
            }
        }
        foreach (var child in box.Children) PaintBox(child, screen, totalWidth);
        PaintScrollbar(box, screen, totalWidth);
    }

    public static LayoutFrame RenderFrame(IComponent root, int width, int height, Action requestRender)
    {
        var safeWidth = Math.Max(1, width);
        var safeHeight = Math.Max(1, height);
        var context = new Context { Viewport = new LayoutViewport(safeWidth, safeHeight), RequestRender = requestRender };
        var rootBox = LayoutComponent(context, root, 0, 0, safeWidth, safeHeight, new LayoutRect(0, 0, safeWidth, safeHeight));
        var lines = Enumerable.Repeat("", safeHeight).ToList();
        PaintBox(rootBox, lines, safeWidth);
        return new LayoutFrame { Root = rootBox, Width = safeWidth, Height = safeHeight, Lines = lines, PrimaryScrollView = context.PrimaryScrollView };
    }

    /// <summary>Return the visual hit path from the deepest component to the layout root.</summary>
    public static List<LayoutBox> GetBoxesAt(LayoutFrame frame, int x, int y)
    {
        var result = new List<(LayoutBox Box, int Depth)>();
        void Visit(LayoutBox box, int depth)
        {
            if (!box.Clip.Contains(x, y)) return;
            result.Add((box, depth));
            foreach (var child in box.Children) Visit(child, depth + 1);
        }
        Visit(frame.Root, 0);
        return result.OrderByDescending(r => r.Box.Layer).ThenByDescending(r => r.Depth).Select(r => r.Box).ToList();
    }

    public static LayoutBox? GetScrollViewBox(LayoutFrame frame, ScrollView scrollView)
    {
        LayoutBox? Visit(LayoutBox box)
        {
            if (ReferenceEquals(box.ScrollView, scrollView)) return box;
            foreach (var child in box.Children)
            {
                if (Visit(child) is { } match) return match;
            }
            return null;
        }
        return Visit(frame.Root);
    }

    public static List<ScrollView> GetScrollViewsAt(LayoutFrame frame, int x, int y)
    {
        var result = new List<(ScrollView ScrollView, int Depth)>();
        void Visit(LayoutBox box, int depth)
        {
            if (!box.Clip.Contains(x, y)) return;
            if (box.ScrollView is { } scrollView && box.Rect.Contains(x, y)) result.Add((scrollView, depth));
            foreach (var child in box.Children) Visit(child, depth + 1);
        }
        Visit(frame.Root, 0);
        return result.OrderByDescending(r => r.Depth).Select(r => r.ScrollView).ToList();
    }
}
