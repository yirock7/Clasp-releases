// SPDX-License-Identifier: GPL-3.0-or-later

using AppleDeviceHub.Core.Artwork;

namespace AppleDeviceHub.Core.Tests.Artwork;

public sealed class ArtworkParserTests
{
    private const string Open = "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 300 230\">";
    private const string Styles = "<style>svg { --a: #112233; --b: #445566; } svg[data-colorway=\"alt\"] { --a: #ffffff; }</style>";
    private const string LidData = "x=71;width=158;depth=26;rx=26;ry=13;hingeY=107;height=34;tilt=30;open=104;domeRadius=24";

    private static ArtworkDocument Parse(string body) => ArtworkParser.Parse(Open + Styles + body + "</svg>");

    [Fact]
    public void A_document_with_every_element_parses()
    {
        var doc = ArtworkParser.Parse($$"""
            {{Open}}
              {{Styles}}
              <defs>
                <linearGradient id="lg" x1="0" y1="0" x2="0" y2="1">
                  <stop offset="0" stop-color="var(--a)"/>
                  <stop offset="1" stop-color="#abc" stop-opacity=".5"/>
                </linearGradient>
                <radialGradient id="rg" cx=".4" cy=".35" r=".8">
                  <stop offset="0" stop-color="#fff"/>
                  <stop offset="1" stop-color="#fff" stop-opacity="0"/>
                </radialGradient>
                <g id="bud"><circle cx="5" cy="6" r="2" fill="url(#rg)"/></g>
              </defs>
              <g data-part="shadow" opacity=".5"><ellipse cx="150" cy="208" rx="86" ry="8" fill="var(--b)"/></g>
              <g data-part="lid" id="lid" data-lid="{{LidData}}" data-side-fill="url(#lg)" data-rim-stroke="var(--a)">
                <rect x="79" y="3.5" width="142" height="19" rx="20" fill="url(#lg)"/>
              </g>
              <g data-part="body" transform="translate(1,2)">
                <path id="p" d="M0,0 L10,0 L10,10 Z" fill="none" stroke="var(--a)" stroke-width="2" stroke-linecap="round" stroke-opacity=".4"/>
                <rect x="1" y="2" width="3" height="4" fill="#fff" fill-opacity=".25"/>
              </g>
              <g data-part="buds">
                <g data-part="bud-left"><use href="#bud"/></g>
                <g data-part="bud-right"><use href="#bud" transform="translate(300,0) scale(-1,1)"/></g>
              </g>
              <g data-part="light"><circle cx="150" cy="160" r="2.3" fill="#3ddc66"/></g>
            </svg>
            """);

        Assert.Equal(300, doc.Width);
        Assert.Equal(230, doc.Height);
        Assert.Equal(2, doc.Gradients.Count);
        var lg = Assert.IsType<ArtLinearGradient>(doc.Gradients["lg"]);
        Assert.Equal(2, lg.Stops.Count);
        Assert.True(lg.Stops[0].Color.IsToken);
        Assert.Equal(0.5, lg.Stops[1].Opacity);
        var rg = Assert.IsType<ArtRadialGradient>(doc.Gradients["rg"]);
        Assert.Equal(0.4, rg.Cx);
        Assert.Equal(["alt"], doc.Colorways.Select(c => c.Id).ToArray());

        Assert.Equal(5, doc.Nodes.Count);
        var shadow = Assert.IsType<ArtGroup>(doc.Nodes[0]);
        Assert.Equal(ArtPart.Shadow, shadow.Part);
        Assert.Equal(0.5, shadow.Opacity);
        var shadowEllipse = Assert.IsType<ArtEllipseNode>(shadow.Children[0]);
        Assert.Equal(86, shadowEllipse.Rx);

        var lid = Assert.IsType<ArtLidNode>(doc.Nodes[1]);
        Assert.Same(lid, doc.Lid);
        Assert.Equal("lid", lid.Id);
        Assert.Equal(26, lid.Spec.Depth);
        Assert.Equal(104, lid.Spec.OpenDegrees);
        Assert.Equal(ArtPaintKind.Gradient, lid.SideFill.Kind);
        Assert.Equal("lg", lid.SideFill.GradientId);
        Assert.True(lid.RimStroke.Color.IsToken);
        var inside = Assert.IsType<ArtRectNode>(Assert.Single(lid.InsideChildren));
        Assert.Equal(20, inside.Rx);
        Assert.Equal(20, inside.Ry); // ry defaults to rx

        var body = Assert.IsType<ArtGroup>(doc.Nodes[2]);
        Assert.Equal(ArtPart.Body, body.Part);
        Assert.Equal(new ArtMatrix(1, 0, 0, 1, 1, 2), body.Transform);
        var path = Assert.IsType<ArtPathNode>(body.Children[0]);
        Assert.Equal("p", path.Id);
        Assert.Single(path.Figures);
        Assert.True(path.Style.Fill.Kind == ArtPaintKind.None);
        Assert.Equal(2, path.Style.StrokeWidth);
        Assert.True(path.Style.RoundCap);
        Assert.Equal(0.4, path.Style.StrokeOpacity);
        var rect = Assert.IsType<ArtRectNode>(body.Children[1]);
        Assert.Equal(0.25, rect.Style.FillOpacity);
        Assert.Equal(ArtPaintKind.None, rect.Style.Stroke.Kind);

        var buds = Assert.IsType<ArtGroup>(doc.Nodes[3]);
        var right = Assert.IsType<ArtGroup>(buds.Children[1]);
        Assert.Equal(ArtPart.BudRight, right.Part);
        var use = Assert.IsType<ArtGroup>(Assert.Single(right.Children));
        Assert.Equal(200, use.Transform.Apply(100, 0).X, 9);
        var def = Assert.IsType<ArtGroup>(Assert.Single(use.Children));
        var circle = Assert.IsType<ArtEllipseNode>(Assert.Single(def.Children));
        Assert.Equal(2, circle.Rx);
        Assert.Equal(2, circle.Ry);

        var light = Assert.IsType<ArtGroup>(doc.Nodes[4]);
        Assert.Equal(ArtPart.Light, light.Part);
    }

    [Fact]
    public void Fill_defaults_to_black_and_stroke_to_none()
    {
        var doc = Parse("<rect x=\"0\" y=\"0\" width=\"1\" height=\"1\"/>");
        var rect = Assert.IsType<ArtRectNode>(Assert.Single(doc.Nodes));
        Assert.Equal(ArtPaintKind.Solid, rect.Style.Fill.Kind);
        Assert.Equal(new ArtColor(255, 0, 0, 0), rect.Style.Fill.Color.Literal);
        Assert.Equal(ArtPaintKind.None, rect.Style.Stroke.Kind);
    }

    [Fact]
    public void Colorway_display_names_and_palette_overlay_come_through_the_document()
    {
        var doc = Parse("");
        Assert.Equal("Alt", doc.Colorways[0].DisplayName);
        Assert.Equal(new ArtColor(255, 255, 255, 255), doc.PaletteFor("alt").Resolve("a"));
    }

    [Theory]
    [InlineData("<filter id=\"f\"/>", "filter")]
    [InlineData("<text>hi</text>", "text")]
    [InlineData("<rect width=\"1\" height=\"1\" style=\"fill:red\"/>", "style")]
    [InlineData("<ellipse cx=\"1\" cy=\"1\" rx=\"1\" ry=\"1\" filter=\"url(#f)\"/>", "filter")]
    [InlineData("<rect width=\"1\" height=\"1\" clip-path=\"url(#c)\"/>", "clip-path")]
    [InlineData("<g clip-path=\"url(#c)\"/>", "clip-path")]
    [InlineData("<g data-part=\"wheel\"/>", "wheel")]
    [InlineData("<rect width=\"1\" height=\"1\" fill=\"var(--missing)\"/>", "missing")]
    [InlineData("<rect width=\"1\" height=\"1\" fill=\"url(#nope)\"/>", "nope")]
    [InlineData("<use href=\"#nope\"/>", "nope")]
    [InlineData("<rect width=\"1\" height=\"1\" fill=\"red\"/>", "fill")]
    [InlineData("<rect width=\"1\" height=\"abc\"/>", "height")]
    [InlineData("<rect width=\"1\" height=\"1\" stroke-linecap=\"square\"/>", "square")]
    [InlineData("<path d=\"M0,0 L5\"/>", "path")]
    [InlineData("<g transform=\"skewX(3)\"/>", "skewX")]
    [InlineData("<g data-lid=\"x=1\"/>", "data-lid")]
    [InlineData("<defs><rect width=\"1\" height=\"1\"/></defs>", "rect")]
    public void Anything_outside_the_subset_is_rejected_with_its_name(string body, string mentions)
    {
        var ex = Assert.Throws<ArtworkFormatException>(() => Parse(body));
        Assert.Contains(mentions, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_lids_are_rejected()
    {
        var lid = $"<g data-part=\"lid\" data-lid=\"{LidData}\" data-side-fill=\"var(--a)\"/>";
        var ex = Assert.Throws<ArtworkFormatException>(() => Parse(lid + lid));
        Assert.Contains("one lid", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("x=71;width=158")]
    [InlineData("x=71;width=158;depth=26;rx=26;ry=13;hingeY=107;height=34;tilt=30;open=104;domeRadius=24;bogus=1")]
    [InlineData("x=71;width=158;depth=26;rx=26;ry=13;hingeY=107;height=34;tilt=30;open=104;domeRadius=24;shine=1.2")]
    [InlineData("x=71;width=158;depth=26;rx=26;ry=13;hingeY=107;height=34;tilt=30;open=104;domeRadius=24;shine=-0.1")]
    [InlineData("x=71;width=158;depth=26;rx=26;ry=13;hingeY=107;height=34;tilt=30;open=20;domeRadius=24")]
    public void Bad_lid_data_is_rejected(string data)
    {
        Assert.Throws<ArtworkFormatException>(() => Parse($"<g data-part=\"lid\" data-lid=\"{data}\" data-side-fill=\"var(--a)\"/>"));
    }

    [Fact]
    public void Lid_shine_defaults_and_can_be_set()
    {
        static ArtLidNode LidOf(string data) =>
            Assert.IsType<ArtLidNode>(Assert.Single(Parse($"<g data-part=\"lid\" data-lid=\"{data}\" data-side-fill=\"var(--a)\"/>").Nodes));

        Assert.Equal(0.55, LidOf(LidData).Spec.Shine);
        Assert.Equal(0.12, LidOf(LidData + ";shine=0.12").Spec.Shine);
        Assert.Equal(0, LidOf(LidData + ";shine=0").Spec.Shine);
    }

    [Fact]
    public void Document_level_problems_are_rejected()
    {
        Assert.Throws<ArtworkFormatException>(() => ArtworkParser.Parse("<svg"));
        Assert.Throws<ArtworkFormatException>(() => ArtworkParser.Parse("<svg xmlns=\"http://www.w3.org/2000/svg\"/>"));
        Assert.Throws<ArtworkFormatException>(() => ArtworkParser.Parse("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"5 0 10 10\"/>"));
        Assert.Throws<ArtworkFormatException>(() => ArtworkParser.Parse("<svg viewBox=\"0 0 10 10\"/>"));
        Assert.Throws<ArtworkFormatException>(() => ArtworkParser.Parse(Open + "<style>svg { --a: #fff } svg[data-colorway=\"x\"] { --zzz: #000 }</style></svg>"));
    }
}
