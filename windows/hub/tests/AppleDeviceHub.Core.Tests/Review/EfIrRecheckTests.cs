using AppleDeviceHub.Core.Contracts;
using AppleDeviceHub.Core.Media;

namespace AppleDeviceHub.Core.Tests.Review;

public class EfIrRecheckTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private sealed class Media : IMediaSessionController
    {
        public MediaSnapshot Current { get; private set; } = new("player#1", MediaPlayback.Playing);
        public bool CallActive => false;
        public event EventHandler<MediaSnapshot>? Changed;
        public bool PauseRaisesPaused { get; set; }
        public TaskCompletionSource PauseEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReleasePause { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int PauseCount, PlayCount;
        public void Raise(MediaSnapshot s) { Current = s; Changed?.Invoke(this, s); }
        public Task<bool> TryPauseAsync(string id, CancellationToken ct)
        {
            Interlocked.Increment(ref PauseCount);
            if (PauseRaisesPaused) Raise(Current with { Playback = MediaPlayback.Paused });
            PauseEntered.TrySetResult();
            return ReleasePause.Task;
        }
        public Task<bool> TryPlayAsync(string id, CancellationToken ct)
        {
            Interlocked.Increment(ref PlayCount);
            Raise(Current with { Playback = MediaPlayback.Playing });
            return Task.FromResult(true);
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
        public void Emit(PodPlacement a, PodPlacement b)
        {
            Current = new DeviceState("dev", "AirPods", "AirPods Pro", DataOrigin.Hardware, ++_seq, T0,
                DeviceCapabilities.EarDetection, LinkState.Unknown with { ProtocolSession = Observation<bool>.At(true, T0) },
                BatteryState.Unknown, ListeningState.Unknown, default, default)
            { Ears = new EarState(Observation<PodPlacement>.At(a, T0), Observation<PodPlacement>.At(b, T0)) };
            StateChanged?.Invoke(this, Current);
        }
    }

    // A: Clasp's pause FAILS, but its completion lands after a same-count placement change
    // (OutOfEar -> InCase). The completion is ignored (epoch moved), so the Sent claim survives.
    // The user then pauses by hand within ConfirmWindow: it becomes Clasp's claim and is resumed.
    [Fact]
    public async Task A_failed_pause_completing_after_a_placement_change_lets_a_user_pause_be_resumed()
    {
        var media = new Media { PauseRaisesPaused = false };
        var ears = new Ears();
        await using var c = new EarMediaCoordinator(new EarMediaPolicy { Enabled = true }, media, () => T0);
        c.Attach(ears);
        ears.Emit(PodPlacement.InEar, PodPlacement.InEar);
        ears.Emit(PodPlacement.OutOfEar, PodPlacement.OutOfEar);  // pause decided, sent
        await media.PauseEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        ears.Emit(PodPlacement.InCase, PodPlacement.InCase);      // same count, epoch++
        media.ReleasePause.SetResult(false);                       // Windows refused the pause
        await c.Pending.WaitAsync(TimeSpan.FromSeconds(5));
        media.Raise(media.Current with { Playback = MediaPlayback.Paused }); // USER pauses by hand
        ears.Emit(PodPlacement.InEar, PodPlacement.InEar);
        await c.Pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, media.PlayCount); // expected: Clasp did not pause anything
    }

    // B (AnyRemoved): pause 1 in flight, second bud removed before Clasp's Paused report arrives.
    // Expect a conservative outcome: no resume (lost), never a wrong resume.
    [Fact]
    public async Task B_second_removal_while_pause_in_flight_is_conservative()
    {
        var media = new Media { PauseRaisesPaused = false };
        var ears = new Ears();
        await using var c = new EarMediaCoordinator(new EarMediaPolicy { Enabled = true, Trigger = EarPauseTrigger.AnyRemoved }, media, () => T0);
        c.Attach(ears);
        ears.Emit(PodPlacement.InEar, PodPlacement.InEar);
        ears.Emit(PodPlacement.OutOfEar, PodPlacement.InEar);   // pause 1 sent
        await media.PauseEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        ears.Emit(PodPlacement.OutOfEar, PodPlacement.OutOfEar); // new claim (Sent=false), pause 2 queued
        Assert.Equal(EarMediaActionKind.Pause, c.LastAction.Kind);
        media.Raise(media.Current with { Playback = MediaPlayback.Paused }); // Clasp's pause 1 takes effect
        media.ReleasePause.SetResult(true);
        await c.Pending.WaitAsync(TimeSpan.FromSeconds(5));
        ears.Emit(PodPlacement.InEar, PodPlacement.InEar);
        await c.Pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, media.PauseCount);
        Assert.Equal(0, media.PlayCount); // conservative: Clasp's own pause is not resumed
    }
}
