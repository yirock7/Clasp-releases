using AppleDeviceHub.Core.AirPods;
using AppleDeviceHub.Core.Contracts;
using AppleDeviceHub.Core.Presentation;
using Microsoft.Extensions.Time.Testing;

namespace AppleDeviceHub.Core.Tests;

public class ConnectionTests
{
    private static readonly object Gate = new();

    private static void Dispatch(Action action)
    {
        lock (Gate) action();
    }

    private static LinkState NearbyOnly(DateTimeOffset at) => new(
        Observation<bool>.At(true, at), Observation<bool>.At(false, at),
        Observation<bool>.At(false, at), Observation<bool>.At(false, at));

    [Fact]
    public async Task Demo_disconnect_keeps_the_buds_nearby_and_connect_brings_them_back()
    {
        var states = new List<DeviceState>();
        await using var demo = new DemoAirPodsTransport(TestSupport.Instant());
        await demo.StartAsync();

        var off = await demo.SendAsync(new DisconnectCommand(Guid.NewGuid()));
        Assert.Equal(CommandOutcome.Acknowledged, off.Outcome);
        await TestSupport.WaitUntil(() => demo.Current!.Link.Summarize() == ConnectionSummary.Nearby);

        demo.StateChanged += (_, s) => { lock (states) states.Add(s); };
        var on = await demo.SendAsync(new ConnectCommand(Guid.NewGuid()));
        Assert.Equal(CommandOutcome.Acknowledged, on.Outcome);
        await TestSupport.WaitUntil(() => demo.Current!.Link.Summarize() == ConnectionSummary.Connected);

        lock (states)
        {
            Assert.Equal(new[] { ConnectionSummary.Connecting, ConnectionSummary.Connected }, states.Select(s => s.Link.Summarize()));
        }
    }

    [Fact]
    public async Task Demo_connect_is_refused_when_the_buds_are_not_nearby()
    {
        await using var demo = new DemoAirPodsTransport(TestSupport.Instant());
        await demo.StartAsync();
        demo.SimulateOutOfRange();

        var result = await demo.SendAsync(new ConnectCommand(Guid.NewGuid()));

        Assert.Equal(CommandOutcome.TransportUnavailable, result.Outcome);
        Assert.Contains("Open the case", result.Detail);
    }

    [Fact]
    public async Task Connect_shows_connecting_until_the_device_reports_the_connection()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var transport = new ScriptedTransport();
        await using var vm = new HubViewModel(transport, transport.DeviceId, Dispatch, time: time);
        transport.Publish(null, time.GetUtcNow(), link: NearbyOnly(time.GetUtcNow()));
        Assert.Equal("Ready to connect", vm.ConnectionHeadline);
        Assert.True(vm.IsConnectActionEnabled);

        await vm.ConnectAsync(); // acknowledged, but no connected state yet

        Assert.True(vm.IsConnecting);
        Assert.False(vm.IsConnected);
        Assert.Equal("Connecting…", vm.ConnectionHeadline);
        Assert.False(vm.IsConnectActionEnabled); // no double-connect while an attempt runs

        transport.Publish(ListeningMode.NoiseCancellation, time.GetUtcNow());

        Assert.True(vm.IsConnected);
        Assert.False(vm.IsConnectPending);
        Assert.Equal("Disconnect", vm.ConnectActionText);
        Assert.Equal("", vm.ConnectionHint);
    }

    [Fact]
    public async Task Connect_that_never_lands_times_out_with_an_instruction()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var transport = new ScriptedTransport();
        await using var vm = new HubViewModel(transport, transport.DeviceId, Dispatch, time: time);
        transport.Publish(null, time.GetUtcNow(), link: NearbyOnly(time.GetUtcNow()));

        await vm.ConnectAsync();
        time.Advance(HubViewModel.ConnectTimeout);

        Assert.False(vm.IsConnecting);
        Assert.Equal("Ready to connect", vm.ConnectionHeadline);
        Assert.Contains("Keep the case open", vm.LastErrorText);
    }

    [Fact]
    public async Task Refused_connect_explains_why_and_offers_connect_again()
    {
        var transport = new ScriptedTransport
        {
            OnSend = c => new CommandResult(c.CommandId, CommandOutcome.TransportUnavailable, DataOrigin.Hardware, "Bluetooth is off."),
        };
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using var vm = new HubViewModel(transport, transport.DeviceId, Dispatch, time: time);
        transport.Publish(null, time.GetUtcNow(), link: NearbyOnly(time.GetUtcNow()));

        await vm.ConnectAsync();

        Assert.False(vm.IsConnecting);
        Assert.Equal("Couldn't connect: Bluetooth is off.", vm.LastErrorText);
        Assert.True(vm.IsConnectActionEnabled);
    }

    [Fact]
    public async Task Unknown_link_never_claims_a_connection_state()
    {
        var transport = new ScriptedTransport();
        await using var vm = new HubViewModel(transport, transport.DeviceId, Dispatch);

        Assert.Equal("Looking for your AirPods…", vm.ConnectionHeadline);
        Assert.False(vm.IsConnectActionEnabled);
        await vm.ConnectAsync(); // nothing reported yet: nothing to connect to
        Assert.False(vm.IsConnecting);
    }
}
