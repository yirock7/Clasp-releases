using AppleDeviceHub.Core.Battery;
using AppleDeviceHub.Core.Ble;

namespace AppleDeviceHub.Core.Tests.Battery;

/// <summary>The low-battery alert follows the device's layout and noun, and a single battery re-arms from its level alone.</summary>
public sealed class ModelAwareLowBatteryAlertTests
{
    private const IdentityConfidence Mine = IdentityConfidence.LikelyYours;
    private const BatteryLayout Single = BatteryLayout.SingleBattery;

    [Fact]
    public void A_single_battery_alert_has_its_own_title_and_body()
    {
        var alert = new LowBatteryAlertPolicy().Evaluate("d", Mine, 20, null, false, 20, Single, "AirPods");
        Assert.Equal("AirPods battery low", alert!.Title);
        Assert.Equal("The battery is at about 20%. Charge them soon.", alert.Body);
        Assert.Equal(20, alert.Percent);
    }

    [Fact]
    public void A_single_battery_fires_once_and_re_arms_when_the_level_rises_without_charging_data()
    {
        var clock = new ManualTimeProvider();
        var p = new LowBatteryAlertPolicy(clock);
        Assert.NotNull(p.Evaluate("d", Mine, 20, null, false, 20, Single, "Beats"));
        Assert.Null(p.Evaluate("d", Mine, 20, null, false, 20, Single, "Beats"));
        Assert.Null(p.Evaluate("d", Mine, 30, null, false, 20, Single, "Beats")); // not above threshold + hysteresis
        clock.Advance(TimeSpan.FromHours(2)); // re-arming also needs the cooldown to have passed
        Assert.Null(p.Evaluate("d", Mine, 40, null, false, 20, Single, "Beats")); // re-arms here
        Assert.Null(p.Evaluate("d", Mine, 30, null, false, 20, Single, "Beats"));
        Assert.NotNull(p.Evaluate("d", Mine, 20, null, false, 20, Single, "Beats"));
    }

    [Fact]
    public void Earbuds_still_need_charging_evidence_to_re_arm()
    {
        var p = new LowBatteryAlertPolicy();
        Assert.NotNull(p.Evaluate("d", Mine, 20, 50, false, 20));
        Assert.Null(p.Evaluate("d", Mine, 60, 60, false, 20));
        Assert.Null(p.Evaluate("d", Mine, 20, 50, false, 20));
    }

    [Fact]
    public void Beats_earbuds_without_a_case_battery_do_not_mention_the_case()
    {
        var alert = new LowBatteryAlertPolicy().Evaluate("d", Mine, 20, 50, false, 20, BatteryLayout.EarbudsNoCaseBattery, "Beats");
        Assert.Equal("Beats battery low", alert!.Title);
        Assert.Equal("The left earbud is at about 20%. Charge them soon.", alert.Body);
    }

    [Fact]
    public void The_airpods_alert_text_is_unchanged()
    {
        var alert = new LowBatteryAlertPolicy().Evaluate("d", Mine, 20, 50, false, 20);
        Assert.Equal("AirPods battery low", alert!.Title);
        Assert.Equal("The left earbud is at about 20%. Put them in the case to charge.", alert.Body);
    }
}
