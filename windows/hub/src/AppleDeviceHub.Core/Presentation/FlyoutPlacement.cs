namespace AppleDeviceHub.Core.Presentation;

/// <summary>A rectangle in physical screen pixels.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public int CenterX => X + Width / 2;
    public int CenterY => Y + Height / 2;

    public bool Contains(int x, int y) => x >= X && x < Right && y >= Y && y < Bottom;
}

public enum TaskbarEdge
{
    Unknown = 0,
    Bottom = 1,
    Top = 2,
    Left = 3,
    Right = 4,
}

/// <param name="Bounds">Where to put the window, physical pixels.</param>
/// <param name="Edge">Which screen edge the flyout grows from (drives the entrance direction).</param>
/// <param name="AnchorSource">"tray icon", "cursor", "screen corner", or "overflow-corner" — recorded for honest diagnostics.</param>
public readonly record struct PlacementResult(PixelRect Bounds, TaskbarEdge Edge, string AnchorSource);

/// <summary>
/// Pure placement math for a tray-anchored flyout. The caller supplies the notification-icon
/// rectangle (Shell_NotifyIconGetRect) when Windows provides one, otherwise the cursor; the
/// monitor and work-area rectangles come from the monitor containing that anchor.
/// </summary>
public static class FlyoutPlacement
{
    /// <summary>Maps the APPBARDATA.uEdge value (ABE_LEFT 0, ABE_TOP 1, ABE_RIGHT 2, ABE_BOTTOM 3) to an edge.</summary>
    public static TaskbarEdge EdgeFromAppBar(uint abe) => abe switch
    {
        0 => TaskbarEdge.Left,
        1 => TaskbarEdge.Top,
        2 => TaskbarEdge.Right,
        3 => TaskbarEdge.Bottom,
        _ => TaskbarEdge.Unknown,
    };

    public static PlacementResult Place(
        PixelRect? iconRect,
        (int X, int Y)? cursor,
        PixelRect monitor,
        PixelRect workArea,
        int width,
        int height,
        int margin,
        TaskbarEdge knownEdge = TaskbarEdge.Unknown)
    {
        width = Math.Min(width, Math.Max(1, workArea.Width - 2 * margin));
        height = Math.Min(height, Math.Max(1, workArea.Height - 2 * margin));

        // An icon rect that is not on this monitor (e.g. stale after a display change) is ignored.
        if (iconRect is { } r && !monitor.Contains(r.CenterX, r.CenterY)) iconRect = null;

        string source;
        int ax, ay;
        if (iconRect is { } icon) { ax = icon.CenterX; ay = icon.CenterY; source = "tray icon"; }
        else if (cursor is { } c) { ax = c.X; ay = c.Y; source = "cursor"; }
        else { ax = workArea.Right; ay = workArea.Bottom; source = "screen corner"; }

        var edge = DetectTaskbarEdge(monitor, workArea, ax, ay, knownEdge);
        int x, y;
        switch (edge)
        {
            case TaskbarEdge.Top:
                x = ax - width / 2;
                y = Math.Max(workArea.Y, iconRect?.Bottom ?? workArea.Y) + margin;
                break;
            case TaskbarEdge.Left:
                x = Math.Max(workArea.X, iconRect?.Right ?? workArea.X) + margin;
                y = ay - height / 2;
                break;
            case TaskbarEdge.Right:
                x = Math.Min(workArea.Right, iconRect?.X ?? workArea.Right) - width - margin;
                y = ay - height / 2;
                break;
            default: // Bottom, or Unknown treated as the Windows 11 default.
                x = ax - width / 2;
                y = Math.Min(workArea.Bottom, iconRect?.Y ?? workArea.Bottom) - height - margin;
                edge = TaskbarEdge.Bottom;
                break;
        }

        x = Math.Clamp(x, workArea.X + margin, Math.Max(workArea.X + margin, workArea.Right - width - margin));
        y = Math.Clamp(y, workArea.Y + margin, Math.Max(workArea.Y + margin, workArea.Bottom - height - margin));
        return new PlacementResult(new PixelRect(x, y, width, height), edge, source);
    }

    /// <summary>
    /// True when the icon rectangle lies inside a visible overflow popup's rectangle (the icon is in the
    /// hidden-icons flyout, not on the taskbar). Compared by the icon's centre.
    /// </summary>
    public static bool IsIconInOverflow(PixelRect? iconRect, IEnumerable<PixelRect> overflowRects)
    {
        if (iconRect is not { } icon) return false;
        foreach (var o in overflowRects)
            if (o.Width > 0 && o.Height > 0 && o.Contains(icon.CenterX, icon.CenterY)) return true;
        return false;
    }

    /// <summary>
    /// Places the flyout in the taskbar-side corner of the work area, like the Windows volume and Wi-Fi
    /// panels, for opens that came from the hidden-icons popup (which closes and would leave a flyout
    /// floating above nothing). The tray sits at the end of the taskbar, so the flyout hugs the
    /// bottom-right corner (bottom/right/left taskbars; a left taskbar's tray is still at the bottom)
    /// or the top-right corner (top taskbar), inset by <paramref name="margin"/> from both edges.
    /// </summary>
    public static PlacementResult PlaceAtTaskbarCorner(
        PixelRect monitor, PixelRect workArea, int width, int height, int margin,
        TaskbarEdge knownEdge = TaskbarEdge.Unknown)
    {
        width = Math.Min(width, Math.Max(1, workArea.Width - 2 * margin));
        height = Math.Min(height, Math.Max(1, workArea.Height - 2 * margin));

        var edge = DetectTaskbarEdge(monitor, workArea, workArea.Right, workArea.Bottom, knownEdge);
        int x, y;
        switch (edge)
        {
            case TaskbarEdge.Top:
                x = workArea.Right - width - margin;
                y = workArea.Y + margin;
                break;
            case TaskbarEdge.Left:
                x = workArea.X + margin;
                y = workArea.Bottom - height - margin;
                break;
            default: // Bottom and Right: bottom-right corner of the work area.
                x = workArea.Right - width - margin;
                y = workArea.Bottom - height - margin;
                break;
        }
        return new PlacementResult(new PixelRect(x, y, width, height), edge, "overflow-corner");
    }

    /// <summary>
    /// The taskbar edge is where the work area is smaller than the monitor. With an auto-hide
    /// taskbar the two are equal, so use the edge Windows reports for the taskbar
    /// (<paramref name="knownEdge"/>, from ABM_GETTASKBARPOS), and only if that is unknown fall back
    /// to the screen edge nearest the anchor.
    /// </summary>
    public static TaskbarEdge DetectTaskbarEdge(
        PixelRect monitor, PixelRect workArea, int anchorX, int anchorY, TaskbarEdge knownEdge = TaskbarEdge.Unknown)
    {
        if (workArea.Bottom < monitor.Bottom) return TaskbarEdge.Bottom;
        if (workArea.Y > monitor.Y) return TaskbarEdge.Top;
        if (workArea.X > monitor.X) return TaskbarEdge.Left;
        if (workArea.Right < monitor.Right) return TaskbarEdge.Right;
        if (knownEdge != TaskbarEdge.Unknown) return knownEdge;

        int toBottom = monitor.Bottom - anchorY, toTop = anchorY - monitor.Y;
        int toLeft = anchorX - monitor.X, toRight = monitor.Right - anchorX;
        int min = Math.Min(Math.Min(toBottom, toTop), Math.Min(toLeft, toRight));
        if (min == toBottom) return TaskbarEdge.Bottom;
        if (min == toTop) return TaskbarEdge.Top;
        if (min == toLeft) return TaskbarEdge.Left;
        return TaskbarEdge.Right;
    }
}
