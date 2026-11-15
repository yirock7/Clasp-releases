using AppleDeviceHub.Core.Ble;
using AppleDeviceHub.Core.Contracts;
using Microsoft.Extensions.Time.Testing;

namespace AppleDeviceHub.Core.Tests.Ble;

/// <summary>Review EF-REVIEW P2: a Stop that arrives while the source is still starting.</summary>
public class BroadcastStartStopRaceTests
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeAdvertisementSource _source = new();

    [Fact]
    public async Task Stop_during_start_leaves_the_source_stopped()
    {
        var release = new TaskCompletionSource();
        _source.OnStart = async _ =>
        {
            await release.Task;
            return new AdvertisementSourceStatus(AdvertisementSourceState.Running);
        };
        await using var t = new BroadcastObservationTransport(_source, _clock);
        await t.StartAsync();

        var start = t.SendAsync(new ConnectCommand(Guid.NewGuid()));
        Assert.Equal(CommandOutcome.Acknowledged, (await t.SendAsync(new DisconnectCommand(Guid.NewGuid()))).Outcome);
        release.SetResult(); // the radio comes up after the user already stopped
        await start;

        Assert.False(t.IsObserving);
        Assert.Equal(AdvertisementSourceState.Stopped, _source.Status.State);
    }

    /// <summary>EF-IR-01: start A waits, Stop retires it, start B runs, then A ends late.</summary>
    private async Task<(BroadcastObservationTransport, Task<CommandResult>, TaskCompletionSource<AdvertisementSourceStatus>, CancellationTokenSource)> RetiredStartWithReplacement()
    {
        var first = new TaskCompletionSource<AdvertisementSourceStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        _source.OnStart = ct => Interlocked.Increment(ref calls) == 1
            ? first.Task.WaitAsync(ct)
            : Task.FromResult(new AdvertisementSourceStatus(AdvertisementSourceState.Running));
        var t = new BroadcastObservationTransport(_source, _clock);
        await t.StartAsync();
        var cts = new CancellationTokenSource();
        var oldStart = t.SendAsync(new ConnectCommand(Guid.NewGuid()), cts.Token);
        await t.SendAsync(new DisconnectCommand(Guid.NewGuid()));
        Assert.Equal(CommandOutcome.Acknowledged, (await t.SendAsync(new ConnectCommand(Guid.NewGuid()))).Outcome);
        return (t, oldStart, first, cts);
    }

    [Fact]
    public async Task A_retired_start_that_fails_late_leaves_the_replacement_alone()
    {
        var (t, oldStart, first, _) = await RetiredStartWithReplacement();
        await using var _t = t;
        var stops = _source.StopCount;
        first.SetResult(new AdvertisementSourceStatus(AdvertisementSourceState.Unavailable, "old attempt failed"));
        await oldStart;
        Assert.Equal(stops, _source.StopCount);
        Assert.True(t.IsObserving);
        Assert.Null(t.UnavailableReason); // the old failure is not reported as the current state
    }

    [Fact]
    public async Task A_retired_start_cancelled_late_leaves_the_replacement_alone()
    {
        var (t, oldStart, _, cts) = await RetiredStartWithReplacement();
        await using var _t = t;
        var stops = _source.StopCount;
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => oldStart);
        Assert.Equal(stops, _source.StopCount);
        Assert.True(t.IsObserving);
    }

    [Fact]
    public async Task A_retired_start_that_succeeds_late_leaves_the_replacement_alone()
    {
        var (t, oldStart, first, _) = await RetiredStartWithReplacement();
        await using var _t = t;
        var stops = _source.StopCount;
        first.SetResult(new AdvertisementSourceStatus(AdvertisementSourceState.Running));
        await oldStart;
        Assert.Equal(stops, _source.StopCount);
        Assert.True(t.IsObserving);
    }

    [Fact]
    public async Task Dispose_during_a_start_stops_and_disposes_the_source_once_it_returns()
    {
        var first = new TaskCompletionSource<AdvertisementSourceStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        _source.OnStart = _ => first.Task;
        var t = new BroadcastObservationTransport(_source, _clock);
        await t.StartAsync();
        var start = t.SendAsync(new ConnectCommand(Guid.NewGuid()));
        await t.DisposeAsync();
        Assert.True(_source.Disposed);
        first.SetResult(new AdvertisementSourceStatus(AdvertisementSourceState.Running));
        await start;
        Assert.False(t.IsObserving);
        Assert.Equal(AdvertisementSourceState.Stopped, _source.Status.State);
    }

    [Fact]
    public async Task A_newer_start_keeps_the_source_running()
    {
        var first = new TaskCompletionSource();
        var calls = 0;
        _source.OnStart = async _ =>
        {
            if (Interlocked.Increment(ref calls) == 1) await first.Task;
            return new AdvertisementSourceStatus(AdvertisementSourceState.Running);
        };
        await using var t = new BroadcastObservationTransport(_source, _clock);
        await t.StartAsync();

        var slow = t.SendAsync(new ConnectCommand(Guid.NewGuid()));
        await t.SendAsync(new DisconnectCommand(Guid.NewGuid()));
        Assert.Equal(CommandOutcome.Acknowledged, (await t.SendAsync(new ConnectCommand(Guid.NewGuid()))).Outcome);
        first.SetResult();
        await slow;

        Assert.True(t.IsObserving);
        Assert.Equal(AdvertisementSourceState.Running, _source.Status.State);
    }
}
