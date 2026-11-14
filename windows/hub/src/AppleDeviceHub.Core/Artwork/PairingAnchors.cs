// SPDX-License-Identifier: GPL-3.0-or-later

using AppleDeviceHub.Core.Ble;

namespace AppleDeviceHub.Core.Artwork;

/// <summary>A point on the 200 x 150 stage.</summary>
public readonly record struct StagePoint(double X, double Y);

/// <summary>Where the pairing animation puts its ring (the button) and its flash (the status light) on a per-model drawing.</summary>
public readonly record struct PairingAnchorPoints(StagePoint Button, StagePoint Light);

/// <summary>
/// Places the pairing guide's marks on a per-model drawing. The drawings are front views and only the status light is marked in the
/// file (<c>data-part="light"</c>); buttons are not drawn, so each is placed by a fixed fraction of a part's box:
/// the earbud for <see cref="PairingAnimationKind.EarbudButton"/>, otherwise the body (the case body for case kinds, the whole
/// headphone for the rest).
/// The fractions are per kind and were set by eye on the shipped drawings.
/// The setup button of an AirPods case is on the back, which a front view cannot show, so its ring sits on the case's right edge,
/// where a thumb would find it. The light uses the light part's centre when the drawing has one, otherwise the same fractions.
/// </summary>
public static class PairingAnchors
{
    private readonly record struct Spot(double X, double Y);

    private static (Spot Button, Spot Light) FractionsFor(PairingAnimationKind kind) => kind switch
    {
        PairingAnimationKind.CaseSetupButton => (new Spot(1.0, 0.5), new Spot(0.5, 0.3)),
        PairingAnimationKind.CaseDoubleTap => (new Spot(0.5, 0.72), new Spot(0.5, 0.3)),
        PairingAnimationKind.CaseButton => (new Spot(0.5, 0.7), new Spot(0.5, 0.3)),
        PairingAnimationKind.EarbudButton => (new Spot(0.5, 0.4), new Spot(0.5, 0.4)),
        PairingAnimationKind.MaxButton => (new Spot(0.9, 0.35), new Spot(0.86, 0.82)),
        PairingAnimationKind.ModuleButton => (new Spot(0.8, 0.45), new Spot(0.8, 0.62)),
        PairingAnimationKind.EarcupButton => (new Spot(0.92, 0.6), new Spot(0.92, 0.78)),
        PairingAnimationKind.RearEarcupButton => (new Spot(0.08, 0.6), new Spot(0.08, 0.78)),
        _ => (new Spot(0.5, 0.5), new Spot(0.5, 0.5)),
    };

    /// <summary>The anchors for <paramref name="kind"/> on <paramref name="document"/>, in stage units.</summary>
    public static PairingAnchorPoints For(PairingAnimationKind kind, ArtworkDocument document)
    {
        var fit = StageFit.Of(ArtworkBounds.ContentOf(document));
        var (button, light) = FractionsFor(kind);

        var area = kind == PairingAnimationKind.EarbudButton
            ? ArtworkBounds.OfPart(document, ArtPart.BudLeft)
            : ArtworkBounds.OfPart(document, ArtPart.Body);
        area ??= ArtworkBounds.ContentOf(document);

        StagePoint At(ArtBounds box, Spot s)
        {
            var (x, y) = fit.Map(box.MinX + (box.Width * s.X), box.MinY + (box.Height * s.Y));
            return new StagePoint(x, y);
        }

        var buttonPoint = At(area.Value, button);
        var lightPoint = ArtworkBounds.OfPart(document, ArtPart.Light) is { } lightBox
            ? At(lightBox, new Spot(0.5, 0.5))
            : At(area.Value, light);
        return new PairingAnchorPoints(buttonPoint, lightPoint);
    }
}
