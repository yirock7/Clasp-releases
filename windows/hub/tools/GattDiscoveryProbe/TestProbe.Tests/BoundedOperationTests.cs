using GattDiscoveryProbe.Analysis;

namespace GattDiscoveryProbe.TestProbe.Tests;

public sealed class BoundedOperationTests
{
    [Fact]
    public async Task SuccessBeforeTimeoutIsReturnedAndRemainsCallerOwned()
    {
        var tracker = new BoundedOperationTracker();
        var source = NewSource<CountingDisposable>();
        var operation = tracker.Start("fast", source.Task);
        var value = new CountingDisposable();
        source.SetResult(value);

        BoundedOperationResult<CountingDisposable> result = await operation.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal("Completed", result.OperationStatus);
        Assert.Same(value, result.Value);
        Assert.Equal(0, value.DisposeCount);
        Assert.Empty((await tracker.WaitForCompletionGraceAsync(TimeSpan.Zero)).LateCompletions);
    }

    [Fact]
    public async Task SuccessAfterTimeoutDisposesDisposableItemsExactlyOnce()
    {
        var tracker = new BoundedOperationTracker();
        var source = NewSource<List<IDisposable>>();
        var operation = tracker.Start("late-list", source.Task);
        BoundedOperationResult<List<IDisposable>> waiting = await operation.WaitAsync(TimeSpan.FromMilliseconds(10));
        var first = new CountingDisposable();
        var second = new CountingDisposable();
        var value = new List<IDisposable> { first, second };

        Assert.Equal("StoppedWaiting", waiting.OperationStatus);
        source.SetResult(value);
        BoundedOperationDrain drain = await tracker.WaitForCompletionGraceAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(1, second.DisposeCount);
        Assert.Single(drain.LateCompletions);
        Assert.Equal("Completed", drain.LateCompletions[0].OperationStatus);
        Assert.True(drain.LateCompletions[0].LateResultDisposed);
    }

    [Fact]
    public async Task CancellationThenSuccessDisposesLateResult()
    {
        var tracker = new BoundedOperationTracker();
        var source = NewSource<CountingDisposable>();
        var operation = tracker.Start("cancelled-success", source.Task);
        using var cancellation = new CancellationTokenSource();
        Task<BoundedOperationResult<CountingDisposable>> waiting = operation.WaitAsync(TimeSpan.FromSeconds(5), cancellation.Token);
        cancellation.Cancel();
        BoundedOperationResult<CountingDisposable> stopped = await waiting;
        var value = new CountingDisposable();
        source.SetResult(value);

        BoundedOperationDrain drain = await tracker.WaitForCompletionGraceAsync(TimeSpan.FromSeconds(1));

        Assert.Equal("StoppedWaiting", stopped.OperationStatus);
        Assert.Equal(1, value.DisposeCount);
        Assert.Single(drain.LateCompletions);
        Assert.True(drain.LateCompletions[0].LateResultDisposed);
    }

    [Fact]
    public async Task CancellationThenFailureIsObservedAndRecorded()
    {
        var tracker = new BoundedOperationTracker();
        var source = NewSource<CountingDisposable>();
        var operation = tracker.Start("cancelled-failure", source.Task);
        using var cancellation = new CancellationTokenSource();
        Task<BoundedOperationResult<CountingDisposable>> waiting = operation.WaitAsync(TimeSpan.FromSeconds(5), cancellation.Token);
        cancellation.Cancel();
        Assert.Equal("StoppedWaiting", (await waiting).OperationStatus);
        source.SetException(new InvalidOperationException("late failure"));

        BoundedOperationDrain drain = await tracker.WaitForCompletionGraceAsync(TimeSpan.FromSeconds(1));

        Assert.Single(drain.LateCompletions);
        Assert.Equal("Failed", drain.LateCompletions[0].OperationStatus);
        Assert.Equal(nameof(InvalidOperationException), drain.LateCompletions[0].ExceptionType);
    }

    [Fact]
    public async Task LateFailureIsObservedAndRecordedWithoutThrowingFromGraceWait()
    {
        var tracker = new BoundedOperationTracker();
        var source = NewSource<CountingDisposable>();
        var operation = tracker.Start("timed-out-failure", source.Task);
        Assert.Equal("StoppedWaiting", (await operation.WaitAsync(TimeSpan.FromMilliseconds(10))).OperationStatus);
        source.SetException(new InvalidOperationException("late failure"));

        BoundedOperationDrain drain = await tracker.WaitForCompletionGraceAsync(TimeSpan.FromSeconds(1));

        Assert.Single(drain.LateCompletions);
        Assert.Equal("Failed", drain.LateCompletions[0].OperationStatus);
        Assert.Equal(nameof(InvalidOperationException), drain.LateCompletions[0].ExceptionType);
        Assert.Empty(drain.OutstandingOperations);
    }

    [Fact]
    public async Task CleanupTriggeredTwiceStillDisposesOnlyOnce()
    {
        var tracker = new BoundedOperationTracker();
        var source = NewSource<CountingDisposable>();
        var operation = tracker.Start("duplicate-cleanup", source.Task);
        Assert.Equal("StoppedWaiting", (await operation.WaitAsync(TimeSpan.FromMilliseconds(10))).OperationStatus);
        var value = new CountingDisposable();
        source.SetResult(value);
        await tracker.WaitForCompletionGraceAsync(TimeSpan.FromSeconds(1));

        operation.DisposeLateResultOnce(value);

        Assert.Equal(1, value.DisposeCount);
    }

    [Fact]
    public async Task GracePeriodReportsStillOutstandingOperations()
    {
        var tracker = new BoundedOperationTracker();
        var source = NewSource<CountingDisposable>();
        var operation = tracker.Start("still-running", source.Task);
        Assert.Equal("StoppedWaiting", (await operation.WaitAsync(TimeSpan.FromMilliseconds(10))).OperationStatus);

        BoundedOperationDrain drain = await tracker.WaitForCompletionGraceAsync(TimeSpan.FromMilliseconds(20));

        Assert.Equal(new[] { "still-running" }, drain.OutstandingOperations);
        source.SetResult(new CountingDisposable());
        await tracker.WaitForCompletionGraceAsync(TimeSpan.FromSeconds(1));
    }

    private static TaskCompletionSource<T> NewSource<T>()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class CountingDisposable : IDisposable
    {
        public int DisposeCount { get; private set; }

        public void Dispose() => DisposeCount++;
    }
}
