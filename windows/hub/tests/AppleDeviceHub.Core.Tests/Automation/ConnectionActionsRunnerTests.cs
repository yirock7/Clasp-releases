using AppleDeviceHub.App.Automation;
using AppleDeviceHub.Core.Audio;
using AppleDeviceHub.Core.Automation;
using AppleDeviceHub.Core.Media;

namespace AppleDeviceHub.Core.Tests.Automation;

public sealed class ConnectionActionsRunnerTests
{
    private sealed class FakePlayback : IPlaybackController
    {
        public int PlayCount { get; private set; }
        public PlaybackSnapshot Current => PlaybackSnapshot.NoPlayer;
        public event EventHandler<PlaybackSnapshot>? Changed;

        public Task<PlaybackCommandResult> PlayAsync(CancellationToken cancellationToken = default)
        {
            PlayCount++;
            return Task.FromResult(new PlaybackCommandResult(PlaybackCommandStatus.Sent, "sent"));
        }

        public Task<PlaybackCommandResult> PauseAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new PlaybackCommandResult(PlaybackCommandStatus.Sent, "sent"));

        public Task<PlaybackCommandResult> NextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new PlaybackCommandResult(PlaybackCommandStatus.Sent, "sent"));

        public Task<PlaybackCommandResult> PreviousAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new PlaybackCommandResult(PlaybackCommandStatus.Sent, "sent"));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeAudioOutput : IAudioOutputController
    {
        public int DefaultOutputWrites { get; private set; }
        public string? DefaultId { get; set; }
        public event EventHandler? Changed { add { } remove { } }

        public AudioOutputSnapshot Read() => new([], new Dictionary<AudioOutputRole, string?> { [AudioOutputRole.Multimedia] = DefaultId });
        public AudioOutputWriteResult TryRestore(AudioOutputRestoreRequest request) => new(false, "unused");
        public AudioOutputWriteResult TrySetDefaultOutput(string endpointId)
        {
            DefaultOutputWrites++;
            return new(true, "written");
        }
        public float? ReadVolume(string endpointId) => null;
        public AudioOutputWriteResult TrySetVolume(string endpointId, float scalar) => new(false, "unused");
        public void Dispose() { }
    }

    [Fact]
    public async Task Play_plan_runs_when_audio_output_control_is_unavailable()
    {
        var playback = new FakePlayback();
        using var runner = new ConnectionActionsRunner(
            audio: null,
            playback,
            _ => throw new InvalidOperationException("No audio action should be requested for a Play-only plan."));

        await runner.RunAsync(new ConnectionActionPlan(
            [new ConnectionAction(ConnectionActionKind.Play)],
            "Play only."));

        Assert.Equal(1, playback.PlayCount);
    }

    [Fact]
    public async Task Queued_output_change_is_rechecked_when_it_reaches_the_audio_thread()
    {
        var audio = new FakeAudioOutput();
        var playback = new FakePlayback();
        var queuedWork = new TaskCompletionSource<Func<AudioOutputWriteResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var workResult = new TaskCompletionSource<AudioOutputWriteResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var runner = new ConnectionActionsRunner(
            audio,
            playback,
            work =>
            {
                queuedWork.TrySetResult(work);
                return workResult.Task;
            });
        var applicable = true;
        var run = runner.RunAsync(
            new ConnectionActionPlan([new ConnectionAction(ConnectionActionKind.SwitchOutput, "airpods-output")], "switch"),
            () => applicable);

        var dispatched = await queuedWork.Task.WaitAsync(TimeSpan.FromSeconds(2));
        applicable = false; // Simulate a disconnect/reconnect before the UI-thread callback runs.
        var result = dispatched();
        workResult.TrySetResult(result);
        await run;

        Assert.False(result.Applied);
        Assert.Equal(0, audio.DefaultOutputWrites);
    }

    private static Task<AudioOutputWriteResult> Inline(Func<AudioOutputWriteResult> work) => Task.FromResult(work());

    [Fact]
    public async Task Play_with_an_expected_endpoint_runs_while_that_output_is_still_the_default()
    {
        var audio = new FakeAudioOutput { DefaultId = "airpods-output" };
        var playback = new FakePlayback();
        using var runner = new ConnectionActionsRunner(audio, playback, Inline);

        await runner.RunAsync(new ConnectionActionPlan(
            [new ConnectionAction(ConnectionActionKind.Play, "AIRPODS-output")], "play"));

        Assert.Equal(1, playback.PlayCount);
    }

    [Fact]
    public async Task Play_is_skipped_and_logged_without_names_when_the_default_output_changed()
    {
        var audio = new FakeAudioOutput { DefaultId = "pc-speakers" };
        var playback = new FakePlayback();
        var log = new List<string>();
        using var runner = new ConnectionActionsRunner(audio, playback, Inline, log.Add);

        await runner.RunAsync(new ConnectionActionPlan(
            [new ConnectionAction(ConnectionActionKind.Play, "airpods-output")], "play"));

        Assert.Equal(0, playback.PlayCount);
        Assert.Contains(log, line => line.Contains("action=Play") && line.Contains("default output changed"));
        Assert.DoesNotContain(log, line => line.Contains("airpods-output") || line.Contains("pc-speakers"));
    }

    [Fact]
    public async Task Play_with_an_expected_endpoint_is_skipped_when_the_output_cannot_be_checked()
    {
        var playback = new FakePlayback();
        using var runner = new ConnectionActionsRunner(null, playback, Inline);

        await runner.RunAsync(new ConnectionActionPlan(
            [new ConnectionAction(ConnectionActionKind.Play, "airpods-output")], "play"));

        Assert.Equal(0, playback.PlayCount);
    }

    [Fact]
    public async Task The_output_is_read_again_after_the_play_delay_not_before()
    {
        var audio = new FakeAudioOutput { DefaultId = "airpods-output" };
        var playback = new FakePlayback();
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        using var runner = new ConnectionActionsRunner(audio, playback, Inline, timeProvider: time);

        var run = runner.RunAsync(new ConnectionActionPlan(
            [new ConnectionAction(ConnectionActionKind.Play, "airpods-output", Delay: TimeSpan.FromSeconds(1.5))], "play"));
        audio.DefaultId = "pc-speakers"; // the sound moves during the delay
        time.Advance(TimeSpan.FromSeconds(2));
        await run;

        Assert.Equal(0, playback.PlayCount);
    }
}
