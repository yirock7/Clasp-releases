using AppleDeviceHub.Core.Ble;
using AppleDeviceHub.Core.Contracts;
using Microsoft.Extensions.Time.Testing;

namespace AppleDeviceHub.Core.Tests.Ble;

/// <summary>
/// A listener restart (watchdog or recovery) that a user Stop, a new Start or Dispose retires must not touch the
/// source afterwards. Uses a source whose calls can be held open, and which models the real one: Stop ends a
/// start in progress.
/// </summary>
public sealed class RestartRaceTests
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero));
    private readonly GatedSource _source = new();

    private BroadcastObservationTransport Create() => new(_source, _clock);

    private static Task<CommandResult> Connect(BroadcastObservationTransport t) => t.SendAsync(new ConnectCommand(Guid.NewGuid()));

    private static Task<CommandResult> Disconnect(BroadcastObservationTransport t) => t.SendAsync(new DisconnectCommand(Guid.NewGuid()));

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 300 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition(), "The condition was not met in time.");
    }

    private static async Task CompletesAsync(Task task)
        => Assert.Same(task, await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5))));

    [Fact]
    public async Task A_restart_held_before_stop_never_starts_the_source_after_the_stop()
    {
        await using var t = Create();
        await t.StartAsync();
        await Connect(t);
        Assert.True(_source.Scanning);

        var restartStop = _source.HoldNextStop();
        _clock.Advance(t.Options.WatchdogAfter); // the watchdog launches a restart
        await WaitUntilAsync(() => restartStop.Entered.Task.IsCompleted);

        var stop = Disconnect(t); // waits for the held restart
        await Task.Delay(50);
        Assert.False(stop.IsCompleted);
        restartStop.Release();
        await CompletesAsync(stop);

        Assert.Equal(1, _source.StartCount); // the restart never started the source
        Assert.False(_source.Scanning);
        Assert.False(t.IsObserving);
    }

    [Fact]
    public async Task Stop_then_a_new_start_while_the_old_restart_is_held_ends_with_the_new_listener_running()
    {
        await using var t = Create();
        await t.StartAsync();
        await Connect(t);

        var restartStop = _source.HoldNextStop();
        _clock.Advance(t.Options.WatchdogAfter);
        await WaitUntilAsync(() => restartStop.Entered.Task.IsCompleted);

        var stop = Disconnect(t);
        await WaitUntilAsync(() => !t.IsObserving);
        var start = Connect(t); // a new observation begins while the old restart is still held
        await Task.Delay(50);
        Assert.False(start.IsCompleted); // it waits for the retired restart before touching the source

        restartStop.Release();
        await CompletesAsync(stop);
        await CompletesAsync(start);

        Assert.True(t.IsObserving);
        Assert.True(_source.Scanning);
        Assert.Equal("Start", _source.Calls[^1]); // the retired restart never stopped the new listener
        Assert.Equal(2, _source.StartCount);
    }

    [Fact]
    public async Task Stop_while_the_restarts_start_is_held_returns_and_leaves_the_source_stopped()
    {
        await using var t = Create();
        await t.StartAsync();
        await Connect(t);

        var restartStart = _source.HoldNextStart();
        _clock.Advance(t.Options.WatchdogAfter);
        await WaitUntilAsync(() => restartStart.Entered.Task.IsCompleted);

        await CompletesAsync(Disconnect(t)); // Stop ends the start in progress and returns

        Assert.False(_source.Scanning);
        Assert.False(t.IsObserving);
    }

    [Fact]
    public async Task Dispose_waits_for_a_held_restart_and_leaves_nothing_scanning()
    {
        var t = Create();
        await t.StartAsync();
        await Connect(t);

        var restartStop = _source.HoldNextStop();
        _clock.Advance(t.Options.WatchdogAfter);
        await WaitUntilAsync(() => restartStop.Entered.Task.IsCompleted);

        var dispose = t.DisposeAsync().AsTask();
        await Task.Delay(50);
        Assert.False(dispose.IsCompleted);
        restartStop.Release();
        await CompletesAsync(dispose);

        Assert.False(_source.Scanning);
        Assert.Equal(1, _source.StartCount);
        Assert.True(_source.Disposed);
    }

    [Fact]
    public async Task Dispose_does_not_wait_on_a_restart_held_in_its_start_and_leaves_nothing_scanning()
    {
        var t = Create();
        await t.StartAsync();
        await Connect(t);

        var restartStart = _source.HoldNextStart();
        _clock.Advance(t.Options.WatchdogAfter);
        await WaitUntilAsync(() => restartStart.Entered.Task.IsCompleted);

        await CompletesAsync(t.DisposeAsync().AsTask()); // the source stop ends the held start; quitting is not stalled

        Assert.False(_source.Scanning);
        Assert.True(_source.Disposed);
    }

    /// <summary>A hold on one source call: it reports when it was entered and waits until released.</summary>
    private sealed class Hold
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Wait()
        {
            Entered.TrySetResult();
            return _release.Task;
        }

        public void Release() => _release.TrySetResult();
    }

    private sealed class GatedSource : IAdvertisementSource
    {
        private readonly Lock _gate = new();
        private readonly List<string> _calls = new();
        private Hold? _stopHold, _startHold;
        private TaskCompletionSource? _startEndedByStop;

        public volatile bool Scanning;
        public bool Disposed { get; private set; }
        public int StartCount { get { lock (_gate) return _calls.Count(c => c == "Start"); } }
        public IReadOnlyList<string> Calls { get { lock (_gate) return _calls.ToArray(); } }
        public AdvertisementSourceStatus Status { get; private set; } = AdvertisementSourceStatus.Stopped;

        public event EventHandler<AdvertisementSighting>? Received { add { } remove { } }
        public event EventHandler<AdvertisementSourceStatus>? StatusChanged { add { } remove { } }

        public Hold HoldNextStop()
        {
            var hold = new Hold();
            lock (_gate) _stopHold = hold;
            return hold;
        }

        public Hold HoldNextStart()
        {
            var hold = new Hold();
            lock (_gate) _startHold = hold;
            return hold;
        }

        public async Task<AdvertisementSourceStatus> StartAsync(CancellationToken cancellationToken = default)
        {
            Hold? hold;
            var endedByStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                _calls.Add("Start");
                hold = _startHold;
                _startHold = null;
                if (hold is not null) _startEndedByStop = endedByStop;
            }
            if (hold is not null)
            {
                // Held until released, or until a Stop ends it (as the real source does).
                await Task.WhenAny(hold.Wait(), endedByStop.Task).ConfigureAwait(false);
                if (endedByStop.Task.IsCompleted) return Status = AdvertisementSourceStatus.Stopped;
            }
            Scanning = true;
            return Status = new AdvertisementSourceStatus(AdvertisementSourceState.Running);
        }

        public async Task StopAsync()
        {
            Hold? hold;
            TaskCompletionSource? inProgress;
            lock (_gate)
            {
                _calls.Add("Stop");
                hold = _stopHold;
                _stopHold = null;
                inProgress = _startEndedByStop;
                _startEndedByStop = null;
            }
            inProgress?.TrySetResult();
            if (hold is not null) await hold.Wait().ConfigureAwait(false);
            Scanning = false;
            Status = AdvertisementSourceStatus.Stopped;
        }

        public void Dispose() => Disposed = true;
    }
}
