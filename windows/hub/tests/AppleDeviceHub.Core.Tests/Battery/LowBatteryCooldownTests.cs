using AppleDeviceHub.Core.Battery;
using AppleDeviceHub.Core.Ble;
using AppleDeviceHub.Core.Tests.Ble;

namespace AppleDeviceHub.Core.Tests.Battery;

/// <summary>A hand-advanced monotonic clock.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private long _ticks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => _ticks;
    public void Advance(TimeSpan by) => _ticks += by.Ticks;
}

/// <summary>Finding 6: a forged battery clue or a flapping charge flag cannot spam or re-arm low-battery toasts.</summary>
public sealed class LowBatteryCooldownTests
{
    private const IdentityConfidence Mine = IdentityConfidence.LikelyYours;

    [Fact]
    public void A_battery_only_match_never_alerts_and_does_not_use_up_the_alert()
    {
        var p = new LowBatteryAlertPolicy(new ManualTimeProvider());
        Assert.Null(p.Evaluate("d", IdentityConfidence.LikelyByBattery, 10, 10, false, 20));
        Assert.NotNull(p.Evaluate("d", Mine, 10, 10, false, 20));
    }

    [Fact]
    public void A_battery_only_match_cannot_re_arm()
    {
        var clock = new ManualTimeProvider();
        var p = new LowBatteryAlertPolicy(clock);
        Assert.NotNull(p.Evaluate("d", Mine, 10, 60, false, 20));
        clock.Advance(TimeSpan.FromHours(3));
        Assert.Null(p.Evaluate("d", IdentityConfidence.LikelyByBattery, 90, 90, true, 20)); // forged charge cycle
        Assert.Null(p.Evaluate("d", Mine, 10, 60, false, 20));                              // still disarmed
    }

    [Fact]
    public void Alternating_charging_and_levels_for_an_hour_does_not_alert_again()
    {
        var clock = new ManualTimeProvider();
        var p = new LowBatteryAlertPolicy(clock);
        Assert.NotNull(p.Evaluate("d", Mine, 10, 10, false, 20));
        for (int minute = 0; minute < 60; minute += 5)
        {
            clock.Advance(TimeSpan.FromMinutes(5));
            Assert.Null(p.Evaluate("d", Mine, 90, 90, true, 20));   // "charging", above threshold + 10
            Assert.Null(p.Evaluate("d", Mine, 90, 90, false, 20));
            Assert.Null(p.Evaluate("d", Mine, 10, 10, false, 20));  // low again
        }
    }

    [Fact]
    public void After_the_cooldown_a_genuine_charge_cycle_re_arms()
    {
        var clock = new ManualTimeProvider();
        var p = new LowBatteryAlertPolicy(clock);
        Assert.NotNull(p.Evaluate("d", Mine, 10, 10, false, 20));
        Assert.Null(p.Evaluate("d", Mine, 40, 40, true, 20));      // charged, but inside the cooldown
        clock.Advance(TimeSpan.FromHours(2));
        Assert.Null(p.Evaluate("d", Mine, 50, 50, false, 20));     // cooldown over: re-arms on this reading
        Assert.NotNull(p.Evaluate("d", Mine, 10, 10, false, 20));
    }

    [Fact]
    public void Just_under_the_cooldown_does_not_re_arm()
    {
        var clock = new ManualTimeProvider();
        var p = new LowBatteryAlertPolicy(clock);
        Assert.NotNull(p.Evaluate("d", Mine, 10, 10, false, 20));
        p.Evaluate("d", Mine, 50, 50, true, 20);
        clock.Advance(TimeSpan.FromHours(2) - TimeSpan.FromSeconds(1));
        Assert.Null(p.Evaluate("d", Mine, 50, 50, true, 20));
        Assert.Null(p.Evaluate("d", Mine, 10, 10, false, 20));
    }

    [Fact]
    public void Cooldown_is_per_device()
    {
        var clock = new ManualTimeProvider();
        var p = new LowBatteryAlertPolicy(clock);
        Assert.NotNull(p.Evaluate("a", Mine, 10, 10, false, 20));
        clock.Advance(TimeSpan.FromMinutes(30));
        Assert.NotNull(p.Evaluate("b", Mine, 10, 10, false, 20));
    }
}

public sealed class ForgedBatteryAlertScenarioTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    private static ProximityMessage Message(byte pods)
        => ProximityMessageParser.Parse(Payload.Build(model: Payload.ProThree, pods: pods, lid: 0x0B, flagsAndCase: 0x47))!;

    [Fact]
    public void Cycling_all_levels_then_sending_ten_percent_against_eighty_does_not_alert()
    {
        var matcher = new PairedIdentityMatcher();
        matcher.SetPairedModel(Payload.ProThree);
        matcher.SetWindowsBattery(80, T0);
        var policy = new LowBatteryAlertPolicy(new ManualTimeProvider());

        for (int level = 0; level <= 9; level++)
        {
            var rating = matcher.Observe(1, Message((byte)(level * 0x11)), T0.AddSeconds(level));
            Assert.Null(policy.Evaluate("d", rating, level * 10, level * 10, false, 20));
        }
        var low = matcher.Observe(1, Message(0x11), T0.AddSeconds(12));
        Assert.Equal(IdentityConfidence.Probable, low);
        Assert.Null(policy.Evaluate("d", low, 10, 10, false, 20));
    }
}
