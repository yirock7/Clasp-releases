using AppleDeviceHub.Core.Ble;
using AppleDeviceHub.Core.Contracts;
using AppleDeviceHub.Core.Media;
using AppleDeviceHub.Core.Tests.Ble;
using Microsoft.Extensions.Time.Testing;

namespace AppleDeviceHub.Core.Tests.Review;

public class EfIrReviewVariantTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private sealed class Media : IMediaSessionController
    {
        public MediaSnapshot Current { get; private set; } = new("player#1", MediaPlayback.Playing);
        public bool CallActive => false;
        public event EventHandler<MediaSnapshot>? Changed;
        public TaskCompletionSource PauseEntered { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReleasePause { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource PlayEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool>? ReleasePlay { get; set; }
        public int PauseCount, PlayCount;
        public void Raise(MediaSnapshot s) { Current = s; Changed?.Invoke(this, s); }
        public Task<bool> TryPauseAsync(string id, CancellationToken ct)
        {
            Interlocked.Increment(ref PauseCount);
            Raise(Current with { Playback = MediaPlayback.Paused });
            PauseEntered.TrySetResult();
            return ReleasePause.Task;
        }
        public Task<bool> TryPlayAsync(string id, CancellationToken ct)
        {
            Interlocked.Increment(ref PlayCount);
            Raise(Current with { Playback = MediaPlayback.Playing });
            PlayEntered.TrySetResult();
            return ReleasePlay?.Task ?? Task.FromResult(true);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Ears : IAirPodsTransport
    {
        private long _seq;
        public TransportInfo Info { get; } = new("Ears", DataOrigin.Hardware, HubContract.Version, "test");
        public DeviceState? Current { get; private set; }
        public event EventHandler<DeviceState>? StateChanged;
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<CommandResult> SendAsync(DeviceCommand command, CancellationToken cancellationToken = default)
            => Task.FromResult(new CommandResult(command.CommandId, CommandOutcome.Unsupported, DataOrigin.Hardware));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void Emit(PodPlacement p)
        {
            Current = new DeviceState("dev", "AirPods", "AirPods Pro", DataOrigin.Hardware, ++_seq, T0,
                DeviceCapabilities.EarDetection, LinkState.Unknown with { ProtocolSession = Observation<bool>.At(true, T0) },
                BatteryState.Unknown, ListeningState.Unknown, default, default)
            { Ears = new EarState(Observation<PodPlacement>.At(p, T0), Observation<PodPlacement>.At(p, T0)) };
            StateChanged?.Invoke(this, Current);
        }
    }

    // EF-IR-02 variant: a later ear removal does not invalidate a Resume still queued behind the pause.
    [Fact]
    public async Task Removing_again_must_drop_a_queued_resume()
    {
        var media = new Media();
        var ears = new Ears();
        await using var c = new EarMediaCoordinator(new EarMediaPolicy { Enabled = true }, media, () => T0);
        c.Attach(ears);
        ears.Emit(PodPlacement.InEar);
        ears.Emit(PodPlacement.OutOfEar);
        await media.PauseEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        ears.Emit(PodPlacement.InEar);      // Resume queued behind the outstanding pause
        Assert.Equal(EarMediaActionKind.Resume, c.LastAction.Kind);
        ears.Emit(PodPlacement.OutOfEar);   // out again before the queued Resume runs
        media.ReleasePause.SetResult(true);
        await c.Pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, media.PlayCount);   // buds are out: nothing should start playing
    }

    // Variant: the user pauses by hand before the queued Pause dispatches; the claim is confirmed
    // by the user pause and later the coordinator resumes a session it never paused.
    [Fact]
    public async Task A_user_pause_before_a_queued_pause_dispatches_must_not_become_the_claim()
    {
        var media = new Media { ReleasePlay = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var ears = new Ears();
        await using var c = new EarMediaCoordinator(new EarMediaPolicy { Enabled = true }, media, () => T0);
        c.Attach(ears);
        ears.Emit(PodPlacement.InEar);
        ears.Emit(PodPlacement.OutOfEar);           // pause 1
        await media.PauseEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        media.ReleasePause.SetResult(true);
        await c.Pending.WaitAsync(TimeSpan.FromSeconds(5));
        ears.Emit(PodPlacement.InEar);              // resume 1, Play is slow
        await media.PlayEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        ears.Emit(PodPlacement.OutOfEar);           // pause 2 queued behind the slow Play
        Assert.Equal(EarMediaActionKind.Pause, c.LastAction.Kind);
        media.Raise(media.Current with { Playback = MediaPlayback.Paused }); // the USER pauses by hand
        media.ReleasePlay!.SetResult(true);
        await c.Pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, media.PauseCount);          // pause 2 was never sent
        ears.Emit(PodPlacement.InEar);
        await c.Pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, media.PlayCount);           // must not resume the user pause
    }

    // EF-IR-01 at the transport/contract level: A waits, Stop, B runs, Stop B, then A reports Running late.
    [Fact]
    public async Task A_retired_start_succeeding_after_the_replacement_stopped_must_not_leave_the_source_running()
    {
        var clock = new FakeTimeProvider(T0);
        var source = new FakeAdvertisementSource();
        var first = new TaskCompletionSource<AdvertisementSourceStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        source.OnStart = _ => Interlocked.Increment(ref calls) == 1 ? first.Task : Task.FromResult(new AdvertisementSourceStatus(AdvertisementSourceState.Running));
        await using var t = new BroadcastObservationTransport(source, clock);
        await t.StartAsync();
        var a = t.SendAsync(new ConnectCommand(Guid.NewGuid()));
        await t.SendAsync(new DisconnectCommand(Guid.NewGuid()));
        await t.SendAsync(new ConnectCommand(Guid.NewGuid()));
        await t.SendAsync(new DisconnectCommand(Guid.NewGuid()));
        first.SetResult(new AdvertisementSourceStatus(AdvertisementSourceState.Running));
        await a;
        Assert.False(t.IsObserving);
        Assert.Equal(AdvertisementSourceState.Stopped, source.Status.State);
    }

    // EF-IR-01 review F3 (a retired start's failure STATUS EVENT ends the replacement) is fixed
    // in the concrete source, not here: WinRtAdvertisementSource now publishes start-time status
    // only while that start is current (SetStatusIfCurrent), and IAdvertisementSource documents
    // that retired starts must not raise StatusChanged. The reviewer's transport-level version of
    // this test raised such an event from the fake source; the transport cannot distinguish it
    // from a genuine radio loss of the current observation (which it must keep honouring), so
    // that test is not adopted. The WinRT path needs a radio and is verified by review only.
}
