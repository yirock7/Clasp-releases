using AppleDeviceHub.Core.Contracts;
using AppleDeviceHub.Core.Media;

namespace AppleDeviceHub.Core.Tests.Media;

/// <summary>
/// EF-IR-02: queued media actions and their completions are bound to the decision epoch. A
/// Resume queued behind a slow Pause must be dropped by anything that invalidates decisions.
/// </summary>
public class EarMediaInvalidationTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Reports Paused as soon as a pause is requested, but completes the request only when released.</summary>
    private sealed class SlowPauseMedia : IMediaSessionController
    {
        public MediaSnapshot Current { get; private set; } = new("player#1", MediaPlayback.Playing);
        public bool CallActive => false;
        public event EventHandler<MediaSnapshot>? Changed;
        public TaskCompletionSource PauseEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReleasePause { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int PauseCount;
        public int PlayCount;

        public void Raise(MediaSnapshot s) { Current = s; Changed?.Invoke(this, s); }

        public Task<bool> TryPauseAsync(string sessionId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref PauseCount);
            Raise(Current with { Playback = MediaPlayback.Paused });
            PauseEntered.TrySetResult();
            return ReleasePause.Task;
        }

        public Task<bool> TryPlayAsync(string sessionId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref PlayCount);
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

        public void Ears(PodPlacement p)
        {
            Current = new DeviceState("dev", "AirPods", "AirPods Pro", DataOrigin.Hardware, ++_seq, T0,
                DeviceCapabilities.EarDetection, LinkState.Unknown with { ProtocolSession = Observation<bool>.At(true, T0) },
                BatteryState.Unknown, ListeningState.Unknown, default, default)
            {
                Ears = new EarState(Observation<PodPlacement>.At(p, T0), Observation<PodPlacement>.At(p, T0)),
            };
            StateChanged?.Invoke(this, Current);
        }
    }

    /// <summary>Holds queued coordinator work so settings changes can be applied before dispatch.</summary>
    private sealed class QueuedTaskScheduler : TaskScheduler
    {
        private readonly Queue<Task> _tasks = new();

        public int Count { get { lock (_tasks) return _tasks.Count; } }

        protected override IEnumerable<Task>? GetScheduledTasks()
        {
            lock (_tasks) return _tasks.ToArray();
        }

        protected override void QueueTask(Task task)
        {
            lock (_tasks) _tasks.Enqueue(task);
        }

        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;

        public void RunAll()
        {
            while (true)
            {
                Task? task;
                lock (_tasks) task = _tasks.Count == 0 ? null : _tasks.Dequeue();
                if (task is null) return;
                TryExecuteTask(task);
            }
        }
    }

    /// <summary>Wear, remove (pause starts, Windows reports Paused), return (Resume queued behind the pause).</summary>
    private static async Task<(EarMediaCoordinator, SlowPauseMedia, EarTransport)> ResumeQueuedBehindPause()
    {
        var media = new SlowPauseMedia();
        var transport = new EarTransport();
        var c = new EarMediaCoordinator(new EarMediaPolicy { Enabled = true }, media, () => T0);
        c.Attach(transport);
        transport.Ears(PodPlacement.InEar);
        transport.Ears(PodPlacement.OutOfEar);
        await media.PauseEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        transport.Ears(PodPlacement.InEar);
        Assert.Equal(EarMediaActionKind.Resume, c.LastAction.Kind);
        return (c, media, transport);
    }

    private static async Task Finish(EarMediaCoordinator c, SlowPauseMedia media)
    {
        media.ReleasePause.TrySetResult(true);
        await c.Pending.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Without_invalidation_the_queued_resume_runs()
    {
        // Control case: the scenario itself does resume, so the tests below prove the invalidation.
        var (c, media, _) = await ResumeQueuedBehindPause();
        await using var _c = c;
        await Finish(c, media);
        Assert.Equal(1, media.PlayCount);
    }

    [Fact]
    public async Task Transport_replacement_drops_the_queued_resume()
    {
        var (c, media, _) = await ResumeQueuedBehindPause();
        await using var _c = c;
        c.Attach(new EarTransport());
        await Finish(c, media);
        Assert.Equal(0, media.PlayCount);
    }

    [Fact]
    public async Task Turning_the_setting_off_and_on_drops_the_queued_resume()
    {
        var (c, media, _) = await ResumeQueuedBehindPause();
        await using var _c = c;
        c.Enabled = false;
        c.Enabled = true;
        await Finish(c, media);
        Assert.Equal(0, media.PlayCount);
    }

    [Fact]
    public async Task Turning_the_setting_off_before_a_queued_pause_dispatch_drops_that_pause()
    {
        var media = new SlowPauseMedia();
        var transport = new EarTransport();
        var scheduler = new QueuedTaskScheduler();
        await using var c = new EarMediaCoordinator(
            new EarMediaPolicy { Enabled = true }, media, () => T0, diagnosticLog: null, commandScheduler: scheduler);
        c.Attach(transport);
        transport.Ears(PodPlacement.InEar);
        transport.Ears(PodPlacement.OutOfEar);

        Assert.Equal(1, scheduler.Count);
        Assert.Equal(0, media.PauseCount);
        c.Enabled = false;
        c.Enabled = true;

        scheduler.RunAll();
        await c.Pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, media.PauseCount);
        Assert.Equal(0, media.PlayCount);
    }

    [Fact]
    public async Task Turning_the_setting_off_during_an_issued_pause_does_not_create_a_later_resume()
    {
        var media = new SlowPauseMedia();
        var transport = new EarTransport();
        await using var c = new EarMediaCoordinator(new EarMediaPolicy { Enabled = true }, media, () => T0);
        c.Attach(transport);
        transport.Ears(PodPlacement.InEar);
        transport.Ears(PodPlacement.OutOfEar);
        await media.PauseEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // This pause was already handed to Windows, so settings cannot undo it. Its late
        // completion must not become a new claim that later resumes playback.
        c.Enabled = false;
        media.ReleasePause.TrySetResult(true);
        await c.Pending.WaitAsync(TimeSpan.FromSeconds(5));
        c.Enabled = true;
        transport.Ears(PodPlacement.InEar);
        await c.Pending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, media.PauseCount);
        Assert.Equal(0, media.PlayCount);
    }

    [Fact]
    public async Task The_user_acting_on_the_session_drops_the_queued_resume()
    {
        var (c, media, _) = await ResumeQueuedBehindPause();
        await using var _c = c;
        media.Raise(media.Current with { Playback = MediaPlayback.Playing });
        media.Raise(media.Current with { Playback = MediaPlayback.Paused }); // play, then pause by hand
        await Finish(c, media);
        Assert.Equal(0, media.PlayCount);
    }

    [Fact]
    public async Task Disposal_drops_the_queued_resume()
    {
        var (c, media, _) = await ResumeQueuedBehindPause();
        var dispose = c.DisposeAsync().AsTask();
        media.ReleasePause.TrySetResult(true);
        await dispose.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, media.PlayCount);
    }

    [Fact]
    public async Task A_stale_pause_completion_cannot_create_a_new_claim()
    {
        // Sleep while the pause request is outstanding; its late success must not be treated as
        // Clasp's pause, so a later return of the buds resumes nothing.
        var media = new SlowPauseMedia();
        var transport = new EarTransport();
        await using var c = new EarMediaCoordinator(new EarMediaPolicy { Enabled = true }, media, () => T0);
        c.Attach(transport);
        transport.Ears(PodPlacement.InEar);
        transport.Ears(PodPlacement.OutOfEar);
        await media.PauseEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        c.Forget();
        await Finish(c, media);
        transport.Ears(PodPlacement.InEar);
        await c.Pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, media.PlayCount);
    }
}
