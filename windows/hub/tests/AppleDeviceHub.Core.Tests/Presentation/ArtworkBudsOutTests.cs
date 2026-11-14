using AppleDeviceHub.Core.Ble;
using AppleDeviceHub.Core.Contracts;
using AppleDeviceHub.Core.Presentation;

namespace AppleDeviceHub.Core.Tests.Presentation;

/// <summary>Which buds the hero drawing pulls out of the case, and how long that observation counts.</summary>
public sealed class ArtworkBudsOutTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    private static Observation<PodPlacement> At(PodPlacement placement, double secondsAgo = 0)
        => Observation<PodPlacement>.At(placement, Now - TimeSpan.FromSeconds(secondsAgo));

    private static DeviceState State(EarState ears, bool sessionOpen = false, bool broadcast = false, BatteryLayout layout = BatteryLayout.EarbudsWithCase)
        => new("dev", "AirPods", "AirPods", DataOrigin.Hardware, 1, Now,
            broadcast ? DeviceCapabilities.BroadcastObservation : DeviceCapabilities.None,
            new LinkState(Observation<bool>.At(true, Now), Observation<bool>.At(false, Now), Observation<bool>.At(sessionOpen, Now), Observation<bool>.At(false, Now)),
            BatteryState.Unknown, ListeningState.Unknown, default, default)
        { Ears = ears, Layout = layout };

    [Theory]
    [InlineData(PodPlacement.InEar, true)]
    [InlineData(PodPlacement.OutOfEar, true)]
    [InlineData(PodPlacement.InCase, false)]
    [InlineData(PodPlacement.Disconnected, false)]
    public void A_bud_is_out_only_when_its_placement_is_in_ear_or_out_of_ear(PodPlacement placement, bool expectedOut)
    {
        var sides = new EarState(At(placement), At(placement)) { Left = At(placement), Right = At(placement) };
        Assert.Equal(new ArtworkBudsOut(expectedOut, expectedOut), HubViewModel.FreshBudsOut(State(sides), Now));
    }

    [Fact]
    public void Unknown_ears_and_a_missing_state_have_no_buds_out()
    {
        Assert.Equal(ArtworkBudsOut.None, HubViewModel.FreshBudsOut(State(EarState.Unknown), Now));
        Assert.Equal(ArtworkBudsOut.None, HubViewModel.FreshBudsOut(null, Now));
        Assert.False(ArtworkBudsOut.None.Any);
    }

    [Fact]
    public void With_known_sides_each_bud_is_judged_by_itself()
    {
        var leftOnly = new EarState(At(PodPlacement.InEar), At(PodPlacement.InCase))
        {
            Left = At(PodPlacement.InEar),
            Right = At(PodPlacement.InCase),
        };
        Assert.Equal(new ArtworkBudsOut(true, false), HubViewModel.FreshBudsOut(State(leftOnly), Now));

        var rightOnly = new EarState(At(PodPlacement.InCase), At(PodPlacement.OutOfEar))
        {
            Left = At(PodPlacement.InCase),
            Right = At(PodPlacement.OutOfEar),
        };
        Assert.Equal(new ArtworkBudsOut(false, true), HubViewModel.FreshBudsOut(State(rightOnly), Now));
    }

    [Fact]
    public void Without_known_sides_the_buds_split_out_only_when_both_are_out()
    {
        // Primary and secondary are not left and right, so one bud out says nothing about which side.
        var one = new EarState(At(PodPlacement.InEar), At(PodPlacement.InCase));
        Assert.Equal(ArtworkBudsOut.None, HubViewModel.FreshBudsOut(State(one, sessionOpen: true), Now));
        var unknownOther = new EarState(At(PodPlacement.InEar), Observation<PodPlacement>.Unknown);
        Assert.Equal(ArtworkBudsOut.None, HubViewModel.FreshBudsOut(State(unknownOther, sessionOpen: true), Now));

        var both = new EarState(At(PodPlacement.InEar), At(PodPlacement.OutOfEar));
        Assert.Equal(new ArtworkBudsOut(true, true), HubViewModel.FreshBudsOut(State(both, sessionOpen: true), Now));
    }

    [Fact]
    public void A_broadcast_observation_counts_for_fifteen_seconds_and_future_ones_never_count()
    {
        var aged = new EarState(At(PodPlacement.InEar, 14), At(PodPlacement.InEar, 14)) { Left = At(PodPlacement.InEar, 14), Right = At(PodPlacement.InEar, 14) };
        Assert.True(HubViewModel.FreshBudsOut(State(aged, broadcast: true), Now).Any);

        var stale = new EarState(At(PodPlacement.InEar, 16), At(PodPlacement.InEar, 16)) { Left = At(PodPlacement.InEar, 16), Right = At(PodPlacement.InEar, 16) };
        Assert.Equal(ArtworkBudsOut.None, HubViewModel.FreshBudsOut(State(stale, broadcast: true), Now));

        var future = new EarState(At(PodPlacement.InEar, -2), At(PodPlacement.InEar, -2)) { Left = At(PodPlacement.InEar, -2), Right = At(PodPlacement.InEar, -2) };
        Assert.Equal(ArtworkBudsOut.None, HubViewModel.FreshBudsOut(State(future, broadcast: true), Now));

        // One stale side does not stop the fresh one.
        var mixed = new EarState(At(PodPlacement.InEar, 16), At(PodPlacement.InEar, 1)) { Left = At(PodPlacement.InEar, 16), Right = At(PodPlacement.OutOfEar, 1) };
        Assert.Equal(new ArtworkBudsOut(false, true), HubViewModel.FreshBudsOut(State(mixed, broadcast: true), Now));
    }

    [Fact]
    public void A_control_session_holds_the_buds_out_and_the_expiry_is_null_until_it_ends()
    {
        var old = new EarState(At(PodPlacement.InEar, 1800), At(PodPlacement.OutOfEar, 1800));
        var open = State(old, sessionOpen: true);
        Assert.Equal(new ArtworkBudsOut(true, true), HubViewModel.FreshBudsOut(open, Now));
        Assert.Null(HubViewModel.NextArtworkCueExpiry(open, Now));

        Assert.Equal(ArtworkBudsOut.None, HubViewModel.FreshBudsOut(State(old, sessionOpen: false), Now));
    }

    [Fact]
    public void An_out_of_ear_broadcast_reading_schedules_its_own_expiry()
    {
        var ears = new EarState(At(PodPlacement.OutOfEar, 3), At(PodPlacement.OutOfEar, 3)) { Left = At(PodPlacement.OutOfEar, 3), Right = At(PodPlacement.OutOfEar, 3) };
        var expiry = HubViewModel.NextArtworkCueExpiry(State(ears, broadcast: true), Now);
        Assert.Equal(Now - TimeSpan.FromSeconds(3) + HubViewModel.ArtworkEarFreshness + TimeSpan.FromTicks(1), expiry);
    }

    [Theory]
    [InlineData(BatteryLayout.EarbudsNoCaseBattery)]
    [InlineData(BatteryLayout.SingleBattery)]
    public void Only_the_earbuds_with_case_layout_pulls_buds_out(BatteryLayout layout)
    {
        var ears = new EarState(At(PodPlacement.InEar), At(PodPlacement.InEar)) { Left = At(PodPlacement.InEar), Right = At(PodPlacement.InEar) };
        Assert.Equal(ArtworkBudsOut.None, HubViewModel.FreshBudsOut(State(ears, layout: layout), Now));
    }
}
