// SPDX-License-Identifier: GPL-3.0-or-later

namespace AppleDeviceHub.Core.Artwork;

/// <summary>An axis-aligned box in drawing units.</summary>
public readonly record struct ArtBounds(double MinX, double MinY, double MaxX, double MaxY)
{
    public double Width => MaxX - MinX;

    public double Height => MaxY - MinY;

    public double CentreX => (MinX + MaxX) / 2;

    public double CentreY => (MinY + MaxY) / 2;
}

/// <summary>
/// Where a part of a drawing sits, from its geometry. Curves are bounded by their control points, strokes are ignored and a rotated
/// ellipse is bounded by its rotated box, which is close enough to centre and size a part (the earbud pulled out of the case).
/// Not used for painting.
/// </summary>
public static class ArtworkBounds
{
    /// <summary>The box of the first group declared as <paramref name="part"/>, in drawing coordinates, or null when there is none or it is empty.</summary>
    public static ArtBounds? OfPart(ArtworkDocument document, ArtPart part)
    {
        foreach (var node in document.Nodes)
        {
            if (FindPart(node, ArtMatrix.Identity, part) is { } found)
            {
                return OfNode(found.Node, found.Parent);
            }
        }

        return null;
    }

    private static (ArtNode Node, ArtMatrix Parent)? FindPart(ArtNode node, ArtMatrix parent, ArtPart part)
    {
        if (node is not ArtGroup group)
        {
            return null;
        }

        if (group.Part == part)
        {
            return (node, parent);
        }

        var matrix = parent * group.Transform;
        foreach (var child in group.Children)
        {
            if (FindPart(child, matrix, part) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>How much of the soft contact shadow counts as content: only its centre line, since the faded rim is not worth shrinking the drawing for.</summary>
    private const double ShadowCoreX = 0.75;

    private const double ShadowCoreY = 0.0;

    /// <summary>
    /// The box the drawing should be fitted to: every shape, the lid at its closed and fully open angles (so the opening lid is never
    /// cut off) and the core of the shadow. Fitting to this instead of the whole viewBox removes the empty margin around the case.
    /// </summary>
    public static ArtBounds ContentOf(ArtworkDocument document)
    {
        var box = new Accumulator();
        foreach (var node in document.Nodes)
        {
            if (node is ArtGroup { Part: ArtPart.Shadow } shadow)
            {
                if (OfNode(shadow, ArtMatrix.Identity) is { } s)
                {
                    var hw = s.Width * ShadowCoreX / 2;
                    var hh = s.Height * ShadowCoreY / 2;
                    box.Point(ArtMatrix.Identity, s.CentreX - hw, s.CentreY - hh);
                    box.Point(ArtMatrix.Identity, s.CentreX + hw, s.CentreY + hh);
                }

                continue;
            }

            Add(node, ArtMatrix.Identity, ref box);
        }

        if (document.Lid is { } lid)
        {
            var spec = lid.Spec;
            foreach (var degrees in new[] { 0.0, spec.OpenDegrees })
            {
                var projection = LidSolid.Projection(spec, degrees);
                for (var i = 0; i <= 8; i++)
                {
                    var shape = LidSolid.SliceShape(spec, i, 8);
                    var m = lid.Transform * LidSolid.SliceTransform(spec, shape, projection);
                    box.Point(m, shape.X, shape.YLocal);
                    box.Point(m, shape.X + shape.Width, shape.YLocal + shape.Depth);
                }
            }
        }

        return box.IsEmpty ? new ArtBounds(0, 0, document.Width, document.Height) : new ArtBounds(box.MinX, box.MinY, box.MaxX, box.MaxY);
    }

    /// <summary>The box of one node; <paramref name="parent"/> maps the node's parent space to drawing space.</summary>
    public static ArtBounds? OfNode(ArtNode node, ArtMatrix parent)
    {
        var box = new Accumulator();
        Add(node, parent, ref box);
        return box.IsEmpty ? null : new ArtBounds(box.MinX, box.MinY, box.MaxX, box.MaxY);
    }

    private struct Accumulator
    {
        public double MinX = double.MaxValue;
        public double MinY = double.MaxValue;
        public double MaxX = double.MinValue;
        public double MaxY = double.MinValue;

        public Accumulator()
        {
        }

        public readonly bool IsEmpty => MinX > MaxX;

        public void Point(ArtMatrix m, double x, double y)
        {
            var (px, py) = m.Apply(x, y);
            MinX = Math.Min(MinX, px);
            MinY = Math.Min(MinY, py);
            MaxX = Math.Max(MaxX, px);
            MaxY = Math.Max(MaxY, py);
        }
    }

    private static void Add(ArtNode node, ArtMatrix parent, ref Accumulator box)
    {
        var m = parent * node.Transform;
        switch (node)
        {
            case ArtGroup group:
                foreach (var child in group.Children)
                {
                    Add(child, m, ref box);
                }

                break;
            case ArtPathNode path:
                foreach (var figure in path.Figures)
                {
                    box.Point(m, figure.Start.X, figure.Start.Y);
                    foreach (var segment in figure.Segments)
                    {
                        switch (segment)
                        {
                            case CubicSegment cubic:
                                box.Point(m, cubic.Control1.X, cubic.Control1.Y);
                                box.Point(m, cubic.Control2.X, cubic.Control2.Y);
                                break;
                            case QuadraticSegment quad:
                                box.Point(m, quad.Control.X, quad.Control.Y);
                                break;
                        }

                        box.Point(m, segment.To.X, segment.To.Y);
                    }
                }

                break;
            case ArtRectNode rect:
                box.Point(m, rect.X, rect.Y);
                box.Point(m, rect.X + rect.Width, rect.Y);
                box.Point(m, rect.X, rect.Y + rect.Height);
                box.Point(m, rect.X + rect.Width, rect.Y + rect.Height);
                break;
            case ArtEllipseNode ellipse:
                box.Point(m, ellipse.Cx - ellipse.Rx, ellipse.Cy - ellipse.Ry);
                box.Point(m, ellipse.Cx + ellipse.Rx, ellipse.Cy - ellipse.Ry);
                box.Point(m, ellipse.Cx - ellipse.Rx, ellipse.Cy + ellipse.Ry);
                box.Point(m, ellipse.Cx + ellipse.Rx, ellipse.Cy + ellipse.Ry);
                break;
        }
    }
}
