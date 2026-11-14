using AppleDeviceHub.Core.Ble;
using AppleDeviceHub.Core.Contracts;

namespace AppleDeviceHub.Core.Media;

/// <summary>
/// The system media sessions as Windows exposes them (implemented in the App over
/// GlobalSystemMediaTransportControlsSessionManager). Every member must be safe to call from
/// any thread and must not throw for ordinary failures.
/// </summary>
public interface IMediaSessionController : IAsyncDisposable
{
    /// <summary>The current session, or <see cref="MediaSnapshot.None"/>.</summary>
    MediaSnapshot Current { get; }

    /// <summary>
    /// True when a call is known to be in progress. Windows offers no general call API, so an
    /// implementation that cannot tell returns false; the policy still never resumes across
    /// a session change, which covers most call apps taking over media.
    /// </summary>
    bool CallActive { get; }

    /// <summary>Raised when the current session or its playback state changes. Any thread.</summary>
    event EventHandler<MediaSnapshot>? Changed;

    /// <summary>Pause exactly this session. False if it is no longer current or Windows refused.</summary>
    Task<bool> TryPauseAsync(string sessionId, CancellationToken cancellationToken);

    /// <summary>Play exactly this session. False if it is no longer current or Windows refused.</summary>
    Task<bool> TryPlayAsync(string sessionId, CancellationToken cancellationToken);
}

/// <summary>
/// Connects a device transport, <see cref="EarMediaPolicy"/> and the media controller. Policy
/// calls are serialised under one lock; media commands run outside it. After
/// <see cref="DisposeAsync"/> no further media command is issued.
/// </summary>
public sealed class EarMediaCoordinator : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly EarMediaPolicy _policy;
    private readonly IMediaSessionController? _media;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Action<string>? _diagnosticLog;
    private readonly TaskScheduler _commandScheduler;
    private readonly CancellationTokenSource _disposed = new();
    private IAirPodsTransport? _transport;
    private BroadcastObservationTransport? _broadcastSightingTransport;
    private Task _pending = Task.CompletedTask;

    public EarMediaCoordinator(
        EarMediaPolicy policy,
        IMediaSessionController? media,
        Func<DateTimeOffset>? clock = null,
        Action<string>? diagnosticLog = null)
        : this(policy, media, clock, diagnosticLog, TaskScheduler.Default)
    {
    }

    internal EarMediaCoordinator(
        EarMediaPolicy policy,
        IMediaSessionController? media,
        Func<DateTimeOffset>? clock,
        Action<string>? diagnosticLog,
        TaskScheduler commandScheduler)
    {
        _policy = policy;
        _media = media;
        _clock = clock ?? (() => DateTimeOffset.Now);
        _diagnosticLog = diagnosticLog;
        _commandScheduler = commandScheduler;
        if (_media is not null) _media.Changed += OnMediaChanged;
    }

    /// <summary>The last decision, for diagnostics and the settings page.</summary>
    public EarMediaAction LastAction { get; private set; } = EarMediaAction.NoneBecause("no state yet");

    /// <summary>Completes when the last media command issued has finished (tests, shutdown).</summary>
    public Task Pending { get { lock (_gate) return _pending; } }

    public bool Enabled
    {
        get { lock (_gate) return _policy.Enabled; }
        set
        {
            lock (_gate) _policy.Enabled = value;
        }
    }

    /// <summary>
    /// Gate and stability filter for ear evidence from nearby broadcasts (owner decision
    /// 2026-10-20). Its <see cref="BroadcastEarSource.Enabled"/> is the "Ear detection (nearby
    /// estimate)" setting, off by default; use <see cref="BroadcastEarDetection"/> to change it.
    /// </summary>
    public BroadcastEarSource BroadcastEar { get; } = new();

    /// <summary>The "Ear detection (nearby estimate)" setting. Off by default; turning it off forgets any pending resume.</summary>
    public bool BroadcastEarDetection
    {
        get { lock (_gate) return BroadcastEar.Enabled; }
        set
        {
            lock (_gate)
            {
                BroadcastEar.Enabled = value;
                if (!value) _policy.Forget();
            }
        }
    }

    /// <summary>
    /// Identity confidence of the broadcast source for the selected paired AirPods
    /// (<c>BroadcastObservationTransport.IdentityConfidence</c>). Null means Unknown: never acts.
    /// </summary>
    public Func<IdentityConfidence>? BroadcastIdentity { get; set; }

    /// <summary>Whether Windows reports the selected AirPods connected. Null means not connected: never acts.</summary>
    public Func<bool>? WindowsConnected { get; set; }

    /// <summary>
    /// Raised (outside the lock, on the thread that delivered the state) with every gate result for a
    /// broadcast state while <see cref="BroadcastEarDetection"/> is on. The output-switch adapter
    /// listens to this so it uses exactly the same gated, stable evidence.
    /// </summary>
    public event EventHandler<BroadcastEarResult>? BroadcastEarEvaluated;

    /// <summary>Windows connected/disconnected the AirPods: a disconnect forgets pending resumes and stability.</summary>
    public void NotifyWindowsConnection(bool connected)
    {
        if (connected) return;
        lock (_gate)
        {
            BroadcastEar.Reset();
            _policy.Forget();
        }
        BroadcastEarEvaluated?.Invoke(this, BroadcastEarResult.Rejected("AirPods not connected in Windows"));
    }

    /// <summary>Follow a transport (or none). Switching transports forgets any pending resume.</summary>
    public void Attach(IAirPodsTransport? transport)
    {
        lock (_gate)
        {
            if (_transport is not null) _transport.StateChanged -= OnStateChanged;
            if (_broadcastSightingTransport is not null)
                _broadcastSightingTransport.EarSightingObserved -= OnBroadcastSightingObserved;
            _transport = transport;
            _broadcastSightingTransport = transport as BroadcastObservationTransport;
            _policy.Forget();
            BroadcastEar.Reset();
            if (transport is not null && !_disposed.IsCancellationRequested)
            {
                transport.StateChanged += OnStateChanged;
                if (_broadcastSightingTransport is not null)
                    _broadcastSightingTransport.EarSightingObserved += OnBroadcastSightingObserved;
            }
        }
    }

    private void OnBroadcastSightingObserved(object? sender, DeviceState state)
        => OnStateChanged(sender, state);

    /// <summary>Call on sleep, resume from sleep, or engine restart: never resume across them.</summary>
    public void Forget()
    {
        lock (_gate)
        {
            _policy.Forget();
            BroadcastEar.Reset();
        }
    }

    private void OnMediaChanged(object? sender, MediaSnapshot snapshot)
    {
        lock (_gate)
        {
            if (_disposed.IsCancellationRequested || _media is null) return;
            _policy.OnMediaChanged(snapshot, _clock());
        }
    }

    private void OnStateChanged(object? sender, DeviceState state)
    {
        BroadcastEarResult? broadcast = null;
        lock (_gate)
        {
            if (_disposed.IsCancellationRequested || !ReferenceEquals(sender, _transport)) return;
            var now = _clock();
            EarMediaAction action;
            if (state.Origin == DataOrigin.Broadcast && BroadcastEar.Enabled)
            {
                var identity = SafeIdentity();
                var connected = SafeConnected();
                var result = BroadcastEar.Evaluate(state, identity, connected, now);
                broadcast = result;
                action = result.Verdict switch
                {
                    BroadcastEarVerdict.Stable when _media is not null => _policy.OnBroadcastEar(state.DeviceId, result.Reading!, _media.Current, _media.CallActive, now),
                    BroadcastEarVerdict.Stable => EarMediaAction.NoneBecause("Windows media sessions are unavailable; ear evidence remains available for output switching."),
                    BroadcastEarVerdict.Pending => EarMediaAction.NoneBecause(result.Reason),
                    _ => _policy.OnBroadcastUnavailable(result.Reason),
                };
            }
            else
            {
                action = _media is null
                    ? EarMediaAction.NoneBecause("Windows media sessions are unavailable.")
                    : _policy.OnDeviceState(state, _media.Current, _media.CallActive, now);
            }
            LastAction = action;
            if (_media is not null && action.Kind != EarMediaActionKind.None && action.SessionId is not null)
            {
                var epoch = _policy.Epoch; // the decision is valid only while this is unchanged
                _pending = _pending.ContinueWith(_ => RunAsync(action, epoch), CancellationToken.None,
                    TaskContinuationOptions.None, _commandScheduler).Unwrap();
            }
        }
        if (broadcast is { } result2) BroadcastEarEvaluated?.Invoke(this, result2);
    }

    private IdentityConfidence SafeIdentity()
    {
        try { return BroadcastIdentity?.Invoke() ?? IdentityConfidence.Unknown; }
        catch (Exception) { return IdentityConfidence.Unknown; }
    }

    private bool SafeConnected()
    {
        try { return WindowsConnected?.Invoke() ?? false; }
        catch (Exception) { return false; }
    }

    private async Task RunAsync(EarMediaAction action, long epoch)
    {
        var media = _media;
        if (media is null) return;
        var sessionId = action.SessionId!;
        var isPause = action.Kind == EarMediaActionKind.Pause;
        CancellationToken token;
        lock (_gate)
        {
            // Revalidate at dispatch: anything that invalidated the decision while it waited its
            // turn (Forget on sleep, a transport/device/setting change, unusable ear data, a call,
            // the user acting on the media) drops it, whether it is a Pause or a Resume.
            if (_disposed.IsCancellationRequested || _policy.Epoch != epoch)
            {
                if (isPause && _policy.Epoch == epoch) _policy.PauseFailed();
                return;
            }
            if (isPause && !_policy.IsPauseStillWanted(sessionId))
            {
                // Abandoned or already (wrongly) settled before sending: never keep a claim for a
                // pause that Clasp did not send.
                if (_policy.Epoch == epoch) _policy.PauseFailed();
                return;
            }
            var current = media.Current;
            var expected = isPause ? MediaPlayback.Playing : MediaPlayback.Paused;
            if (current.SessionId != sessionId || current.Playback != expected)
            {
                if (isPause) _policy.PauseFailed();
                return;
            }
            token = _disposed.Token;
            if (isPause) _policy.PauseSending(sessionId);
        }

        Log($"origin=automatic event=command-dispatch action={action.Kind} sessionRef={MediaDiagnosticIdentity.SessionReference(sessionId)} reason={action.Reason}");
        bool ok;
        try
        {
            ok = isPause
                ? await media.TryPauseAsync(sessionId, token).ConfigureAwait(false)
                : await media.TryPlayAsync(sessionId, token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Any failure of the media call is a failed action; it must never fault the queue
            // (which would make DisposeAsync rethrow) (EF-IR review F6).
            ok = false;
        }

        if (!isPause) return;
        lock (_gate)
        {
            // A refused/failed pause always drops the claim it sent, even if later ear changes
            // moved the epoch on; otherwise a manual pause could later confirm that claim.
            if (!ok)
            {
                _policy.SentPauseFailed(sessionId);
                return;
            }
            // A successful completion from an invalidated decision changes nothing.
            if (_policy.Epoch != epoch) return;
            _policy.PauseIssued(sessionId);
        }
    }

    private void Log(string message)
    {
        if (_diagnosticLog is null) return;
        try { _diagnosticLog($"at={_clock():O} {message}"); }
        catch (Exception) { /* Diagnostics must never interfere with ear policy or media commands. */ }
    }

    public async ValueTask DisposeAsync()
    {
        Task pending;
        lock (_gate)
        {
            if (_disposed.IsCancellationRequested) return;
            _disposed.Cancel();
            if (_transport is not null) _transport.StateChanged -= OnStateChanged;
            _transport = null;
            if (_broadcastSightingTransport is not null)
                _broadcastSightingTransport.EarSightingObserved -= OnBroadcastSightingObserved;
            _broadcastSightingTransport = null;
            _policy.Forget();
            pending = _pending;
        }
        if (_media is not null) _media.Changed -= OnMediaChanged;
        try { await pending.ConfigureAwait(false); } catch (Exception) { /* RunAsync never faults; defensive */ }
        _disposed.Dispose();
    }
}
