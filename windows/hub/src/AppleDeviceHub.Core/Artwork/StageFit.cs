// SPDX-License-Identifier: GPL-3.0-or-later

namespace AppleDeviceHub.Core.Artwork;

/// <summary>
/// How a drawing is scaled and centred into the 200 x 150 stage the app draws every model on: the content box (see
/// <see cref="ArtworkBounds.ContentOf"/>) scaled uniformly to fill 98% of the stage and centred. The view and anything that
/// lays marks over the drawing (pairing rings, ripples) both use this, so they always agree.
/// </summary>
public readonly record struct StageFit(double Scale, double OffsetX, double OffsetY)
{
    public const double StageWidth = 200;
    public const double StageHeight = 150;

    /// <summary>How much of the stage the content box may fill (a small margin keeps strokes and the lid edge off the border).</summary>
    public const double FitFill = 0.98;

    public static StageFit Of(ArtBounds content)
    {
        var scale = Math.Min(StageWidth * FitFill / content.Width, StageHeight * FitFill / content.Height);
        return new StageFit(scale, (StageWidth / 2) - (scale * content.CentreX), (StageHeight / 2) - (scale * content.CentreY));
    }

    /// <summary>A point in drawing units, in stage units.</summary>
    public (double X, double Y) Map(double x, double y) => ((Scale * x) + OffsetX, (Scale * y) + OffsetY);
}
