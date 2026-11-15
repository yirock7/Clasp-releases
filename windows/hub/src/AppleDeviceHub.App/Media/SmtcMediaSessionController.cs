using AppleDeviceHub.Core.Media;
using Windows.Media.Control;

namespace AppleDeviceHub.App.Media;

/// <summary>
/// <see cref="IMediaSessionController"/> over Windows' System Media Transport Controls. Only
/// created while the user's "pause media when AirPods are removed" setting is on.
/// <para>
/// Session ids are "app id#generation", where the generation increases on every
/// CurrentSessionChanged. Any session change therefore produces a new id, so a pause made on
/// one session can never be resumed on another, even from the same player.
/// </para>
/// It cannot detect calls (Windows has no general API), so <see cref="CallActive"/> is false;
/// see <see cref="IMediaSessionController.CallActive"/>.
/// </summary>
internal sealed class SmtcMediaSessionController : IMediaSessionController
{
    private readonly object _gate = new();
    private readonly GlobalSystemMediaTransportControlsSessionManager _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private long _generation;
    private MediaSnapshot _current = MediaSnapshot.None;
    private bool _disposed;

    private SmtcMediaSessionController(GlobalSystemMediaTransportControlsSessionManager manager)
    {
        _manager = manager;
        _manager.CurrentSessionChanged += OnCurrentSessionChanged;
        Track(_manager.GetCurrentSession());
    }

    /// <summary>Null when Windows refuses or the API is unavailable; the feature then stays inactive.</summary>
    public static async Task<SmtcMediaSessionController?> TryCreateAsync()
    {
        try
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            return manager is null ? null : new SmtcMediaSessionController(manager);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    public MediaSnapshot Current { get { lock (_gate) return _current; } }

    public bool CallActive => false;

    public event EventHandler<MediaSnapshot>? Changed;

    public Task<bool> TryPauseAsync(string sessionId, CancellationToken cancellationToken)
        => SendAsync(sessionId, static s => s.TryPauseAsync(), cancellationToken);

    public Task<bool> TryPlayAsync(string sessionId, CancellationToken cancellationToken)
        => SendAsync(sessionId, static s => s.TryPlayAsync(), cancellationToken);

    private async Task<bool> SendAsync(
        string sessionId,
        Func<GlobalSystemMediaTransportControlsSession, Windows.Foundation.IAsyncOperation<bool>> send,
        CancellationToken cancellationToken)
    {
        GlobalSystemMediaTransportControlsSession? session;
        lock (_gate)
        {
            if (_disposed || _current.SessionId != sessionId) return false;
            session = _session;
        }
        if (session is null) return false;
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await send(session);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            return false;
        }
    }

    private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
        => Track(sender.GetCurrentSession());

    private void Track(GlobalSystemMediaTransportControlsSession? session)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_session is not null) _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            _session = session;
            _generation++;
            if (session is not null) session.PlaybackInfoChanged += OnPlaybackInfoChanged;
        }
        Publish();
    }

    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
        // Re-read the tracked session rather than trusting the sender: WinRT may hand a different
        // wrapper object for the same session, and a late event from a session we already left
        // then only causes a harmless re-read of the current one.
        => Publish();

    private void Publish()
    {
        MediaSnapshot snapshot;
        lock (_gate)
        {
            if (_disposed) return;
            snapshot = Read(_session, _generation);
            if (snapshot == _current) return;
            _current = snapshot;
        }
        Changed?.Invoke(this, snapshot);
    }

    private static MediaSnapshot Read(GlobalSystemMediaTransportControlsSession? session, long generation)
    {
        if (session is null) return MediaSnapshot.None;
        try
        {
            var playback = session.GetPlaybackInfo()?.PlaybackStatus switch
            {
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => MediaPlayback.Playing,
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => MediaPlayback.Paused,
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped => MediaPlayback.Stopped,
                _ => MediaPlayback.Unknown,
            };
            return new MediaSnapshot($"{session.SourceAppUserModelId}#{generation}", playback);
        }
        catch (Exception)
        {
            // Never let a WinRT failure escape into the event callback; an unknown snapshot
            // makes the policy forget any pending resume, which is the safe outcome.
            return new MediaSnapshot($"unknown#{generation}", MediaPlayback.Unknown);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            if (_session is not null) _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            _session = null;
        }
        _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
        return ValueTask.CompletedTask;
    }
}
