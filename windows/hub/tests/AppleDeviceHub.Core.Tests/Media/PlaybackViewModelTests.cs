using AppleDeviceHub.Core.Media;
using AppleDeviceHub.Core.Presentation;

namespace AppleDeviceHub.Core.Tests.Media;

public sealed class PlaybackViewModelTests
{
    [Fact]
    public async Task QueuedStaleSnapshot_UsesLatestControllerStateWhenApplied()
    {
        var initial = Snapshot("player-a", "Old title");
        var current = Snapshot("player-b", "New title");
        var controller = new FakePlaybackController(initial);
        var dispatchQueue = new Queue<Action>();
        await using var viewModel = new PlaybackViewModel(controller, dispatchQueue.Enqueue);

        // Drain the constructor's initial UI callback before setting up the stale event.
        Assert.Single(dispatchQueue);
        dispatchQueue.Dequeue()();
        controller.SetCurrentWithoutEvent(current);
        controller.RaiseChanged(initial); // late delivery from the player that Windows already left

        Assert.Single(dispatchQueue);
        dispatchQueue.Dequeue()();

        Assert.Equal(current, viewModel.Current);
        Assert.Equal("player-b", viewModel.PlayerName);
        Assert.Equal("New title", viewModel.Title);
    }

    private static PlaybackSnapshot Snapshot(string sessionId, string title)
        => new(sessionId, sessionId, title, MediaPlayback.Playing,
            true, true, true, true, $"Controlling {sessionId}.");

    private sealed class FakePlaybackController(PlaybackSnapshot initial) : IPlaybackController
    {
        public PlaybackSnapshot Current { get; private set; } = initial;
        public bool IsDisposed { get; private set; }

        public event EventHandler<PlaybackSnapshot>? Changed;

        public Task<PlaybackCommandResult> PlayAsync(CancellationToken cancellationToken = default)
            => Result(PlaybackAction.Play);

        public Task<PlaybackCommandResult> PauseAsync(CancellationToken cancellationToken = default)
            => Result(PlaybackAction.Pause);

        public Task<PlaybackCommandResult> NextAsync(CancellationToken cancellationToken = default)
            => Result(PlaybackAction.Next);

        public Task<PlaybackCommandResult> PreviousAsync(CancellationToken cancellationToken = default)
            => Result(PlaybackAction.Previous);

        public void SetCurrentWithoutEvent(PlaybackSnapshot snapshot) => Current = snapshot;

        public void RaiseChanged(PlaybackSnapshot snapshot) => Changed?.Invoke(this, snapshot);

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }

        private Task<PlaybackCommandResult> Result(PlaybackAction action)
            => Task.FromResult(Current.Supports(action)
                ? new PlaybackCommandResult(PlaybackCommandStatus.Sent, $"{action} sent")
                : new PlaybackCommandResult(PlaybackCommandStatus.Unsupported, $"{action} unsupported"));
    }

    [Fact]
    public async Task A_routine_status_is_marked_and_a_command_result_is_not()
    {
        var routine = new PlaybackSnapshot("s", "Spotify", "Song", MediaPlayback.Playing, true, true, true, true,
            "Controlling Spotify through Windows media controls.", StatusIsRoutine: true);
        await using var viewModel = new PlaybackViewModel(new FakePlaybackController(routine), action => action());

        Assert.True(viewModel.StatusIsRoutine);

        await viewModel.NextAsync();

        Assert.False(viewModel.StatusIsRoutine); // whatever the command reported is shown
    }

    [Fact]
    public async Task Problem_and_no_player_statuses_are_never_routine()
    {
        await using var noPlayer = new PlaybackViewModel(new FakePlaybackController(PlaybackSnapshot.NoPlayer), action => action());
        await using var unavailable = new PlaybackViewModel(null, action => action());
        var failed = new PlaybackSnapshot("s", "Spotify", null, MediaPlayback.Unknown, false, false, false, false,
            "Windows could not read playback controls for Spotify.");
        await using var failedRead = new PlaybackViewModel(new FakePlaybackController(failed), action => action());

        Assert.False(noPlayer.StatusIsRoutine);
        Assert.False(unavailable.StatusIsRoutine);
        Assert.False(failedRead.StatusIsRoutine);
    }
}
