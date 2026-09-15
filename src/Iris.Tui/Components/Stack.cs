using Iris.Tui.Layout;

namespace Iris.Tui.Components;

public sealed class StackEntryOptions
{
    /// <summary>Fixed basis in cells; null means "auto".</summary>
    public int? Basis { get; init; }
    public int? Grow { get; init; }
    public int? Shrink { get; init; }
    public int? MinSize { get; init; }
    public int? MaxSize { get; init; }
    public Func<LayoutViewport, bool>? Visible { get; init; }
}

/// <summary>Flex-like stack of components.</summary>
public abstract class Stack : Container, ILayoutComponent
{
    protected readonly List<StackLayoutEntry> Entries = [];
    protected readonly int Gap;
    protected readonly StackAlign Align;

    protected Stack(IEnumerable<(IComponent Component, StackEntryOptions? Options)> children, int gap = 0, StackAlign align = StackAlign.Stretch)
    {
        Gap = Math.Max(0, gap);
        Align = align;
        foreach (var (component, options) in children) AddChild(component, options);
    }

    protected abstract bool Horizontal { get; }

    public override void AddChild(IComponent component) => AddChild(component, null);

    public void AddChild(IComponent component, StackEntryOptions? options)
    {
        base.AddChild(component);
        Entries.Add(new StackLayoutEntry
        {
            Component = component,
            Basis = options?.Basis is { } basis ? Math.Max(0, basis) : null,
            Grow = options?.Grow is { } grow ? Math.Max(0, grow) : null,
            Shrink = options?.Shrink is { } shrink ? Math.Max(0, shrink) : null,
            MinSize = options?.MinSize is { } min ? Math.Max(0, min) : null,
            MaxSize = options?.MaxSize is { } max ? Math.Max(0, max) : null,
            Visible = options?.Visible,
        });
    }

    public override void RemoveChild(IComponent component)
    {
        base.RemoveChild(component);
        var index = Entries.FindIndex(e => ReferenceEquals(e.Component, component));
        if (index != -1) Entries.RemoveAt(index);
    }

    public override void Clear()
    {
        base.Clear();
        Entries.Clear();
    }

    public LayoutNode GetLayoutNode() => new StackLayoutNode(Horizontal, Entries, Gap, Align);

    public static List<StackLayoutEntry> VisibleEntries(IReadOnlyList<StackLayoutEntry> entries, LayoutViewport viewport) =>
        entries.Where(e => e.Visible?.Invoke(viewport) ?? true).ToList();

    private static int ClampSize(int size, StackLayoutEntry entry)
    {
        var min = Math.Max(0, entry.MinSize ?? 0);
        var max = Math.Max(min, entry.MaxSize ?? int.MaxValue);
        return Math.Max(min, Math.Min(max, Math.Max(0, size)));
    }

    private static void Distribute(int[] sizes, IReadOnlyList<StackLayoutEntry> entries, int amount, bool grow)
    {
        var remaining = amount;
        while (remaining > 0)
        {
            var candidates = new List<int>();
            for (var index = 0; index < entries.Count; index++)
            {
                var entry = entries[index];
                if (grow ? (entry.Grow ?? 0) > 0 && sizes[index] < (entry.MaxSize ?? int.MaxValue) : (entry.Shrink ?? 1) > 0 && sizes[index] > (entry.MinSize ?? 0))
                {
                    candidates.Add(index);
                }
            }
            if (candidates.Count == 0) return;

            double Weight(int index) => grow ? entries[index].Grow ?? 0 : (entries[index].Shrink ?? 1) * (double)Math.Max(1, sizes[index]);
            var totalWeight = candidates.Sum(Weight);
            var distributed = 0;
            foreach (var index in candidates)
            {
                if (remaining <= 0) break;
                var entry = entries[index];
                var proposed = Math.Max(1, (int)Math.Floor(remaining * Weight(index) / totalWeight));
                var capacity = grow ? (long)(entry.MaxSize ?? int.MaxValue) - sizes[index] : sizes[index] - (entry.MinSize ?? 0);
                var delta = (int)Math.Min(Math.Min(remaining, proposed), capacity);
                if (delta <= 0) continue;
                sizes[index] += grow ? delta : -delta;
                remaining -= delta;
                distributed += delta;
            }
            if (distributed == 0) return;
        }
    }

    public static int[] AllocateSizes(IReadOnlyList<StackLayoutEntry> entries, IReadOnlyList<int> intrinsicSizes, int? availableSize, int gap)
    {
        var sizes = entries.Select((entry, index) => ClampSize(entry.Basis ?? (index < intrinsicSizes.Count ? intrinsicSizes[index] : 0), entry)).ToArray();
        if (availableSize is not { } available) return sizes;
        var contentSize = Math.Max(0, available - Math.Max(0, entries.Count - 1) * gap);
        var total = sizes.Sum();
        if (total < contentSize) Distribute(sizes, entries, contentSize - total, grow: true);
        else if (total > contentSize) Distribute(sizes, entries, total - contentSize, grow: false);
        return sizes;
    }
}

/// <summary>Vertical stack.</summary>
public sealed class VStack : Stack
{
    public VStack(IEnumerable<(IComponent Component, StackEntryOptions? Options)>? children = null, int gap = 0, StackAlign align = StackAlign.Stretch)
        : base(children ?? [], gap, align)
    {
    }

    protected override bool Horizontal => false;

    public override List<string> Render(int width)
    {
        var viewport = new LayoutViewport(Math.Max(1, width), int.MaxValue);
        var entries = VisibleEntries(Entries, viewport);
        var rendered = entries.Select(e => e.Component.Render(viewport.Width)).ToList();
        var sizes = AllocateSizes(entries, rendered.Select(l => l.Count).ToList(), null, Gap);
        var lines = new List<string>();
        for (var index = 0; index < entries.Count; index++)
        {
            if (index > 0)
            {
                for (var g = 0; g < Gap; g++) lines.Add("");
            }
            var childLines = rendered[index].Take(sizes[index]).ToList();
            lines.AddRange(childLines);
            for (var padding = childLines.Count; padding < sizes[index]; padding++) lines.Add("");
        }
        return lines;
    }
}

/// <summary>Horizontal stack.</summary>
public sealed class HStack : Stack
{
    public HStack(IEnumerable<(IComponent Component, StackEntryOptions? Options)>? children = null, int gap = 0, StackAlign align = StackAlign.Stretch)
        : base(children ?? [], gap, align)
    {
    }

    protected override bool Horizontal => true;

    public override List<string> Render(int width)
    {
        var safeWidth = Math.Max(1, width);
        var viewport = new LayoutViewport(safeWidth, int.MaxValue);
        var entries = VisibleEntries(Entries, viewport);
        if (entries.Count == 0) return [];
        var intrinsicWidths = entries.Select(e => e.Component.Render(safeWidth).Select(TextUtils.VisibleWidth).DefaultIfEmpty(0).Max()).ToList();
        var widths = AllocateSizes(entries, intrinsicWidths, safeWidth, Gap);
        var rendered = entries.Select((e, index) => widths[index] == 0 ? [] : e.Component.Render(widths[index])).ToList();
        var height = rendered.Select(l => l.Count).DefaultIfEmpty(0).Max();
        var result = Enumerable.Repeat("", height).ToList();
        var x = 0;
        for (var index = 0; index < rendered.Count; index++)
        {
            var lines = rendered[index];
            var offset = Align switch
            {
                StackAlign.Center => (height - lines.Count) / 2,
                StackAlign.End => height - lines.Count,
                _ => 0,
            };
            for (var row = 0; row < lines.Count; row++)
            {
                var target = row + offset;
                if (target < 0 || target >= result.Count) continue;
                result[target] = TuiBase.CompositeLine(result[target], lines[row], x, widths[index], safeWidth);
            }
            x += widths[index] + Gap;
        }
        return result;
    }
}
