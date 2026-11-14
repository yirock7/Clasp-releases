using AppleDeviceHub.Core.Artwork;
using AppleDeviceHub.Core.Ble;
using Xunit;

namespace AppleDeviceHub.Core.Tests.Ble;

public class PairingAnimationTests
{
    [Fact]
    public void Every_guide_has_a_plan_and_only_the_fallback_is_static()
    {
        foreach (var guide in PairingInstructions.Guides)
        {
            var plan = PairingAnimations.For(guide);
            Assert.False(string.IsNullOrWhiteSpace(plan.Description));
            Assert.Equal(!guide.IsFallback, plan.IsAnimated);
        }
    }

    [Fact]
    public void Every_animated_guide_has_a_caption_a_location_and_a_light_cue_that_its_steps_repeat()
    {
        foreach (var guide in PairingInstructions.Guides.Where(g => !g.IsFallback))
        {
            var plan = PairingAnimations.For(guide);
            var cue = plan.Cue;
            Assert.NotNull(cue);
            Assert.False(string.IsNullOrWhiteSpace(cue.Caption), guide.Key);
            Assert.True(Enum.IsDefined(cue.Location), guide.Key);
            Assert.Contains(cue.Spoken, plan.Description);
            // The step list says the same thing as the chip: the light (or tone) cue appears in the steps.
            Assert.Contains(cue.LightText.Replace("The ", string.Empty, StringComparison.Ordinal), string.Join(' ', guide.Steps), StringComparison.OrdinalIgnoreCase);
            if (cue.Gesture == PairingGesture.DoubleTap)
            {
                Assert.Equal(2, cue.Taps);
                Assert.Contains("Double-tap", cue.Caption);
            }
            else
            {
                Assert.Equal(0, cue.Taps);
                Assert.Contains("Hold", cue.Caption);
            }

            // A caption names a time only when the makers publish one, and then the step says it too.
            Assert.Equal(cue.HoldSeconds is not null, cue.Caption.EndsWith($"· {cue.HoldSeconds} s", StringComparison.Ordinal));
            if (cue.HoldSeconds is int seconds) Assert.Contains($"{seconds} seconds", string.Join(' ', guide.Steps));
        }

        Assert.Null(PairingAnimations.For(PairingInstructions.Fallback).Cue);
    }

    [Theory]
    [InlineData("airpods-setup-button", PairingLocation.Back)]
    [InlineData("airpods-double-tap", PairingLocation.CaseFront)]
    [InlineData("airpods-max", PairingLocation.EarCup)]
    [InlineData("beats-case-system-button", PairingLocation.InsideCase)]
    [InlineData("beats-solo-buds", PairingLocation.Earbud)]
    [InlineData("beats-flex", PairingLocation.Module)]
    [InlineData("beats-360", PairingLocation.Back)]
    public void Maps_guides_to_locations(string key, PairingLocation location)
        => Assert.Equal(location, PairingAnimations.CueOf(key)!.Location);

    [Theory]
    [InlineData("airpods-setup-button", PairingAnimationKind.CaseSetupButton)]
    [InlineData("airpods-double-tap", PairingAnimationKind.CaseDoubleTap)]
    [InlineData("airpods-max", PairingAnimationKind.MaxButton)]
    [InlineData("beats-case-system-button", PairingAnimationKind.CaseButton)]
    [InlineData("beats-solo-buds", PairingAnimationKind.EarbudButton)]
    [InlineData("beats-flex", PairingAnimationKind.ModuleButton)]
    [InlineData("check-guide", PairingAnimationKind.None)]
    public void Maps_guides_to_kinds(string key, PairingAnimationKind kind)
        => Assert.Equal(kind, PairingAnimations.KindOf(key));

    [Fact]
    public void Plans_use_the_catalog_artwork()
    {
        Assert.Equal(ArtworkGroup.MaxHeadphones, PairingAnimations.For(PairingInstructions.Find(0x0A20)!).Art);
        Assert.Equal(ArtworkGroup.Neckband, PairingAnimations.For(PairingInstructions.Find(0x1020)!).Art);
        Assert.Equal(ArtworkGroup.Generic, PairingAnimations.For(PairingInstructions.Fallback).Art);
    }

    [Fact]
    public void Cycle_has_a_drawing_for_every_model_covers_the_shapes_and_wraps()
    {
        var models = PairingArtworkCycle.Models;
        Assert.Equal(models.Count, models.Distinct().Count());
        // Every model has its own per-model drawing, and the drawings are all different.
        var keys = models.Select(id => ArtworkCatalog.DrawingKeyFor(id)).ToList();
        Assert.DoesNotContain(null, keys);
        Assert.Equal(keys.Count, keys.Distinct().Count());
        var groups = models.Select(id => AppleModelCatalog.ArtworkOf(id)).ToHashSet();
        Assert.Contains(ArtworkGroup.ProEarbuds, groups);
        Assert.Contains(ArtworkGroup.StemEarbuds, groups);
        Assert.Contains(ArtworkGroup.MaxHeadphones, groups);
        Assert.Contains(ArtworkGroup.OverEarHeadphones, groups);
        Assert.Contains(ArtworkGroup.OnEarHeadphones, groups);
        Assert.Contains(ArtworkGroup.BudsWithCase, groups);
        Assert.Contains(ArtworkGroup.EarHookWithCase, groups);
        Assert.Contains(ArtworkGroup.Neckband, groups);
        Assert.Equal(0, PairingArtworkCycle.Next(models.Count - 1));
        Assert.Equal(1, PairingArtworkCycle.Next(0));
        Assert.Equal(ArtworkGroup.ProEarbuds, AppleModelCatalog.ArtworkOf(PairingArtworkCycle.Resting));
        Assert.Equal(models[0], PairingArtworkCycle.Resting);
    }

    [Fact]
    public void Plans_carry_the_guides_model_id_and_the_fallback_has_none()
    {
        Assert.Equal((ushort?)0x0A20, PairingAnimations.For(PairingInstructions.Find(0x0A20)!).ModelId);
        Assert.Null(PairingAnimations.For(PairingInstructions.Fallback).ModelId);
        // Beats 360 is listed but has no per-model drawing yet: it keeps the group drawing.
        var beats360 = PairingAnimations.For(PairingInstructions.Find(0x3820)!);
        Assert.NotNull(beats360.ModelId);
        Assert.Null(ArtworkCatalog.DrawingKeyFor(beats360.ModelId));
    }

    [Fact]
    public void Nothing_found_means_still_looking()
    {
        Assert.Null(DeviceDetection.Decide(null, null, false));
        Assert.Null(DeviceDetection.Decide("  ", "AirPods Pro 3", false));
    }

    [Fact]
    public void A_live_hub_model_wins_over_the_picker()
    {
        var found = DeviceDetection.Decide("Sam AirPods", "AirPods Max (USB-C)", true);
        Assert.Equal("AirPods Max (USB-C)", found?.Name);
        Assert.Equal(ArtworkGroup.MaxHeadphones, found?.Art);
    }

    [Fact]
    public void An_unrecognised_hub_model_falls_back_to_the_picker()
    {
        var found = DeviceDetection.Decide("Sam AirPods Pro", "Unrecognised Apple audio device", true);
        Assert.Equal("Sam AirPods Pro", found?.Name);
        Assert.Equal(ArtworkGroup.ProEarbuds, found?.Art);
    }

    [Fact]
    public void A_picked_device_is_drawn_by_the_longest_matching_model_name()
    {
        Assert.Equal(ArtworkGroup.ProEarbuds, DeviceDetection.Decide("My AirPods Pro 3", null, false)?.Art);
        Assert.Equal(ArtworkGroup.StemEarbuds, DeviceDetection.Decide("Sam AirPods", null, false)?.Art);
        Assert.Equal(ArtworkGroup.Generic, DeviceDetection.Decide("Headset", null, false)?.Art);
    }

    [Fact]
    public void A_found_device_carries_the_model_id_to_draw()
    {
        Assert.Equal((ushort?)0x0A20, DeviceDetection.Decide("Sam AirPods", "AirPods Max (Lightning)", true)?.ModelId);
        Assert.Equal((ushort?)0x2720, DeviceDetection.Decide("My AirPods Pro 3", null, false)?.ModelId);
        // A paired device that only says "AirPods" is drawn as the plain stem earbuds.
        Assert.Equal((ushort?)0x0F20, DeviceDetection.Decide("Sam AirPods", null, false)?.ModelId);
        // Nothing to go on: no model id, so the group drawing stays.
        Assert.Null(DeviceDetection.Decide("Headset", null, false)?.ModelId);
        Assert.Equal(ArtworkCatalog.AirPods, ArtworkCatalog.DrawingKeyFor(DeviceDetection.Decide("Sam AirPods", null, false)?.ModelId));
    }
}
