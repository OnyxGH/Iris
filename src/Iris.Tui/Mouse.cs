namespace Iris.Tui;

public enum TuiMouseEventType
{
    Press,
    Release,
    Move,
    Drag,
    Click,
    Wheel,
}

public enum TuiMouseButton
{
    Left,
    Middle,
    Right,
    None,
}

/// <summary>Normalized cell-based mouse event. Coordinates are zero-based. Port of pi-tui TuiMouseEvent.</summary>
public sealed record TuiMouseEvent
{
    public required TuiMouseEventType Type { get; init; }
    public required TuiMouseButton Button { get; init; }

    /// <summary>Coordinates local to the receiving component.</summary>
    public required int X { get; init; }
    public required int Y { get; init; }

    /// <summary>Absolute terminal coordinates.</summary>
    public required int ScreenX { get; init; }
    public required int ScreenY { get; init; }

    /// <summary>Current component bounds.</summary>
    public required int Width { get; init; }
    public required int Height { get; init; }

    public bool Shift { get; init; }
    public bool Alt { get; init; }
    public bool Ctrl { get; init; }

    /// <summary>Logical lines. Negative values scroll up.</summary>
    public int? WheelDelta { get; init; }

    /// <summary>Consecutive click count when type is click.</summary>
    public int? ClickCount { get; init; }
}

public record TuiMouseEventResult
{
    /// <summary>Stop propagation and suppress renderer-level fallback behavior.</summary>
    public bool Handled { get; init; }

    /// <summary>Route subsequent drag/release events to this component. Implies handled.</summary>
    public bool Capture { get; init; }

    /// <summary>Give keyboard focus to this component. Implies handled.</summary>
    public bool Focus { get; init; }

    /// <summary>Explicitly request or suppress a render. Move and release default to false; others to true.</summary>
    public bool? Render { get; init; }

    public static readonly TuiMouseEventResult HandledResult = new() { Handled = true };
    public static readonly TuiMouseEventResult HandledFocus = new() { Handled = true, Focus = true };
}

/// <summary>Target metadata used by containers and alternate-screen dispatch.</summary>
public sealed record TuiMouseDispatchTarget(IComponent Component, int OriginX, int OriginY, int Width, int Height);

/// <summary>Result of dispatching to a concrete component.</summary>
public sealed record TuiMouseDispatchResult : TuiMouseEventResult
{
    public required TuiMouseDispatchTarget Target { get; init; }

    /// <summary>Keyboard focus target, which may be a delegating parent container.</summary>
    public IComponent? FocusTarget { get; init; }
}

/// <summary>Component with a normalized mouse handler.</summary>
public interface IMouseComponent : IComponent
{
    TuiMouseEventResult? HandleMouse(TuiMouseEvent mouseEvent);
}

public static class MouseDispatch
{
    /// <summary>Dispatch an event to a component and retain the exact target and coordinate transform.</summary>
    public static TuiMouseDispatchResult? Dispatch(IComponent component, TuiMouseEvent mouseEvent)
    {
        if (component is not IMouseComponent mouse) return null;
        var result = mouse.HandleMouse(mouseEvent);
        if (result is null) return null;
        if (result is TuiMouseDispatchResult dispatched) return dispatched;
        if (!result.Handled && !result.Capture && !result.Focus) return null;
        return new TuiMouseDispatchResult
        {
            Handled = true,
            Capture = result.Capture,
            Focus = result.Focus,
            Render = result.Render,
            FocusTarget = result.Focus ? component : null,
            Target = new TuiMouseDispatchTarget(component, mouseEvent.ScreenX - mouseEvent.X, mouseEvent.ScreenY - mouseEvent.Y, mouseEvent.Width, mouseEvent.Height),
        };
    }

    /// <summary>Recreate local coordinates for a previously dispatched mouse target.</summary>
    public static TuiMouseEvent Retarget(TuiMouseEvent mouseEvent, TuiMouseDispatchTarget target) => mouseEvent with
    {
        X = mouseEvent.ScreenX - target.OriginX,
        Y = mouseEvent.ScreenY - target.OriginY,
        Width = target.Width,
        Height = target.Height,
    };
}

/// <summary>Adds mouse handling to an existing component without changing its rendering. Port of pi-tui MouseRegion.</summary>
public sealed class MouseRegion(IComponent child, Func<TuiMouseEvent, TuiMouseEventResult?> onMouse) : IMouseComponent
{
    public IComponent Child => child;

    public List<string> Render(int width) => child.Render(width);

    public TuiMouseEventResult? HandleMouse(TuiMouseEvent mouseEvent) => MouseDispatch.Dispatch(child, mouseEvent) ?? onMouse(mouseEvent);

    public void Invalidate() => child.Invalidate();
}
