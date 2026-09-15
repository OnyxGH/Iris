namespace Iris.Tui.Layout;

public readonly record struct LayoutViewport(int Width, int Height);

public enum StackAlign
{
    Stretch,
    Start,
    Center,
    End,
}

/// <summary>A stack child with flex-like sizing. Port of pi-tui StackLayoutEntry.</summary>
public sealed class StackLayoutEntry
{
    public required IComponent Component { get; init; }

    /// <summary>Fixed basis in cells; null means "auto" (the intrinsic size).</summary>
    public int? Basis { get; init; }

    public int? Grow { get; init; }
    public int? Shrink { get; init; }
    public int? MinSize { get; init; }
    public int? MaxSize { get; init; }
    public Func<LayoutViewport, bool>? Visible { get; init; }
}

public abstract record LayoutNode;

public sealed record StackLayoutNode(bool Horizontal, IReadOnlyList<StackLayoutEntry> Entries, int Gap, StackAlign Align) : LayoutNode;

public sealed record ScrollLayoutNode(IComponent Component, Components.ScrollView State) : LayoutNode;

/// <summary>Component that participates in fullscreen layout. Port of pi-tui LayoutComponent.</summary>
public interface ILayoutComponent : IComponent
{
    LayoutNode GetLayoutNode();
}
