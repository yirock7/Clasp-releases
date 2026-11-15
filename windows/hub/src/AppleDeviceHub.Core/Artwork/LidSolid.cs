// SPDX-License-Identifier: GPL-3.0-or-later

namespace AppleDeviceHub.Core.Artwork;

/// <summary>Lid geometry from a drawing's <c>data-lid</c> attribute.</summary>
public readonly record struct LidSpec(
    double X,
    double Width,
    double Depth,
    double Rx,
    double Ry,
    double HingeY,
    double Height,
    double TiltDegrees,
    double OpenDegrees,
    double DomeRadius,
    double Shine = LidSpec.DefaultShine)
{
    /// <summary>Strength of the white highlight on the top outside slice when a drawing gives no <c>shine</c>.</summary>
    public const double DefaultShine = 0.55;
}

/// <summary>One slice of the lid solid: its height above the rim plane and the rim rounded-rect shrunk to that height.</summary>
public readonly record struct LidSliceShape(double H, double X, double YLocal, double Width, double Depth, double Rx, double Ry);

/// <summary>How a lid angle maps every slice: a vertical scale about the hinge line plus a shift per unit of slice height.</summary>
public readonly record struct LidProjection(double ScaleY, double ShiftPerUnitHeight, bool ShowsInside);

/// <summary>
/// The lid is a stack of thin slices from its rim plane (i = 0) to its top (i = n), rotated together about the
/// hinge line. Seen straight-on and tipped forward by the tilt angle, a rotation about a horizontal axis is just a
/// vertical scale and shift per slice. All functions are pure.
/// </summary>
public static class LidSolid
{
    public const int DefaultSliceCount = 64;

    /// <summary>The rim rounded-rect of slice <paramref name="i"/> of <paramref name="n"/>, in rim-local coordinates.</summary>
    public static LidSliceShape SliceShape(LidSpec spec, int i, int n)
    {
        var u = (double)i / n;
        var h = spec.Height * u;
        var domeStart = spec.Height - spec.DomeRadius;
        var dx = 0.0;
        if (spec.DomeRadius > 0 && h > domeStart)
        {
            var t = h - domeStart;
            dx = spec.DomeRadius - Math.Sqrt(Math.Max(0, (spec.DomeRadius * spec.DomeRadius) - (t * t)));
        }

        var kx = 1 - (dx / (spec.Width / 2));
        var kz = Math.Sqrt(1 - (0.97 * u * u));
        var width = spec.Width * kx;
        var depth = spec.Depth * kz;
        var centreX = spec.X + (spec.Width / 2);
        return new LidSliceShape(h, centreX - (width / 2), (spec.Depth / 2) - (depth / 2), width, depth, spec.Rx * kx, spec.Ry * kz);
    }

    /// <summary>The slice projection for lid angle θ (0 closed to <see cref="LidSpec.OpenDegrees"/> open).</summary>
    public static LidProjection Projection(LidSpec spec, double thetaDegrees)
    {
        var phi = spec.TiltDegrees * Math.PI / 180;
        var theta = thetaDegrees * Math.PI / 180;
        var scale = Math.Sin(phi - theta) / Math.Sin(phi);
        return new LidProjection(scale, -Math.Cos(phi - theta), thetaDegrees > spec.TiltDegrees);
    }

    /// <summary>The transform that places a slice's rim-local shapes on the canvas: shift to its height, then the vertical scale.</summary>
    public static ArtMatrix SliceTransform(LidSpec spec, LidSliceShape shape, LidProjection projection) =>
        ArtMatrix.Translate(0, spec.HingeY + (projection.ShiftPerUnitHeight * shape.H)) * ArtMatrix.Scale(1, projection.ScaleY);

    /// <summary>Strength of the white overlay on an outside slice at relative height <paramref name="u"/> (0..1) for a lid whose top highlight is <paramref name="shine"/> (0..1).</summary>
    public static double SliceLightness(double u, double shine) => shine * u;

    /// <summary>Opacity of the buds: hidden up to 6 degrees, fully shown from 20 degrees.</summary>
    public static double BudOpacity(double thetaDegrees) => Math.Clamp((thetaDegrees - 6) / 14, 0, 1);
}
