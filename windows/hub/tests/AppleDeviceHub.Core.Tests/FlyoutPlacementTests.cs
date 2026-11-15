using AppleDeviceHub.Core.Presentation;

namespace AppleDeviceHub.Core.Tests;

public class FlyoutPlacementTests
{
    // 1920x1080 monitor with a 48 px bottom taskbar (Windows 11 default at 100%).
    private static readonly PixelRect Monitor = new(0, 0, 1920, 1080);
    private static readonly PixelRect BottomWork = new(0, 0, 1920, 1032);
    private const int W = 380, H = 500, M = 12;

    [Fact]
    public void Bottom_taskbar_places_the_flyout_above_the_tray_and_inside_the_work_area()
    {
        var icon = new PixelRect(1800, 1044, 24, 24);
        var r = FlyoutPlacement.Place(icon, null, Monitor, BottomWork, W, H, M);

        Assert.Equal(TaskbarEdge.Bottom, r.Edge);
        Assert.Equal("tray icon", r.AnchorSource);
        Assert.Equal(1032 - H - M, r.Bounds.Y);
        Assert.Equal(1920 - W - M, r.Bounds.X); // centred on the icon, then clamped to the right edge
    }

    [Fact]
    public void Overflow_corner_bottom_taskbar_sits_in_the_bottom_right_corner()
    {
        var r = FlyoutPlacement.PlaceAtTaskbarCorner(Monitor, BottomWork, W, H, M);
        Assert.Equal("overflow-corner", r.AnchorSource);
        Assert.Equal(TaskbarEdge.Bottom, r.Edge);
        Assert.Equal(new PixelRect(1920 - W - M, 1032 - H - M, W, H), r.Bounds);
    }

    [Fact]
    public void Overflow_corner_top_taskbar_sits_in_the_top_right_corner()
    {
        var work = new PixelRect(0, 48, 1920, 1032);
        var r = FlyoutPlacement.PlaceAtTaskbarCorner(Monitor, work, W, H, M);
        Assert.Equal(TaskbarEdge.Top, r.Edge);
        Assert.Equal(new PixelRect(1920 - W - M, 48 + M, W, H), r.Bounds);
    }

    [Fact]
    public void Overflow_corner_left_taskbar_hugs_the_taskbar_side_at_the_bottom()
    {
        var work = new PixelRect(72, 0, 1848, 1080);
        var r = FlyoutPlacement.PlaceAtTaskbarCorner(Monitor, work, W, H, M);
        Assert.Equal(TaskbarEdge.Left, r.Edge);
        Assert.Equal(new PixelRect(72 + M, 1080 - H - M, W, H), r.Bounds);
    }

    [Fact]
    public void Overflow_corner_right_taskbar_hugs_the_taskbar_side_at_the_bottom()
    {
        var work = new PixelRect(0, 0, 1848, 1080);
        var r = FlyoutPlacement.PlaceAtTaskbarCorner(Monitor, work, W, H, M);
        Assert.Equal(TaskbarEdge.Right, r.Edge);
        Assert.Equal(new PixelRect(1848 - W - M, 1080 - H - M, W, H), r.Bounds);
    }

    [Fact]
    public void Overflow_corner_scales_with_dpi_and_uses_the_secondary_monitor_origin()
    {
        // 150%: taskbar 72 px, margin 18 px, on a monitor whose origin is not (0,0).
        var monitor = new PixelRect(1920, 0, 2560, 1440);
        var work = new PixelRect(1920, 0, 2560, 1368);
        int w = 570, h = 750, m = 18;
        var r = FlyoutPlacement.PlaceAtTaskbarCorner(monitor, work, w, h, m);
        Assert.Equal(new PixelRect(4480 - w - m, 1368 - h - m, w, h), r.Bounds);
    }

    [Fact]
    public void Overflow_corner_shrinks_to_fit_a_small_work_area()
    {
        var work = new PixelRect(0, 0, 400, 300);
        var r = FlyoutPlacement.PlaceAtTaskbarCorner(new PixelRect(0, 0, 400, 348), work, W, H, M);
        Assert.Equal(300 - 2 * M, r.Bounds.Height);
        Assert.True(r.Bounds.X >= M);
    }

    [Fact]
    public void Icon_inside_an_overflow_rect_is_detected_and_pinned_icons_are_not()
    {
        var overflow = new[] { new PixelRect(1500, 600, 300, 400) };
        Assert.True(FlyoutPlacement.IsIconInOverflow(new PixelRect(1600, 700, 24, 24), overflow));
        Assert.False(FlyoutPlacement.IsIconInOverflow(new PixelRect(1800, 1044, 24, 24), overflow));
        Assert.False(FlyoutPlacement.IsIconInOverflow(null, overflow));
        Assert.False(FlyoutPlacement.IsIconInOverflow(new PixelRect(1600, 700, 24, 24), Array.Empty<PixelRect>()));
    }

    [Fact]
    public void Top_taskbar_places_the_flyout_below_it()
    {
        var work = new PixelRect(0, 48, 1920, 1032);
        var r = FlyoutPlacement.Place(new PixelRect(1800, 12, 24, 24), null, Monitor, work, W, H, M);
        Assert.Equal(TaskbarEdge.Top, r.Edge);
        Assert.Equal(48 + M, r.Bounds.Y);
    }

    [Fact]
    public void Auto_hide_taskbar_is_inferred_from_the_icon_position()
    {
        // Work area equals the monitor when the taskbar auto-hides.
        var icon = new PixelRect(1800, 1044, 24, 24);
        var r = FlyoutPlacement.Place(icon, null, Monitor, Monitor, W, H, M);
        Assert.Equal(TaskbarEdge.Bottom, r.Edge);
        Assert.Equal(1044 - H - M, r.Bounds.Y); // above the icon, not over the revealed taskbar
    }

    [Fact]
    public void Missing_icon_rectangle_falls_back_to_the_cursor()
    {
        var r = FlyoutPlacement.Place(null, (1500, 1050), Monitor, BottomWork, W, H, M);
        Assert.Equal("cursor", r.AnchorSource);
        Assert.Equal(1500 - W / 2, r.Bounds.X);
    }

    [Fact]
    public void Icon_rectangle_on_another_monitor_is_ignored()
    {
        var r = FlyoutPlacement.Place(new PixelRect(5000, 1044, 24, 24), (1500, 1050), Monitor, BottomWork, W, H, M);
        Assert.Equal("cursor", r.AnchorSource);
    }

    [Fact]
    public void Secondary_monitor_with_negative_coordinates_keeps_the_flyout_on_that_monitor()
    {
        var left = new PixelRect(-2560, 0, 2560, 1440);
        var leftWork = new PixelRect(-2560, 0, 2560, 1392);
        var r = FlyoutPlacement.Place(new PixelRect(-100, 1400, 32, 32), null, left, leftWork, W * 2, H * 2, M * 2);
        Assert.True(r.Bounds.X >= leftWork.X && r.Bounds.Right <= leftWork.Right);
        Assert.True(r.Bounds.Y >= leftWork.Y && r.Bounds.Bottom <= leftWork.Bottom);
    }

    [Fact]
    public void Flyout_taller_than_the_work_area_is_shrunk_to_fit()
    {
        var small = new PixelRect(0, 0, 1280, 672);
        var r = FlyoutPlacement.Place(null, null, new PixelRect(0, 0, 1280, 720), small, W, 2000, M);
        Assert.Equal(672 - 2 * M, r.Bounds.Height);
        Assert.Equal(M, r.Bounds.Y);
    }

    // Auto-hide: the work area equals the monitor, so the edge must come from Windows (ABM_GETTASKBARPOS),
    // not from whichever screen edge happens to be closest to the anchor.
    [Fact]
    public void Auto_hide_left_taskbar_uses_the_reported_edge_even_when_the_anchor_is_nearer_the_bottom()
    {
        var icon = new PixelRect(12, 1040, 24, 24); // tray at the bottom of a left taskbar: 16 px from the bottom, 12 from the left
        var guessed = FlyoutPlacement.Place(icon, null, Monitor, Monitor, W, H, M);
        Assert.Equal(TaskbarEdge.Left, guessed.Edge); // nearest-edge guess happens to work here...
        var lowIcon = new PixelRect(30, 1060, 24, 24); // ...but not here (8 px from the bottom, 30 from the left)
        Assert.Equal(TaskbarEdge.Bottom, FlyoutPlacement.Place(lowIcon, null, Monitor, Monitor, W, H, M).Edge);

        var r = FlyoutPlacement.Place(lowIcon, null, Monitor, Monitor, W, H, M, TaskbarEdge.Left);
        Assert.Equal(TaskbarEdge.Left, r.Edge);
        Assert.Equal(lowIcon.Right + M, r.Bounds.X);
    }

    [Fact]
    public void Auto_hide_top_taskbar_uses_the_reported_edge_for_a_mid_screen_cursor_anchor()
    {
        var r = FlyoutPlacement.Place(null, (900, 500), Monitor, Monitor, W, H, M, TaskbarEdge.Top);
        Assert.Equal(TaskbarEdge.Top, r.Edge);
        Assert.Equal(M, r.Bounds.Y);
    }

    [Fact]
    public void Auto_hide_right_taskbar_uses_the_reported_edge_for_a_mid_screen_cursor_anchor()
    {
        var r = FlyoutPlacement.Place(null, (900, 500), Monitor, Monitor, W, H, M, TaskbarEdge.Right);
        Assert.Equal(TaskbarEdge.Right, r.Edge);
        Assert.Equal(1920 - W - M, r.Bounds.X);
    }

    [Fact]
    public void Overflow_corner_with_auto_hide_top_taskbar_goes_to_the_top_right()
    {
        var r = FlyoutPlacement.PlaceAtTaskbarCorner(Monitor, Monitor, W, H, M, TaskbarEdge.Top);
        Assert.Equal(TaskbarEdge.Top, r.Edge);
        Assert.Equal(new PixelRect(1920 - W - M, M, W, H), r.Bounds);
    }

    [Fact]
    public void A_visible_work_area_difference_wins_over_the_reported_edge()
    {
        Assert.Equal(TaskbarEdge.Bottom, FlyoutPlacement.DetectTaskbarEdge(Monitor, BottomWork, 10, 10, TaskbarEdge.Top));
    }

    [Theory]
    [InlineData(0u, TaskbarEdge.Left)]
    [InlineData(1u, TaskbarEdge.Top)]
    [InlineData(2u, TaskbarEdge.Right)]
    [InlineData(3u, TaskbarEdge.Bottom)]
    [InlineData(9u, TaskbarEdge.Unknown)]
    public void Appbar_edge_codes_map_to_taskbar_edges(uint abe, TaskbarEdge expected)
        => Assert.Equal(expected, FlyoutPlacement.EdgeFromAppBar(abe));
}
