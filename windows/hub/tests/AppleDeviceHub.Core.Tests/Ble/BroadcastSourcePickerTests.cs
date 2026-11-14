using AppleDeviceHub.Core.Ble;
using AppleDeviceHub.Core.Contracts;
using AppleDeviceHub.Core.Presentation;
using Microsoft.Extensions.Time.Testing;

namespace AppleDeviceHub.Core.Tests.Ble;

/// <summary>Offline regression for the owner's connected-but-empty broadcast flyout.</summary>
public class BroadcastSourcePickerTests
{
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.Parse("2026-10-15T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
    private readonly FakeAdvertisementSource _source = new();

    private async Task<(HubViewModel Vm, BroadcastObservationTransport Transport)> Start()
    {
        var transport = new BroadcastObservationTransport(_source, _clock);
        var vm = new HubViewModel(transport, BroadcastObservationTransport.DeviceId, action => action(), time: _clock);
        await vm.StartAsync();
        await vm.ConnectAsync();
        return (vm, transport);
    }

    [Fact]
    public async Task Two_sources_can_be_inspected_separately_without_claiming_a_connection()
    {
        var (vm, transport) = await Start();
        await using var owner = vm;
        _source.Emit(Payload.Sighting(Payload.Build(pods: 0x58), address: 1));
        _source.Emit(Payload.Sighting(Payload.Build(pods: 0x32, flagsAndCase: 0x46), address: 2));

        Assert.Equal(BroadcastPhase.Ambiguous, transport.Phase);
        Assert.Equal(2, vm.BroadcastSources.Count);
        Assert.Null(vm.SelectedBroadcastSource); // never auto-choose, even for a single source
        Assert.False(transport.Current!.Battery.Left.Level.HasValue); // automatic attribution still refuses
        Assert.False(vm.IsConnected);
        Assert.False(vm.IsConversationAwarenessInputEnabled);

        vm.SelectBroadcastSource(vm.BroadcastSources[0].Id);
        Assert.Contains("Left: ~80%", vm.SelectedBroadcastBatteryText);
        Assert.Contains("Right: ~50%", vm.SelectedBroadcastBatteryText);
        vm.SelectBroadcastSource(vm.BroadcastSources[1].Id);
        Assert.Contains("Left: ~20%", vm.SelectedBroadcastBatteryText);
        Assert.Contains("Right: ~30%", vm.SelectedBroadcastBatteryText);
        Assert.Contains("Case: ~60%", vm.SelectedBroadcastBatteryText);
        Assert.Contains("not confirmed", vm.ConnectionHeadline);
        Assert.False(transport.IsIdentityConfirmed);
        Assert.Equal(1, _source.StartCount);
        Assert.Equal(0, _source.StopCount); // selection does not reconnect or restart observation
    }

    [Fact]
    public async Task Live_updates_keep_the_selected_item_and_never_mix_other_source_values()
    {
        var (vm, _) = await Start();
        await using var owner = vm;
        _source.Emit(Payload.Sighting(Payload.Build(pods: 0x58), address: 1));
        var selected = vm.BroadcastSources[0];
        vm.SelectBroadcastSource(selected.Id);
        _source.Emit(Payload.Sighting(Payload.Build(pods: 0x11), address: 2));
        _source.Emit(Payload.Sighting(Payload.Build(pods: 0x47), address: 1));
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Same(selected, vm.SelectedBroadcastSource);
        Assert.Contains("Left: ~70%", vm.SelectedBroadcastBatteryText);
        Assert.Contains("Right: ~40%", vm.SelectedBroadcastBatteryText);
    }

    [Fact]
    public async Task Expiry_and_stop_clear_selection_and_same_address_never_inherits_it()
    {
        var (vm, _) = await Start();
        await using var owner = vm;
        _source.Emit(Payload.Sighting(Payload.Build(), address: 1));
        var first = vm.BroadcastSources[0].Id;
        vm.SelectBroadcastSource(first);
        _clock.Advance(TimeSpan.FromSeconds(11));
        Assert.Empty(vm.BroadcastSources);
        Assert.Null(vm.SelectedBroadcastSource);
        Assert.Equal("", vm.SelectedBroadcastBatteryText);
        _source.Emit(Payload.Sighting(Payload.Build(), address: 1));
        Assert.NotEqual(first, vm.BroadcastSources[0].Id);
        Assert.Null(vm.SelectedBroadcastSource);
        vm.SelectBroadcastSource(vm.BroadcastSources[0].Id);
        await vm.ConnectAsync(); // stop
        Assert.Empty(vm.BroadcastSources);
        Assert.False(vm.HasSelectedBroadcastSource);
        await vm.ConnectAsync(); // new observation
        _source.Emit(Payload.Sighting(Payload.Build(), address: 1));
        Assert.Null(vm.SelectedBroadcastSource);
    }

    [Fact]
    public async Task Unknown_batteries_remain_unknown_and_names_never_establish_identity()
    {
        var (vm, _) = await Start();
        await using var owner = vm;
        _source.Emit(Payload.Sighting(Payload.Build(pods: 0xFF, flagsAndCase: 0x0F), name: "Owner's AirPods"));
        var source = Assert.Single(vm.BroadcastSources);
        vm.SelectBroadcastSource(source.Id);
        Assert.DoesNotContain("Owner", source.Label);
        Assert.DoesNotContain("AABBCCDDEEFF", source.Label);
        Assert.Contains("Left: not reported", vm.SelectedBroadcastBatteryText);
        Assert.Contains("Right: not reported", vm.SelectedBroadcastBatteryText);
        Assert.Contains("Case: not reported", vm.SelectedBroadcastBatteryText);
    }

    [Fact]
    public async Task Source_list_is_bounded_and_rejected_packets_do_not_add_sources()
    {
        var (vm, _) = await Start();
        await using var owner = vm;
        _source.Emit(Payload.Sighting([0x10, 0x01, 0x00]));
        Assert.Empty(vm.BroadcastSources);
        for (ulong address = 1; address <= 40; address++)
            _source.Emit(Payload.Sighting(Payload.Build(model: (ushort)(0x3000 + address)), address)); // distinct models: identical siblings would merge into one source
        Assert.Equal(32, vm.BroadcastSources.Count);
    }
}
