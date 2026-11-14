// SPDX-License-Identifier: GPL-3.0-or-later

using AppleDeviceHub.Core.Artwork;

namespace AppleDeviceHub.Core.Tests.Artwork;

public sealed class SvgPathParserTests
{
    private static PathFigure Single(string d)
    {
        var figures = SvgPathParser.Parse(d);
        Assert.Single(figures);
        return figures[0];
    }

    [Fact]
    public void Absolute_commands_produce_absolute_segments()
    {
        var f = Single("M10,20 L30,40 H50 V60 C1,2 3,4 5,6 Q7,8 9,10 A5,6 30 1 0 11,12 Z");
        Assert.Equal(new ArtPoint(10, 20), f.Start);
        Assert.True(f.Closed);
        Assert.Equal(6, f.Segments.Count);
        Assert.Equal(new LineSegment(new ArtPoint(30, 40)), f.Segments[0]);
        Assert.Equal(new LineSegment(new ArtPoint(50, 40)), f.Segments[1]);
        Assert.Equal(new LineSegment(new ArtPoint(50, 60)), f.Segments[2]);
        Assert.Equal(new CubicSegment(new ArtPoint(1, 2), new ArtPoint(3, 4), new ArtPoint(5, 6)), f.Segments[3]);
        Assert.Equal(new QuadraticSegment(new ArtPoint(7, 8), new ArtPoint(9, 10)), f.Segments[4]);
        Assert.Equal(new ArcSegment(5, 6, 30, true, false, new ArtPoint(11, 12)), f.Segments[5]);
    }

    [Fact]
    public void Relative_commands_are_made_absolute()
    {
        var f = Single("m10,10 l5,5 h5 v-5 c1,1 2,2 3,3 q1,1 2,2 a4,4 0 0 1 6,0");
        Assert.Equal(new ArtPoint(10, 10), f.Start);
        Assert.False(f.Closed);
        Assert.Equal(new ArtPoint(15, 15), f.Segments[0].To);
        Assert.Equal(new ArtPoint(20, 15), f.Segments[1].To);
        Assert.Equal(new ArtPoint(20, 10), f.Segments[2].To);
        Assert.Equal(new CubicSegment(new ArtPoint(21, 11), new ArtPoint(22, 12), new ArtPoint(23, 13)), f.Segments[3]);
        Assert.Equal(new QuadraticSegment(new ArtPoint(24, 14), new ArtPoint(25, 15)), f.Segments[4]);
        Assert.Equal(new ArcSegment(4, 4, 0, false, true, new ArtPoint(31, 15)), f.Segments[5]);
    }

    [Fact]
    public void Smooth_commands_reflect_the_previous_control_point()
    {
        var f = Single("M0,0 C0,10 10,10 10,0 S20,-10 20,0 Q25,5 30,0 T40,0");
        Assert.Equal(new CubicSegment(new ArtPoint(10, -10), new ArtPoint(20, -10), new ArtPoint(20, 0)), f.Segments[1]);
        Assert.Equal(new QuadraticSegment(new ArtPoint(35, -5), new ArtPoint(40, 0)), f.Segments[3]);
    }

    [Fact]
    public void Smooth_command_without_a_previous_curve_uses_the_current_point()
    {
        var f = Single("M5,5 S10,10 20,20");
        Assert.Equal(new CubicSegment(new ArtPoint(5, 5), new ArtPoint(10, 10), new ArtPoint(20, 20)), f.Segments[0]);
    }

    [Fact]
    public void Extra_pairs_after_move_are_implicit_lines_and_commands_repeat()
    {
        var f = Single("M0,0 10,0 10,10 l1,1 2,2");
        Assert.Equal(4, f.Segments.Count);
        Assert.Equal(new ArtPoint(10, 0), f.Segments[0].To);
        Assert.Equal(new ArtPoint(10, 10), f.Segments[1].To);
        Assert.Equal(new ArtPoint(11, 11), f.Segments[2].To);
        Assert.Equal(new ArtPoint(13, 13), f.Segments[3].To);
    }

    [Fact]
    public void Numbers_may_start_with_a_dot_use_exponents_and_run_together()
    {
        var f = Single("M-.5,.25 L1e-3,2E1 3-4 .5.5");
        Assert.Equal(new ArtPoint(-0.5, 0.25), f.Start);
        Assert.Equal(new ArtPoint(0.001, 20), f.Segments[0].To);
        Assert.Equal(new ArtPoint(3, -4), f.Segments[1].To);
        Assert.Equal(new ArtPoint(0.5, 0.5), f.Segments[2].To);
    }

    [Fact]
    public void Arc_flags_can_be_written_without_separators()
    {
        var f = Single("M0,0 a5 5 0 1110 0");
        var arc = Assert.IsType<ArcSegment>(f.Segments[0]);
        Assert.True(arc.LargeArc);
        Assert.True(arc.Sweep);
        Assert.Equal(new ArtPoint(10, 0), arc.To);
    }

    [Fact]
    public void Move_starts_a_new_figure_and_drawing_after_close_starts_one_at_the_start_point()
    {
        var figures = SvgPathParser.Parse("M0,0 L10,0 L10,10 Z L5,5 M50,50 L60,60");
        Assert.Equal(3, figures.Count);
        Assert.True(figures[0].Closed);
        Assert.Equal(new ArtPoint(0, 0), figures[1].Start);
        Assert.False(figures[1].Closed);
        Assert.Equal(new ArtPoint(50, 50), figures[2].Start);
    }

    [Theory]
    [InlineData("L10,10")]
    [InlineData("M0,0 X5,5")]
    [InlineData("M0,0 L10")]
    [InlineData("M0,0 L10,abc")]
    [InlineData("M0,0 A5 5 0 2 0 1 1")]
    [InlineData("10,10 M0,0")]
    [InlineData("M0,0 Z 5,5")]
    public void Malformed_data_throws(string d)
    {
        Assert.Throws<ArtworkFormatException>(() => SvgPathParser.Parse(d));
    }

    [Fact]
    public void Empty_data_has_no_figures()
    {
        Assert.Empty(SvgPathParser.Parse("  "));
    }
}

public sealed class ArtMatrixTests
{
    private static void AssertMatrix(ArtMatrix m, double a, double b, double c, double d, double e, double f)
    {
        Assert.Equal(a, m.A, 9);
        Assert.Equal(b, m.B, 9);
        Assert.Equal(c, m.C, 9);
        Assert.Equal(d, m.D, 9);
        Assert.Equal(e, m.E, 9);
        Assert.Equal(f, m.F, 9);
    }

    [Fact]
    public void Empty_text_is_the_identity()
    {
        Assert.True(ArtMatrix.Parse("").IsIdentity);
    }

    [Fact]
    public void Translate_scale_and_matrix_parse()
    {
        AssertMatrix(ArtMatrix.Parse("translate(5)"), 1, 0, 0, 1, 5, 0);
        AssertMatrix(ArtMatrix.Parse("translate(5, 6)"), 1, 0, 0, 1, 5, 6);
        AssertMatrix(ArtMatrix.Parse("scale(2)"), 2, 0, 0, 2, 0, 0);
        AssertMatrix(ArtMatrix.Parse("scale(-1 2)"), -1, 0, 0, 2, 0, 0);
        AssertMatrix(ArtMatrix.Parse("matrix(1,2,3,4,5,6)"), 1, 2, 3, 4, 5, 6);
    }

    [Fact]
    public void Rotate_about_a_point_keeps_that_point_fixed()
    {
        var m = ArtMatrix.Parse("rotate(90 10 10)");
        var (x, y) = m.Apply(10, 10);
        Assert.Equal(10, x, 9);
        Assert.Equal(10, y, 9);
        var (px, py) = m.Apply(20, 10);
        Assert.Equal(10, px, 9);
        Assert.Equal(20, py, 9);
    }

    [Fact]
    public void A_sequence_applies_the_last_function_first()
    {
        // translate(300,0) scale(-1,1): mirror first, then shift, so x=100 goes to 300-100=200.
        var m = ArtMatrix.Parse("translate(300,0) scale(-1,1)");
        Assert.Equal(200, m.Apply(100, 7).X, 9);
        var comma = ArtMatrix.Parse("translate(300,0),scale(-1,1)");
        Assert.Equal(m, comma);
    }

    [Theory]
    [InlineData("skewX(10)")]
    [InlineData("translate(1,2,3)")]
    [InlineData("translate(1")]
    [InlineData("rotate(1 2)")]
    [InlineData("scale")]
    public void Unsupported_or_malformed_transforms_throw(string text)
    {
        Assert.Throws<ArtworkFormatException>(() => ArtMatrix.Parse(text));
    }
}

public sealed class ArtColorTests
{
    [Theory]
    [InlineData("#fff", 255, 255, 255, 255)]
    [InlineData("#1a2", 255, 0x11, 0xaa, 0x22)]
    [InlineData("#34c759", 255, 0x34, 0xc7, 0x59)]
    [InlineData("#34C75980", 0x80, 0x34, 0xc7, 0x59)]
    public void Hex_forms_parse(string text, int a, int r, int g, int b)
    {
        Assert.True(ArtColor.TryParse(text, out var c));
        Assert.Equal(new ArtColor((byte)a, (byte)r, (byte)g, (byte)b), c);
    }

    [Theory]
    [InlineData("")]
    [InlineData("fff")]
    [InlineData("#ff")]
    [InlineData("#fffff")]
    [InlineData("#gggggg")]
    [InlineData("red")]
    public void Other_text_is_rejected(string text)
    {
        Assert.False(ArtColor.TryParse(text, out _));
        Assert.Throws<ArtworkFormatException>(() => ArtColor.Parse(text));
    }
}

public sealed class ArtworkStyleScannerTests
{
    [Fact]
    public void Default_palette_and_two_colourways_are_read_in_order()
    {
        var styles = ArtworkStyleScanner.Parse("""
            /* palette */
            svg { --body: #fff; --edge: #c2c6ce; }
            svg[data-colorway="electric-orange"] { --body: #ff7a00; }
            svg[data-colorway='white'] { }
            """);
        Assert.Equal(2, styles.Defaults.Count);
        Assert.Equal(new ArtColor(255, 255, 255, 255), styles.Defaults["body"]);
        Assert.Equal(["electric-orange", "white"], styles.Colorways.Select(c => c.Id).ToArray());
        Assert.Equal("Electric Orange", styles.Colorways[0].DisplayName);
        Assert.Equal("White", styles.Colorways[1].DisplayName);
        Assert.Equal(new ArtColor(255, 255, 0x7a, 0), styles.Colorways[0].Overrides["body"]);
        Assert.Empty(styles.Colorways[1].Overrides);
    }

    [Theory]
    [InlineData("rect { --a: #fff }")]
    [InlineData("svg { color: #fff }")]
    [InlineData("svg { --a: red }")]
    [InlineData("svg { --a #fff }")]
    [InlineData("svg { --a: #fff")]
    [InlineData("svg[data-colorway=white] { }")]
    [InlineData("svg[data-colorway=\"a\"] { } svg[data-colorway=\"a\"] { }")]
    [InlineData("svg { --a: #fff } .x")]
    public void Anything_else_is_rejected(string css)
    {
        Assert.Throws<ArtworkFormatException>(() => ArtworkStyleScanner.Parse(css));
    }

    [Fact]
    public void Palette_for_overlays_a_colourway_and_unknown_ids_give_the_defaults()
    {
        var doc = ArtworkParser.Parse("""
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 10 10">
              <style>svg { --a: #111111; --b: #222222; } svg[data-colorway="x"] { --b: #333333; }</style>
            </svg>
            """);
        Assert.Equal(new ArtColor(255, 0x22, 0x22, 0x22), doc.PaletteFor(null).Resolve("b"));
        Assert.Equal(new ArtColor(255, 0x22, 0x22, 0x22), doc.PaletteFor("nope").Resolve("b"));
        var x = doc.PaletteFor("x");
        Assert.Equal(new ArtColor(255, 0x33, 0x33, 0x33), x.Resolve("b"));
        Assert.Equal(new ArtColor(255, 0x11, 0x11, 0x11), x.Resolve("a"));
    }
}
