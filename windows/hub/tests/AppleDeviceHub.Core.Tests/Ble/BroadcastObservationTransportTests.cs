using AppleDeviceHub.Core.AirPods;
using AppleDeviceHub.Core.Ble;
using AppleDeviceHub.Core.Contracts;
using Microsoft.Extensions.Time.Testing;

namespace AppleDeviceHub.Core.Tests.Ble;

/// <summary>Transport behaviour against a scripted source and a fake clock. No radio is touched.</summary>
public class BroadcastObservationTransportTests
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeAdvertisementSource _source = new();
    private readonly List<DeviceState> _events = new();

    private BroadcastObservationTransport Create(BroadcastObservationOptions? options = null)
    {
        var t = new BroadcastObservationTransport(_source, _clock, options);
        t.StateChanged += (_, s) => _events.Add(s);
        return t;
    }

    private static Task<CommandResult> Connect(BroadcastObservationTransport t) => t.SendAsync(new ConnectCommand(Guid.NewGuid()));

    private static Task<CommandResult> Disconnect(BroadcastObservationTransport t) => t.SendAsync(new DisconnectCommand(Guid.NewGuid()));

    [Fact]
    public async Task Start_publishes_an_unknown_state_and_does_not_listen()
    {
        await using var t = Create();
        await t.StartAsync();
        await t.StartAsync(); // idempotent

        Assert.Equal(0, _source.StartCount);
        Assert.False(t.IsObserving);
        var s = Assert.Single(_events);
        Assert.Equal(DataOrigin.Broadcast, s.Origin);
        Assert.Equal(DataOrigin.Broadcast, t.Info.Origin);
        Assert.Equal(BroadcastObservationTransport.DeviceId, s.DeviceId);
        Assert.True(s.Supports(DeviceCapabilities.BroadcastObservation | DeviceCapabilities.ConnectControl | DeviceCapabilities.Battery));
        Assert.False(s.Supports(DeviceCapabilities.ListeningModes));
        Assert.False(s.Supports(DeviceCapabilities.ConversationAwareness));
        Assert.False(s.Supports(DeviceCapabilities.AdaptiveStrength));
        Assert.False(s.Supports(DeviceCapabilities.ControlSessionOnly));
        Assert.Equal(ConnectionSummary.Unknown, s.Link.Summarize());
        Assert.False(s.Battery.Left.Level.HasValue);
    }

    [Fact]
    public async Task Sightings_before_the_user_starts_observing_are_ignored()
    {
        await using var t = Create();
        await t.StartAsync();
        _source.Emit(Payload.Sighting(Payload.Build()));
        Assert.Single(_events);
        Assert.Null(t.LastDecision);
    }

    [Fact]
    public async Task Connect_starts_observing_and_an_accepted_broadcast_is_shown_unconfirmed()
    {
        await using var t = Create();
        await t.StartAsync();

        var result = await Connect(t);
        Assert.Equal(CommandOutcome.Acknowledged, result.Outcome);
        Assert.Equal(DataOrigin.Broadcast, result.Origin);
        Assert.Equal(1, _source.StartCount);
        Assert.Equal(BroadcastPhase.Waiting, t.Phase);

        _source.Emit(Payload.Sighting(Payload.Build()));
        var s = t.Current!;
        Assert.Equal(BroadcastPhase.Fresh, t.Phase);
        Assert.Equal(80, s.Battery.Left.Level.Value);
        Assert.Equal(50, s.Battery.Right.Level.Value);
        Assert.Equal(70, s.Battery.Case.Level.Value);
        Assert.True(s.Battery.Case.Charging.Value);
        Assert.True(s.Link.Nearby.Value);
        Assert.False(s.Link.ProtocolSession.HasValue);
        Assert.False(s.Link.AudioConnected.HasValue);
        Assert.Equal("AirPods Pro 2 (USB-C)", s.ModelName);
        Assert.Contains("unconfirmed", s.DisplayName, StringComparison.OrdinalIgnoreCase);
        Assert.False(t.IsIdentityConfirmed);
        Assert.True(t.EarsAreApproximate);
        // Ear bits parsed -> EarDetection, display only.
        Assert.True(s.Supports(DeviceCapabilities.EarDetection));
        Assert.Equal(PodPlacement.InEar, s.Ears.Primary.Value);
        Assert.Equal(PodPlacement.OutOfEar, s.Ears.Secondary.Value);
    }

    [Fact]
    public async Task Duplicate_packets_keep_ui_state_suppressed_but_forward_a_fresh_ear_sighting()
    {
        await using var t = Create();
        var earSightings = new List<DeviceState>();
        t.EarSightingObserved += (_, state) => earSightings.Add(state);
        await t.StartAsync();
        await Connect(t);
        var sighting = Payload.Sighting(Payload.Build());

        _source.Emit(sighting);
        int stateEventsAfterFirstPacket = _events.Count;
        _clock.Advance(TimeSpan.FromMilliseconds(1200));
        _source.Emit(sighting);

        Assert.Equal(stateEventsAfterFirstPacket, _events.Count); // identical UI state stays suppressed
        var fresh = Assert.Single(earSightings);
        Assert.Equal(_clock.GetUtcNow(), fresh.Ears.Primary.ObservedAt);
        Assert.Equal(_clock.GetUtcNow(), fresh.Ears.Secondary.ObservedAt);
    }

    [Fact]
    public async Task Unlisted_model_shows_no_model_name_claim()
    {
        await using var t = Create();
        await t.StartAsync();
        await Connect(t);
        _source.Emit(Payload.Sighting(Payload.Build(model: Payload.Unlisted)));
        Assert.StartsWith(AppleModelCatalog.UnrecognisedName, t.Current!.ModelName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Short_frame_without_status_byte_is_refused_and_claims_nothing()
    {
        await using var t = Create();
        await t.StartAsync();
        await Connect(t);
        _source.Emit(Payload.Sighting([0x07, 0x03, 0x01, 0x24, 0x20]));
        Assert.Equal(BroadcastPhase.Waiting, t.Phase);
        Assert.False(t.Current!.Supports(DeviceCapabilities.EarDetection));
        Assert.False(t.Current.Ears.Primary.HasValue);
    }

    [Theory]
    [MemberData(nameof(Writes))]
    public async Task Every_write_command_is_unsupported(DeviceCommand command)
    {
        await using var t = Create();
        await t.StartAsync();
        Assert.Equal(CommandOutcome.Unsupported, (await t.SendAsync(command)).Outcome);
        await Connect(t);
        var result = await t.SendAsync(command);
        Assert.Equal(CommandOutcome.Unsupported, result.Outcome);
        Assert.Equal(DataOrigin.Broadcast, result.Origin);
    }

    public static TheoryData<DeviceCommand> Writes => new()
    {
        new SetListeningModeCommand(Guid.NewGuid(), ListeningMode.NoiseCancellation),
        new SetConversationAwarenessCommand(Guid.NewGuid(), true),
        new SetAdaptiveStrengthCommand(Guid.NewGuid(), 50),
    };

    [Fact]
    public async Task Connect_and_disconnect_are_idempotent_and_nothing_is_published_after_stop()
    {
        await using var t = Create();
        await t.StartAsync();
        await Connect(t);
        await Connect(t);
        Assert.Equal(1, _source.StartCount);

        _source.Emit(Payload.Sighting(Payload.Build()));
        Assert.Equal(CommandOutcome.Acknowledged, (await Disconnect(t)).Outcome);
        Assert.Equal(CommandOutcome.Acknowledged, (await Disconnect(t)).Outcome);
        Assert.Equal(1, _source.StopCount);
        Assert.False(t.IsObserving);
        Assert.Equal(BroadcastPhase.NotObserving, t.Phase);
        Assert.False(t.Current!.Battery.Left.Level.HasValue);

        int count = _events.Count;
        _source.Emit(Payload.Sighting(Payload.Build(pods: 0x33)));
        _clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(count, _events.Count);
    }

    [Fact]
    public async Task Values_go_stale_then_nearby_becomes_false()
    {
        await using var t = Create();
        await t.StartAsync();
        await Connect(t);
        _source.Emit(Payload.Sighting(Payload.Build()));
        var heard = _clock.GetUtcNow();

        _clock.Advance(t.Options.StaleAfter - TimeSpan.FromSeconds(1));
        Assert.Equal(BroadcastPhase.Fresh, t.Phase);

        _clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(BroadcastPhase.Stale, t.Phase);
        var stale = t.Current!;
        Assert.Equal(Freshness.Stale, stale.Battery.Left.Level.FreshnessAt(_clock.GetUtcNow(), t.Options.StaleAfter));
        Assert.Equal(heard, stale.Battery.Left.Level.ObservedAt);
        Assert.False(stale.Ears.Primary.HasValue); // approximate ears are dropped once stale
        Assert.False(stale.Supports(DeviceCapabilities.EarDetection));
        Assert.True(stale.Link.Nearby.Value);
        Assert.Equal(Freshness.Stale, stale.Link.Nearby.FreshnessAt(_clock.GetUtcNow(), t.Options.StaleAfter));

        _clock.Advance(t.Options.LostAfter);
        Assert.Equal(BroadcastPhase.Lost, t.Phase);
        var lost = t.Current!;
        Assert.False(lost.Link.Nearby.Value);
        Assert.Equal(ConnectionSummary.NotDetected, lost.Link.Summarize());
        Assert.False(lost.Link.ProtocolSession.HasValue);
        Assert.False(lost.Link.AudioConnected.HasValue);

        // Hearing it again makes it fresh.
        _source.Emit(Payload.Sighting(Payload.Build()));
        Assert.Equal(BroadcastPhase.Fresh, t.Phase);
        Assert.True(t.Current!.Link.Nearby.Value);
    }

    [Fact]
    public async Task Duplicates_within_the_window_do_not_bump_the_sequence()
    {
        await using var t = Create();
        await t.StartAsync();
        await Connect(t);

        _source.Emit(Payload.Sighting(Payload.Build()));
        long seq = t.Current!.Sequence;
        _clock.Advance(TimeSpan.FromSeconds(1));
        _source.Emit(Payload.Sighting(Payload.Build()));
        Assert.Equal(seq, t.Current!.Sequence);

        // A changed payload publishes at once.
        _source.Emit(Payload.Sighting(Payload.Build(pods: 0x57)));
        Assert.True(t.Current!.Sequence > seq);
        seq = t.Current!.Sequence;

        // The same payload after the window refreshes the observation time.
        _clock.Advance(TimeSpan.FromSeconds(6));
        _source.Emit(Payload.Sighting(Payload.Build(pods: 0x57)));
        Assert.True(t.Current!.Sequence > seq);
        Assert.Equal(_clock.GetUtcNow(), t.Current!.Battery.Left.Level.ObservedAt);
    }

    [Fact]
    public async Task Sequence_increases_strictly_across_every_event()
    {
        await using var t = Create();
        await t.StartAsync();
        await Connect(t);
        _source.Emit(Payload.Sighting(Payload.Build()));
        _source.Emit(Payload.Sighting(Payload.Build(pods: 0x44)));
        _clock.Advance(TimeSpan.FromSeconds(40));
        _clock.Advance(TimeSpan.FromSeconds(100));
        await Disconnect(t);
        await Connect(t);
        _source.Emit(Payload.Sighting(Payload.Build()));

        Assert.True(_events.Count >= 7);
        for (int i = 1; i < _events.Count; i++) Assert.True(_events[i].Sequence > _events[i - 1].Sequence);
    }

    [Fact]
    public async Task Two_sources_show_ambiguity_and_no_values()
    {
        await using var t = Create();
        await t.StartAsync();
        await Connect(t);
        _source.Emit(Payload.Sighting(Payload.Build(), address: 1));
        _source.Emit(Payload.Sighting(Payload.Build(pods: 0x22), address: 2));

        Assert.Equal(BroadcastPhase.Ambiguous, t.Phase);
        var s = t.Current!;
        Assert.False(s.Battery.Left.Level.HasValue);
        Assert.False(s.Link.Nearby.HasValue);
        Assert.Equal(AttributionVerdict.Ambiguous, t.LastDecision!.Verdict);

        // Further sightings while still ambiguous do not publish again.
        int count = _events.Count;
        _source.Emit(Payload.Sighting(Payload.Build(), address: 1));
        Assert.Equal(count, _events.Count);

        // After every candidate goes quiet past the window, ambiguity is no longer claimed.
        _clock.Advance(TimeSpan.FromSeconds(12));
        Assert.NotEqual(BroadcastPhase.Ambiguous, t.Phase);
    }

    [Fact]
    public async Task A_partial_reading_does_not_bridge_two_conflicting_pairs()
    {
        await using var t = Create();
        await t.StartAsync();
        await Connect(t);
        _source.Emit(Payload.Sighting(Payload.Build(pods: 0xAA, flagsAndCase: 0x47, lid: 1), address: 1)); // case 70, lid 1
        _source.Emit(Payload.Sighting(Payload.Build(pods: 0xAA, flagsAndCase: 0x4F, lid: 0), address: 2)); // no case level: fits both
        _source.Emit(Payload.Sighting(Payload.Build(pods: 0xAA, flagsAndCase: 0x43, lid: 2), address: 3)); // case 30, lid 2

        Assert.Equal(BroadcastPhase.Ambiguous, t.Phase);
        Assert.Equal(AttributionVerdict.Ambiguous, t.LastDecision!.Verdict);
        Assert.Equal(2, t.Sources.Count); // the partial reading joins one pair, the conflicting one stays separate
    }

    [Fact]
    public async Task Name_only_sightings_never_produce_values()
    {
        await using var t = Create();
        await t.StartAsync();
        await Connect(t);
        _source.Emit(Payload.Sighting([0x10, 0x02, 0x00, 0x00], name: "AirPods Pro"));
        Assert.Equal(BroadcastPhase.Waiting, t.Phase);
        Assert.Equal(AttributionVerdict.Rejected, t.LastDecision!.Verdict);
    }

    [Fact]
    public async Task Radio_unavailable_at_start_is_reported_and_retried_until_the_radio_is_back()
    {
        _source.OnStart = _ => Task.FromResult(new AdvertisementSourceStatus(AdvertisementSourceState.Unavailable, "Bluetooth is turned off."));
        await using var t = Create();
        int statusChanges = 0;
        t.StatusChanged += (_, _) => Interlocked.Increment(ref statusChanges);
        await t.StartAsync();

        var result = await Connect(t);
        Assert.Equal(CommandOutcome.Acknowledged, result.Outcome); // accepted; being retried
        Assert.Contains("Bluetooth is turned off", result.Detail, StringComparison.Ordinal);
        Assert.Equal("Bluetooth is turned off.", t.UnavailableReason);
        Assert.True(t.IsObserving);
        Assert.Equal(1, statusChanges);

        // Radio back on: the retry brings it up without any user action.
        _source.OnStart = null;
        _clock.Advance(t.Options.RestartRetryInterval);
        await WaitUntilAsync(() => t.UnavailableReason is null);
        Assert.True(t.IsObserving);
        _source.Emit(Payload.Sighting(Payload.Build()));
        Assert.Equal(BroadcastPhase.Fresh, t.Phase);
    }

    [Fact]
    public async Task A_user_stop_during_failed_start_retries_ends_them()
    {
        _source.OnStart = _ => Task.FromResult(new AdvertisementSourceStatus(AdvertisementSourceState.Unavailable, "Bluetooth is turned off."));
        await using var t = Create();
        await t.StartAsync();
        await Connect(t);
        await Disconnect(t);

        Assert.False(t.IsObserving);
        Assert.Null(t.UnavailableReason);
        int starts = _source.StartCount;
        _source.OnStart = null;
        _clock.Advance(TimeSpan.FromMinutes(5));
        await Task.Delay(50);
        Assert.Equal(starts, _source.StartCount);
    }

    [Fact]
    public async Task Source_going_unavailable_while_observing_explains_keeps_the_reading_and_keeps_observing()
    {
        await using var t = Create();
        await t.StartAsync();
        await Connect(t);
        _source.Emit(Payload.Sighting(Payload.Build()));

        _source.RaiseStatus(new AdvertisementSourceStatus(AdvertisementSourceState.Unavailable, "The Bluetooth radio is not available."));
        Assert.True(t.IsObserving); // only the user's Stop ends observing
        Assert.Equal("The Bluetooth radio is not available.", t.UnavailableReason);
        Assert.True(t.Current!.Battery.Left.Level.HasValue); // the last reading stays, aging as usual
    }

    [Fact]
    public async Task A_stopped_listener_is_restarted_until_it_runs_again()
    {
        await using var t = Create();
        await t.StartAsync();
        await Connect(t);
        int statusChanges = 0;
        t.StatusChanged += (_, _) => Interlocked.Increment(ref statusChanges);

        // Radio off: the restart fails at first, then works once the radio is back.
        _source.OnStart = _ => Task.FromResult(new AdvertisementSourceStatus(AdvertisementSourceState.Unavailable, "Bluetooth is turned off."));
        _source.RaiseStatus(new AdvertisementSourceStatus(AdvertisementSourceState.Stopped));
        Assert.Equal("Bluetooth listening stopped unexpectedly.", t.UnavailableReason);

        _clock.Advance(t.Options.TickInterval);
        await WaitUntilAsync(() => _source.StartCount >= 2);
        await WaitUntilAsync(() => t.UnavailableReason == "Bluetooth is turned off.");
        Assert.True(t.IsObserving);

        // No new attempt before the retry interval has passed.
        int attempts = _source.StartCount;
        _clock.Advance(TimeSpan.FromSeconds(5));
        await Task.Delay(50);
        Assert.Equal(attempts, _source.StartCount);

        _source.OnStart = null; // radio back on
        _clock.Advance(t.Options.RestartRetryInterval);
        await WaitUntilAsync(() => t.UnavailableReason is null);
        Assert.True(t.IsObserving);
        Assert.True(statusChanges >= 2);

        // And it hears again.
        _source.Emit(Payload.Sighting(Payload.Build()));
        Assert.Equal(BroadcastPhase.Fresh, t.Phase);
    }

    [Fact]
    public async Task A_user_stop_stays_stopped_even_after_the_listener_dropped()
    {
        await using var t = Create();
        await t.StartAsync();
        await Connect(t);
        _source.RaiseStatus(new AdvertisementSourceStatus(AdvertisementSourceState.Stopped));
        await Disconnect(t);

        int starts = _source.StartCount;
        _clock.Advance(TimeSpan.FromMinutes(30));
        await Task.Delay(50);
        Assert.Equal(starts, _source.StartCount);
        Assert.False(t.IsObserving);
    }

    [Fact]
    public async Task Silence_restarts_the_listener_once_per_watchdog_interval()
    {
        await using var t = Create();
        await t.StartAsync();
        await Connect(t);
        Assert.Equal(1, _source.StartCount);

        _clock.Advance(t.Options.WatchdogAfter - TimeSpan.FromSeconds(5));
        await Task.Delay(50);
        Assert.Equal(1, _source.StartCount); // not yet

        _clock.Advance(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => _source.StartCount == 2);
        Assert.True(t.IsObserving);

        // Still silent: one more restart per interval, not one per tick.
        _clock.Advance(t.Options.WatchdogAfter - TimeSpan.FromSeconds(30));
        await Task.Delay(50);
        Assert.Equal(2, _source.StartCount);
        _clock.Advance(TimeSpan.FromSeconds(60));
        await WaitUntilAsync(() => _source.StartCount == 3);
    }

    [Fact]
    public async Task Any_apple_advertisement_keeps_the_watchdog_quiet()
    {
        await using var t = Create();
        await t.StartAsync();
        await Connect(t);

        for (int i = 0; i < 20; i++)
        {
            _clock.Advance(TimeSpan.FromMinutes(1));
            _source.Emit(Payload.Sighting([0x10, 0x01, 0x00])); // another Apple message type: heard, not attributed
        }
        await Task.Delay(50);
        Assert.Equal(1, _source.StartCount);
    }

    [Fact]
    public async Task A_different_model_with_unknown_case_does_not_inherit_the_earlier_models_case()
    {
        await using var t = Create();
        await t.StartAsync();
        await Connect(t);
        _source.Emit(Payload.Sighting(Payload.Build(model: Payload.ProTwoUsbC), address: 1));
        Assert.Equal(70, t.Current!.Battery.Case.Level.Value);

        // The first source goes quiet past the candidate window, then another model is heard in ear (case unknown).
        _clock.Advance(TimeSpan.FromSeconds(12));
        _source.Emit(Payload.Sighting(Payload.Build(model: Payload.ProThree, pods: 0x23, flagsAndCase: 0x4F), address: 2));

        var s = t.Current!;
        Assert.Equal(BroadcastPhase.Fresh, t.Phase);
        Assert.False(s.Battery.Case.Level.HasValue);
        Assert.Equal(30, s.Battery.Left.Level.Value);
        Assert.Equal(20, s.Battery.Right.Level.Value);
        Assert.Equal(AppleModelCatalog.NameOf(Payload.ProThree), s.ModelName);
    }

    [Fact]
    public async Task A_sibling_bud_in_ear_keeps_the_case_level_from_the_case_open_packet()
    {
        await using var t = Create();
        await t.StartAsync();
        await Connect(t);
        _source.Emit(Payload.Sighting(Payload.Build(), address: 1));
        _source.Emit(Payload.Sighting(Payload.Build(flagsAndCase: 0x4F, status: 0x02), address: 2));

        var s = t.Current!;
        Assert.Equal(70, s.Battery.Case.Level.Value);
        // The sibling reports the sides swapped (opposite primary bit).
        Assert.Equal(50, s.Battery.Left.Level.Value);
        Assert.Equal(80, s.Battery.Right.Level.Value);
    }

    [Fact]
    public async Task A_new_models_own_case_level_is_remembered_after_the_old_one_is_dropped()
    {
        await using var t = Create();
        await t.StartAsync();
        await Connect(t);
        _source.Emit(Payload.Sighting(Payload.Build(model: Payload.ProTwoUsbC), address: 1));
        _clock.Advance(TimeSpan.FromSeconds(12));
        _source.Emit(Payload.Sighting(Payload.Build(model: Payload.ProThree, flagsAndCase: 0x43), address: 2));
        Assert.Equal(30, t.Current!.Battery.Case.Level.Value);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition(), "The condition was not met in time.");
    }

    [Fact]
    public async Task Cancelled_start_rolls_back_cleanly()
    {
        var gate = new TaskCompletionSource<AdvertisementSourceStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        _source.OnStart = async ct =>
        {
            using var reg = ct.Register(() => gate.TrySetCanceled(ct));
            return await gate.Task;
        };
        await using var t = Create();
        await t.StartAsync();
        using var cts = new CancellationTokenSource();

        var pending = t.SendAsync(new ConnectCommand(Guid.NewGuid()), cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        Assert.False(t.IsObserving);
        Assert.Equal(BroadcastPhase.NotObserving, t.Phase);
        Assert.Equal(1, _source.StopCount);
        int count = _events.Count;
        _source.Emit(Payload.Sighting(Payload.Build()));
        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(count, _events.Count);
    }

    [Fact]
    public async Task Dispose_is_idempotent_silences_events_and_disposes_the_source()
    {
        var t = Create();
        await t.StartAsync();
        await Connect(t);
        _source.Emit(Payload.Sighting(Payload.Build()));
        int count = _events.Count;

        await t.DisposeAsync();
        await t.DisposeAsync();
        Assert.True(_source.Disposed);
        Assert.Equal(1, _source.StopCount);

        _source.Emit(Payload.Sighting(Payload.Build(pods: 0x11)));
        _clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(count, _events.Count);
        Assert.Equal(CommandOutcome.TransportUnavailable, (await Connect(t)).Outcome);
        Assert.Equal(CommandOutcome.TransportUnavailable,
            (await t.SendAsync(new SetListeningModeCommand(Guid.NewGuid(), ListeningMode.Off))).Outcome);
    }

    [Fact]
    public async Task A_throwing_subscriber_does_not_escape_into_the_platform_callback()
    {
        await using var t = Create();
        await t.StartAsync();
        await Connect(t);
        t.StateChanged += (_, _) => throw new InvalidOperationException("subscriber fault");
        _source.Emit(Payload.Sighting(Payload.Build())); // must not throw
    }
}

public class BroadcastTransportSelectionTests
{
    [Fact]
    public void Ble_is_an_explicit_opt_in_choice()
    {
        var choice = TransportSelection.FromArgs(["--transport=ble"]);
        Assert.Equal(TransportKind.Broadcast, choice.Kind);
        Assert.True(choice.IsAvailable);
        Assert.False(choice.IsDemo);
        Assert.Null(choice.Hardware);
    }

    [Fact]
    public void Default_is_still_the_demo()
    {
        Assert.Equal(TransportKind.Demo, TransportSelection.FromArgs([]).Kind);
        Assert.True(TransportSelection.FromArgs([]).IsDemo);
    }

    [Fact]
    public void Hardware_arguments_with_ble_are_refused()
    {
        var choice = TransportSelection.FromArgs(["--transport=ble", "--device=dev-1"]);
        Assert.False(choice.IsAvailable);
    }

    [Fact]
    public void Space_separated_form_is_refused()
        => Assert.False(TransportSelection.FromArgs(["--transport", "ble"]).IsAvailable);
}
