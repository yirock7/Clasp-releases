using AppleDeviceHub.Core.Ble;
using AppleDeviceHub.Core.Contracts;
using AppleDeviceHub.Core.Presentation;
using Microsoft.Extensions.Time.Testing;

namespace AppleDeviceHub.Core.Tests.Ble;

/// <summary>The flyout's primary action for a broadcast transport is Start/Stop observing.</summary>
public class BroadcastViewModelTests
{
    private static readonly object Gate = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeAdvertisementSource _source = new();

    private static void Dispatch(Action action)
    {
        lock (Gate) action();
    }

    private async Task<(HubViewModel, BroadcastObservationTransport)> Start()
    {
        var t = new BroadcastObservationTransport(_source, _clock);
        var vm = new HubViewModel(t, BroadcastObservationTransport.DeviceId, Dispatch, time: _clock);
        await vm.StartAsync();
        return (vm, t);
    }

    [Fact]
    public async Task Before_observing_the_action_is_Start_observing_and_nothing_listens()
    {
        var (vm, _) = await Start();
        await using var _vm = vm;
        Assert.True(vm.IsBroadcastObservation);
        Assert.False(vm.IsObserving);
        Assert.Equal("Start listening", vm.ConnectActionText);
        Assert.True(vm.IsConnectActionEnabled);
        Assert.Equal("Not listening", vm.ConnectionHeadline);
        Assert.Equal(0, _source.StartCount);
    }

    [Fact]
    public async Task Start_then_stop_observing_without_waiting_for_a_connection()
    {
        var (vm, t) = await Start();
        await using var _vm = vm;

        await vm.ConnectAsync();
        Assert.True(t.IsObserving);
        Assert.True(vm.IsObserving);
        Assert.Equal("Stop listening", vm.ConnectActionText);
        Assert.Equal("Listening for AirPods…", vm.ConnectionHeadline);
        Assert.Equal(HubViewModel.BroadcastLimitsText, vm.ConnectionHint);
        Assert.False(vm.IsConnecting); // no "Connecting…" that could time out
        Assert.Null(vm.LastErrorText);

        _source.Emit(Payload.Sighting(Payload.Build()));
        Assert.Equal("Pick a nearby device below", vm.ConnectionHeadline);
        Assert.False(vm.IsConnected);

        _clock.Advance(HubViewModel.SessionConnectTimeout + TimeSpan.FromSeconds(1));
        Assert.Null(vm.LastErrorText); // never a connect timeout

        await vm.ConnectAsync(); // the same action toggles
        Assert.False(t.IsObserving);
        Assert.Equal("Start listening", vm.ConnectActionText);
        Assert.Equal(1, _source.StopCount);
    }

    [Fact]
    public async Task Radio_unavailable_is_reported_and_observing_keeps_retrying_until_the_user_stops()
    {
        _source.OnStart = _ => Task.FromResult(new AdvertisementSourceStatus(AdvertisementSourceState.Unavailable, "Bluetooth is turned off."));
        var (vm, _) = await Start();
        await using var _vm = vm;

        await vm.ConnectAsync();
        Assert.True(vm.IsObserving); // a failed start is not a user stop: it keeps trying
        Assert.Null(vm.LastErrorText);
        Assert.True(vm.HasUnavailableReason);
        Assert.Equal("Bluetooth isn't available. Trying again…", vm.ConnectionHeadline);
        Assert.True(vm.IsConnectActionEnabled);

        await vm.ConnectAsync(); // the same action toggles: the user's Stop ends it
        Assert.False(vm.IsObserving);
        Assert.False(vm.HasUnavailableReason);
    }

    [Fact]
    public async Task Settings_controls_are_never_offered()
    {
        var (vm, _) = await Start();
        await using var _vm = vm;
        await vm.ConnectAsync();
        _source.Emit(Payload.Sighting(Payload.Build()));
        Assert.False(vm.IsConversationAwarenessInputEnabled);
        Assert.False(vm.IsDemo);
    }

    [Fact]
    public async Task Ambiguous_is_false_before_observing_with_one_source_and_after_stopping()
    {
        var (vm, _) = await Start();
        await using var _vm = vm;
        Assert.False(vm.IsBroadcastAmbiguous);

        await vm.ConnectAsync();
        Assert.False(vm.IsBroadcastAmbiguous);
        _source.Emit(Payload.Sighting(Payload.Build(pods: 0x58), address: 1));
        Assert.False(vm.IsBroadcastAmbiguous); // one pair heard: the display can follow it

        await vm.ConnectAsync(); // stop
        Assert.False(vm.IsBroadcastAmbiguous);
    }

    [Fact]
    public async Task Ambiguous_is_true_while_two_different_pairs_are_heard_and_raises_a_change()
    {
        var (vm, t) = await Start();
        await using var _vm = vm;
        await vm.ConnectAsync();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        _source.Emit(Payload.Sighting(Payload.Build(pods: 0x58), address: 1));
        _source.Emit(Payload.Sighting(Payload.Build(pods: 0x32, flagsAndCase: 0x46), address: 2));

        Assert.Equal(BroadcastPhase.Ambiguous, t.Phase);
        Assert.True(vm.IsBroadcastAmbiguous);
        Assert.True(vm.HasBroadcastSources);
        Assert.Contains(nameof(HubViewModel.IsBroadcastAmbiguous), raised); // the flyout refreshes on this name
    }

    [Fact]
    public async Task Ambiguous_clears_when_the_second_pair_goes_quiet()
    {
        var (vm, _) = await Start();
        await using var _vm = vm;
        await vm.ConnectAsync();
        _source.Emit(Payload.Sighting(Payload.Build(pods: 0x58), address: 1));
        _source.Emit(Payload.Sighting(Payload.Build(pods: 0x32, flagsAndCase: 0x46), address: 2));
        Assert.True(vm.IsBroadcastAmbiguous);

        _clock.Advance(TimeSpan.FromSeconds(30));
        Assert.False(vm.IsBroadcastAmbiguous);
    }
}
