using System.Globalization;
using AppleDeviceHub.Core.Battery;
using AppleDeviceHub.Core.Ble;
using AppleDeviceHub.Core.Contracts;
using AppleDeviceHub.Core.Presentation;

namespace AppleDeviceHub.Core.Tests;

/// <summary>
/// English text must not change with the Windows regional format: percent signs and decimal numbers keep their
/// original en-US shape ("55%", "1.5") under cultures that would otherwise write "55 %", "%55" or "1,5".
/// </summary>
public sealed class CultureInvariantFormattingTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    public static TheoryData<string> Cultures => new() { "de-DE", "tr-TR", "fr-FR" };

    private static void UnderCulture(string name, Action action)
    {
        var culture = CultureInfo.CurrentCulture;
        var ui = CultureInfo.CurrentUICulture;
        try
        {
            // Only the number format is regional; the UI language stays English like the resource text.
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            action();
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = ui;
        }
    }

    [Theory, MemberData(nameof(Cultures))]
    public void Battery_percent_text_is_55_percent_in_any_culture(string name) => UnderCulture(name, () =>
    {
        var item = new BatteryItemViewModel("L", "Left earbud");
        item.Update(BatteryComponent.From(55, false, Now), Now, isDemo: false);
        Assert.Equal("55%", item.PercentText);
    });

    [Theory, MemberData(nameof(Cultures))]
    public void Broadcast_percent_is_approximate_55_percent_in_any_culture(string name) => UnderCulture(name, () =>
    {
        var source = new BroadcastSource(Guid.NewGuid(), 1, 0x2014, "AirPods Pro", -60, 55, 55, 55, Now);
        var vm = new BroadcastSourceViewModel(source);
        Assert.Contains("~55%", vm.BatteryText);
        Assert.DoesNotContain("55 %", vm.BatteryText);
        Assert.DoesNotContain("%55", vm.BatteryText);
    });

    [Theory, MemberData(nameof(Cultures))]
    public void Drain_estimate_uses_a_decimal_point_in_any_culture(string name) => UnderCulture(name, () =>
    {
        var text = DrainEstimator.Format(TimeSpan.FromHours(1.5), TimeSpan.FromHours(2.5));
        Assert.Contains("1.5", text);
        Assert.Contains("2.5", text);
        Assert.DoesNotContain(",", text);
    });

    [Theory, MemberData(nameof(Cultures))]
    public void Chart_summary_uses_a_decimal_point_in_any_culture(string name)
    {
        var samples = Enumerable.Range(0, 6)
            .Select(i => new BatterySample(Now.AddMinutes(-2).AddHours(i - 5), 100 - i * 8, 100 - i * 8, null, null, null, null, BatterySampleSource.Driver))
            .ToArray();
        string expected = BatteryChartSummary.Text(samples, Now);
        Assert.Contains(" hr left", expected);
        UnderCulture(name, () => Assert.Equal(expected, BatteryChartSummary.Text(samples, Now)));
    }
}
