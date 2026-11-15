using AppleDeviceHub.Core.Presentation;

namespace AppleDeviceHub.Core.Tests.Presentation;

public sealed class AuroraEdgePolicyTests
{
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    public void Off_in_high_contrast_or_reduced_transparency(bool contrast, bool transparency, bool motion)
        => Assert.Equal(AuroraEdgeMode.Off, AuroraEdgePolicy.Decide(contrast, transparency, motion));

    [Fact]
    public void Reduced_motion_keeps_the_resting_look_without_the_glow()
        => Assert.Equal(AuroraEdgeMode.Resting, AuroraEdgePolicy.Decide(false, false, true));

    [Fact]
    public void Full_effects_glow_on_open()
        => Assert.Equal(AuroraEdgeMode.Glowing, AuroraEdgePolicy.Decide(false, false, false));

    [Fact]
    public void Ring_opacity_tapers_from_one_to_almost_nothing_without_increasing()
    {
        Assert.Equal(1.0, AuroraEdgePolicy.RingOpacity(0, 12), 6);
        double previous = 1.0;
        for (int i = 1; i < 12; i++)
        {
            double o = AuroraEdgePolicy.RingOpacity(i, 12);
            Assert.True(o < previous);
            previous = o;
        }
        Assert.True(previous < 0.01);
        Assert.Equal(0, AuroraEdgePolicy.RingOpacity(12, 12));
    }
}
