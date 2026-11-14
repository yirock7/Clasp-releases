using System.Runtime.InteropServices;
using AppleDeviceHub.Core.Media;
using Windows.Media.Control;

namespace AppleDeviceHub.App.Media;

/// <summary>
/// Driver-free manual playback through the current Windows System Media Transport Controls
/// session. This source is separate from ear-detection pause/resume and is created independently
/// of any AirPods or Bluetooth connection.
/// </summary>
internal sealed class WindowsPlaybackController : IPlaybackSessionSource
{
    private readonly object _gate = new();
    private readonly GlobalSystemMediaTransportControlsSessionManager _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private PlaybackSnapshot _current = PlaybackSnapshot.NoPlayer;
    private MetadataRequest? _pendingMetadata;
    private Task? _metadataWorker;
    private bool _metadataWorkerRunning;
    private long _generation;
    private long _metadataRevision;
    private long _metadataWorkerVersion;
    private bool _disposed;

    private WindowsPlaybackController(GlobalSystemMediaTransportControlsSessionManager manager)
    {
        _manager = manager;
        _manager.CurrentSessionChanged += OnCurrentSessionChanged;
        try
        {
            Track(_manager.GetCurrentSession());
        }
        catch
        {
            _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
            throw;
        }
    }

    /// <summary>
    /// Creates the Windows SMTC source and its coordinator. Null means the operating system did
    /// not provide access to the session manager; a valid controller may still have no player.
    /// </summary>
    internal static async Task<IPlaybackController?> TryCreateAsync(Action<string>? diagnosticLog = null)
    {
        try
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            if (manager is null) return null;
            return new ManualPlaybackCoordinator(new WindowsPlaybackController(manager), diagnosticLog: diagnosticLog);
        }
        catch (Exception exception) when (exception is COMException
            or UnauthorizedAccessException
            or InvalidOperationException
            or PlatformNotSupportedException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public PlaybackSnapshot Current { get { lock (_gate) return _current; } }

    /// <inheritdoc />
    public event EventHandler<PlaybackSnapshot>? Changed;

    /// <inheritdoc />
    public async Task<PlaybackCommandResult> ExecuteAsync(
        PlaybackAction action,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        GlobalSystemMediaTransportControlsSession? session;
        PlaybackSnapshot snapshot;
        lock (_gate)
        {
            if (_disposed)
                return Result(PlaybackCommandStatus.Cancelled, "Windows media controls have been closed.");

            snapshot = _current;
            if (_session is null || snapshot.SessionId != sessionId)
                return Result(PlaybackCommandStatus.Cancelled, "The selected Windows player changed before the request was sent.");
            if (!snapshot.Supports(action))
                return Result(PlaybackCommandStatus.Unsupported, UnsupportedMessage(snapshot.PlayerName, action));
            session = _session;
        }

        if (cancellationToken.IsCancellationRequested)
            return Result(PlaybackCommandStatus.Cancelled, "The Windows media request was cancelled before it was sent.");

        try
        {
            // These are direct, explicit SMTC methods. There is no PlayPause toggle and no
            // simulated keyboard input. We captured this session only after checking its id;
            // if Windows switches after that point, the command remains scoped to this old
            // session object and the coordinator suppresses its stale completion.
            var request = action switch
            {
                PlaybackAction.Play => session.TryPlayAsync(),
                PlaybackAction.Pause => session.TryPauseAsync(),
                PlaybackAction.Next => session.TrySkipNextAsync(),
                PlaybackAction.Previous => session.TrySkipPreviousAsync(),
                _ => null,
            };
            if (request is null)
                return Result(PlaybackCommandStatus.Unsupported, UnsupportedMessage(snapshot.PlayerName, action));

            var accepted = await request;
            if (cancellationToken.IsCancellationRequested)
                return Result(PlaybackCommandStatus.Cancelled, "The selected player changed while Windows was handling the request.");
            if (!accepted)
                return Result(PlaybackCommandStatus.Refused, $"Windows did not accept the {ActionName(action)} request.");

            lock (_gate)
            {
                if (_disposed || _current.SessionId != sessionId || !ReferenceEquals(_session, session))
                    return Result(PlaybackCommandStatus.Cancelled, "The selected player changed before Windows finished the request.");
            }

            return Result(PlaybackCommandStatus.Sent, $"{ActionName(action)} request sent to {snapshot.PlayerName}.");
        }
        catch (OperationCanceledException)
        {
            return Result(PlaybackCommandStatus.Cancelled, "The Windows media request was cancelled.");
        }
        catch (Exception exception) when (exception is COMException
            or UnauthorizedAccessException
            or InvalidOperationException)
        {
            return Result(PlaybackCommandStatus.Refused, $"Windows refused the {ActionName(action)} request.");
        }
        catch (Exception)
        {
            return Result(PlaybackCommandStatus.Failed, "Windows could not complete the media request.");
        }
    }

    private void OnCurrentSessionChanged(
        GlobalSystemMediaTransportControlsSessionManager sender,
        CurrentSessionChangedEventArgs args)
    {
        try { Track(sender.GetCurrentSession()); }
        catch (Exception) { TrackUnavailable(); }
    }

    private void Track(GlobalSystemMediaTransportControlsSession? session)
    {
        PlaybackSnapshot snapshot;
        long generation;
        lock (_gate)
        {
            if (_disposed) return;

            // WinRT may report the same current session again with the same object, for example
            // when current-session state is refreshed. Preserve identity only for that exact
            // non-null instance. Matching AUMIDs do not prove that a player session is the same.
            if (session is not null && ReferenceEquals(session, _session))
            {
                generation = _generation;
                _metadataRevision++;
                _pendingMetadata = null;
                _current = ReadSnapshot(session, generation, _current.Title);
            }
            else
            {
                UnsubscribeFromSessionLocked();
                _session = session;
                generation = ++_generation;
                _metadataRevision++;
                _pendingMetadata = null;
                if (session is null)
                {
                    _current = PlaybackSnapshot.NoPlayer;
                }
                else
                {
                    session.PlaybackInfoChanged += OnPlaybackInfoChanged;
                    session.MediaPropertiesChanged += OnMediaPropertiesChanged;
                    _current = ReadSnapshot(session, generation, title: null);
                }
            }
            snapshot = _current;
        }

        RaiseChanged(snapshot);
        if (session is not null) RequestMetadataRefresh(session, generation);
    }

    private void TrackUnavailable()
    {
        PlaybackSnapshot snapshot;
        lock (_gate)
        {
            if (_disposed) return;
            UnsubscribeFromSessionLocked();
            _session = null;
            _generation++;
            _metadataRevision++;
            _pendingMetadata = null;
            _current = PlaybackSnapshot.Unavailable;
            snapshot = _current;
        }
        RaiseChanged(snapshot);
    }

    private void OnPlaybackInfoChanged(
        GlobalSystemMediaTransportControlsSession sender,
        PlaybackInfoChangedEventArgs args)
    {
        PlaybackSnapshot? snapshot = null;
        lock (_gate)
        {
            if (_disposed || _session is null) return;
            // WinRT can deliver a different wrapper for the same session, and a queued event
            // from the session we just left is harmless if it only refreshes our current one.
            var session = _session;
            var title = _current.Title;
            _current = ReadSnapshot(session, _generation, title);
            snapshot = _current;
        }
        if (snapshot is not null) RaiseChanged(snapshot);
    }

    private void OnMediaPropertiesChanged(
        GlobalSystemMediaTransportControlsSession sender,
        MediaPropertiesChangedEventArgs args)
    {
        GlobalSystemMediaTransportControlsSession session;
        long generation;
        lock (_gate)
        {
            if (_disposed || _session is null) return;
            session = _session;
            generation = _generation;
            _current = _current with { Title = null };
        }
        RequestMetadataRefresh(session, generation);
        RaiseChanged(Current);
    }

    private void RequestMetadataRefresh(GlobalSystemMediaTransportControlsSession session, long generation)
    {
        bool startWorker = false;
        long workerVersion = 0;
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(session, _session) || generation != _generation) return;
            var revision = ++_metadataRevision;
            _pendingMetadata = new MetadataRequest(session, generation, revision);
            if (!_metadataWorkerRunning)
            {
                _metadataWorkerRunning = true;
                workerVersion = ++_metadataWorkerVersion;
                startWorker = true;
            }
        }

        if (!startWorker) return;
        var worker = RefreshMetadataLoopAsync();
        lock (_gate)
        {
            // The worker may finish synchronously, or a later request may already have started a
            // newer worker. In both cases, keep the stored handle paired with its own generation.
            if (_metadataWorkerRunning && _metadataWorkerVersion == workerVersion)
                _metadataWorker = worker;
        }
    }

    private async Task RefreshMetadataLoopAsync()
    {
        while (true)
        {
            MetadataRequest? request;
            lock (_gate)
            {
                if (_disposed || _pendingMetadata is null)
                {
                    _pendingMetadata = null;
                    _metadataWorkerRunning = false;
                    _metadataWorker = null;
                    return;
                }
                request = _pendingMetadata;
                _pendingMetadata = null;
            }

            string? title = null;
            try
            {
                var properties = await request.Session.TryGetMediaPropertiesAsync();
                title = string.IsNullOrWhiteSpace(properties?.Title) ? null : properties.Title;
            }
            catch (Exception)
            {
                // A stale, stopped, or non-cooperating player may refuse metadata independently
                // of its playback controls. Keep the rest of the current snapshot usable.
            }

            PlaybackSnapshot? snapshot = null;
            lock (_gate)
            {
                if (!_disposed
                    && ReferenceEquals(_session, request.Session)
                    && _generation == request.Generation
                    && _metadataRevision == request.Revision)
                {
                    var status = _current.PlayerName is null
                        ? "Windows media controls are ready."
                        : $"Controlling { _current.PlayerName } through Windows media controls.";
                    _current = _current with { Title = title, Status = status, StatusIsRoutine = _current.PlayerName is not null };
                    snapshot = _current;
                }
            }
            if (snapshot is not null) RaiseChanged(snapshot);
        }
    }

    private static PlaybackSnapshot ReadSnapshot(
        GlobalSystemMediaTransportControlsSession session,
        long generation,
        string? title)
    {
        string appUserModelId;
        try { appUserModelId = session.SourceAppUserModelId; }
        catch (Exception) { appUserModelId = "unknown"; }

        var sessionId = $"manual:{appUserModelId}:{generation}";
        string playerName;
        try { playerName = FormatPlayerName(appUserModelId); }
        catch (Exception) { playerName = "Windows media player"; }

        try
        {
            var info = session.GetPlaybackInfo();
            var controls = info?.Controls;
            var playback = info?.PlaybackStatus switch
            {
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => MediaPlayback.Playing,
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => MediaPlayback.Paused,
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped => MediaPlayback.Stopped,
                _ => MediaPlayback.Unknown,
            };
            return new PlaybackSnapshot(
                sessionId,
                playerName,
                title,
                playback,
                controls?.IsPlayEnabled == true,
                controls?.IsPauseEnabled == true,
                controls?.IsNextEnabled == true,
                controls?.IsPreviousEnabled == true,
                $"Controlling {playerName} through Windows media controls.",
                StatusIsRoutine: true);
        }
        catch (Exception)
        {
            return new PlaybackSnapshot(sessionId, playerName, title, MediaPlayback.Unknown,
                false, false, false, false,
                $"Windows could not read playback controls for {playerName}.");
        }
    }

    private static string FormatPlayerName(string? appUserModelId)
    {
        if (string.IsNullOrWhiteSpace(appUserModelId)) return "Windows media player";
        var appName = appUserModelId[(appUserModelId.LastIndexOf('!') + 1)..];
        if (string.IsNullOrWhiteSpace(appName)
            || string.Equals(appName, "App", StringComparison.OrdinalIgnoreCase)
            || string.Equals(appName, "Application", StringComparison.OrdinalIgnoreCase))
            return appUserModelId;
        return appName;
    }

    private void UnsubscribeFromSessionLocked()
    {
        if (_session is null) return;
        _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
        _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
    }

    private void RaiseChanged(PlaybackSnapshot snapshot)
    {
        var handlers = Changed;
        if (handlers is null) return;
        foreach (EventHandler<PlaybackSnapshot> handler in handlers.GetInvocationList())
        {
            try { handler(this, snapshot); }
            catch (Exception) { /* A presentation subscriber cannot break a Windows callback. */ }
        }
    }

    private static PlaybackCommandResult Result(PlaybackCommandStatus status, string message)
        => new(status, message);

    private static string ActionName(PlaybackAction action) => action switch
    {
        PlaybackAction.Play => "Play",
        PlaybackAction.Pause => "Pause",
        PlaybackAction.Next => "Next",
        PlaybackAction.Previous => "Previous",
        _ => "Playback",
    };

    private static string UnsupportedMessage(string? playerName, PlaybackAction action)
        => $"{playerName ?? "The current Windows player"} does not support {ActionName(action)}. No command was sent.";

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Task? metadataWorker;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _pendingMetadata = null;
            metadataWorker = _metadataWorker;
            UnsubscribeFromSessionLocked();
            _session = null;
        }

        _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
        if (metadataWorker is not null)
        {
            try { await metadataWorker.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (TimeoutException) { /* Metadata has no path to update the UI after disposal. */ }
            catch (Exception) { /* Ordinary player failures do not block shutdown. */ }
        }
    }

    private sealed record MetadataRequest(
        GlobalSystemMediaTransportControlsSession Session,
        long Generation,
        long Revision);
}
