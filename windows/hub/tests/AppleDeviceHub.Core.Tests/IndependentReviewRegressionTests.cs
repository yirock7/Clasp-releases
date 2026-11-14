using AppleDeviceHub.Core.Ble;
using AppleDeviceHub.Core.Contracts;
using AppleDeviceHub.Core.Media;
using AppleDeviceHub.Core.Tests.Ble;

namespace AppleDeviceHub.Core.Tests;

public class IndependentReviewRegressionTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-10-15T12:00:00Z");

    [Fact]
    public async Task Old_failed_start_must_not_stop_a_new_observation()
    {
        var first = new TaskCompletionSource<AdvertisementSourceStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new FakeAdvertisementSource();
        int starts = 0;
        source.OnStart = _ => ++starts == 1 ? first.Task : Task.FromResult(new AdvertisementSourceStatus(AdvertisementSourceState.Running));
        await using var transport = new BroadcastObservationTransport(source);
        var oldStart = transport.SendAsync(new ConnectCommand(Guid.NewGuid()));
        await transport.SendAsync(new DisconnectCommand(Guid.NewGuid()));
        await transport.SendAsync(new ConnectCommand(Guid.NewGuid()));
        int stopsBeforeOldFailure = source.StopCount;
        first.SetResult(new AdvertisementSourceStatus(AdvertisementSourceState.Unavailable, "old attempt failed"));
        await oldStart;
        Assert.Equal(stopsBeforeOldFailure, source.StopCount);
    }

    [Fact]
    public async Task Forget_must_invalidate_an_already_queued_resume()
    {
        var media = new DelayedPauseMedia();
        var transport = new EarTransport();
        await using var coordinator = new EarMediaCoordinator(new EarMediaPolicy { Enabled = true }, media, () => At);
        coordinator.Attach(transport);
        transport.Emit(PodPlacement.InEar);
        transport.Emit(PodPlacement.OutOfEar);
        await media.PauseEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        transport.Emit(PodPlacement.InEar); // resume is queued behind the outstanding pause
        coordinator.Forget(); // e.g. sleep, disconnect or engine replacement
        media.ReleasePause.SetResult(true);
        await coordinator.Pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, media.PlayCount);
    }

    [Fact]
    public void Stale_ear_transition_must_not_pause_media()
    {
        var policy = new EarMediaPolicy { Enabled = true };
        var media = new MediaSnapshot("player#1", MediaPlayback.Playing);
        var now = At.AddHours(1);
        policy.OnDeviceState(EarTransport.State(PodPlacement.InEar, 1), media, false, now);
        var action = policy.OnDeviceState(EarTransport.State(PodPlacement.OutOfEar, 2), media, false, now);
        Assert.Equal(EarMediaActionKind.None, action.Kind);
    }

    private sealed class DelayedPauseMedia : IMediaSessionController
    {
        public MediaSnapshot Current { get; private set; } = new("player#1", MediaPlayback.Playing);
        public bool CallActive => false;
        public event EventHandler<MediaSnapshot>? Changed;
        public TaskCompletionSource<bool> PauseEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReleasePause { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int PlayCount { get; private set; }
        public Task<bool> TryPauseAsync(string id, CancellationToken token)
        {
            Current = Current with { Playback = MediaPlayback.Paused };
            Changed?.Invoke(this, Current);
            PauseEntered.TrySetResult(true);
            return ReleasePause.Task;
        }
        public Task<bool> TryPlayAsync(string id, CancellationToken token) { PlayCount++; return Task.FromResult(true); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class EarTransport : IAirPodsTransport
    {
        private long _sequence;
        public TransportInfo Info { get; } = new("ears", DataOrigin.Hardware, HubContract.Version, "offline review");
        public DeviceState? Current { get; private set; }
        public event EventHandler<DeviceState>? StateChanged;
        public Task StartAsync(CancellationToken token = default) => Task.CompletedTask;
        public Task<CommandResult> SendAsync(DeviceCommand command, CancellationToken token = default)
            => Task.FromResult(new CommandResult(command.CommandId, CommandOutcome.Unsupported, DataOrigin.Hardware));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void Emit(PodPlacement placement) { Current = State(placement, ++_sequence); StateChanged?.Invoke(this, Current); }
        public static DeviceState State(PodPlacement placement, long sequence) =>
            new("dev", "AirPods", "test", DataOrigin.Hardware, sequence, At,
                DeviceCapabilities.EarDetection, LinkState.Unknown with { ProtocolSession = Observation<bool>.At(true, At) },
                BatteryState.Unknown, ListeningState.Unknown, default, default)
            { Ears = new EarState(Observation<PodPlacement>.At(placement, At), Observation<PodPlacement>.At(placement, At)) };
    }
}
