// SPDX-License-Identifier: GPL-3.0-or-later

using AppleDeviceHub.Core.Artwork;

namespace AppleDeviceHub.Core.Tests.Artwork;

public sealed class LidSolidTests
{
    // The numbers from the reference hinge page.
    private static readonly LidSpec Spec = new(71, 158, 26, 26, 13, 107, 34, 30, 104, 24);

    [Fact]
    public void Closed_lid_is_unscaled_and_shows_the_outside()
    {
        var p = LidSolid.Projection(Spec, 0);
        Assert.Equal(1, p.ScaleY, 12);
        Assert.Equal(-Math.Cos(Math.PI / 6), p.ShiftPerUnitHeight, 12);
        Assert.False(p.ShowsInside);
    }

    [Fact]
    public void Edge_on_at_the_tilt_angle_has_no_height_and_still_shows_the_outside()
    {
        var p = LidSolid.Projection(Spec, 30);
        Assert.Equal(0, p.ScaleY, 12);
        Assert.False(p.ShowsInside);
    }

    [Fact]
    public void Fully_open_is_flipped_and_shows_the_inside()
    {
        var p = LidSolid.Projection(Spec, 104);
        Assert.True(p.ScaleY < 0);
        Assert.Equal(Math.Sin(-74 * Math.PI / 180) / 0.5, p.ScaleY, 12);
        Assert.Equal(-1.9225, p.ScaleY, 4);
        Assert.Equal(-0.2756, p.ShiftPerUnitHeight, 4);
        Assert.True(p.ShowsInside);
    }

    [Fact]
    public void Sixty_degrees_mirrors_the_closed_height()
    {
        var p = LidSolid.Projection(Spec, 60);
        Assert.Equal(-1, p.ScaleY, 12);
        Assert.Equal(-Math.Cos(Math.PI / 6), p.ShiftPerUnitHeight, 12);
        Assert.True(p.ShowsInside);
    }

    [Fact]
    public void Slice_zero_is_the_unscaled_rim()
    {
        var s = LidSolid.SliceShape(Spec, 0, 64);
        Assert.Equal(0, s.H);
        Assert.Equal(71, s.X, 12);
        Assert.Equal(0, s.YLocal, 12);
        Assert.Equal(158, s.Width, 12);
        Assert.Equal(26, s.Depth, 12);
        Assert.Equal(26, s.Rx, 12);
        Assert.Equal(13, s.Ry, 12);
    }

    [Fact]
    public void Top_slice_is_narrower_by_the_dome_and_much_shallower()
    {
        var s = LidSolid.SliceShape(Spec, 64, 64);
        Assert.Equal(34, s.H, 12);
        // dx = 24 (the full dome radius), so the width loses 2 * 24.
        Assert.Equal(110, s.Width, 9);
        Assert.Equal(95, s.X, 9);
        var kz = Math.Sqrt(0.03);
        Assert.Equal(26 * kz, s.Depth, 9);
        Assert.Equal(13 - (26 * kz / 2), s.YLocal, 9);
        Assert.Equal(26 * 110 / 158.0, s.Rx, 9);
        Assert.Equal(13 * kz, s.Ry, 9);
    }

    [Fact]
    public void Mid_slice_matches_the_reference_formulas()
    {
        var s = LidSolid.SliceShape(Spec, 32, 64);
        Assert.Equal(17, s.H, 12);
        // h = 17 is 7 into the dome: dx = 24 - sqrt(24^2 - 7^2) = 24 - sqrt(527).
        var dx = 24 - Math.Sqrt(527);
        Assert.Equal(158 - (2 * dx), s.Width, 9);
        Assert.Equal(155.913, s.Width, 3);
        Assert.Equal(26 * Math.Sqrt(0.7575), s.Depth, 9);
    }

    [Fact]
    public void Below_the_dome_slices_keep_the_full_width()
    {
        // h = 8.5 (i = 16) is under the 10 where the dome starts.
        var s = LidSolid.SliceShape(Spec, 16, 64);
        Assert.Equal(158, s.Width, 12);
        Assert.True(s.Depth < 26);
    }

    [Fact]
    public void Slice_transform_shifts_to_the_slice_height_then_scales_about_the_hinge()
    {
        var shape = LidSolid.SliceShape(Spec, 64, 64);
        var m = LidSolid.SliceTransform(Spec, shape, LidSolid.Projection(Spec, 0));
        Assert.Equal(107 - (34 * Math.Cos(Math.PI / 6)), m.Apply(0, 0).Y, 9);

        var open = LidSolid.SliceTransform(Spec, LidSolid.SliceShape(Spec, 0, 64), LidSolid.Projection(Spec, 104));
        Assert.Equal(107, open.Apply(0, 0).Y, 9);
        Assert.Equal(107 + (26 * -1.9225234), open.Apply(0, 26).Y, 3);
    }

    [Fact]
    public void Lightness_and_bud_opacity()
    {
        Assert.Equal(0, LidSolid.SliceLightness(0, 0.55));
        Assert.Equal(0.275, LidSolid.SliceLightness(0.5, 0.55), 12);
        Assert.Equal(0.55, LidSolid.SliceLightness(1, 0.55), 12);
        Assert.Equal(0.06, LidSolid.SliceLightness(0.5, 0.12), 12);
        Assert.Equal(0, LidSolid.BudOpacity(0));
        Assert.Equal(0, LidSolid.BudOpacity(6));
        Assert.Equal(0.5, LidSolid.BudOpacity(13), 12);
        Assert.Equal(1, LidSolid.BudOpacity(20));
        Assert.Equal(1, LidSolid.BudOpacity(104));
    }
}
