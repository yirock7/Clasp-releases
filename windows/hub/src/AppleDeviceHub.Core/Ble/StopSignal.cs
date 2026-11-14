namespace AppleDeviceHub.Core.Ble;

/// <summary>
/// A re-armable "stop happened" signal for a start that is waiting on something slow. Each
/// <see cref="Replace"/> retires the current signal and installs a fresh one; the retired signal is
/// completed by the returned action, which the caller runs outside its own lock.
/// </summary>
public sealed class StopSignal
{
    private readonly Lock _gate = new();
    private TaskCompletionSource _current = New();

    /// <summary>Completes when the signal that was current at the time of the call is retired.</summary>
    public Task Current { get { lock (_gate) return _current.Task; } }

    /// <summary>Install a fresh signal. Run the returned action (outside any lock) to wake the old waiters.</summary>
    public Action Replace()
    {
        TaskCompletionSource old;
        lock (_gate)
        {
            old = _current;
            _current = New();
        }
        return () => old.TrySetResult();
    }

    /// <summary>
    /// Run <paramref name="work"/> on the thread pool (so a synchronous block cannot defeat the interrupt)
    /// and wait for it or for <paramref name="signal"/>. Returns false when the signal won; the abandoned
    /// work is left to finish on its own, so it must not throw.
    /// </summary>
    public static async Task<(bool Completed, T? Result)> RunUnlessSignalledAsync<T>(Func<Task<T>> work, Task signal)
    {
        var task = Task.Run(work);
        var winner = await Task.WhenAny(task, signal).ConfigureAwait(false);
        return ReferenceEquals(winner, task) ? (true, await task.ConfigureAwait(false)) : (false, default);
    }

    private static TaskCompletionSource New() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
