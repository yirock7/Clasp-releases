using AppleDeviceHub.Core.Battery;
using AppleDeviceHub.Core.Ble;
using AppleDeviceHub.Core.Contracts;
using Microsoft.Extensions.Time.Testing;

namespace AppleDeviceHub.Core.Tests.Ble;

/// <summary>A case level remembered from an earlier run is shown, labelled with its original time, only for the likely selected pair.</summary>
public sealed class RememberedCaseTransportTests : IAsyncDisposable
{
    private const ushort ProThree = Payload.ProThree;
    private const byte CaseUnknown = 0x4F;   // earbuds in the ears: no case level
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeAdvertisementSource _source = new();
    private readonly BroadcastObservationTransport _transport;
    private DateTimeOffset RememberedAt => _clock.GetUtcNow().AddHours(-3);

    public RememberedCaseTransportTests() => _transport = new BroadcastObservationTransport(_source, _clock);

    public async ValueTask DisposeAsync() => await _transport.DisposeAsync();

    private async Task StartAsync()
    {
        await _transport.StartAsync();
        await _transport.SendAsync(new ConnectCommand(Guid.NewGuid()));
    }

    private void EmitInEar(byte pods = 0x58, ulong address = 1)
        => _source.Emit(Payload.Sighting(Payload.Build(model: ProThree, pods: pods, flagsAndCase: CaseUnknown), address: address));

    [Fact]
    public async Task A_remembered_case_shows_with_its_original_time_and_unknown_charging_after_an_in_ear_packet()
    {
        _transport.SetPairedDevice(ProThree, "dev");
        var at = RememberedAt;
        _transport.SetRememberedCase(60, at);
        await StartAsync();
        EmitInEar();

        Assert.Equal(IdentityConfidence.Probable, _transport.IdentityConfidence);
        var battery = _transport.Current!.Battery;
        Assert.Equal(60, battery.Case.Level.Value);
        Assert.Equal(at, battery.Case.Level.ObservedAt);
        Assert.False(battery.Case.Charging.HasValue);
        Assert.True(battery.Left.Level.HasValue);

        // It is old by now, so the ring labels it with its age.
        var item = new Core.Presentation.BatteryItemViewModel("Case", "Case");
        item.Update(battery.Case, _clock.GetUtcNow(), isDemo: false);
        Assert.True(item.IsStale);
        Assert.False(item.IsCharging);
        Assert.Equal("3 h ago", item.StatusText);
    }

    [Fact]
    public async Task A_remembered_case_never_becomes_a_history_sample()
    {
        _transport.SetPairedDevice(ProThree, "dev");
        _transport.SetRememberedCase(60, RememberedAt);
        await StartAsync();
        EmitInEar();

        var sample = BatterySampling.FromState(_transport.Current!.Battery, BatterySampleSource.Broadcast)!;
        Assert.Null(sample.Case);
        Assert.Null(sample.CaseCharging);
        Assert.NotNull(sample.Left);
    }

    [Fact]
    public async Task It_is_not_shown_while_the_identity_is_unknown()
    {
        _transport.SetRememberedCase(60, RememberedAt);   // no paired device: nothing links the source to the owner
        await StartAsync();
        EmitInEar();

        Assert.Equal(IdentityConfidence.Unknown, _transport.IdentityConfidence);
        Assert.True(_transport.Current!.Battery.Left.Level.HasValue);
        Assert.False(_transport.Current.Battery.Case.Level.HasValue);
    }

    [Fact]
    public async Task It_is_not_shown_before_any_earbud_is_heard()
    {
        _transport.SetPairedDevice(ProThree, "dev");
        _transport.SetRememberedCase(60, RememberedAt);
        await StartAsync();

        Assert.False(_transport.Current!.Battery.Case.Level.HasValue);
    }

    [Fact]
    public async Task A_real_case_reading_replaces_it()
    {
        _transport.SetPairedDevice(ProThree, "dev");
        _transport.SetRememberedCase(60, RememberedAt);
        await StartAsync();
        EmitInEar();
        _clock.Advance(TimeSpan.FromSeconds(1));
        _source.Emit(Payload.Sighting(Payload.Build(model: ProThree, flagsAndCase: 0x43), address: 1)); // case 30

        var caseNow = _transport.Current!.Battery.Case;
        Assert.Equal(30, caseNow.Level.Value);
        Assert.Equal(_clock.GetUtcNow(), caseNow.Level.ObservedAt);

        // Back in the ears: the real reading is kept, not the older remembered one.
        _clock.Advance(TimeSpan.FromSeconds(30));
        EmitInEar();
        Assert.Equal(30, _transport.Current!.Battery.Case.Level.Value);
    }

    [Fact]
    public async Task It_is_cleared_when_the_selected_device_changes()
    {
        _transport.SetPairedDevice(ProThree, "one");
        _transport.SetRememberedCase(60, RememberedAt);
        await StartAsync();
        EmitInEar();
        Assert.Equal(60, _transport.Current!.Battery.Case.Level.Value);

        _transport.SetPairedDevice(ProThree, "two");
        EmitInEar(address: 2);

        Assert.True(_transport.IdentityConfidence.IsLikely());
        Assert.True(_transport.Current!.Battery.Left.Level.HasValue);
        Assert.False(_transport.Current.Battery.Case.Level.HasValue);
    }

    [Fact]
    public async Task A_message_from_another_pair_clears_it()
    {
        _transport.SetPairedDevice(ProThree, "dev");
        _transport.SetRememberedCase(60, RememberedAt);
        await StartAsync();
        EmitInEar(pods: 0x58);
        Assert.Equal(60, _transport.Current!.Battery.Case.Level.Value);

        // The first source goes quiet past the candidate window; another source with unrelated bud levels is heard.
        _clock.Advance(TimeSpan.FromSeconds(12));
        EmitInEar(pods: 0x23, address: 2);

        Assert.Equal(BroadcastPhase.Fresh, _transport.Phase);
        Assert.True(_transport.IdentityConfidence.IsLikely());
        Assert.False(_transport.Current!.Battery.Case.Level.HasValue);
    }

    [Fact]
    public async Task A_sibling_bud_message_keeps_it()
    {
        _transport.SetPairedDevice(ProThree, "dev");
        _transport.SetRememberedCase(60, RememberedAt);
        await StartAsync();
        _source.Emit(Payload.Sighting(Payload.Build(model: ProThree, flagsAndCase: CaseUnknown), address: 1));
        _source.Emit(Payload.Sighting(Payload.Build(model: ProThree, flagsAndCase: CaseUnknown, status: 0x02), address: 2));

        Assert.Equal(60, _transport.Current!.Battery.Case.Level.Value);
    }

    [Fact]
    public async Task Ambiguity_drops_it_so_it_cannot_attach_to_the_wrong_pair_afterwards()
    {
        _transport.SetPairedDevice(ProThree, "dev");
        _transport.SetRememberedCase(60, RememberedAt);
        await StartAsync();
        EmitInEar(pods: 0x58, address: 1);
        EmitInEar(pods: 0x23, address: 2);       // two different pairs at once
        Assert.Equal(BroadcastPhase.Ambiguous, _transport.Phase);

        _clock.Advance(TimeSpan.FromSeconds(12));
        EmitInEar(pods: 0x23, address: 2);       // only the second is heard now

        Assert.Equal(BroadcastPhase.Fresh, _transport.Phase);
        Assert.False(_transport.Current!.Battery.Case.Level.HasValue);
    }

    [Fact]
    public async Task It_survives_turning_detection_off_and_on()
    {
        _transport.SetPairedDevice(ProThree, "dev");
        var at = RememberedAt;
        _transport.SetRememberedCase(60, at);
        await StartAsync();
        EmitInEar();
        await _transport.SendAsync(new DisconnectCommand(Guid.NewGuid()));
        Assert.False(_transport.Current!.Battery.Case.Level.HasValue);

        await _transport.SendAsync(new ConnectCommand(Guid.NewGuid()));
        EmitInEar();

        Assert.Equal(60, _transport.Current!.Battery.Case.Level.Value);
        Assert.Equal(at, _transport.Current.Battery.Case.Level.ObservedAt);
    }

    [Fact]
    public async Task A_real_reading_at_probable_identity_is_carried_over_a_stop_and_start_with_its_time_and_unknown_charging()
    {
        _transport.SetPairedDevice(ProThree, "dev");
        await StartAsync();
        var readAt = _clock.GetUtcNow();
        _source.Emit(Payload.Sighting(Payload.Build(model: ProThree, flagsAndCase: 0x43), address: 1)); // case 30
        Assert.True(_transport.IdentityConfidence.IsLikely());
        Assert.Equal(30, _transport.Current!.Battery.Case.Level.Value);

        await _transport.SendAsync(new DisconnectCommand(Guid.NewGuid()));
        _clock.Advance(TimeSpan.FromMinutes(5));
        await _transport.SendAsync(new ConnectCommand(Guid.NewGuid()));
        EmitInEar();

        var battery = _transport.Current!.Battery;
        Assert.Equal(30, battery.Case.Level.Value);
        Assert.Equal(readAt, battery.Case.Level.ObservedAt);
        Assert.False(battery.Case.Charging.HasValue);
    }

    [Fact]
    public async Task A_real_reading_at_unknown_identity_is_not_carried_over_a_stop_and_start()
    {
        // No paired device: the source cannot be tied to the owner.
        await StartAsync();
        _source.Emit(Payload.Sighting(Payload.Build(model: ProThree, flagsAndCase: 0x43), address: 1));
        Assert.Equal(IdentityConfidence.Unknown, _transport.IdentityConfidence);
        Assert.Equal(30, _transport.Current!.Battery.Case.Level.Value);

        await _transport.SendAsync(new DisconnectCommand(Guid.NewGuid()));
        await _transport.SendAsync(new ConnectCommand(Guid.NewGuid()));
        EmitInEar();

        Assert.True(_transport.Current!.Battery.Left.Level.HasValue);
        Assert.False(_transport.Current.Battery.Case.Level.HasValue);
    }

    [Fact]
    public async Task Ambiguity_after_a_real_reading_leaves_nothing_to_attach_to_the_next_accepted_pair()
    {
        _transport.SetPairedDevice(ProThree, "dev");
        await StartAsync();
        _source.Emit(Payload.Sighting(Payload.Build(model: ProThree, pods: 0x58, flagsAndCase: 0x43), address: 1)); // case 30
        Assert.Equal(30, _transport.Current!.Battery.Case.Level.Value);
        EmitInEar(pods: 0x23, address: 2);       // a different pair at once
        Assert.Equal(BroadcastPhase.Ambiguous, _transport.Phase);

        _clock.Advance(TimeSpan.FromSeconds(12));
        EmitInEar(pods: 0x23, address: 2);       // only the second pair is heard, with no case level

        Assert.Equal(BroadcastPhase.Fresh, _transport.Phase);
        Assert.False(_transport.Current!.Battery.Case.Level.HasValue);
    }

    [Fact]
    public async Task Setting_it_while_the_earbuds_are_already_showing_republishes()
    {
        _transport.SetPairedDevice(ProThree, "dev");
        await StartAsync();
        EmitInEar();
        Assert.False(_transport.Current!.Battery.Case.Level.HasValue);

        _transport.SetRememberedCase(45, RememberedAt);
        Assert.Equal(45, _transport.Current!.Battery.Case.Level.Value);

        _transport.SetRememberedCase(null, null);
        Assert.False(_transport.Current!.Battery.Case.Level.HasValue);
    }

    [Fact]
    public async Task Out_of_range_levels_and_times_in_the_future_are_not_shown()
    {
        _transport.SetPairedDevice(ProThree, "dev");
        await StartAsync();
        EmitInEar();

        _transport.SetRememberedCase(150, RememberedAt);
        Assert.False(_transport.Current!.Battery.Case.Level.HasValue);
        _transport.SetRememberedCase(60, _clock.GetUtcNow().AddHours(2));
        Assert.False(_transport.Current!.Battery.Case.Level.HasValue);
    }
}
