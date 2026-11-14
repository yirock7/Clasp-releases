using AppleDeviceHub.Core.Ble;
using AppleDeviceHub.Core.Contracts;
using AppleDeviceHub.Core.Media;
using Microsoft.Extensions.Time.Testing;

namespace AppleDeviceHub.Core.Tests.Media;

/// <summary>P2-B: opt-in ear detection from nearby broadcasts (owner decision 2026-10-20).</summary>
public class BroadcastEarDetectionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
    private const PodPlacement In = PodPlacement.InEar;
    private const PodPlacement Out = PodPlacement.OutOfEar;

    private sealed class FakeMedia : IMediaSessionController
    {
        public MediaSnapshot Current { get; set; } = new("player#1", MediaPlayback.Playing);
        public bool CallActive { get; set; }
        public List<string> Calls { get; } = new();
        public event EventHandler<MediaSnapshot>? Changed;

        public void Raise(MediaSnapshot s) { Current = s; Changed?.Invoke(this, s); }

        public Task<bool> TryPauseAsync(string sessionId, CancellationToken cancellationToken)
        {
            Calls.Add("pause " + sessionId);
            Raise(Current with { Playback = MediaPlayback.Paused });
            return Task.FromResult(true);
        }

        public Task<bool> TryPlayAsync(string sessionId, CancellationToken cancellationToken)
        {
            Calls.Add("play " + sessionId);
            Raise(Current with { Playback = MediaPlayback.Playing });
            return Task.FromResult(true);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BroadcastTransport : IAirPodsTransport
    {
        private long _seq;
        public TransportInfo Info { get; } = new("Broadcast", DataOrigin.Broadcast, HubContract.Version, "test");
        public DeviceState? Current { get; private set; }
        public event EventHandler<DeviceState>? StateChanged;
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<CommandResult> SendAsync(DeviceCommand command, CancellationToken cancellationToken = default)
            => Task.FromResult(new CommandResult(command.CommandId, CommandOutcome.Unsupported, DataOrigin.Broadcast));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void Ears(PodPlacement a, PodPlacement b, DateTimeOffset observedAt, DateTimeOffset publishedAt)
        {
            Current = Build(a, b, observedAt, publishedAt, ++_seq);
            StateChanged?.Invoke(this, Current);
        }

        public static DeviceState Build(PodPlacement a, PodPlacement b, DateTimeOffset observedAt, DateTimeOffset publishedAt, long seq = 1)
            => new("bc", "Nearby AirPods", "AirPods Pro", DataOrigin.Broadcast, seq, publishedAt,
                DeviceCapabilities.Battery | DeviceCapabilities.BroadcastObservation | DeviceCapabilities.EarDetection,
                LinkState.Unknown, BatteryState.Unknown, ListeningState.Unknown, default, default)
            {
                Ears = new EarState(Observation<PodPlacement>.At(a, observedAt), Observation<PodPlacement>.At(b, observedAt)),
            };
    }

    /// <summary>A rig with a movable clock; the setting is OFF unless a test turns it on.</summary>
    private sealed class Rig
    {
        public DateTimeOffset Now = T0;
        public IdentityConfidence Identity = IdentityConfidence.LikelyYours;
        public bool Connected = true;
        public readonly FakeMedia Media = new();
        public readonly BroadcastTransport Transport = new();
        public readonly EarMediaCoordinator Coordinator;
        public readonly List<BroadcastEarResult> Results = new();

        public Rig(bool setting)
        {
            Coordinator = new EarMediaCoordinator(new EarMediaPolicy { Enabled = true }, Media, () => Now)
            {
                BroadcastIdentity = () => Identity,
                WindowsConnected = () => Connected,
            };
            Coordinator.BroadcastEarEvaluated += (_, r) => Results.Add(r);
            Coordinator.BroadcastEarDetection = setting;
            Coordinator.Attach(Transport);
        }

        public void Advance(double seconds) => Now += TimeSpan.FromSeconds(seconds);

        /// <summary>One broadcast sighting heard now.</summary>
        public void Hear(PodPlacement a, PodPlacement b) => Transport.Ears(a, b, Now, Now);

        /// <summary>Two sightings 1.2 s apart: the minimum for a stable reading.</summary>
        public void HearStable(PodPlacement a, PodPlacement b)
        {
            Hear(a, b);
            Advance(1.2);
            Hear(a, b);
        }
    }

    // ---- BroadcastEarSource ----

    private static DeviceState S(PodPlacement a, PodPlacement b, DateTimeOffset at) => BroadcastTransport.Build(a, b, at, at);

    [Fact]
    public void Source_is_off_by_default_and_never_releases_a_reading()
    {
        var source = new BroadcastEarSource();
        Assert.False(source.Enabled);
        var r = source.Evaluate(S(In, In, T0), IdentityConfidence.LikelyYours, true, T0);
        Assert.Equal(BroadcastEarVerdict.Rejected, r.Verdict);
        Assert.Contains("off", r.Reason);
    }

    [Theory]
    [InlineData(IdentityConfidence.Unknown, false)]
    [InlineData(IdentityConfidence.Probable, false)]
    [InlineData(IdentityConfidence.LikelyYours, true)]
    public void Source_needs_an_independent_pairing_clue(IdentityConfidence identity, bool released)
    {
        var source = new BroadcastEarSource { Enabled = true };
        source.Evaluate(S(In, In, T0), identity, true, T0);
        var now = T0.AddSeconds(1.2);
        var r = source.Evaluate(S(In, In, now), identity, true, now);
        Assert.Equal(released ? BroadcastEarVerdict.Stable : BroadcastEarVerdict.Rejected, r.Verdict);
    }

    [Fact]
    public void Source_rejects_when_not_connected_in_windows_or_not_broadcast()
    {
        var source = new BroadcastEarSource { Enabled = true };
        Assert.Equal(BroadcastEarVerdict.Rejected,
            source.Evaluate(S(In, In, T0), IdentityConfidence.LikelyYours, windowsConnected: false, T0).Verdict);
        var hardware = S(In, In, T0) with { Origin = DataOrigin.Hardware };
        Assert.Equal(BroadcastEarVerdict.Rejected,
            source.Evaluate(hardware, IdentityConfidence.LikelyYours, true, T0).Verdict);
    }

    [Fact]
    public void Source_rejects_stale_readings()
    {
        var source = new BroadcastEarSource { Enabled = true };
        var late = T0.AddSeconds(6); // older than MaxAge (5 s) when evaluated
        var r = source.Evaluate(S(In, In, T0), IdentityConfidence.LikelyYours, true, late);
        Assert.Equal(BroadcastEarVerdict.Rejected, r.Verdict);
        Assert.Contains("stale", r.Reason);
    }

    [Fact]
    public void Source_needs_two_distinct_sightings_over_a_second()
    {
        var source = new BroadcastEarSource { Enabled = true };
        Assert.Equal(BroadcastEarVerdict.Pending, source.Evaluate(S(In, In, T0), IdentityConfidence.LikelyYours, true, T0).Verdict);

        // The same observation republished (identity rating changed): still one sighting.
        var again = T0.AddSeconds(1.5);
        var sameSighting = BroadcastTransport.Build(In, In, T0, again);
        Assert.Equal(BroadcastEarVerdict.Pending, source.Evaluate(sameSighting, IdentityConfidence.LikelyYours, true, T0.AddSeconds(2)).Verdict);

        // A second sighting only 0.4 s later: too soon.
        var quick = T0.AddSeconds(0.4);
        var fresh = new BroadcastEarSource { Enabled = true };
        fresh.Evaluate(S(In, In, T0), IdentityConfidence.LikelyYours, true, T0);
        Assert.Equal(BroadcastEarVerdict.Pending, fresh.Evaluate(S(In, In, quick), IdentityConfidence.LikelyYours, true, quick).Verdict);

        var stable = T0.AddSeconds(1.1);
        var ok = fresh.Evaluate(S(In, In, stable), IdentityConfidence.LikelyYours, true, stable);
        Assert.Equal(BroadcastEarVerdict.Stable, ok.Verdict);
        Assert.Equal(stable, ok.Reading!.ObservedAt);
        Assert.Equal(2, ok.Reading.InEarCount);
    }

    [Fact]
    public void Source_a_gate_failure_clears_stability()
    {
        var source = new BroadcastEarSource { Enabled = true };
        source.Evaluate(S(In, In, T0), IdentityConfidence.LikelyYours, true, T0);
        var t1 = T0.AddSeconds(1.2);
        source.Evaluate(S(In, In, t1), IdentityConfidence.Unknown, true, t1); // identity lost
        var t2 = T0.AddSeconds(2.4);
        Assert.Equal(BroadcastEarVerdict.Pending, source.Evaluate(S(In, In, t2), IdentityConfidence.LikelyYours, true, t2).Verdict);
    }

    [Fact]
    public void Source_rejects_missing_ear_bits_and_disconnected_buds()
    {
        var source = new BroadcastEarSource { Enabled = true };
        var none = S(In, In, T0) with { Ears = EarState.Unknown };
        Assert.Equal(BroadcastEarVerdict.Rejected, source.Evaluate(none, IdentityConfidence.LikelyYours, true, T0).Verdict);
        var gone = S(In, PodPlacement.Disconnected, T0);
        Assert.Equal(BroadcastEarVerdict.Rejected, source.Evaluate(gone, IdentityConfidence.LikelyYours, true, T0).Verdict);
    }

    // ---- Coordinator: media actions ----

    [Fact]
    public async Task Setting_off_by_default_broadcast_never_drives_media()
    {
        var rig = new Rig(setting: false);
        rig.HearStable(In, In);
        rig.Advance(2);
        rig.HearStable(Out, Out);
        await rig.Coordinator.Pending;
        Assert.Empty(rig.Media.Calls);
        Assert.Empty(rig.Results);
        await rig.Coordinator.DisposeAsync();
    }

    [Theory]
    [InlineData(IdentityConfidence.Unknown)] // includes "two pairs of the same model nearby"
    [InlineData(IdentityConfidence.Probable)] // same-model sole candidate, without an independent clue
    public async Task Identity_without_an_independent_clue_never_acts(IdentityConfidence identity)
    {
        var rig = new Rig(setting: true) { Identity = identity };
        rig.HearStable(In, In);
        rig.Advance(2);
        rig.HearStable(Out, Out);
        await rig.Coordinator.Pending;
        Assert.Empty(rig.Media.Calls);
        Assert.Contains("pairing", rig.Coordinator.LastAction.Reason);
        await rig.Coordinator.DisposeAsync();
    }

    [Fact]
    public async Task Identity_dropping_to_ambiguous_after_baseline_cancels_the_pause()
    {
        var rig = new Rig(setting: true);
        rig.HearStable(In, In);
        rig.Advance(2);
        rig.Identity = IdentityConfidence.Unknown; // a second pair appears
        rig.HearStable(Out, Out);
        await rig.Coordinator.Pending;
        Assert.Empty(rig.Media.Calls);
        await rig.Coordinator.DisposeAsync();
    }

    [Fact]
    public async Task Not_connected_in_windows_never_acts()
    {
        var rig = new Rig(setting: true) { Connected = false };
        rig.HearStable(In, In);
        rig.Advance(2);
        rig.HearStable(Out, Out);
        await rig.Coordinator.Pending;
        Assert.Empty(rig.Media.Calls);
        await rig.Coordinator.DisposeAsync();
    }

    [Fact]
    public async Task Stale_broadcast_never_acts()
    {
        var rig = new Rig(setting: true);
        rig.HearStable(In, In);
        rig.Advance(2);
        // Both sightings were heard long ago relative to when the state is delivered.
        var heard = rig.Now;
        rig.Advance(10);
        rig.Transport.Ears(Out, Out, heard, rig.Now);
        rig.Transport.Ears(Out, Out, heard.AddSeconds(1.2), rig.Now);
        await rig.Coordinator.Pending;
        Assert.Empty(rig.Media.Calls);
        await rig.Coordinator.DisposeAsync();
    }

    [Fact]
    public async Task Jitter_blip_is_absorbed_while_worn()
    {
        var rig = new Rig(setting: true);
        rig.HearStable(In, In);
        rig.Advance(2);
        rig.Hear(Out, In);   // one jittery sighting (H2)
        rig.Advance(0.5);
        rig.Hear(In, In);    // back before it was stable
        rig.Advance(1.2);
        rig.Hear(In, In);
        await rig.Coordinator.Pending;
        Assert.Empty(rig.Media.Calls);
        await rig.Coordinator.DisposeAsync();
    }

    [Fact]
    public async Task Removal_pauses_once_and_putting_back_resumes_once()
    {
        var rig = new Rig(setting: true);
        rig.HearStable(In, In);            // baseline
        rig.Advance(2);
        rig.Hear(Out, Out);                // first sighting: not stable yet
        await rig.Coordinator.Pending;
        Assert.Empty(rig.Media.Calls);
        rig.Advance(1.2);
        rig.Hear(Out, Out);                // stable: pause
        await rig.Coordinator.Pending;
        Assert.Equal(new[] { "pause player#1" }, rig.Media.Calls);

        rig.Advance(6);                    // more of the same reading: nothing new
        rig.Hear(Out, Out);
        rig.Advance(1.2);
        rig.Hear(Out, Out);
        await rig.Coordinator.Pending;
        Assert.Equal(new[] { "pause player#1" }, rig.Media.Calls);

        rig.Advance(6);
        rig.HearStable(In, In);            // put back: resume
        await rig.Coordinator.Pending;
        Assert.Equal(new[] { "pause player#1", "play player#1" }, rig.Media.Calls);

        rig.Advance(6);
        rig.HearStable(In, In);
        await rig.Coordinator.Pending;
        Assert.Equal(2, rig.Media.Calls.Count);
        await rig.Coordinator.DisposeAsync();
    }

    [Fact]
    public async Task Does_not_resume_something_the_user_paused_or_when_clasp_did_not_pause()
    {
        var rig = new Rig(setting: true);
        rig.Media.Current = new("player#1", MediaPlayback.Paused); // nothing playing at removal
        rig.HearStable(In, In);
        rig.Advance(2);
        rig.HearStable(Out, Out);
        rig.Advance(2);
        rig.HearStable(In, In);
        await rig.Coordinator.Pending;
        Assert.Empty(rig.Media.Calls);
        await rig.Coordinator.DisposeAsync();
    }

    [Fact]
    public async Task Call_in_progress_means_no_action()
    {
        var rig = new Rig(setting: true);
        rig.HearStable(In, In);
        rig.Advance(2);
        rig.Media.CallActive = true;
        rig.HearStable(Out, Out);
        await rig.Coordinator.Pending;
        Assert.Empty(rig.Media.Calls);
        await rig.Coordinator.DisposeAsync();
    }

    [Fact]
    public async Task Turning_the_setting_off_or_disconnecting_forgets_the_pending_resume()
    {
        var rig = new Rig(setting: true);
        rig.HearStable(In, In);
        rig.Advance(2);
        rig.HearStable(Out, Out);
        await rig.Coordinator.Pending;
        Assert.Single(rig.Media.Calls);

        rig.Coordinator.NotifyWindowsConnection(false);
        rig.Advance(2);
        rig.HearStable(In, In);
        rig.Advance(2);
        rig.HearStable(In, In);
        await rig.Coordinator.Pending;
        Assert.Single(rig.Media.Calls); // never resumed across the disconnect
        await rig.Coordinator.DisposeAsync();
    }

    [Fact]
    public async Task Raises_gate_results_for_the_output_switch_adapter()
    {
        var rig = new Rig(setting: true);
        rig.HearStable(In, In);
        Assert.Equal(BroadcastEarVerdict.Pending, rig.Results[0].Verdict);
        Assert.Equal(BroadcastEarVerdict.Stable, rig.Results[1].Verdict);
        await rig.Coordinator.DisposeAsync();
    }

    [Fact]
    public async Task Output_switch_receives_stable_duplicate_sightings_without_media_sessions()
    {
        var clock = new FakeTimeProvider(T0);
        var source = new AppleDeviceHub.Core.Tests.Ble.FakeAdvertisementSource();
        await using var transport = new BroadcastObservationTransport(source, clock);
        transport.SetPairedDevice(AppleDeviceHub.Core.Tests.Ble.Payload.ProTwoUsbC, "paired-device");
        transport.SetWindowsBattery(80);
        await transport.StartAsync();
        await transport.SendAsync(new ConnectCommand(Guid.NewGuid()));

        var coordinator = new EarMediaCoordinator(new EarMediaPolicy { Enabled = false }, media: null, () => clock.GetUtcNow())
        {
            BroadcastIdentity = () => transport.IdentityConfidence,
            WindowsConnected = () => true,
            BroadcastEarDetection = true,
        };
        var results = new List<BroadcastEarResult>();
        coordinator.BroadcastEarEvaluated += (_, result) => results.Add(result);
        coordinator.Attach(transport);
        int stateEvents = 0;
        transport.StateChanged += (_, _) => stateEvents++;
        var packet = AppleDeviceHub.Core.Tests.Ble.Payload.Sighting(
            AppleDeviceHub.Core.Tests.Ble.Payload.Build(model: AppleDeviceHub.Core.Tests.Ble.Payload.ProTwoUsbC));

        source.Emit(packet);
        int afterFirstSighting = stateEvents;
        clock.Advance(TimeSpan.FromMilliseconds(1200));
        source.Emit(packet);

        Assert.Equal(afterFirstSighting, stateEvents); // duplicate UI state remains suppressed
        Assert.Equal([BroadcastEarVerdict.Pending, BroadcastEarVerdict.Stable], results.Select(result => result.Verdict));
        Assert.Equal(EarMediaActionKind.None, coordinator.LastAction.Kind);
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task Hardware_path_is_unchanged_and_ignores_the_broadcast_setting()
    {
        var media = new FakeMedia();
        var c = new EarMediaCoordinator(new EarMediaPolicy { Enabled = true }, media, () => T0) { };
        var transport = new HardwareTransport();
        c.Attach(transport);
        transport.Ears(In, In);
        transport.Ears(Out, Out);
        await c.Pending;
        Assert.Equal(new[] { "pause player#1" }, media.Calls);
        await c.DisposeAsync();
    }

    private sealed class HardwareTransport : IAirPodsTransport
    {
        private long _seq;
        public TransportInfo Info { get; } = new("Ears", DataOrigin.Hardware, HubContract.Version, "test");
        public DeviceState? Current { get; private set; }
        public event EventHandler<DeviceState>? StateChanged;
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<CommandResult> SendAsync(DeviceCommand command, CancellationToken cancellationToken = default)
            => Task.FromResult(new CommandResult(command.CommandId, CommandOutcome.Unsupported, DataOrigin.Hardware));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void Ears(PodPlacement a, PodPlacement b)
        {
            var on = Observation<bool>.At(true, T0);
            Current = new DeviceState("dev", "AirPods", "AirPods Pro", DataOrigin.Hardware, ++_seq, T0,
                DeviceCapabilities.EarDetection, LinkState.Unknown with { ProtocolSession = on },
                BatteryState.Unknown, ListeningState.Unknown, default, default)
            {
                Ears = new EarState(Observation<PodPlacement>.At(a, T0), Observation<PodPlacement>.At(b, T0)),
            };
            StateChanged?.Invoke(this, Current);
        }
    }
}
