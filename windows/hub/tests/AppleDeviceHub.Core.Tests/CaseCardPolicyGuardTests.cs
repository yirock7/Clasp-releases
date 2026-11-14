using AppleDeviceHub.Core.Contracts;
using AppleDeviceHub.Core.Presentation;

namespace AppleDeviceHub.Core.Tests;

/// <summary>
/// The guards around the owner's transition rule (CaseCardPolicy.ShouldPresent, which
/// currently returns false once the guards pass). They are tested through IsEligible so they hold whatever rule is written.
/// </summary>
public class CaseCardPolicyGuardTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private static DeviceState S(long seq, DataOrigin origin = DataOrigin.Hardware, string id = "dev")
        => new(id, "AirPods", "AirPods Pro", origin, seq, T0, DeviceCapabilities.Battery, LinkState.Unknown,
            BatteryState.Unknown, ListeningState.Unknown, default, default);

    [Theory]
    [InlineData(DataOrigin.Hardware, true)]
    [InlineData(DataOrigin.Demo, true)] // Demo still presents; the card shows its Demo chip
    [InlineData(DataOrigin.Broadcast, false)]
    [InlineData(DataOrigin.Unknown, false)]
    public void Only_attributed_origins_may_present_automatically(DataOrigin origin, bool eligible)
        => Assert.Equal(eligible, new CaseCardPolicy().IsEligible(S(1, origin), S(2, origin), out _));

    [Fact]
    public void Disabled_setting_suppresses_everything()
        => Assert.False(new CaseCardPolicy { AutoPresentEnabled = false }.IsEligible(null, S(1), out _));

    [Theory]
    [InlineData(5, 5)]
    [InlineData(5, 4)]
    public void Duplicate_or_out_of_order_reports_are_suppressed(long previous, long next)
        => Assert.False(new CaseCardPolicy().IsEligible(S(previous), S(next), out _));

    [Fact]
    public void A_newer_report_is_eligible_and_compares_against_the_previous_one()
    {
        var prev = S(1);
        Assert.True(new CaseCardPolicy().IsEligible(prev, S(2), out var comparable));
        Assert.Same(prev, comparable);
    }

    [Fact]
    public void A_device_switch_is_judged_as_a_first_report()
    {
        // Sequence numbers of different devices are unrelated, so a lower one is not a duplicate.
        Assert.True(new CaseCardPolicy().IsEligible(S(9, id: "a"), S(1, id: "b"), out var comparable));
        Assert.Null(comparable);
    }

    [Fact]
    public void ShouldPresent_never_presents_an_ineligible_state()
    {
        var policy = new CaseCardPolicy();
        Assert.False(policy.ShouldPresent(S(1, DataOrigin.Broadcast), S(2, DataOrigin.Broadcast), T0));
        Assert.False(policy.ShouldPresent(S(5), S(5), T0));
    }
}
