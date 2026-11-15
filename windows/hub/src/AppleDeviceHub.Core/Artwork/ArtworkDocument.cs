// SPDX-License-Identifier: GPL-3.0-or-later

namespace AppleDeviceHub.Core.Artwork;

/// <summary>A piece of a drawing the app animates (<c>data-part</c>).</summary>
public enum ArtPart
{
    Shadow = 1,
    Body = 2,
    Buds = 3,
    BudLeft = 4,
    BudRight = 5,
    Light = 6,
    Lid = 7,
}

public static class ArtPartNames
{
    public static bool TryParse(string? name, out ArtPart part)
    {
        switch (name)
        {
            case "shadow": part = ArtPart.Shadow; return true;
            case "body": part = ArtPart.Body; return true;
            case "buds": part = ArtPart.Buds; return true;
            case "bud-left": part = ArtPart.BudLeft; return true;
            case "bud-right": part = ArtPart.BudRight; return true;
            case "light": part = ArtPart.Light; return true;
            case "lid": part = ArtPart.Lid; return true;
            default: part = default; return false;
        }
    }

    public static string ToAttribute(ArtPart part) => part switch
    {
        ArtPart.Shadow => "shadow",
        ArtPart.Body => "body",
        ArtPart.Buds => "buds",
        ArtPart.BudLeft => "bud-left",
        ArtPart.BudRight => "bud-right",
        ArtPart.Light => "light",
        ArtPart.Lid => "lid",
        _ => throw new ArgumentOutOfRangeException(nameof(part)),
    };
}

public readonly record struct ArtGradientStop(double Offset, ArtColorRef Color, double Opacity);

/// <summary>A gradient in objectBoundingBox units (all coordinates are fractions of the painted shape's box).</summary>
public abstract record ArtGradient(string Id, IReadOnlyList<ArtGradientStop> Stops);

public sealed record ArtLinearGradient(string Id, double X1, double Y1, double X2, double Y2, IReadOnlyList<ArtGradientStop> Stops)
    : ArtGradient(Id, Stops);

public sealed record ArtRadialGradient(string Id, double Cx, double Cy, double R, IReadOnlyList<ArtGradientStop> Stops)
    : ArtGradient(Id, Stops);

/// <summary>Fill and stroke settings shared by every shape.</summary>
public sealed record ArtShapeStyle(
    ArtPaint Fill,
    ArtPaint Stroke,
    double StrokeWidth,
    bool RoundCap,
    double FillOpacity,
    double StrokeOpacity);

public abstract record ArtNode(string? Id, ArtMatrix Transform, double Opacity);

public sealed record ArtGroup(string? Id, ArtPart? Part, ArtMatrix Transform, double Opacity, IReadOnlyList<ArtNode> Children)
    : ArtNode(Id, Transform, Opacity);

public abstract record ArtShapeNode(string? Id, ArtMatrix Transform, double Opacity, ArtShapeStyle Style)
    : ArtNode(Id, Transform, Opacity);

public sealed record ArtPathNode(string? Id, ArtMatrix Transform, double Opacity, ArtShapeStyle Style, IReadOnlyList<PathFigure> Figures)
    : ArtShapeNode(Id, Transform, Opacity, Style);

public sealed record ArtRectNode(
    string? Id, ArtMatrix Transform, double Opacity, ArtShapeStyle Style,
    double X, double Y, double Width, double Height, double Rx, double Ry)
    : ArtShapeNode(Id, Transform, Opacity, Style);

/// <summary>An ellipse; a circle is stored with equal radii.</summary>
public sealed record ArtEllipseNode(
    string? Id, ArtMatrix Transform, double Opacity, ArtShapeStyle Style,
    double Cx, double Cy, double Rx, double Ry)
    : ArtShapeNode(Id, Transform, Opacity, Style);

/// <summary>
/// The lid solid. <see cref="InsideChildren"/> are the lid's inside face, in rim-local coordinates
/// (x as drawn, y from 0 at the hinge line to <see cref="LidSpec.Depth"/> at the front edge).
/// </summary>
public sealed record ArtLidNode(
    string? Id, ArtMatrix Transform, double Opacity,
    LidSpec Spec, ArtPaint SideFill, ArtPaint RimStroke, IReadOnlyList<ArtNode> InsideChildren)
    : ArtNode(Id, Transform, Opacity);

/// <summary>A parsed drawing: scene nodes in paint order plus gradients, palette and colourways.</summary>
public sealed record ArtworkDocument(
    double Width,
    double Height,
    IReadOnlyList<ArtNode> Nodes,
    IReadOnlyDictionary<string, ArtGradient> Gradients,
    ArtworkPalette DefaultPalette,
    IReadOnlyList<ArtColorway> Colorways,
    ArtLidNode? Lid)
{
    /// <summary>The default palette overlaid with the colourway's overrides; an unknown or null id gives the defaults.</summary>
    public ArtworkPalette PaletteFor(string? colorwayId)
    {
        if (colorwayId is not null)
        {
            foreach (var way in Colorways)
            {
                if (string.Equals(way.Id, colorwayId, StringComparison.Ordinal))
                {
                    return DefaultPalette.Overlay(way.Overrides);
                }
            }
        }

        return DefaultPalette;
    }
}
