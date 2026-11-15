using AppleDeviceHub.Core.Ble;
using AppleDeviceHub.Core.Contracts;
using AppleDeviceHub.Core.Presentation;
using AppleDeviceHub.Core.Tests.Ble;
using Microsoft.Extensions.Time.Testing;

namespace AppleDeviceHub.Core.Tests.Presentation;

// ATTRIBUTION: the Max frame (62 04 80) comes from the public test suites of CAPod and MagicPodsCore (see ModelLayoutTransportTests).

/// <summary>The view model, tray badge and picker text follow the device's battery layout and noun.</summary>
public sealed class ModelAwareDisplayTests
{
    private const ushort StudioPro = 0x1720;
    private const ushort Max = 0x0A20;
    private static readonly object Gate = new();
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
    private readonly FakeTimeProvider _clock = new(Now);
    private readonly FakeAdvertisementSource _source = new();

    private static void Dispatch(Action action)
    {
        lock (Gate) action();
    }

    private async Task<HubViewModel> StartAsync(ushort? pairedModel = null)
    {
        var transport = new BroadcastObservationTransport(_source, _clock);
        if (pairedModel is ushort model) transport.SetPairedDevice(model, "paired");
        var vm = new HubViewModel(transport, BroadcastObservationTransport.DeviceId, Dispatch, time: _clock);
        await vm.StartAsync();
        await vm.ConnectAsync();
        return vm;
    }

    private void Emit(ushort model, byte status, byte pods, byte flagsAndCase)
        => _source.Emit(Payload.Sighting(Payload.Build(model: model, status: status, pods: pods, flagsAndCase: flagsAndCase)));

    [Fact]
    public async Task A_max_frame_fills_only_the_single_battery()
    {
        await using var vm = await StartAsync();
        Emit(Max, 0x62, 0x04, 0x80);

        Assert.True(vm.IsSingleBattery);
        Assert.False(vm.HasCaseBattery);
        Assert.Equal("40%", vm.Single.PercentText);
        Assert.True(vm.Single.IsKnown);
        Assert.Equal("Headphones battery, 40 percent", vm.Single.AutomationName);
        Assert.Equal("—", vm.Right.PercentText);
        Assert.Equal("—", vm.Case.PercentText);
    }

    [Fact]
    public async Task A_studio_pro_nibble_of_zero_reads_under_ten_percent()
    {
        await using var vm = await StartAsync();
        Emit(StudioPro, 0x00, 0x00, 0x00);

        Assert.True(vm.IsSingleBattery);
        Assert.Equal("<10%", vm.Single.PercentText);
        Assert.Equal("—", vm.Right.PercentText);
        Assert.Equal("—", vm.Case.PercentText);
    }

    [Fact]
    public async Task Earbuds_keep_three_gauges_and_an_unknown_single_gauge()
    {
        await using var vm = await StartAsync();
        Emit(Payload.ProThree, 0x22, 0x58, 0x47);

        Assert.False(vm.IsSingleBattery);
        Assert.True(vm.HasCaseBattery);
        Assert.True(vm.Left.IsKnown);
        Assert.True(vm.Right.IsKnown);
        Assert.Equal("—", vm.Single.PercentText);
        Assert.False(vm.Single.IsKnown);
    }

    [Fact]
    public async Task Selecting_a_single_battery_model_without_observing_updates_the_view_model()
    {
        var transport = new BroadcastObservationTransport(_source, _clock);
        await using var vm = new HubViewModel(transport, BroadcastObservationTransport.DeviceId, Dispatch, time: _clock);
        await vm.StartAsync();
        Assert.False(vm.IsSingleBattery);

        transport.SetPairedDevice(StudioPro, "studio");

        Assert.False(transport.IsObserving);
        Assert.True(vm.IsSingleBattery);
    }

    [Fact]
    public async Task Layout_change_raises_notifications_once()
    {
        await using var vm = await StartAsync();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        Emit(StudioPro, 0x00, 0x06, 0x00);
        Assert.Contains(nameof(HubViewModel.IsSingleBattery), raised);
        Assert.Contains(nameof(HubViewModel.DeviceNoun), raised);

        raised.Clear();
        _clock.Advance(TimeSpan.FromSeconds(1));
        Emit(StudioPro, 0x00, 0x07, 0x00);
        Assert.DoesNotContain(nameof(HubViewModel.IsSingleBattery), raised);
        Assert.DoesNotContain(nameof(HubViewModel.DeviceNoun), raised);
    }

    [Fact]
    public async Task The_selected_source_headline_uses_the_device_noun()
    {
        await using var beats = await StartAsync(StudioPro);
        Emit(StudioPro, 0x00, 0x06, 0x00);
        beats.SelectBroadcastSource(beats.BroadcastSources[0].Id);
        Assert.Equal("Beats", beats.DeviceNoun);
        Assert.Equal("Your Beats (likely)", beats.ConnectionHeadline);
    }

    [Fact]
    public async Task The_airpods_selected_source_headline_text_is_unchanged()
    {
        await using var airpods = await StartAsync(Payload.ProThree);
        Emit(Payload.ProThree, 0x22, 0x58, 0x47);
        airpods.SelectBroadcastSource(airpods.BroadcastSources[0].Id);
        Assert.Equal("AirPods", airpods.DeviceNoun);
        Assert.Equal(IdentityConfidenceText.Likely, airpods.ConnectionHeadline);
    }

    [Fact]
    public async Task The_noun_defaults_to_airpods_before_any_state()
    {
        var transport = new BroadcastObservationTransport(_source, _clock);
        await using var vm = new HubViewModel(transport, BroadcastObservationTransport.DeviceId, Dispatch, time: _clock);
        Assert.Equal("AirPods", vm.DeviceNoun);
        Assert.False(vm.IsSingleBattery);
        Assert.True(vm.HasCaseBattery);
    }

    private static BroadcastSource Source(ushort model, BatteryLayout layout, int? left, int? right, int? @case,
        IdentityConfidence identity = IdentityConfidence.Unknown)
        => new(Guid.NewGuid(), 1, model, "name", -60, left, right, @case, Now, Identity: identity, Layout: layout);

    [Fact]
    public void Picker_text_follows_the_layout()
    {
        Assert.Equal("Approximate battery\nBattery: ~60%",
            new BroadcastSourceViewModel(Source(StudioPro, BatteryLayout.SingleBattery, 60, null, null)).BatteryText);
        Assert.Equal("Approximate battery\nBattery: <10%",
            new BroadcastSourceViewModel(Source(StudioPro, BatteryLayout.SingleBattery, 0, null, null)).BatteryText);
        Assert.Equal("Approximate battery\nLeft: ~90%    Right: ~80%",
            new BroadcastSourceViewModel(Source(0x2620, BatteryLayout.EarbudsNoCaseBattery, 90, 80, null)).BatteryText);
        Assert.Equal("Approximate battery\nLeft: ~90%    Right: <10%\nCase: not reported",
            new BroadcastSourceViewModel(Source(Payload.ProThree, BatteryLayout.EarbudsWithCase, 90, 0, null)).BatteryText);
    }

    [Fact]
    public void Picker_text_updates_when_the_source_updates()
    {
        var item = new BroadcastSourceViewModel(Source(StudioPro, BatteryLayout.SingleBattery, 60, null, null));
        item.Update(Source(StudioPro, BatteryLayout.SingleBattery, 50, null, null));
        Assert.Equal("Approximate battery\nBattery: ~50%", item.BatteryText);
    }

    [Fact]
    public void Picker_identity_text_names_the_family()
    {
        Assert.Equal("Your Beats (likely)",
            new BroadcastSourceViewModel(Source(StudioPro, BatteryLayout.SingleBattery, 60, null, null, IdentityConfidence.Probable)).IdentityText);
        Assert.Equal(IdentityConfidenceText.Likely,
            new BroadcastSourceViewModel(Source(Payload.ProThree, BatteryLayout.EarbudsWithCase, 60, 60, 60, IdentityConfidence.Probable)).IdentityText);
    }

    private static DeviceState State(BatteryLayout layout, string noun, BatteryComponent left, BatteryComponent right = default)
        => new("id", "name", "model", DataOrigin.Broadcast, 1, Now, DeviceCapabilities.Battery,
            LinkState.Unknown, new BatteryState(left, right, default), ListeningState.Unknown, default, default)
        {
            Layout = layout,
            DeviceNoun = noun,
        };

    [Fact]
    public void Tray_badge_for_a_single_battery_uses_the_noun()
    {
        var badge = TrayBadgeModel.From(State(BatteryLayout.SingleBattery, "Beats", BatteryComponent.From(60, null, Now)), outputIsAirPods: true);
        Assert.Equal("60", badge.Text);
        Assert.Equal("Beats: 60%", badge.Tooltip);

        var unknown = TrayBadgeModel.From(State(BatteryLayout.SingleBattery, "Beats", default), outputIsAirPods: true);
        Assert.Null(unknown.Text);
        Assert.Equal("Beats: battery unknown", unknown.Tooltip);
    }

    [Fact]
    public void Tray_badge_keeps_the_airpods_text()
    {
        var badge = TrayBadgeModel.From(State(BatteryLayout.EarbudsWithCase, "AirPods", BatteryComponent.From(80, null, Now), BatteryComponent.From(60, null, Now)), outputIsAirPods: true);
        Assert.Equal("AirPods: 70%", badge.Tooltip);
        Assert.Equal(TrayBadgeModel.UnknownTooltip, TrayBadgeModel.From(null, outputIsAirPods: true).Tooltip);
        Assert.Equal("AirPods: 80% (from Windows)", TrayBadgeModel.FromWindowsBattery(80, true).Tooltip);
        Assert.Equal("Beats: 80% (from Windows)", TrayBadgeModel.FromWindowsBattery(80, true, noun: "Beats").Tooltip);
    }
}
