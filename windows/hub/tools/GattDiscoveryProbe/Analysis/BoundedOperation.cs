using System.Collections;

namespace GattDiscoveryProbe.Analysis;

/// <summary>Tracks asynchronous work that may outlive the caller's bounded wait.</summary>
public sealed class BoundedOperationTracker
{
    private readonly object _gate = new();
    private readonly List<TrackedOperation> _operations = [];
    private readonly List<LateCompletionRecord> _lateCompletions = [];
    private readonly Action<LateCompletionRecord>? _lateCompletionObserver;

    public BoundedOperationTracker(Action<LateCompletionRecord>? lateCompletionObserver = null)
    {
        _lateCompletionObserver = lateCompletionObserver;
    }

    public BoundedOperation<T> Start<T>(
        string operationName,
        Task<T> operation,
        Func<T, string?>? lateDisposer = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        ArgumentNullException.ThrowIfNull(operation);

        var tracked = new BoundedOperation<T>(operationName, operation, lateDisposer ?? (value => DisposeDisposableGraph(value)), RecordLateCompletion);
        lock (_gate)
            _operations.Add(new TrackedOperation(operationName, tracked.CompletionSignal, () => tracked.CompletionObserved));
        return tracked;
    }

    /// <summary>
    /// Waits for already-started work for at most <paramref name="gracePeriod"/>. Any operation
    /// still running when that period expires is returned by name for the final report.
    /// </summary>
    public async Task<BoundedOperationDrain> WaitForCompletionGraceAsync(TimeSpan gracePeriod)
    {
        if (gracePeriod < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(gracePeriod));

        Task[] pending;
        lock (_gate)
            pending = _operations.Where(operation => !operation.IsCompleted()).Select(operation => operation.CompletionSignal).ToArray();

        if (pending.Length > 0 && gracePeriod > TimeSpan.Zero)
        {
            try
            {
                await Task.WhenAll(pending).WaitAsync(gracePeriod).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Late results remain observed by their continuations; report those still running.
            }
        }

        lock (_gate)
        {
            string[] outstanding = _operations
                .Where(operation => !operation.IsCompleted())
                .Select(operation => operation.OperationName)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            return new BoundedOperationDrain(_lateCompletions.ToArray(), outstanding);
        }
    }

    /// <summary>Disposes IDisposable values recursively, including items contained in collections.</summary>
    public static string? DisposeDisposableGraph(object? value)
    {
        if (value is null)
            return null;

        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var failures = new List<string>();

        void Visit(object? item)
        {
            if (item is null || item is string || item.GetType().IsValueType || !visited.Add(item))
                return;

            if (item is IEnumerable items)
            {
                try
                {
                    foreach (object? child in items)
                        Visit(child);
                }
                catch (Exception ex)
                {
                    failures.Add($"enumerating {item.GetType().Name}: {ex.GetType().Name}");
                }
            }

            if (item is IDisposable disposable)
            {
                try
                {
                    disposable.Dispose();
                }
                catch (Exception ex)
                {
                    failures.Add($"disposing {item.GetType().Name}: {ex.GetType().Name}");
                }
            }
        }

        Visit(value);
        return failures.Count == 0 ? null : string.Join("; ", failures);
    }

    private void RecordLateCompletion(LateCompletionRecord completion)
    {
        lock (_gate)
            _lateCompletions.Add(completion);
        try { _lateCompletionObserver?.Invoke(completion); }
        catch (Exception) { /* logging must not break late-result cleanup */ }
    }

    private sealed record TrackedOperation(string OperationName, Task CompletionSignal, Func<bool> IsCompleted);

    public sealed class BoundedOperation<T>
    {
        private readonly object _gate = new();
        private readonly Task<T> _operation;
        private readonly Func<T, string?> _lateDisposer;
        private readonly Action<LateCompletionRecord> _recordLateCompletion;
        private readonly TaskCompletionSource _completionSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _state; // 0 waiting, 1 caller owns completion, 2 caller stopped waiting
        private int _waitUsed;
        private int _lateCleanupPerformed;

        internal BoundedOperation(
            string operationName,
            Task<T> operation,
            Func<T, string?> lateDisposer,
            Action<LateCompletionRecord> recordLateCompletion)
        {
            OperationName = operationName;
            _operation = operation;
            _lateDisposer = lateDisposer;
            _recordLateCompletion = recordLateCompletion;
            _ = _operation.ContinueWith(
                static (completed, state) => ((BoundedOperation<T>)state!).OnOperationCompleted(completed),
                this,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        public string OperationName { get; }
        internal Task CompletionSignal => _completionSignal.Task;
        internal bool CompletionObserved => _completionSignal.Task.IsCompleted;

        public async Task<BoundedOperationResult<T>> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
                throw new ArgumentOutOfRangeException(nameof(timeout));
            if (Interlocked.Exchange(ref _waitUsed, 1) != 0)
                throw new InvalidOperationException("A bounded operation can be waited on only once.");

            try
            {
                await _completionSignal.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
            {
                bool stoppedWaiting;
                lock (_gate)
                {
                    if (_state == 2)
                    {
                        stoppedWaiting = true;
                    }
                    else if (_operation.IsCompleted)
                    {
                        // If the operation finished before the stop took effect, caller owns its result.
                        _state = 1;
                        stoppedWaiting = false;
                    }
                    else if (_state == 0)
                    {
                        _state = 2;
                        stoppedWaiting = true;
                    }
                    else
                    {
                        stoppedWaiting = false;
                    }
                }

                if (stoppedWaiting)
                {
                    bool timedOut = ex is TimeoutException || !cancellationToken.IsCancellationRequested;
                    return new BoundedOperationResult<T>(default, "StoppedWaiting", timedOut, !timedOut, null);
                }
            }

            return await ReadCompletedResultAsync().ConfigureAwait(false);
        }

        private async Task<BoundedOperationResult<T>> ReadCompletedResultAsync()
        {
            try
            {
                T value = await _operation.ConfigureAwait(false);
                return new BoundedOperationResult<T>(value, "Completed", false, false, null);
            }
            catch (Exception ex)
            {
                return new BoundedOperationResult<T>(default, "Failed", false, false, ex);
            }
        }

        private void OnOperationCompleted(Task<T> completed)
        {
            bool late;
            lock (_gate)
            {
                if (_state == 0)
                    _state = 1;
                late = _state == 2;
            }

            try
            {
                if (late)
                    RecordLateResult(completed);
            }
            catch (Exception ex)
            {
                _recordLateCompletion(new LateCompletionRecord(
                    OperationName, "ObserverFailed", DateTimeOffset.UtcNow, ex.GetType().Name, false, null));
            }
            finally
            {
                _completionSignal.TrySetResult();
            }
        }

        internal string? DisposeLateResultOnce(T value)
        {
            if (Interlocked.Exchange(ref _lateCleanupPerformed, 1) != 0)
                return null;
            return _lateDisposer(value);
        }

        private void RecordLateResult(Task<T> completed)
        {
            if (completed.IsFaulted)
            {
                Exception failure = completed.Exception?.GetBaseException()
                    ?? new InvalidOperationException("The operation failed without an exception detail.");
                _recordLateCompletion(new LateCompletionRecord(
                    OperationName, "Failed", DateTimeOffset.UtcNow, failure.GetType().Name, false, null));
                return;
            }

            if (completed.IsCanceled)
            {
                _recordLateCompletion(new LateCompletionRecord(
                    OperationName, "LateTaskCancelled", DateTimeOffset.UtcNow, null, false, null));
                return;
            }

            string? cleanupFailure;
            try
            {
                cleanupFailure = DisposeLateResultOnce(completed.GetAwaiter().GetResult());
            }
            catch (Exception ex)
            {
                cleanupFailure = ex.GetType().Name;
            }

            _recordLateCompletion(new LateCompletionRecord(
                OperationName, "Completed", DateTimeOffset.UtcNow, null, cleanupFailure is null, cleanupFailure));
        }
    }
}

public sealed record BoundedOperationResult<T>(
    T? Value,
    string OperationStatus,
    bool TimedOut,
    bool Cancelled,
    Exception? Exception);

public sealed record LateCompletionRecord(
    string Operation,
    string OperationStatus,
    DateTimeOffset CompletedUtc,
    string? ExceptionType,
    bool LateResultDisposed,
    string? CleanupError);

public sealed record BoundedOperationDrain(
    IReadOnlyList<LateCompletionRecord> LateCompletions,
    IReadOnlyList<string> OutstandingOperations);
