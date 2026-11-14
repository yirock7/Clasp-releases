using AppleDeviceHub.Core.Ble;
using AppleDeviceHub.Core.Contracts;
using Microsoft.Extensions.Time.Testing;

namespace AppleDeviceHub.Core.Tests.Ble;

/// <summary>The transport reports an identity confidence, wired from the selected paired device. No radio is touched.</summary>
public sealed class BroadcastIdentityTransportTests : IAsyncDisposable
{
    private const ushort ProThree = Payload.ProThree;
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeAdvertisementSource _source = new();
    private readonly BroadcastObservationTransport _transport;

    public BroadcastIdentityTransportTests() => _transport = new BroadcastObservationTransport(_source, _clock);

    public async ValueTask DisposeAsync() => await _transport.DisposeAsync();

    private async Task StartAsync()
    {
        await _transport.StartAsync();
        await _transport.SendAsync(new ConnectCommand(Guid.NewGuid()));
    }

    [Fact]
    public async Task Without_a_paired_device_the_identity_stays_unconfirmed()
    {
        await StartAsync();
        _source.Emit(Payload.Sighting(Payload.Build(model: ProThree)));

        Assert.Equal(IdentityConfidence.Unknown, _transport.IdentityConfidence);
        Assert.Contains("unconfirmed", _transport.Current!.DisplayName, StringComparison.Ordinal);
        Assert.False(_transport.IsIdentityConfirmed);
    }

    [Fact]
    public async Task A_lone_source_of_the_paired_model_is_shown_as_your_airpods_likely()
    {
        _transport.SetPairedDevice(ProThree);
        await StartAsync();
        _source.Emit(Payload.Sighting(Payload.Build(model: ProThree)));

        Assert.Equal(IdentityConfidence.Probable, _transport.IdentityConfidence);
        Assert.Equal("Your AirPods (likely)", _transport.Current!.DisplayName);
        Assert.Equal("AirPods Pro 3", _transport.Current.ModelName);
        Assert.Equal(IdentityConfidence.Probable, Assert.Single(_transport.Sources).Identity);
        Assert.Equal(IdentityConfidence.Probable, _transport.LastDecision!.IdentityConfidence);
        Assert.False(_transport.LastDecision.IdentityConfirmed); // cryptographic confirmation stays false
        Assert.False(_transport.IsIdentityConfirmed);
    }

    [Fact]
    public async Task A_windows_connection_change_beside_the_first_broadcast_raises_it_to_likely_yours()
    {
        _transport.SetPairedDevice(ProThree);
        _transport.ObserveWindowsConnection(false);
        await StartAsync();
        _transport.ObserveWindowsConnection(true);
        _clock.Advance(TimeSpan.FromSeconds(2));
        _source.Emit(Payload.Sighting(Payload.Build(model: ProThree)));

        Assert.Equal(IdentityConfidence.LikelyYours, _transport.IdentityConfidence);
    }

    [Fact]
    public async Task Another_model_nearby_is_not_shown_once_a_paired_model_is_set()
    {
        _transport.SetPairedDevice(ProThree);
        await StartAsync();
        _source.Emit(Payload.Sighting(Payload.Build(model: Payload.ProTwoUsbC), address: 2));
        Assert.Empty(_transport.Sources);

        _source.Emit(Payload.Sighting(Payload.Build(model: ProThree), address: 1));
        Assert.Equal(IdentityConfidence.Probable, _transport.IdentityConfidence);
    }

    [Fact]
    public async Task Two_pro_3_sources_are_ambiguous_and_unknown()
    {
        _transport.SetPairedDevice(ProThree);
        await StartAsync();
        _source.Emit(Payload.Sighting(Payload.Build(model: ProThree), address: 1));
        _source.Emit(Payload.Sighting(Payload.Build(model: ProThree, pods: 0x11), address: 2)); // a second pair: clearly different batteries

        Assert.Equal(BroadcastPhase.Ambiguous, _transport.Phase);
        Assert.Equal(IdentityConfidence.Unknown, _transport.IdentityConfidence);
        Assert.All(_transport.Sources, source => Assert.Equal(IdentityConfidence.Unknown, source.Identity));
    }

    [Fact]
    public async Task Stopping_observation_forgets_the_identity()
    {
        _transport.SetPairedDevice(ProThree);
        await StartAsync();
        _source.Emit(Payload.Sighting(Payload.Build(model: ProThree)));
        await _transport.SendAsync(new DisconnectCommand(Guid.NewGuid()));

        Assert.Equal(IdentityConfidence.Unknown, _transport.IdentityConfidence);
    }

    [Fact]
    public async Task Clearing_the_paired_device_returns_to_unconfirmed()
    {
        _transport.SetPairedDevice(ProThree);
        await StartAsync();
        _source.Emit(Payload.Sighting(Payload.Build(model: ProThree)));
        _transport.SetPairedDevice(null);

        Assert.Equal(IdentityConfidence.Unknown, _transport.IdentityConfidence);
    }

    [Fact]
    public async Task Selecting_another_device_of_the_same_model_clears_values_and_old_pairing_clues()
    {
        _transport.SetPairedDevice(ProThree, "paired-one");
        _transport.ObserveWindowsConnection(false);
        await StartAsync();
        _transport.ObserveWindowsConnection(true);
        _clock.Advance(TimeSpan.FromSeconds(2));
        _source.Emit(Payload.Sighting(Payload.Build(model: ProThree)));
        Assert.Equal(IdentityConfidence.LikelyYours, _transport.IdentityConfidence);

        _transport.SetPairedDevice(ProThree, "paired-two");

        Assert.Equal(BroadcastPhase.Waiting, _transport.Phase);
        Assert.Equal(IdentityConfidence.Unknown, _transport.IdentityConfidence);
        Assert.Empty(_transport.Sources);
        Assert.False(_transport.Current!.Battery.Left.Level.HasValue);

        // This is a fresh baseline for the new paired device, not a continuation of the old timing clue.
        _transport.ObserveWindowsConnection(true);
        _clock.Advance(TimeSpan.FromSeconds(2));
        _source.Emit(Payload.Sighting(Payload.Build(model: ProThree), address: 2));
        Assert.Equal(IdentityConfidence.Probable, _transport.IdentityConfidence);
    }
}
