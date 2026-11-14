using AppleDeviceHub.Core.Contracts;
using AppleDeviceHub.Core.Presentation;

namespace AppleDeviceHub.Core.Tests;

public sealed class BatteryItemViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    private static BatteryItemViewModel Item() => new("L", "Left earbud");

    [Fact]
    public void Broadcast_zero_reads_as_under_ten_percent()
    {
        var item = Item();

        item.Update(BatteryComponent.From(0, false, Now), Now, isDemo: false, inTenPercentSteps: true);

        Assert.Equal("<10%", item.PercentText);
        Assert.Equal(0.05, item.Level01);
        Assert.True(item.IsKnown);
        Assert.True(item.IsLow);
        Assert.Contains("under 10 percent", item.AutomationName);
        Assert.DoesNotContain("0 percent", item.AutomationName.Replace("under 10 percent", ""));
    }

    [Fact]
    public void Broadcast_ten_is_shown_as_is()
    {
        var item = Item();

        item.Update(BatteryComponent.From(10, false, Now), Now, isDemo: false, inTenPercentSteps: true);

        Assert.Equal("10%", item.PercentText);
        Assert.Equal(0.1, item.Level01);
        Assert.Contains("10 percent", item.AutomationName);
    }

    [Fact]
    public void Exact_zero_still_reads_as_zero_percent()
    {
        var item = Item();

        item.Update(BatteryComponent.From(0, false, Now), Now, isDemo: false);

        Assert.Equal("0%", item.PercentText);
        Assert.Equal(0.0, item.Level01);
        Assert.True(item.IsKnown);
        Assert.True(item.IsLow);
        Assert.Contains("0 percent", item.AutomationName);
    }
}
