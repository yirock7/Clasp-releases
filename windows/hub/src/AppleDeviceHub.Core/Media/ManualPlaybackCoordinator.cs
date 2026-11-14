namespace AppleDeviceHub.Core.Media;

/// <summary>
/// Adds session checks, one-at-a-time commands, cancellation and bounded waits to a Windows
/// playback source. A timed-out native request keeps the controller busy until that request
/// actually settles, so a late request can never overlap a second Play/Next/etc. call.
/// </summary>
public sealed class ManualPlaybackCoordinator : IPlaybackController
{
    private static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan DefaultDisposeWait = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();
    private readonly IPlaybackSessionSource _source;
    private readonly TimeSpan _commandTimeout;
    private readonly TimeSpan _disposeWait;
    private readonly Action<string>? _diagnosticLog;
    private readonly CancellationTokenSource _lifetime = new();
    private PlaybackSnapshot _current;
    private CancellationTokenSource? _activeCancellation;
    private Task<PlaybackCommandResult>? _activeOperation;
    private string? _activeSessionId;
    private long _generation;
    private bool _busy;
    private bool _disposed;

    public ManualPlaybackCoordinator(
        IPlaybackSessionSource source,
        TimeSpan? commandTimeout = null,
        TimeSpan? disposeWait = null,
        Action<string>? diagnosticLog = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _commandTimeout = commandTimeout ?? DefaultCommandTimeout;
        _disposeWait = disposeWait ?? DefaultDisposeWait;
        _diagnosticLog = diagnosticLog;
        if (_commandTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(commandTimeout));
        if (_disposeWait < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(disposeWait));

        _current = _source.Current;
        _source.Changed += OnSourceChanged;

        // The source may have changed between the first read and subscribing.
        SynchronizeFromSource(raiseEvent: false);
    }

    /// <inheritdoc />
    public PlaybackSnapshot Current
    {
        get { lock (_gate) return _current; }
    }

    /// <inheritdoc />
    public event EventHandler<PlaybackSnapshot>? Changed;

    /// <inheritdoc />
    public Task<PlaybackCommandResult> PlayAsync(CancellationToken cancellationToken = default)
        => ExecuteAsync(PlaybackAction.Play, cancellationToken);

    /// <inheritdoc />
    public Task<PlaybackCommandResult> PauseAsync(CancellationToken cancellationToken = default)
        => ExecuteAsync(PlaybackAction.Pause, cancellationToken);

    /// <inheritdoc />
    public Task<PlaybackCommandResult> NextAsync(CancellationToken cancellationToken = default)
        => ExecuteAsync(PlaybackAction.Next, cancellationToken);

    /// <inheritdoc />
    public Task<PlaybackCommandResult> PreviousAsync(CancellationToken cancellationToken = default)
        => ExecuteAsync(PlaybackAction.Previous, cancellationToken);

    private async Task<PlaybackCommandResult> ExecuteAsync(PlaybackAction action, CancellationToken cancellationToken)
    {
        SynchronizeFromSource(raiseEvent: true);

        PlaybackSnapshot snapshot;
        long generation;
        CancellationTokenSource commandCancellation;
        lock (_gate)
        {
            if (_disposed)
                return Result(PlaybackCommandStatus.Cancelled, "Playback controls were closed.");
            if (_busy)
                return Result(PlaybackCommandStatus.Busy,
                    "Windows is still working on the last request. If it stays stuck, turn Show playback controls off and on in Settings.");

            snapshot = _current;
            if (!snapshot.HasPlayer)
                return Result(PlaybackCommandStatus.NoPlayer, snapshot.Status);
            if (!snapshot.Supports(action))
                return Result(PlaybackCommandStatus.Unsupported, UnsupportedMessage(snapshot.PlayerName, action));

            _busy = true;
            _activeSessionId = snapshot.SessionId;
            generation = _generation;
            commandCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            _activeCancellation = commandCancellation;
        }

        var commandToken = commandCancellation.Token;

        Task<PlaybackCommandResult> nativeOperation;
        try
        {
            // The session source rechecks that this exact session is still current immediately
            // before dispatch. This catches a switch that happened after our snapshot read.
            Log($"origin=manual event=command-dispatch action={action} sessionRef={MediaDiagnosticIdentity.SessionReference(snapshot.SessionId)} reason=explicit playback request");
            nativeOperation = _source.ExecuteAsync(action, snapshot.SessionId!, commandCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            nativeOperation = Task.FromResult(Result(PlaybackCommandStatus.Cancelled, "The request was canceled."));
        }
        catch (Exception)
        {
            nativeOperation = Task.FromResult(Result(PlaybackCommandStatus.Failed, "Windows couldn't complete that."));
        }

        var settledOperation = ObserveOperationAsync(nativeOperation, action, snapshot.SessionId!, generation, commandCancellation);
        lock (_gate)
        {
            if (ReferenceEquals(_activeCancellation, commandCancellation))
                _activeOperation = settledOperation;
        }

        var timeout = Task.Delay(_commandTimeout, CancellationToken.None);
        var cancelled = Task.Delay(Timeout.InfiniteTimeSpan, commandToken);
        var completed = await Task.WhenAny(settledOperation, timeout, cancelled).ConfigureAwait(false);
        if (completed == settledOperation)
            return await settledOperation.ConfigureAwait(false);

        TryCancel(commandCancellation);
        if (completed == cancelled)
            return Result(PlaybackCommandStatus.Cancelled, "The request was canceled or the player changed.");

        return Result(PlaybackCommandStatus.TimedOut,
            "Windows hasn't responded yet. Clasp will wait before sending another request. If it stays stuck, turn Show playback controls off and on in Settings.");
    }

    private async Task<PlaybackCommandResult> ObserveOperationAsync(
        Task<PlaybackCommandResult> operation,
        PlaybackAction action,
        string sessionId,
        long generation,
        CancellationTokenSource commandCancellation)
    {
        PlaybackCommandResult result;
        try
        {
            result = await operation.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            result = Result(PlaybackCommandStatus.Cancelled, "The request was canceled.");
        }
        catch (Exception)
        {
            result = Result(PlaybackCommandStatus.Failed, "Windows couldn't complete that.");
        }

        bool becameStale;
        lock (_gate)
        {
            becameStale = _disposed || _generation != generation || _current.SessionId != sessionId;
            if (ReferenceEquals(_activeCancellation, commandCancellation))
            {
                _activeCancellation = null;
                _activeOperation = null;
                _activeSessionId = null;
                _busy = false;
            }
        }

        commandCancellation.Dispose();
        if (becameStale)
            result = Result(PlaybackCommandStatus.Cancelled, "The player changed before the request finished.");

        // The action is included in the wording because a successful WinRT Boolean means that
        // Windows accepted the request; it does not mean the player visibly changed state.
        if (!becameStale && result.Status == PlaybackCommandStatus.Sent && string.IsNullOrWhiteSpace(result.Message))
            result = Result(PlaybackCommandStatus.Sent, $"{ActionName(action)} request sent.");
        return result;
    }

    private void Log(string message)
    {
        if (_diagnosticLog is null) return;
        try { _diagnosticLog($"at={DateTimeOffset.UtcNow:O} {message}"); }
        catch (Exception) { /* Diagnostics must never interfere with a playback request. */ }
    }

    private void OnSourceChanged(object? sender, PlaybackSnapshot snapshot)
        => SynchronizeFromSource(raiseEvent: true, eventSessionId: snapshot.SessionId);

    private void SynchronizeFromSource(bool raiseEvent, string? eventSessionId = null)
    {
        PlaybackSnapshot latest;
        bool changed;
        CancellationTokenSource? cancel;
        lock (_gate)
        {
            if (_disposed) return;

            // Read and apply under one coordinator lock. If a callback sampled an old source
            // value before this lock, it could otherwise overwrite a newer callback that already
            // applied. Source updates after this read raise another callback, which will reread
            // the newest value after this lock is released.
            try { latest = _source.Current; }
            catch (Exception) { latest = PlaybackSnapshot.Unavailable; }

            if (eventSessionId is not null && latest.SessionId != eventSessionId)
                return; // late metadata/state event from a session that is no longer selected

            changed = latest != _current;
            if (latest.SessionId != _current.SessionId)
            {
                _generation++;
                if (_busy && _activeSessionId != latest.SessionId)
                {
                    _activeSessionId = null;
                    cancel = _activeCancellation;
                }
                else cancel = null;
            }
            else cancel = null;
            _current = latest;
        }

        TryCancel(cancel);
        if (raiseEvent && changed) RaiseChanged(latest);
    }

    private void RaiseChanged(PlaybackSnapshot snapshot)
    {
        var handlers = Changed;
        if (handlers is null) return;
        foreach (EventHandler<PlaybackSnapshot> handler in handlers.GetInvocationList())
        {
            try { handler(this, snapshot); }
            catch (Exception) { /* A presentation subscriber must not break the SMTC event source. */ }
        }
    }

    private static PlaybackCommandResult Result(PlaybackCommandStatus status, string message)
        => new(status, message);

    private static void TryCancel(CancellationTokenSource? cancellation)
    {
        if (cancellation is null) return;
        try { cancellation.Cancel(); }
        catch (ObjectDisposedException) { /* The operation settled at the same time as cancellation. */ }
        catch (AggregateException) { /* A cancellation callback cannot break the session event path. */ }
    }

    private static string ActionName(PlaybackAction action) => action switch
    {
        PlaybackAction.Play => "Play",
        PlaybackAction.Pause => "Pause",
        PlaybackAction.Next => "Next",
        PlaybackAction.Previous => "Previous",
        _ => "Playback",
    };

    private static string UnsupportedMessage(string? playerName, PlaybackAction action)
    {
        var target = string.IsNullOrWhiteSpace(playerName) ? "The current player" : $"{playerName}";
        return $"{target} doesn't support {ActionName(action)}.";
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Task<PlaybackCommandResult>? activeOperation;
        CancellationTokenSource? activeCancellation;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            activeOperation = _activeOperation;
            activeCancellation = _activeCancellation;
        }

        _source.Changed -= OnSourceChanged;
        TryCancel(_lifetime);
        TryCancel(activeCancellation);
        if (activeOperation is not null && _disposeWait > TimeSpan.Zero)
        {
            try { await activeOperation.WaitAsync(_disposeWait).ConfigureAwait(false); }
            catch (TimeoutException) { /* Do not hold app shutdown on an unresponsive player. */ }
            catch (Exception) { /* Native failures are reported by the command result. */ }
        }

        try { await _source.DisposeAsync().ConfigureAwait(false); }
        catch (Exception) { /* Closing the app must still detach the public controller. */ }
        _lifetime.Dispose();
    }
}
