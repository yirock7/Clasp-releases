// SPDX-License-Identifier: GPL-3.0-or-later

using AppleDeviceHub.Core.Artwork;

namespace AppleDeviceHub.Core.Tests.Artwork;

public sealed class ArtworkBoundsTests
{
    private static ArtworkDocument Parse(string body)
        => ArtworkParser.Parse("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 300 230\"><style>svg { --a: #112233; }</style>" + body + "</svg>");

    [Fact]
    public void A_part_is_bounded_by_its_shapes_with_their_transforms_applied()
    {
        var doc = Parse("""
            <defs><g id="bud"><rect x="10" y="20" width="30" height="40" fill="#fff"/></g></defs>
            <g data-part="buds">
              <g data-part="bud-left"><use href="#bud"/></g>
              <g data-part="bud-right"><use href="#bud" transform="translate(300,0) scale(-1,1)"/></g>
            </g>
            """);

        var left = ArtworkBounds.OfPart(doc, ArtPart.BudLeft);
        var right = ArtworkBounds.OfPart(doc, ArtPart.BudRight);
        Assert.NotNull(left);
        Assert.NotNull(right);
        Assert.Equal(new ArtBounds(10, 20, 40, 60), left.Value);
        Assert.Equal(new ArtBounds(260, 20, 290, 60), right.Value);
        Assert.Equal(25, left.Value.CentreX);
        Assert.Equal(40, left.Value.CentreY);
        Assert.Equal(40, left.Value.Height);
    }

    [Fact]
    public void Paths_and_ellipses_count_and_a_missing_part_is_null()
    {
        var doc = Parse("""
            <g data-part="buds">
              <g data-part="bud-left"><path d="M0,0 C0,50 20,50 20,10 Z" fill="#fff"/><ellipse cx="50" cy="5" rx="10" ry="5" fill="#fff"/></g>
            </g>
            """);
        var left = ArtworkBounds.OfPart(doc, ArtPart.BudLeft);
        Assert.NotNull(left);
        Assert.Equal(0, left.Value.MinX);
        Assert.Equal(60, left.Value.MaxX);
        Assert.Equal(0, left.Value.MinY);
        Assert.Equal(50, left.Value.MaxY);
        Assert.Null(ArtworkBounds.OfPart(doc, ArtPart.BudRight));
    }

    [Fact]
    public void Every_shipped_earbud_drawing_has_two_distinct_bud_boxes_that_mirror_about_the_centre()
    {
        foreach (var key in ArtworkCatalog.Keys)
        {
            var doc = ArtworkParser.Parse(File.ReadAllText(Path.Combine(ArtworkFolder(), ArtworkCatalog.FileNameFor(key))));
            var left = ArtworkBounds.OfPart(doc, ArtPart.BudLeft);
            var right = ArtworkBounds.OfPart(doc, ArtPart.BudRight);
            Assert.Equal(left is null, right is null);
            if (left is null || right is null) continue;
            Assert.True(left.Value.Height > 20, key);
            Assert.True(left.Value.CentreX < right.Value.CentreX, key);
            Assert.Equal(doc.Width, left.Value.CentreX + right.Value.CentreX, 3);
        }
    }

    [Fact]
    public void The_fit_box_hugs_the_case_but_keeps_the_open_lid_and_is_smaller_than_the_viewbox()
    {
        var doc = ArtworkParser.Parse(File.ReadAllText(Path.Combine(ArtworkFolder(), "airpods-pro.svg")));
        var content = ArtworkBounds.ContentOf(doc);
        Assert.True(content.Width < doc.Width * 0.6, "the empty side margin is cropped");
        Assert.True(content.Height < doc.Height, "the empty top margin is cropped");
        Assert.True(content.MinX <= 71 && content.MaxX >= 229, "the whole case is inside");
        Assert.True(content.MinY < 107, "the opened lid rises above the hinge");
        Assert.True(content.MaxY >= 202, "the body bottom is inside");
    }

    [Fact]
    public void Every_shipped_drawing_has_a_fit_box_inside_its_viewbox_margins()
    {
        foreach (var key in ArtworkCatalog.Keys)
        {
            var doc = ArtworkParser.Parse(File.ReadAllText(Path.Combine(ArtworkFolder(), ArtworkCatalog.FileNameFor(key))));
            var content = ArtworkBounds.ContentOf(doc);
            Assert.True(content.Width > 20 && content.Height > 20, key);
            Assert.True(content.Width <= doc.Width + 80 && content.Height <= doc.Height + 80, key);
        }
    }

    private static string ArtworkFolder()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "windows", "hub", "src")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir.FullName, "windows", "hub", "src", "AppleDeviceHub.App", "Assets", "Artwork");
    }
}
