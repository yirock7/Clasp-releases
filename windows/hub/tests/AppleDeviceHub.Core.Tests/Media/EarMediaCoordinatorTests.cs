using AppleDeviceHub.Core.Contracts;
using AppleDeviceHub.Core.Media;

namespace AppleDeviceHub.Core.Tests.Media;

public class EarMediaCoordinatorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeMedia : IMediaSessionController
    {
        public MediaSnapshot Current { get; set; } = new("player#1", MediaPlayback.Playing);
        public bool CallActive { get; set; }
        public bool PauseSucceeds { get; set; } = true;
        public List<string> Calls { get; } = new();
        public event EventHandler<MediaSnapshot>? Changed;

        public void Raise(MediaSnapshot s) { Current = s; Changed?.Invoke(this, s); }

        public Task<bool> TryPauseAsync(string sessionId, CancellationToken cancellationToken)
        {
            Calls.Add("pause " + sessionId);
            if (PauseSucceeds) Raise(Current with { Playback = MediaPlayback.Paused });
            return Task.FromResult(PauseSucceeds);
        }

        public Task<bool> TryPlayAsync(string sessionId, CancellationToken cancellationToken)
        {
            Calls.Add("play " + sessionId);
            Raise(Current with { Playback = MediaPlayback.Playing });
            return Task.FromResult(true);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class EarTransport : IAirPodsTransport
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

    private static (EarMediaCoordinator, FakeMedia, EarTransport) Make()
    {
        var media = new FakeMedia();
        var transport = new EarTransport();
        var c = new EarMediaCoordinator(new EarMediaPolicy { Enabled = true }, media, () => T0);
        c.Attach(transport);
        return (c, media, transport);
    }

    [Fact]
    public async Task Out_then_in_pauses_and_resumes_the_same_session()
    {
        var (c, media, t) = Make();
        t.Ears(PodPlacement.InEar, PodPlacement.InEar);
        t.Ears(PodPlacement.OutOfEar, PodPlacement.OutOfEar);
        await c.Pending;
        t.Ears(PodPlacement.InEar, PodPlacement.InEar);
        await c.Pending;
        Assert.Equal(new[] { "pause player#1", "play player#1" }, media.Calls);
        await c.DisposeAsync();
    }

    [Fact]
    public async Task Failed_pause_never_resumes()
    {
        var (c, media, t) = Make();
        media.PauseSucceeds = false;
        t.Ears(PodPlacement.InEar, PodPlacement.InEar);
        t.Ears(PodPlacement.OutOfEar, PodPlacement.OutOfEar);
        await c.Pending;
        media.Raise(media.Current with { Playback = MediaPlayback.Paused }); // user paused it
        t.Ears(PodPlacement.InEar, PodPlacement.InEar);
        await c.Pending;
        Assert.Equal(new[] { "pause player#1" }, media.Calls);
        await c.DisposeAsync();
    }

    [Fact]
    public async Task Detached_or_disposed_coordinator_issues_nothing()
    {
        var (c, media, t) = Make();
        t.Ears(PodPlacement.InEar, PodPlacement.InEar);
        c.Attach(null);
        t.Ears(PodPlacement.OutOfEar, PodPlacement.OutOfEar);
        await c.Pending;

        c.Attach(t);
        t.Ears(PodPlacement.InEar, PodPlacement.InEar);
        await c.DisposeAsync();
        t.Ears(PodPlacement.OutOfEar, PodPlacement.OutOfEar);
        Assert.Empty(media.Calls);
        await c.DisposeAsync(); // idempotent
    }

    [Fact]
    public async Task Forget_on_sleep_prevents_resume()
    {
        var (c, media, t) = Make();
        t.Ears(PodPlacement.InEar, PodPlacement.InEar);
        t.Ears(PodPlacement.OutOfEar, PodPlacement.OutOfEar);
        await c.Pending;
        c.Forget();
        t.Ears(PodPlacement.InEar, PodPlacement.InEar);
        await c.Pending;
        Assert.Equal(new[] { "pause player#1" }, media.Calls);
        await c.DisposeAsync();
    }

    [Fact]
    public async Task A_throwing_diagnostic_sink_does_not_block_the_ear_pause()
    {
        var media = new FakeMedia();
        var transport = new EarTransport();
        var c = new EarMediaCoordinator(
            new EarMediaPolicy { Enabled = true }, media, () => T0,
            diagnosticLog: _ => throw new InvalidOperationException("log unavailable"));
        c.Attach(transport);

        transport.Ears(PodPlacement.InEar, PodPlacement.InEar);
        transport.Ears(PodPlacement.OutOfEar, PodPlacement.OutOfEar);
        await c.Pending;

        Assert.Equal(new[] { "pause player#1" }, media.Calls);
        await c.DisposeAsync();
    }

    [Fact]
    public async Task Automatic_dispatch_diagnostic_names_action_and_reason_without_raw_session_identity()
    {
        var media = new FakeMedia();
        var transport = new EarTransport();
        var logs = new List<string>();
        await using var c = new EarMediaCoordinator(
            new EarMediaPolicy { Enabled = true }, media, () => T0, diagnosticLog: logs.Add);
        c.Attach(transport);

        transport.Ears(PodPlacement.InEar, PodPlacement.InEar);
        transport.Ears(PodPlacement.OutOfEar, PodPlacement.OutOfEar);
        await c.Pending;

        var entry = Assert.Single(logs);
        Assert.Contains("origin=automatic", entry, StringComparison.Ordinal);
        Assert.Contains("event=command-dispatch", entry, StringComparison.Ordinal);
        Assert.Contains("action=Pause", entry, StringComparison.Ordinal);
        Assert.Contains("reason=removed from ear", entry, StringComparison.Ordinal);
        Assert.Contains("sessionRef=", entry, StringComparison.Ordinal);
        Assert.Contains("at=", entry, StringComparison.Ordinal);
        Assert.DoesNotContain("player#1", entry, StringComparison.Ordinal);
        Assert.Equal(new[] { "pause player#1" }, media.Calls);
    }
}
