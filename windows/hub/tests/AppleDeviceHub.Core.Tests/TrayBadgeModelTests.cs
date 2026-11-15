using AppleDeviceHub.Core.Contracts;
using AppleDeviceHub.Core.Presentation;

namespace AppleDeviceHub.Core.Tests;

public sealed class TrayBadgeModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    private static BatteryComponent Part(int? level, bool? charging = null) => BatteryComponent.From(level, charging, Now);

    private static DeviceState State(BatteryComponent left, BatteryComponent right, BatteryComponent @case = default) =>
        new("id", "AirPods", "AirPods Pro", DataOrigin.Broadcast, 1, Now, DeviceCapabilities.Battery,
            LinkState.Unknown, new BatteryState(left, right, @case), ListeningState.Unknown, default, default);

    [Fact]
    public void Windows_battery_badge_names_its_source_and_respects_the_settings()
    {
        var badge = TrayBadgeModel.FromWindowsBattery(80, outputIsAirPods: true);
        Assert.Equal("80", badge.Text);
        Assert.False(badge.Dimmed);
        Assert.False(badge.Charging);
        Assert.Equal("AirPods: 80% (from Windows)", badge.Tooltip);

        var hidden = TrayBadgeModel.FromWindowsBattery(80, outputIsAirPods: false, showPercentage: false);
        Assert.Null(hidden.Text);
        Assert.True(hidden.Dimmed);
        Assert.EndsWith(", sound is going to another device", hidden.Tooltip);
    }

    [Fact]
    public void Averages_both_reported_pods()
    {
        var badge = TrayBadgeModel.From(State(Part(80), Part(60)), outputIsAirPods: true);
        Assert.Equal("70", badge.Text);
        Assert.False(badge.Dimmed);
        Assert.False(badge.Charging);
    }

    [Fact]
    public void Keeps_five_percent_midpoint_without_inflating_precision()
    {
        Assert.Equal("75", TrayBadgeModel.From(State(Part(80), Part(70)), true).Text);
    }

    [Fact]
    public void Uses_the_single_reported_pod()
    {
        Assert.Equal("40", TrayBadgeModel.From(State(Part(null), Part(40)), true).Text);
        Assert.Equal("90", TrayBadgeModel.From(State(Part(90), Part(null)), true).Text);
    }

    [Fact]
    public void Case_battery_is_ignored()
    {
        Assert.Null(TrayBadgeModel.From(State(default, default, Part(100)), true).Text);
        Assert.Equal("50", TrayBadgeModel.From(State(Part(50), default, Part(100)), true).Text);
    }

    [Fact]
    public void Unknown_battery_has_no_number_and_a_value_free_tooltip()
    {
        foreach (var state in new DeviceState?[] { null, State(default, default), State(Part(255), Part(-1)) })
        {
            var badge = TrayBadgeModel.From(state, outputIsAirPods: true);
            Assert.Null(badge.Text);
            Assert.False(badge.Charging);
            HonestyGuard.AssertClaimsNoValue(badge.Tooltip);
        }
    }

    [Fact]
    public void Unknown_battery_dimmed_tooltip_still_claims_no_value()
    {
        var badge = TrayBadgeModel.From(null, outputIsAirPods: false);
        Assert.True(badge.Dimmed);
        HonestyGuard.AssertClaimsNoValue(badge.Tooltip);
    }

    [Fact]
    public void Charging_when_any_reported_pod_charges()
    {
        var badge = TrayBadgeModel.From(State(Part(50, true), Part(60, false)), true);
        Assert.True(badge.Charging);
        Assert.Contains("charging", badge.Tooltip, StringComparison.Ordinal);
        Assert.False(TrayBadgeModel.From(State(Part(50, false), Part(60)), true).Charging);
    }

    [Fact]
    public void Charging_flag_of_an_unreported_pod_does_not_count()
    {
        // BatteryComponent.From drops the flag when the level is unknown.
        Assert.False(TrayBadgeModel.From(State(Part(null, true), Part(60, false)), true).Charging);
    }

    [Theory]
    [InlineData(true, true, false)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    public void Dims_only_when_enabled_and_output_is_elsewhere(bool outputIsAirPods, bool dimSetting, bool expected)
    {
        var badge = TrayBadgeModel.From(State(Part(80), Part(80)), outputIsAirPods, dimWhenNotOutput: dimSetting);
        Assert.Equal(expected, badge.Dimmed);
    }

    [Fact]
    public void Number_can_be_turned_off_but_tooltip_still_reports()
    {
        var badge = TrayBadgeModel.From(State(Part(80), Part(80)), true, showPercentage: false);
        Assert.Null(badge.Text);
        Assert.Contains("80", badge.Tooltip, StringComparison.Ordinal);
    }

    [Fact]
    public void Full_battery_shows_100()
    {
        Assert.Equal("100", TrayBadgeModel.From(State(Part(100), Part(100)), true).Text);
    }
}
