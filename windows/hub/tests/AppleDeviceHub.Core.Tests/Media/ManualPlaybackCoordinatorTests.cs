using AppleDeviceHub.Core.Media;

namespace AppleDeviceHub.Core.Tests.Media;

public sealed class ManualPlaybackCoordinatorTests
{
    [Fact]
    public async Task ExecuteAsync_NoPlayerOrUnsupportedAction_DoesNotSend()
    {
        var source = new FakePlaybackSessionSource(PlaybackSnapshot.NoPlayer);
        await using var controller = new ManualPlaybackCoordinator(source);

        var noPlayer = await controller.PlayAsync();
        Assert.Equal(PlaybackCommandStatus.NoPlayer, noPlayer.Status);
        Assert.Equal(0, source.SendCount);

        source.Update(Snapshot("player-a", canPlay: true, canNext: false));
        var unsupported = await controller.NextAsync();
        Assert.Equal(PlaybackCommandStatus.Unsupported, unsupported.Status);
        Assert.Contains("doesn't support Next", unsupported.Message, StringComparison.Ordinal);
        Assert.Equal(0, source.SendCount);
    }

    [Fact]
    public async Task ExecuteAsync_UsesExplicitActionForEachButton()
    {
        var source = new FakePlaybackSessionSource(Snapshot("player-a"));
        await using var controller = new ManualPlaybackCoordinator(source);

        Assert.Equal(PlaybackCommandStatus.Sent, (await controller.PlayAsync()).Status);
        Assert.Equal(PlaybackCommandStatus.Sent, (await controller.PauseAsync()).Status);
        Assert.Equal(PlaybackCommandStatus.Sent, (await controller.NextAsync()).Status);
        Assert.Equal(PlaybackCommandStatus.Sent, (await controller.PreviousAsync()).Status);
        Assert.Equal(
            [PlaybackAction.Play, PlaybackAction.Pause, PlaybackAction.Next, PlaybackAction.Previous],
            source.Actions);
    }

    [Fact]
    public async Task Dispatch_diagnostic_uses_an_opaque_session_reference_without_media_metadata()
    {
        var source = new FakePlaybackSessionSource(Snapshot("player-a", title: "Private track title"));
        var logs = new List<string>();
        await using var controller = new ManualPlaybackCoordinator(source, diagnosticLog: logs.Add);

        Assert.Equal(PlaybackCommandStatus.Sent, (await controller.PauseAsync()).Status);

        var entry = Assert.Single(logs);
        Assert.Contains("origin=manual", entry, StringComparison.Ordinal);
        Assert.Contains("event=command-dispatch", entry, StringComparison.Ordinal);
        Assert.Contains("action=Pause", entry, StringComparison.Ordinal);
        Assert.Contains("sessionRef=", entry, StringComparison.Ordinal);
        Assert.Contains("reason=explicit playback request", entry, StringComparison.Ordinal);
        Assert.Contains("at=", entry, StringComparison.Ordinal);
        Assert.DoesNotContain("player-a", entry, StringComparison.Ordinal);
        Assert.DoesNotContain("Private track title", entry, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Throwing_dispatch_logger_does_not_block_a_manual_command()
    {
        var source = new FakePlaybackSessionSource(Snapshot("player-a"));
        await using var controller = new ManualPlaybackCoordinator(
            source, diagnosticLog: _ => throw new InvalidOperationException("log unavailable"));

        Assert.Equal(PlaybackCommandStatus.Sent, (await controller.PauseAsync()).Status);
        Assert.Equal(1, source.SendCount);
    }

    [Fact]
    public async Task SessionSwitch_CancelsOldRequestAndIgnoresStaleSnapshotEvent()
    {
        var source = new FakePlaybackSessionSource(Snapshot("player-a"));
        var pending = new TaskCompletionSource<PlaybackCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.NextOperation = pending;
        await using var controller = new ManualPlaybackCoordinator(source, commandTimeout: TimeSpan.FromSeconds(2));

        var oldRequest = controller.PlayAsync();
        await source.WaitForSendAsync();
        var current = Snapshot("player-b", title: "New player");
        source.Update(current);
        var cancelled = await oldRequest;

        Assert.Equal(PlaybackCommandStatus.Cancelled, cancelled.Status);
        source.RaiseEvent(Snapshot("player-a", title: "Late old title"));
        Assert.Equal(current, controller.Current);

        pending.SetResult(new PlaybackCommandResult(PlaybackCommandStatus.Sent, "old player accepted"));
        var next = await SendWhenAvailableAsync(() => controller.NextAsync());
        Assert.Equal(PlaybackCommandStatus.Sent, next.Status);
        Assert.Equal([PlaybackAction.Play, PlaybackAction.Next], source.Actions);
        Assert.Equal(1, source.MaximumConcurrentSends);
    }

    [Fact]
    public async Task TimedOutNativeRequest_RemainsBusyUntilItSettles()
    {
        var source = new FakePlaybackSessionSource(Snapshot("player-a"));
        var pending = new TaskCompletionSource<PlaybackCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.NextOperation = pending;
        await using var controller = new ManualPlaybackCoordinator(source, commandTimeout: TimeSpan.FromMilliseconds(25));

        var timeout = await controller.PlayAsync();
        Assert.Equal(PlaybackCommandStatus.TimedOut, timeout.Status);
        Assert.Contains("Clasp will wait before sending another", timeout.Message, StringComparison.Ordinal);
        Assert.Contains("turn Show playback controls off and on in Settings", timeout.Message, StringComparison.Ordinal);

        var duplicate = await controller.NextAsync();
        Assert.Equal(PlaybackCommandStatus.Busy, duplicate.Status);
        Assert.Contains("turn Show playback controls off and on in Settings", duplicate.Message, StringComparison.Ordinal);
        Assert.Equal(1, source.SendCount);

        pending.SetResult(new PlaybackCommandResult(PlaybackCommandStatus.Sent, "accepted"));
        var next = await SendWhenAvailableAsync(() => controller.NextAsync());
        Assert.Equal(PlaybackCommandStatus.Sent, next.Status);
        Assert.Equal([PlaybackAction.Play, PlaybackAction.Next], source.Actions);
        Assert.Equal(1, source.MaximumConcurrentSends);
    }

    [Fact]
    public async Task Dispose_CancelsCommandAndIgnoresLateCompletionAndEvents()
    {
        var source = new FakePlaybackSessionSource(Snapshot("player-a"));
        var pending = new TaskCompletionSource<PlaybackCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.NextOperation = pending;
        var controller = new ManualPlaybackCoordinator(
            source,
            commandTimeout: TimeSpan.FromSeconds(10),
            disposeWait: TimeSpan.FromMilliseconds(10));
        var changedCount = 0;
        controller.Changed += (_, _) => changedCount++;

        var command = controller.PlayAsync();
        await source.WaitForSendAsync();
        await controller.DisposeAsync();
        var result = await command;

        Assert.Equal(PlaybackCommandStatus.Cancelled, result.Status);
        Assert.True(source.IsDisposed);
        source.Update(Snapshot("player-b"));
        pending.SetResult(new PlaybackCommandResult(PlaybackCommandStatus.Sent, "late result"));
        await Task.Delay(25);
        Assert.Equal(0, changedCount);
    }

    [Fact]
    public async Task RefusedCommand_IsReturnedAsStatusAndDoesNotFaultNextCommand()
    {
        var source = new FakePlaybackSessionSource(Snapshot("player-a"))
        {
            NextResult = new PlaybackCommandResult(PlaybackCommandStatus.Refused, "Windows refused Pause.")
        };
        await using var controller = new ManualPlaybackCoordinator(source);

        var refused = await controller.PauseAsync();
        Assert.Equal(PlaybackCommandStatus.Refused, refused.Status);

        source.NextResult = new PlaybackCommandResult(PlaybackCommandStatus.Sent, "sent");
        var sent = await controller.NextAsync();
        Assert.Equal(PlaybackCommandStatus.Sent, sent.Status);
        Assert.Equal(2, source.SendCount);
    }

    private static PlaybackSnapshot Snapshot(
        string sessionId,
        bool canPlay = true,
        bool canPause = true,
        bool canNext = true,
        bool canPrevious = true,
        string title = "Current title")
        => new(sessionId, $"Player {sessionId}", title, MediaPlayback.Paused,
            canPlay, canPause, canNext, canPrevious, "Player ready.");

    private static async Task<PlaybackCommandResult> SendWhenAvailableAsync(Func<Task<PlaybackCommandResult>> send)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var result = await send();
            if (result.Status != PlaybackCommandStatus.Busy) return result;
            await Task.Delay(5);
        }
        throw new TimeoutException("The preceding native playback request did not settle.");
    }

    private sealed class FakePlaybackSessionSource(PlaybackSnapshot initial) : IPlaybackSessionSource
    {
        private readonly object _gate = new();
        private PlaybackSnapshot _current = initial;
        private TaskCompletionSource _sendStarted = NewSignal();
        private int _activeSends;

        public PlaybackSnapshot Current { get { lock (_gate) return _current; } }

        public event EventHandler<PlaybackSnapshot>? Changed;

        public int SendCount { get; private set; }
        public int MaximumConcurrentSends { get; private set; }
        public bool IsDisposed { get; private set; }
        public PlaybackCommandResult NextResult { get; set; } = new(PlaybackCommandStatus.Sent, "sent");
        public TaskCompletionSource<PlaybackCommandResult>? NextOperation { get; set; }
        public List<PlaybackAction> Actions { get; } = [];

        public Task WaitForSendAsync() => _sendStarted.Task;

        public Task<PlaybackCommandResult> ExecuteAsync(
            PlaybackAction action,
            string sessionId,
            CancellationToken cancellationToken = default)
        {
            TaskCompletionSource<PlaybackCommandResult>? pending;
            PlaybackCommandResult result;
            lock (_gate)
            {
                if (IsDisposed)
                    return Task.FromResult(new PlaybackCommandResult(PlaybackCommandStatus.Cancelled, "disposed"));
                if (_current.SessionId != sessionId)
                    return Task.FromResult(new PlaybackCommandResult(PlaybackCommandStatus.Cancelled, "changed"));
                SendCount++;
                Actions.Add(action);
                _activeSends++;
                MaximumConcurrentSends = Math.Max(MaximumConcurrentSends, _activeSends);
                pending = NextOperation;
                NextOperation = null;
                result = NextResult;
                _sendStarted.TrySetResult();
                if (pending is null)
                {
                    _activeSends--;
                    return Task.FromResult(result);
                }
            }

            return CompleteAsync(pending.Task);
        }

        private async Task<PlaybackCommandResult> CompleteAsync(Task<PlaybackCommandResult> pending)
        {
            try { return await pending.ConfigureAwait(false); }
            finally { lock (_gate) _activeSends--; }
        }

        public void Update(PlaybackSnapshot snapshot)
        {
            lock (_gate) _current = snapshot;
            Changed?.Invoke(this, snapshot);
        }

        public void RaiseEvent(PlaybackSnapshot staleSnapshot)
            => Changed?.Invoke(this, staleSnapshot);

        public ValueTask DisposeAsync()
        {
            lock (_gate) IsDisposed = true;
            return ValueTask.CompletedTask;
        }

        private static TaskCompletionSource NewSignal()
            => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
