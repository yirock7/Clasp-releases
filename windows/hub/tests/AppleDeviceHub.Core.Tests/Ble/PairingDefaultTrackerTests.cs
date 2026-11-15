using AppleDeviceHub.Core.Ble;
using Xunit;

namespace AppleDeviceHub.Core.Tests.Ble;

public class PairingDefaultTrackerTests
{
    [Fact]
    public void First_known_model_applies_once_and_repeats_do_not()
    {
        var t = new PairingDefaultTracker();
        Assert.True(t.ShouldApply("AirPods Pro 3"));
        Assert.False(t.ShouldApply("AirPods Pro 3"));
        Assert.False(t.ShouldApply("airpods pro 3"));
    }

    [Fact]
    public void Missing_name_never_applies()
    {
        var t = new PairingDefaultTracker();
        Assert.False(t.ShouldApply(null));
        Assert.False(t.ShouldApply(" "));
    }

    [Fact]
    public void A_real_change_applies_until_the_user_picks()
    {
        var t = new PairingDefaultTracker();
        Assert.True(t.ShouldApply("AirPods Pro 3"));
        Assert.True(t.ShouldApply("AirPods Max (USB-C)"));
    }

    [Fact]
    public void After_a_manual_pick_nothing_overrides_it()
    {
        var t = new PairingDefaultTracker();
        Assert.True(t.ShouldApply("AirPods Pro 3"));
        t.UserPicked();
        Assert.False(t.ShouldApply("AirPods Pro 3"));
        Assert.False(t.ShouldApply("AirPods Max (USB-C)"));
    }

    [Fact]
    public void A_pick_before_any_model_is_known_is_kept()
    {
        var t = new PairingDefaultTracker();
        t.UserPicked();
        Assert.False(t.ShouldApply("AirPods Pro 3"));
    }
}
