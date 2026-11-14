using System.ComponentModel;
using AppleDeviceHub.Core.Media;

namespace AppleDeviceHub.Core.Presentation;

/// <summary>
/// Presentation state for manual Windows player controls. This feature uses SMTC directly and
/// remains usable when there is no connected AirPods transport.
/// </summary>
public sealed class PlaybackViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private static readonly TimeSpan DisposeWait = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();
    private readonly object _snapshotGate = new();
    private readonly IPlaybackController? _controller;
    private readonly Action<Action> _dispatchToUi;
    private readonly Action? _beforeManualIntent;
    private readonly CancellationTokenSource _lifetime = new();
    private PlaybackSnapshot _current;
    private PlaybackSnapshot? _pendingSnapshot;
    private string _status;
    private bool _statusIsRoutine;
    private bool _isBusy;
    private bool _snapshotDispatchQueued;
    private bool _disposed;
    private CancellationTokenSource? _activeCommandCancellation;
    private Task<PlaybackCommandResult>? _activeCommand;

    /// <summary>
    /// Creates a view model and takes ownership of <paramref name="controller"/>. The optional
    /// dispatcher marshals state changes onto the UI thread; SMTC events can arrive on any thread.
    /// </summary>
    public PlaybackViewModel(
        IPlaybackController? controller,
        Action<Action>? dispatchToUi = null,
        Action? beforeManualIntent = null)
    {
        _controller = controller;
        _beforeManualIntent = beforeManualIntent;
        _current = controller?.Current ?? PlaybackSnapshot.Unavailable;
        _status = _current.Status;
        _statusIsRoutine = _current.StatusIsRoutine;

        var synchronizationContext = SynchronizationContext.Current;
        _dispatchToUi = dispatchToUi ?? (synchronizationContext is null
            ? static action => action()
            : action => synchronizationContext.Post(static state => ((Action)state!).Invoke(), action));

        if (_controller is not null)
        {
            _controller.Changed += OnControllerChanged;
            QueueSnapshot(_controller.Current);
        }
    }

    /// <summary>Current player and playback details.</summary>
    public PlaybackSnapshot Current => _current;

    /// <summary>Current Windows application identity, or a no-player message in <see cref="Status"/>.</summary>
    public string? PlayerName => Current.PlayerName;

    /// <summary>Current track title when the player exposes it.</summary>
    public string? Title => Current.Title;

    /// <summary>Player availability or the most recent manual command result.</summary>
    public string Status => _status;

    /// <summary>
    /// True when <see cref="Status"/> only says the player is under control (nothing to read). A command's
    /// result, a busy message or any failure is never routine, so a screen can hide this and still show problems.
    /// </summary>
    public bool StatusIsRoutine => _statusIsRoutine;

    /// <summary>True when Windows currently exposes a media session.</summary>
    public bool HasPlayer => Current.HasPlayer;

    /// <summary>True when Play is supported and no command is currently running.</summary>
    public bool CanPlay => !IsBusy && Current.CanPlay;

    /// <summary>True when Pause is supported and no command is currently running.</summary>
    public bool CanPause => !IsBusy && Current.CanPause;

    /// <summary>True when Next is supported and no command is currently running.</summary>
    public bool CanNext => !IsBusy && Current.CanNext;

    /// <summary>True when Previous is supported and no command is currently running.</summary>
    public bool CanPrevious => !IsBusy && Current.CanPrevious;

    /// <summary>True while a manual command is being sent or awaited.</summary>
    public bool IsBusy { get { lock (_gate) return _isBusy; } }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Send an explicit Play request to the current Windows player.</summary>
    public Task<PlaybackCommandResult> PlayAsync(CancellationToken cancellationToken = default)
        => RunCommandAsync(PlaybackAction.Play, cancellationToken);

    /// <summary>Send an explicit Pause request to the current Windows player.</summary>
    public Task<PlaybackCommandResult> PauseAsync(CancellationToken cancellationToken = default)
        => RunCommandAsync(PlaybackAction.Pause, cancellationToken);

    /// <summary>Send an explicit Next request to the current Windows player.</summary>
    public Task<PlaybackCommandResult> NextAsync(CancellationToken cancellationToken = default)
        => RunCommandAsync(PlaybackAction.Next, cancellationToken);

    /// <summary>Send an explicit Previous request to the current Windows player.</summary>
    public Task<PlaybackCommandResult> PreviousAsync(CancellationToken cancellationToken = default)
        => RunCommandAsync(PlaybackAction.Previous, cancellationToken);

    private async Task<PlaybackCommandResult> RunCommandAsync(PlaybackAction action, CancellationToken cancellationToken)
    {
        CancellationTokenSource commandCancellation;
        lock (_gate)
        {
            if (_disposed)
                return new PlaybackCommandResult(PlaybackCommandStatus.Cancelled, "Playback controls were closed.");
            if (_isBusy)
                return new PlaybackCommandResult(PlaybackCommandStatus.Busy, "Still working on the last request.");
            _isBusy = true;
            commandCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            _activeCommandCancellation = commandCancellation;
        }

        QueueBusyState("Sending to Windows…");
        PlaybackCommandResult result;
        Task<PlaybackCommandResult>? commandTask = null;
        try
        {
            if (_controller is null)
            {
                result = new PlaybackCommandResult(PlaybackCommandStatus.Unavailable, PlaybackSnapshot.Unavailable.Status);
            }
            else
            {
                var latest = _controller.Current;
                if (!commandCancellation.IsCancellationRequested && latest.HasPlayer && latest.Supports(action))
                {
                    try { _beforeManualIntent?.Invoke(); }
                    catch (Exception) { /* A policy hook must not prevent a direct user command. */ }
                }

                commandTask = DispatchCommandAsync(_controller, action, commandCancellation.Token);
                lock (_gate)
                {
                    if (ReferenceEquals(_activeCommandCancellation, commandCancellation))
                        _activeCommand = commandTask;
                }
                result = await commandTask.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            result = new PlaybackCommandResult(PlaybackCommandStatus.Cancelled, "Cancelled.");
        }
        catch (Exception)
        {
            result = new PlaybackCommandResult(PlaybackCommandStatus.Failed, "Windows couldn't do that.");
        }

        bool notify;
        lock (_gate)
        {
            notify = !_disposed && ReferenceEquals(_activeCommandCancellation, commandCancellation);
            if (ReferenceEquals(_activeCommandCancellation, commandCancellation))
            {
                _activeCommandCancellation = null;
                _activeCommand = null;
                _isBusy = false;
            }
        }
        commandCancellation.Dispose();

        if (notify)
        {
            DispatchToUi(() =>
            {
                if (IsDisposed()) return;
                _status = result.Message;
                _statusIsRoutine = false;
                OnPropertyChanged(nameof(Status));
                OnPropertyChanged(nameof(IsBusy));
                OnPropertyChanged(nameof(CanPlay));
                OnPropertyChanged(nameof(CanPause));
                OnPropertyChanged(nameof(CanNext));
                OnPropertyChanged(nameof(CanPrevious));
            });
        }

        return result;
    }

    private static Task<PlaybackCommandResult> DispatchCommandAsync(
        IPlaybackController controller,
        PlaybackAction action,
        CancellationToken cancellationToken) => action switch
        {
            PlaybackAction.Play => controller.PlayAsync(cancellationToken),
            PlaybackAction.Pause => controller.PauseAsync(cancellationToken),
            PlaybackAction.Next => controller.NextAsync(cancellationToken),
            PlaybackAction.Previous => controller.PreviousAsync(cancellationToken),
            _ => Task.FromResult(new PlaybackCommandResult(PlaybackCommandStatus.Unsupported, "That isn't available here.")),
        };

    private void OnControllerChanged(object? sender, PlaybackSnapshot snapshot)
        => QueueSnapshot(snapshot);

    private void QueueSnapshot(PlaybackSnapshot snapshot)
    {
        lock (_snapshotGate)
        {
            if (IsDisposed()) return;
            _pendingSnapshot = snapshot;
            if (_snapshotDispatchQueued) return;
            _snapshotDispatchQueued = true;
        }

        DispatchToUi(ApplyLatestSnapshot);
    }

    private void ApplyLatestSnapshot()
    {
        PlaybackSnapshot? snapshot;
        lock (_snapshotGate)
        {
            _snapshotDispatchQueued = false;
            snapshot = _pendingSnapshot;
            _pendingSnapshot = null;
        }

        if (snapshot is null || IsDisposed()) return;

        // Native session callbacks and the UI dispatcher can run out of order. Treat each event
        // as a wake-up and sample the controller at application time so a late payload cannot
        // put an older title/player back onto the screen.
        try { snapshot = _controller?.Current ?? snapshot; }
        catch (Exception) { /* Keep the last queued value if the controller is already closing. */ }
        if (snapshot == _current) return;

        _current = snapshot;
        if (!IsBusy)
        {
            _status = snapshot.Status;
            _statusIsRoutine = snapshot.StatusIsRoutine;
        }
        OnPropertyChanged(nameof(Current));
        OnPropertyChanged(nameof(PlayerName));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(HasPlayer));
        OnPropertyChanged(nameof(CanPlay));
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanNext));
        OnPropertyChanged(nameof(CanPrevious));
    }

    private void QueueBusyState(string status)
    {
        DispatchToUi(() =>
        {
            if (IsDisposed()) return;
            _status = status;
            _statusIsRoutine = false;
            OnPropertyChanged(nameof(Status));
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(CanPlay));
            OnPropertyChanged(nameof(CanPause));
            OnPropertyChanged(nameof(CanNext));
            OnPropertyChanged(nameof(CanPrevious));
        });
    }

    private void DispatchToUi(Action action)
    {
        try { _dispatchToUi(action); }
        catch (Exception)
        {
            lock (_snapshotGate) _snapshotDispatchQueued = false;
        }
    }

    private static void TryCancel(CancellationTokenSource? cancellation)
    {
        if (cancellation is null) return;
        try { cancellation.Cancel(); }
        catch (ObjectDisposedException) { /* The command completed at the same time as shutdown. */ }
        catch (AggregateException) { /* Cancellation callbacks must not break view-model disposal. */ }
    }

    private bool IsDisposed()
    {
        lock (_gate) return _disposed;
    }

    private void OnPropertyChanged(string propertyName)
    {
        var handlers = PropertyChanged;
        if (handlers is null) return;
        foreach (PropertyChangedEventHandler handler in handlers.GetInvocationList())
        {
            try { handler(this, new PropertyChangedEventArgs(propertyName)); }
            catch (Exception) { /* A binding subscriber must not break an SMTC callback. */ }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Task<PlaybackCommandResult>? command;
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            command = _activeCommand;
            cancellation = _activeCommandCancellation;
        }

        TryCancel(_lifetime);
        TryCancel(cancellation);

        if (_controller is not null) _controller.Changed -= OnControllerChanged;
        lock (_snapshotGate) _pendingSnapshot = null;

        if (command is not null)
        {
            try { await command.WaitAsync(DisposeWait).ConfigureAwait(false); }
            catch (TimeoutException) { /* Do not hold app shutdown on an unresponsive player. */ }
            catch (Exception) { /* The controller turns ordinary failures into command results. */ }
        }

        if (_controller is not null)
        {
            try { await _controller.DisposeAsync().ConfigureAwait(false); }
            catch (Exception) { /* View-model disposal still detaches the presentation layer. */ }
        }
        _lifetime.Dispose();
    }
}
