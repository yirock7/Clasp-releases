using AppleDeviceHub.Core.Ble;
using AppleDeviceHub.Core.Presentation;

namespace AppleDeviceHub.Core.Tests;

public sealed class TrayGlyphModelTests
{
    /// <summary>An id that is not in the catalog. Asserted below so the test cannot silently pass on a listed id.</summary>
    private const ushort UnlistedId = 0x7F7F;

    public static IEnumerable<object[]> ListedModelIds() => AppleModelCatalog.ListedIds.Select(id => new object[] { id });

    [Theory]
    [MemberData(nameof(ListedModelIds))]
    public void Every_listed_model_maps_to_its_catalog_artwork_group(ushort modelId)
    {
        var expected = AppleModelCatalog.Find(modelId)!.Artwork;

        var glyph = TrayGlyphModel.For(modelId, hasSelectedDevice: true, connected: true, outputIsDevice: true, dimWhenNotOutput: true);

        Assert.Equal(expected, glyph.Group);
        Assert.False(glyph.Dimmed);
    }

    [Fact]
    public void Unlisted_id_maps_to_generic()
    {
        Assert.Equal(ModelRecognition.Unrecognised, AppleModelCatalog.Recognise(UnlistedId));

        var glyph = TrayGlyphModel.For(UnlistedId, hasSelectedDevice: true, connected: true, outputIsDevice: true, dimWhenNotOutput: true);

        Assert.Equal(ArtworkGroup.Generic, glyph.Group);
    }

    [Fact]
    public void Selected_device_with_unknown_model_id_maps_to_generic()
    {
        var glyph = TrayGlyphModel.For(null, hasSelectedDevice: true, connected: true, outputIsDevice: true, dimWhenNotOutput: true);

        Assert.Equal(ArtworkGroup.Generic, glyph.Group);
    }

    [Fact]
    public void No_selected_device_draws_the_logo()
    {
        Assert.Null(TrayGlyphModel.For(null, hasSelectedDevice: false, connected: true, outputIsDevice: true, dimWhenNotOutput: true).Group);
    }

    [Fact]
    public void No_selected_device_ignores_a_model_id()
    {
        // The id is only trusted from the selected Windows-paired device; without one the logo is drawn.
        Assert.Null(TrayGlyphModel.For(0x2720, hasSelectedDevice: false, connected: true, outputIsDevice: true, dimWhenNotOutput: true).Group);
    }

    [Fact]
    public void Disconnected_device_is_dimmed_even_when_it_is_the_output()
    {
        var glyph = TrayGlyphModel.For(0x2720, hasSelectedDevice: true, connected: false, outputIsDevice: true, dimWhenNotOutput: false);

        Assert.True(glyph.Dimmed);
        Assert.Equal(ArtworkGroup.ProEarbuds, glyph.Group);
    }

    [Fact]
    public void Disconnected_logo_is_dimmed()
    {
        Assert.True(TrayGlyphModel.For(null, hasSelectedDevice: false, connected: false, outputIsDevice: false, dimWhenNotOutput: false).Dimmed);
    }

    [Theory]
    [InlineData(true, true, true, false)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, true, true)]
    [InlineData(false, false, true, true)]
    [InlineData(false, true, false, true)]
    [InlineData(false, false, false, true)]
    public void Dims_when_disconnected_or_when_not_output_and_the_setting_is_on(bool connected, bool outputIsDevice, bool dimWhenNotOutput, bool expectedDimmed)
    {
        // Columns: connected, outputIsDevice, dimWhenNotOutput, expected dimmed.
        var glyph = TrayGlyphModel.For(0x0220, hasSelectedDevice: true, connected, outputIsDevice, dimWhenNotOutput);

        Assert.Equal(expectedDimmed, glyph.Dimmed);
    }

    [Fact]
    public void Glyph_passes_through_TrayBadge_and_is_null_by_default()
    {
        var glyph = TrayGlyphModel.For(0x2720, hasSelectedDevice: true, connected: true, outputIsDevice: true, dimWhenNotOutput: true);

        var withGlyph = TrayBadgeModel.FromWindowsBattery(80, outputIsAirPods: true, glyph: glyph);
        Assert.Equal(glyph, withGlyph.Glyph);
        Assert.Equal("80", withGlyph.Text);

        Assert.Null(TrayBadgeModel.FromWindowsBattery(80, outputIsAirPods: true).Glyph);
        Assert.Null(TrayBadgeModel.From(null, outputIsAirPods: true).Glyph);
    }

    [Fact]
    public void Glyph_equality_changes_when_group_or_dimming_changes()
    {
        var a = TrayGlyphModel.For(0x2720, true, true, true, true);
        var b = TrayGlyphModel.For(0x0220, true, true, true, true);
        var c = TrayGlyphModel.For(0x2720, true, false, true, true);

        Assert.NotEqual(a, b);
        Assert.NotEqual(a, c);
        Assert.Equal(a, TrayGlyphModel.For(0x2720, true, true, true, true));
    }

    [Theory]
    [InlineData(100, false, 0, true, TrayTint.Normal)]
    [InlineData(41, false, 0, true, TrayTint.Normal)]
    [InlineData(40, false, 0, true, TrayTint.Low)]
    [InlineData(21, false, 0, true, TrayTint.Low)]
    [InlineData(20, false, 0, true, TrayTint.Critical)]
    [InlineData(0, false, 0, true, TrayTint.Critical)]
    [InlineData(10, false, 10, true, TrayTint.Critical)]
    [InlineData(11, false, 10, true, TrayTint.Low)]
    [InlineData(25, false, 10, true, TrayTint.Low)]
    [InlineData(30, false, 30, true, TrayTint.Critical)]
    [InlineData(31, false, 30, true, TrayTint.Low)]
    [InlineData(35, false, 30, true, TrayTint.Low)]
    [InlineData(15, true, 20, true, TrayTint.Charging)]
    [InlineData(90, true, 20, true, TrayTint.Charging)]
    [InlineData(15, false, 20, false, TrayTint.Normal)]
    [InlineData(15, true, 20, false, TrayTint.Normal)]
    public void Tint_follows_level_charging_threshold_and_setting(int level, bool charging, int threshold, bool colour, TrayTint expected)
        => Assert.Equal(expected, TrayGlyphModel.TintFor(level, charging, threshold, colour));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Demo_glyph_uses_the_demo_model_artwork_and_ignores_audio_routing(bool connected)
    {
        var glyph = TrayGlyphModel.ForDemo(0x2720, connected, dimWhenNotOutput: true);
        Assert.Equal(AppleModelCatalog.ArtworkOf((ushort)0x2720), glyph.Group);
        Assert.Equal(!connected, glyph.Dimmed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Unknown_level_is_always_normal(bool charging)
        => Assert.Equal(TrayTint.Normal, TrayGlyphModel.TintFor(null, charging, 20, true));
}
