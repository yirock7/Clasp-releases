// SPDX-License-Identifier: GPL-3.0-or-later

using AppleDeviceHub.Core.Artwork;
using AppleDeviceHub.Core.Ble;

namespace AppleDeviceHub.Core.Tests.Artwork;

public sealed class PairingAnchorsTests
{
    [Fact]
    public void The_stage_fit_centres_the_content_and_fills_98_percent_of_the_limiting_side()
    {
        var fit = StageFit.Of(new ArtBounds(10, 20, 310, 120));
        Assert.Equal(200 * 0.98 / 300, fit.Scale, 6);
        var (cx, cy) = fit.Map(160, 70);
        Assert.Equal(100, cx, 6);
        Assert.Equal(75, cy, 6);
        var (left, _) = fit.Map(10, 70);
        Assert.Equal(100 - (200 * 0.98 / 2), left, 6);
    }

    [Fact]
    public void Every_guide_with_a_drawing_has_its_marks_on_the_stage()
    {
        var folder = ArtworkFolder();
        var checkedGuides = 0;
        foreach (var guide in PairingInstructions.Guides)
        {
            var plan = PairingAnimations.For(guide);
            var key = ArtworkCatalog.DrawingKeyFor(plan.ModelId);
            if (key is null || !plan.IsAnimated) continue;
            var doc = ArtworkParser.Parse(File.ReadAllText(Path.Combine(folder, ArtworkCatalog.FileNameFor(key))));
            var anchors = PairingAnchors.For(plan.Kind, doc);
            foreach (var point in new[] { anchors.Button, anchors.Light })
            {
                Assert.InRange(point.X, 0, StageFit.StageWidth);
                Assert.InRange(point.Y, 0, StageFit.StageHeight);
            }

            checkedGuides++;
        }

        Assert.True(checkedGuides >= 8, "most guides have a per-model drawing");
    }

    [Fact]
    public void The_light_is_the_drawings_own_light_part_when_it_has_one()
    {
        var doc = ArtworkParser.Parse(File.ReadAllText(Path.Combine(ArtworkFolder(), "airpods-pro.svg")));
        var light = ArtworkBounds.OfPart(doc, ArtPart.Light);
        Assert.NotNull(light);
        var fit = StageFit.Of(ArtworkBounds.ContentOf(doc));
        var (x, y) = fit.Map(light.Value.CentreX, light.Value.CentreY);
        var anchors = PairingAnchors.For(PairingAnimationKind.CaseSetupButton, doc);
        Assert.Equal(x, anchors.Light.X, 6);
        Assert.Equal(y, anchors.Light.Y, 6);
    }

    [Fact]
    public void An_earbud_ring_sits_on_the_left_bud_and_a_case_setup_ring_on_the_cases_right_edge()
    {
        var doc = ArtworkParser.Parse(File.ReadAllText(Path.Combine(ArtworkFolder(), "airpods-pro.svg")));
        var fit = StageFit.Of(ArtworkBounds.ContentOf(doc));
        var body = ArtworkBounds.OfPart(doc, ArtPart.Body)!.Value;
        var bud = ArtworkBounds.OfPart(doc, ArtPart.BudLeft)!.Value;

        var setup = PairingAnchors.For(PairingAnimationKind.CaseSetupButton, doc).Button;
        Assert.Equal(fit.Map(body.MaxX, body.CentreY).X, setup.X, 6);

        var earbud = PairingAnchors.For(PairingAnimationKind.EarbudButton, doc).Button;
        var (bx0, _) = fit.Map(bud.MinX, bud.MinY);
        var (bx1, _) = fit.Map(bud.MaxX, bud.MaxY);
        Assert.InRange(earbud.X, bx0, bx1);
    }

    [Fact]
    public void Every_animated_kind_has_its_own_anchor_fractions_and_a_rear_ring_sits_on_the_left()
    {
        var doc = ArtworkParser.Parse(File.ReadAllText(Path.Combine(ArtworkFolder(), "airpods-max.svg")));
        var kinds = Enum.GetValues<PairingAnimationKind>().Where(k => k != PairingAnimationKind.None).ToList();
        foreach (var kind in kinds)
        {
            var anchors = PairingAnchors.For(kind, doc);
            Assert.InRange(anchors.Button.X, 0, StageFit.StageWidth);
            Assert.InRange(anchors.Button.Y, 0, StageFit.StageHeight);
            // No kind falls through to the centre-of-the-drawing default.
            Assert.NotEqual(PairingAnchors.For(PairingAnimationKind.None, doc).Button, anchors.Button);
        }

        var rear = PairingAnchors.For(PairingAnimationKind.RearEarcupButton, doc).Button;
        var front = PairingAnchors.For(PairingAnimationKind.EarcupButton, doc).Button;
        Assert.True(rear.X < StageFit.StageWidth / 2 && front.X > StageFit.StageWidth / 2);
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
