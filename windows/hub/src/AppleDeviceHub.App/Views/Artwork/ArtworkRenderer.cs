// SPDX-License-Identifier: GPL-3.0-or-later

using AppleDeviceHub.Core.Artwork;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WM = Microsoft.UI.Xaml.Media;
using Shapes = Microsoft.UI.Xaml.Shapes;

namespace AppleDeviceHub.App.Views;

/// <summary>
/// Turns a parsed drawing into a tree of XAML shapes. Everything is built in code (no XAML loading), which keeps it
/// Native AOT safe. Shapes sit at the origin of their canvas and carry one MatrixTransform that is "move to x,y, then the
/// SVG transform", so SVG user-space transforms such as <c>rotate(6 110 106)</c> behave as they do in SVG.
/// </summary>
internal sealed class ArtworkRenderer
{
    private readonly ArtworkDocument _document;
    private readonly ArtworkPalette _palette;
    private readonly Func<ArtLidNode, UIElement?>? _lidHandler;
    private readonly Dictionary<ArtPart, FrameworkElement> _parts = [];

    /// <param name="lidHandler">Builds the stand-in for a lid node (the hinged solid). Without one a lid node draws nothing.</param>
    public ArtworkRenderer(ArtworkDocument document, ArtworkPalette palette, Func<ArtLidNode, UIElement?>? lidHandler = null)
    {
        _document = document;
        _palette = palette;
        _lidHandler = lidHandler;
    }

    /// <summary>The element made for each part, so callers can animate it (the first group seen for a part wins).</summary>
    public IReadOnlyDictionary<ArtPart, FrameworkElement> Parts => _parts;

    /// <summary>A canvas the size of the drawing holding every top-level node.</summary>
    public Canvas RenderAll()
    {
        var canvas = new Canvas { Width = _document.Width, Height = _document.Height, IsHitTestVisible = false };
        foreach (var node in _document.Nodes)
        {
            if (Render(node) is { } element)
            {
                canvas.Children.Add(element);
            }
        }

        return canvas;
    }

    public UIElement? Render(ArtNode node)
    {
        switch (node)
        {
            case ArtGroup group:
            {
                var canvas = new Canvas { IsHitTestVisible = false };
                ApplyNode(canvas, group.Transform, group.Opacity);
                foreach (var child in group.Children)
                {
                    if (Render(child) is { } element)
                    {
                        canvas.Children.Add(element);
                    }
                }

                if (group.Part is { } part)
                {
                    _parts.TryAdd(part, canvas);
                }

                return canvas;
            }

            case ArtPathNode path:
                return RenderPath(path);
            case ArtRectNode rect:
            {
                var s = rect.Style;
                return CreateRect(rect.X, rect.Y, rect.Width, rect.Height, rect.Rx, rect.Ry, FillBrush(s), StrokeBrush(s), s.StrokeWidth, rect.Transform, rect.Opacity);
            }

            case ArtEllipseNode ellipse:
                return RenderEllipse(ellipse);
            case ArtLidNode lid:
                return _lidHandler?.Invoke(lid) ?? new Canvas { IsHitTestVisible = false };
            default:
                return null;
        }
    }

    /// <summary>
    /// A rounded rectangle at (x, y) in the user space of <paramref name="transform"/>. A stroked rectangle is grown by the stroke
    /// width so the stroke's centre line lies on the SVG outline (XAML draws a rectangle's stroke inside its bounds).
    /// </summary>
    public static Shapes.Rectangle CreateRect(
        double x, double y, double width, double height, double rx, double ry,
        WM.Brush? fill, WM.Brush? stroke, double strokeWidth, ArtMatrix transform, double opacity = 1)
    {
        var grow = stroke is null ? 0 : strokeWidth;
        var half = grow / 2;
        var rect = new Shapes.Rectangle
        {
            Width = Math.Max(0, width + grow),
            Height = Math.Max(0, height + grow),
            RadiusX = Math.Min(rx, width / 2) + (rx > 0 ? half : 0),
            RadiusY = Math.Min(ry, height / 2) + (ry > 0 ? half : 0),
            Fill = fill,
            Stroke = stroke,
            StrokeThickness = stroke is null ? 0 : strokeWidth,
            IsHitTestVisible = false,
        };
        ApplyNode(rect, transform * ArtMatrix.Translate(x - half, y - half), opacity);
        return rect;
    }

    private Shapes.Ellipse RenderEllipse(ArtEllipseNode e)
    {
        var s = e.Style;
        var stroke = StrokeBrush(s);
        var grow = stroke is null ? 0 : s.StrokeWidth;
        var half = grow / 2;
        var shape = new Shapes.Ellipse
        {
            Width = Math.Max(0, (2 * e.Rx) + grow),
            Height = Math.Max(0, (2 * e.Ry) + grow),
            Fill = FillBrush(s),
            Stroke = stroke,
            StrokeThickness = stroke is null ? 0 : s.StrokeWidth,
            IsHitTestVisible = false,
        };
        ApplyNode(shape, e.Transform * ArtMatrix.Translate(e.Cx - e.Rx - half, e.Cy - e.Ry - half), e.Opacity);
        return shape;
    }

    private Shapes.Path RenderPath(ArtPathNode p)
    {
        var s = p.Style;
        var stroke = StrokeBrush(s);
        var geometry = new WM.PathGeometry { FillRule = WM.FillRule.Nonzero };
        foreach (var figure in p.Figures)
        {
            geometry.Figures.Add(ToFigure(figure));
        }

        var shape = new Shapes.Path
        {
            Data = geometry,
            Fill = FillBrush(s),
            Stroke = stroke,
            StrokeThickness = stroke is null ? 0 : s.StrokeWidth,
            StrokeMiterLimit = 4,
            IsHitTestVisible = false,
        };
        if (s.RoundCap)
        {
            shape.StrokeStartLineCap = WM.PenLineCap.Round;
            shape.StrokeEndLineCap = WM.PenLineCap.Round;
        }

        ApplyNode(shape, p.Transform, p.Opacity);
        return shape;
    }

    private static WM.PathFigure ToFigure(Core.Artwork.PathFigure source)
    {
        var figure = new WM.PathFigure
        {
            StartPoint = Pt(source.Start),
            IsClosed = source.Closed,
            IsFilled = true,
        };
        foreach (var segment in source.Segments)
        {
            switch (segment)
            {
                case Core.Artwork.LineSegment line:
                    figure.Segments.Add(new WM.LineSegment { Point = Pt(line.To) });
                    break;
                case CubicSegment cubic:
                    figure.Segments.Add(new WM.BezierSegment { Point1 = Pt(cubic.Control1), Point2 = Pt(cubic.Control2), Point3 = Pt(cubic.To) });
                    break;
                case QuadraticSegment quad:
                    figure.Segments.Add(new WM.QuadraticBezierSegment { Point1 = Pt(quad.Control), Point2 = Pt(quad.To) });
                    break;
                case Core.Artwork.ArcSegment arc:
                    figure.Segments.Add(new WM.ArcSegment
                    {
                        Point = Pt(arc.To),
                        Size = new Windows.Foundation.Size(arc.Rx, arc.Ry),
                        RotationAngle = arc.XAxisRotation,
                        IsLargeArc = arc.LargeArc,
                        SweepDirection = arc.Sweep ? WM.SweepDirection.Clockwise : WM.SweepDirection.Counterclockwise,
                    });
                    break;
            }
        }

        return figure;
    }

    private static Windows.Foundation.Point Pt(ArtPoint p) => new(p.X, p.Y);

    internal static void ApplyNode(UIElement element, ArtMatrix transform, double opacity)
    {
        if (!transform.IsIdentity)
        {
            element.RenderTransform = ToTransform(transform);
        }

        if (opacity < 1)
        {
            element.Opacity = opacity;
        }
    }

    private static WM.MatrixTransform ToTransform(ArtMatrix m) =>
        new() { Matrix = new WM.Matrix(m.A, m.B, m.C, m.D, m.E, m.F) };

    private WM.Brush? FillBrush(ArtShapeStyle s) => BrushFor(s.Fill, s.FillOpacity);

    private WM.Brush? StrokeBrush(ArtShapeStyle s) => s.StrokeWidth > 0 ? BrushFor(s.Stroke, s.StrokeOpacity) : null;

    /// <summary>A brush for a fill or stroke, or null for no paint. <paramref name="opacity"/> goes on the brush.</summary>
    public WM.Brush? BrushFor(ArtPaint paint, double opacity)
    {
        WM.Brush? brush;
        switch (paint.Kind)
        {
            case ArtPaintKind.Solid:
                brush = new WM.SolidColorBrush(ToColor(paint.Color.Resolve(_palette), 1));
                break;
            case ArtPaintKind.Gradient when paint.GradientId is { } id && _document.Gradients.TryGetValue(id, out var gradient):
                brush = GradientBrush(gradient);
                break;
            default:
                return null;
        }

        if (opacity < 1)
        {
            brush.Opacity = opacity;
        }

        return brush;
    }

    private WM.Brush GradientBrush(ArtGradient gradient)
    {
        switch (gradient)
        {
            case ArtLinearGradient linear:
            {
                var brush = new WM.LinearGradientBrush
                {
                    MappingMode = WM.BrushMappingMode.RelativeToBoundingBox,
                    StartPoint = new Windows.Foundation.Point(linear.X1, linear.Y1),
                    EndPoint = new Windows.Foundation.Point(linear.X2, linear.Y2),
                };
                AddStops(brush.GradientStops, gradient.Stops);
                return brush;
            }

            case ArtRadialGradient radial:
            {
                var centre = new Windows.Foundation.Point(radial.Cx, radial.Cy);
                var brush = new WM.RadialGradientBrush
                {
                    MappingMode = WM.BrushMappingMode.RelativeToBoundingBox,
                    Center = centre,
                    GradientOrigin = centre,
                    RadiusX = radial.R,
                    RadiusY = radial.R,
                };
                AddStops(brush.GradientStops, gradient.Stops);
                return brush;
            }

            default:
                return new WM.SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
        }
    }

    private void AddStops(IList<WM.GradientStop> stops, IReadOnlyList<ArtGradientStop> source)
    {
        foreach (var stop in source)
        {
            stops.Add(new WM.GradientStop { Offset = stop.Offset, Color = ToColor(stop.Color.Resolve(_palette), stop.Opacity) });
        }
    }

    private static Windows.UI.Color ToColor(ArtColor c, double opacity) =>
        Windows.UI.Color.FromArgb((byte)Math.Clamp(Math.Round(c.A * opacity), 0, 255), c.R, c.G, c.B);
}
