// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace AppleDeviceHub.Core.Artwork;

/// <summary>
/// Parses a "Clasp artwork SVG" (a strict subset of SVG, see the artwork format notes) into a scene model.
/// Anything outside the subset is rejected with an <see cref="ArtworkFormatException"/> naming the element or attribute.
/// </summary>
public static class ArtworkParser
{
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";

    private static readonly string[] CommonShapeAttrs =
        ["id", "transform", "opacity", "fill", "stroke", "stroke-width", "stroke-linecap", "fill-opacity", "stroke-opacity"];

    private static readonly string[] LidAttrs = ["data-lid", "data-side-fill", "data-rim-stroke"];

    private static readonly string[] LidKeys =
        ["x", "width", "depth", "rx", "ry", "hingeY", "height", "tilt", "open", "domeRadius"];

    private static readonly string[] OptionalLidKeys = ["shine"];

    public static ArtworkDocument Parse(string svgText)
    {
        ArgumentNullException.ThrowIfNull(svgText);
        XDocument doc;
        try
        {
            doc = XDocument.Parse(svgText);
        }
        catch (XmlException ex)
        {
            throw new ArtworkFormatException("The artwork is not well-formed XML: " + ex.Message, ex);
        }

        return new Builder().Build(doc.Root ?? throw new ArtworkFormatException("The artwork has no root element."));
    }

    private sealed class Builder
    {
        private readonly Dictionary<string, ArtColor> _defaults = new(StringComparer.Ordinal);
        private readonly List<ArtColorway> _colorways = [];
        private readonly Dictionary<string, ArtGradient> _gradients = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ArtGroup> _defs = new(StringComparer.Ordinal);
        private ArtLidNode? _lid;

        public ArtworkDocument Build(XElement root)
        {
            if (root.Name != Svg + "svg")
            {
                throw new ArtworkFormatException($"The root element must be <svg xmlns=\"http://www.w3.org/2000/svg\"> (found <{root.Name.LocalName}>).");
            }

            CheckAttrs(root, "viewBox");
            var viewBox = Attr(root, "viewBox") ?? throw new ArtworkFormatException("<svg> needs a viewBox.");
            var (width, height) = ParseViewBox(viewBox);

            // Styles first so every var(--token) can be checked; then defs; then the drawn nodes.
            foreach (var e in root.Elements())
            {
                if (e.Name == Svg + "style")
                {
                    AddStyle(e);
                }
            }

            foreach (var way in _colorways)
            {
                foreach (var token in way.Overrides.Keys)
                {
                    if (!_defaults.ContainsKey(token))
                    {
                        throw new ArtworkFormatException($"Colourway '{way.Id}' overrides '--{token}', which is not in the default palette.");
                    }
                }
            }

            foreach (var e in root.Elements())
            {
                if (e.Name == Svg + "defs")
                {
                    ParseDefs(e);
                }
            }

            var nodes = new List<ArtNode>();
            foreach (var e in root.Elements())
            {
                if (e.Name == Svg + "style" || e.Name == Svg + "defs")
                {
                    continue;
                }

                nodes.Add(ParseNode(e, inDefs: false));
            }

            return new ArtworkDocument(width, height, nodes, _gradients, new ArtworkPalette(_defaults), _colorways, _lid);
        }

        private static (double Width, double Height) ParseViewBox(string text)
        {
            var parts = text.Split([' ', ',', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 4)
            {
                throw new ArtworkFormatException($"viewBox '{text}' must be four numbers.");
            }

            var v = new double[4];
            for (var i = 0; i < 4; i++)
            {
                v[i] = ParseNumber(parts[i], "viewBox");
            }

            if (v[0] != 0 || v[1] != 0 || v[2] <= 0 || v[3] <= 0)
            {
                throw new ArtworkFormatException($"viewBox '{text}' must start at 0 0 and have a positive size.");
            }

            return (v[2], v[3]);
        }

        private void AddStyle(XElement style)
        {
            CheckAttrs(style);
            var styles = ArtworkStyleScanner.Parse(style.Value);
            foreach (var (key, value) in styles.Defaults)
            {
                _defaults[key] = value;
            }

            foreach (var way in styles.Colorways)
            {
                if (_colorways.Exists(w => w.Id == way.Id))
                {
                    throw new ArtworkFormatException($"Colourway '{way.Id}' is declared twice.");
                }

                _colorways.Add(way);
            }
        }

        private void ParseDefs(XElement defs)
        {
            CheckAttrs(defs);
            foreach (var e in defs.Elements())
            {
                var name = e.Name == Svg + e.Name.LocalName ? e.Name.LocalName : "";
                switch (name)
                {
                    case "linearGradient":
                    case "radialGradient":
                        AddGradient(e, name == "radialGradient");
                        break;
                    case "g":
                    {
                        var id = Attr(e, "id") ?? throw new ArtworkFormatException("A <g> inside <defs> needs an id.");
                        EnsureNewId(id);
                        _defs[id] = (ArtGroup)ParseNode(e, inDefs: true);
                        break;
                    }

                    default:
                        throw new ArtworkFormatException($"Unsupported element <{e.Name.LocalName}> inside <defs> (only gradients and <g id>).");
                }
            }
        }

        private void EnsureNewId(string id)
        {
            if (_gradients.ContainsKey(id) || _defs.ContainsKey(id))
            {
                throw new ArtworkFormatException($"The id '{id}' is defined twice in <defs>.");
            }
        }

        private void AddGradient(XElement e, bool radial)
        {
            var element = radial ? "radialGradient" : "linearGradient";
            if (radial)
            {
                CheckAttrs(e, "id", "cx", "cy", "r");
            }
            else
            {
                CheckAttrs(e, "id", "x1", "y1", "x2", "y2");
            }

            var id = Attr(e, "id") ?? throw new ArtworkFormatException($"<{element}> needs an id.");
            EnsureNewId(id);
            var stops = new List<ArtGradientStop>();
            foreach (var s in e.Elements())
            {
                if (s.Name != Svg + "stop")
                {
                    throw new ArtworkFormatException($"Unsupported element <{s.Name.LocalName}> inside <{element}> (only <stop>).");
                }

                CheckAttrs(s, "offset", "stop-color", "stop-opacity");
                var offset = Num(s, "offset", 0);
                if (offset is < 0 or > 1)
                {
                    throw new ArtworkFormatException($"<stop> offset {offset} in gradient '{id}' must be between 0 and 1.");
                }

                var colorText = Attr(s, "stop-color");
                var color = colorText is null ? ArtColorRef.FromLiteral(new ArtColor(255, 0, 0, 0)) : ParseColorRef(colorText, "stop-color");
                stops.Add(new ArtGradientStop(offset, color, Opacity(s, "stop-opacity")));
            }

            if (stops.Count == 0)
            {
                throw new ArtworkFormatException($"Gradient '{id}' has no <stop>.");
            }

            _gradients[id] = radial
                ? new ArtRadialGradient(id, Num(e, "cx", 0.5), Num(e, "cy", 0.5), Num(e, "r", 0.5), stops)
                : new ArtLinearGradient(id, Num(e, "x1", 0), Num(e, "y1", 0), Num(e, "x2", 1), Num(e, "y2", 0), stops);
        }

        private ArtNode ParseNode(XElement e, bool inDefs)
        {
            if (e.Name.Namespace != Svg)
            {
                throw new ArtworkFormatException($"Unsupported element <{e.Name.LocalName}> (not in the SVG namespace).");
            }

            switch (e.Name.LocalName)
            {
                case "g":
                    return ParseGroup(e, inDefs);
                case "use":
                    return ParseUse(e);
                case "path":
                {
                    CheckAttrs(e, [.. CommonShapeAttrs, "d"]);
                    var d = Attr(e, "d") ?? throw new ArtworkFormatException("<path> needs a d attribute.");
                    IReadOnlyList<PathFigure> figures;
                    try
                    {
                        figures = SvgPathParser.Parse(d);
                    }
                    catch (ArtworkFormatException ex)
                    {
                        throw new ArtworkFormatException("Invalid d on <path>: " + ex.Message, ex);
                    }

                    return new ArtPathNode(Attr(e, "id"), Transform(e), Opacity(e, "opacity"), Style(e), figures);
                }

                case "rect":
                {
                    CheckAttrs(e, [.. CommonShapeAttrs, "x", "y", "width", "height", "rx", "ry"]);
                    var w = NonNegative(e, "width");
                    var h = NonNegative(e, "height");
                    var hasRx = Attr(e, "rx") is not null;
                    var hasRy = Attr(e, "ry") is not null;
                    var rx = hasRx ? NonNegative(e, "rx") : 0;
                    var ry = hasRy ? NonNegative(e, "ry") : 0;
                    if (hasRx && !hasRy)
                    {
                        ry = rx;
                    }
                    else if (hasRy && !hasRx)
                    {
                        rx = ry;
                    }

                    return new ArtRectNode(Attr(e, "id"), Transform(e), Opacity(e, "opacity"), Style(e), Num(e, "x", 0), Num(e, "y", 0), w, h, rx, ry);
                }

                case "ellipse":
                    CheckAttrs(e, [.. CommonShapeAttrs, "cx", "cy", "rx", "ry"]);
                    return new ArtEllipseNode(Attr(e, "id"), Transform(e), Opacity(e, "opacity"), Style(e), Num(e, "cx", 0), Num(e, "cy", 0), NonNegative(e, "rx"), NonNegative(e, "ry"));
                case "circle":
                {
                    CheckAttrs(e, [.. CommonShapeAttrs, "cx", "cy", "r"]);
                    var r = NonNegative(e, "r");
                    return new ArtEllipseNode(Attr(e, "id"), Transform(e), Opacity(e, "opacity"), Style(e), Num(e, "cx", 0), Num(e, "cy", 0), r, r);
                }

                default:
                    throw new ArtworkFormatException($"Unsupported element <{e.Name.LocalName}>.");
            }
        }

        private ArtNode ParseGroup(XElement e, bool inDefs)
        {
            var partName = Attr(e, "data-part");
            ArtPart? part = null;
            if (partName is not null)
            {
                if (!ArtPartNames.TryParse(partName, out var parsed))
                {
                    throw new ArtworkFormatException($"Unknown data-part '{partName}' on <g>.");
                }

                if (inDefs)
                {
                    throw new ArtworkFormatException("data-part is not allowed inside <defs>.");
                }

                part = parsed;
            }

            if (part == ArtPart.Lid)
            {
                return ParseLid(e);
            }

            CheckAttrs(e, "id", "data-part", "transform", "opacity");
            var children = e.Elements().Select(c => ParseNode(c, inDefs)).ToList();
            return new ArtGroup(Attr(e, "id"), part, Transform(e), Opacity(e, "opacity"), children);
        }

        private ArtLidNode ParseLid(XElement e)
        {
            CheckAttrs(e, ["id", "data-part", "transform", "opacity", .. LidAttrs]);
            if (_lid is not null)
            {
                throw new ArtworkFormatException("A drawing may have at most one lid (data-part=\"lid\").");
            }

            var spec = ParseLidSpec(Attr(e, "data-lid") ?? throw new ArtworkFormatException("The lid <g> needs a data-lid attribute."));
            var sideText = Attr(e, "data-side-fill") ?? throw new ArtworkFormatException("The lid <g> needs a data-side-fill attribute.");
            var rimText = Attr(e, "data-rim-stroke");
            var inside = e.Elements().Select(c => ParseNode(c, inDefs: false)).ToList();
            var lid = new ArtLidNode(
                Attr(e, "id"),
                Transform(e),
                Opacity(e, "opacity"),
                spec,
                ParsePaint(sideText, "data-side-fill"),
                rimText is null ? ArtPaint.None : ParsePaint(rimText, "data-rim-stroke"),
                inside);
            _lid = lid;
            return lid;
        }

        private static LidSpec ParseLidSpec(string text)
        {
            var values = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var pair in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var eq = pair.IndexOf('=', StringComparison.Ordinal);
                if (eq <= 0)
                {
                    throw new ArtworkFormatException($"data-lid entry '{pair}' must be name=value.");
                }

                var key = pair[..eq].Trim();
                if (Array.IndexOf(LidKeys, key) < 0 && Array.IndexOf(OptionalLidKeys, key) < 0)
                {
                    throw new ArtworkFormatException($"Unknown data-lid key '{key}'.");
                }

                if (!values.TryAdd(key, ParseNumber(pair[(eq + 1)..].Trim(), "data-lid " + key)))
                {
                    throw new ArtworkFormatException($"data-lid key '{key}' appears twice.");
                }
            }

            foreach (var key in LidKeys)
            {
                if (!values.ContainsKey(key))
                {
                    throw new ArtworkFormatException($"data-lid is missing '{key}'.");
                }
            }

            var spec = new LidSpec(
                values["x"], values["width"], values["depth"], values["rx"], values["ry"], values["hingeY"],
                values["height"], values["tilt"], values["open"], values["domeRadius"],
                values.TryGetValue("shine", out var shine) ? shine : LidSpec.DefaultShine);
            if (spec.Width <= 0 || spec.Depth <= 0 || spec.Height <= 0 || spec.DomeRadius < 0)
            {
                throw new ArtworkFormatException("data-lid width, depth and height must be positive and domeRadius must not be negative.");
            }

            if (spec.Shine is < 0 or > 1)
            {
                throw new ArtworkFormatException("data-lid shine must be between 0 and 1.");
            }

            if (spec.TiltDegrees is <= 0 or >= 90 || spec.OpenDegrees <= spec.TiltDegrees || spec.OpenDegrees >= 180)
            {
                throw new ArtworkFormatException("data-lid needs 0 < tilt < 90 and tilt < open < 180.");
            }

            return spec;
        }

        private ArtNode ParseUse(XElement e)
        {
            CheckAttrs(e, "href", "id", "transform", "opacity");
            var href = Attr(e, "href") ?? throw new ArtworkFormatException("<use> needs an href.");
            if (!href.StartsWith('#') || !_defs.TryGetValue(href[1..], out var target))
            {
                throw new ArtworkFormatException($"<use href=\"{href}\"> does not point to a <g id> in <defs>.");
            }

            return new ArtGroup(Attr(e, "id"), null, Transform(e), Opacity(e, "opacity"), [target]);
        }

        private ArtShapeStyle Style(XElement e)
        {
            var fillText = Attr(e, "fill");
            var strokeText = Attr(e, "stroke");
            var cap = Attr(e, "stroke-linecap");
            if (cap is not (null or "butt" or "round"))
            {
                throw new ArtworkFormatException($"stroke-linecap '{cap}' is not supported (use butt or round).");
            }

            return new ArtShapeStyle(
                fillText is null ? ArtPaint.Solid(ArtColorRef.FromLiteral(new ArtColor(255, 0, 0, 0))) : ParsePaint(fillText, "fill"),
                strokeText is null ? ArtPaint.None : ParsePaint(strokeText, "stroke"),
                Num(e, "stroke-width", 1),
                cap == "round",
                Opacity(e, "fill-opacity"),
                Opacity(e, "stroke-opacity"));
        }

        private ArtPaint ParsePaint(string text, string attribute)
        {
            var t = text.Trim();
            if (t == "none")
            {
                return ArtPaint.None;
            }

            if (t.StartsWith("url(", StringComparison.Ordinal))
            {
                if (!t.EndsWith(')') || t.Length < 7 || t[4] != '#')
                {
                    throw new ArtworkFormatException($"{attribute} '{text}' is not url(#id).");
                }

                var id = t[5..^1];
                if (!_gradients.ContainsKey(id))
                {
                    throw new ArtworkFormatException($"{attribute} '{text}' refers to a gradient that is not defined.");
                }

                return ArtPaint.Gradient(id);
            }

            return ArtPaint.Solid(ParseColorRef(t, attribute));
        }

        private ArtColorRef ParseColorRef(string text, string attribute)
        {
            var t = text.Trim();
            if (t.StartsWith("var(", StringComparison.Ordinal))
            {
                if (!t.EndsWith(')'))
                {
                    throw new ArtworkFormatException($"{attribute} '{text}' is not var(--token).");
                }

                var inner = t[4..^1].Trim();
                if (!inner.StartsWith("--", StringComparison.Ordinal) || inner.Length < 3)
                {
                    throw new ArtworkFormatException($"{attribute} '{text}' is not var(--token).");
                }

                var token = inner[2..];
                if (!_defaults.ContainsKey(token))
                {
                    throw new ArtworkFormatException($"{attribute} uses '--{token}', which is not in the default palette.");
                }

                return ArtColorRef.FromToken(token);
            }

            if (!ArtColor.TryParse(t, out var color))
            {
                throw new ArtworkFormatException($"{attribute} '{text}' is not a colour, var(--token) or url(#gradient).");
            }

            return ArtColorRef.FromLiteral(color);
        }

        private static ArtMatrix Transform(XElement e)
        {
            var text = Attr(e, "transform");
            if (text is null)
            {
                return ArtMatrix.Identity;
            }

            try
            {
                return ArtMatrix.Parse(text);
            }
            catch (ArtworkFormatException ex)
            {
                throw new ArtworkFormatException($"Invalid transform on <{e.Name.LocalName}>: {ex.Message}", ex);
            }
        }

        private static string? Attr(XElement e, string name) => e.Attribute(name)?.Value;

        private static void CheckAttrs(XElement e, params string[] allowed)
        {
            foreach (var a in e.Attributes())
            {
                if (a.IsNamespaceDeclaration)
                {
                    continue;
                }

                if (a.Name.Namespace != XNamespace.None || Array.IndexOf(allowed, a.Name.LocalName) < 0)
                {
                    throw new ArtworkFormatException($"The attribute '{a.Name.LocalName}' is not supported on <{e.Name.LocalName}>.");
                }
            }
        }

        private static double ParseNumber(string text, string what)
        {
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || !double.IsFinite(v))
            {
                throw new ArtworkFormatException($"'{text}' is not a number ({what}).");
            }

            return v;
        }

        private static double Num(XElement e, string name, double fallback)
        {
            var text = Attr(e, name);
            return text is null ? fallback : ParseNumber(text, $"{name} on <{e.Name.LocalName}>");
        }

        private static double NonNegative(XElement e, string name)
        {
            var text = Attr(e, name) ?? throw new ArtworkFormatException($"<{e.Name.LocalName}> needs a {name} attribute.");
            var v = ParseNumber(text, $"{name} on <{e.Name.LocalName}>");
            return v >= 0 ? v : throw new ArtworkFormatException($"{name} on <{e.Name.LocalName}> must not be negative.");
        }

        private static double Opacity(XElement e, string name) => Math.Clamp(Num(e, name, 1), 0, 1);
    }
}
