using AppleDeviceHub.Core.Contracts;

namespace AppleDeviceHub.Core.Media;

/// <summary>Playback state of the system's current media session, as Windows reports it.</summary>
public enum MediaPlayback
{
    Unknown = 0,
    Playing = 1,
    Paused = 2,
    Stopped = 3,
}

/// <summary>
/// The system's current media session. <see cref="SessionId"/> must change whenever the
/// session is a different one (another player, or the same player's session recreated); null
/// means there is no current session.
/// </summary>
public sealed record MediaSnapshot(string? SessionId, MediaPlayback Playback)
{
    public static MediaSnapshot None { get; } = new(null, MediaPlayback.Unknown);
}

/// <summary>Which ear change pauses playback.</summary>
public enum EarPauseTrigger
{
    /// <summary>Pause only when no bud is left in an ear (the conservative default).</summary>
    AllRemoved = 0,

    /// <summary>Pause when the number of buds in an ear drops (Apple-like); resume when it is back.</summary>
    AnyRemoved = 1,
}

public enum EarMediaActionKind
{
    None = 0,
    Pause = 1,
    Resume = 2,
}

/// <summary>What the caller should do. <see cref="SessionId"/> names the only session it may act on.</summary>
public readonly record struct EarMediaAction(EarMediaActionKind Kind, string? SessionId, string Reason)
{
    public static EarMediaAction NoneBecause(string reason) => new(EarMediaActionKind.None, null, reason);
}

/// <summary>
/// Pure decision logic for "pause when I take my AirPods out, resume when I put them back".
/// Opt-in (<see cref="Enabled"/> is false by default). <see cref="OnDeviceState"/> only uses ear
/// placement reported over a live AirPods control session (<see cref="DataOrigin.Hardware"/>); a
/// broadcast state passed to it is never used. Broadcast-derived ear evidence (owner decision
/// 2026-10-20) reaches the policy only through <see cref="OnBroadcastEar"/>, with a reading that
/// <see cref="Ble.BroadcastEarSource"/> already gated (setting on, identity Probable or better,
/// fresh, stable, AirPods connected in Windows).
/// <para>
/// Resume is allowed only for the exact media session this policy paused, only after Windows
/// confirmed that pause, and only while nothing else has happened to that session since: a
/// manual play/pause, a player change, a call, a disconnect, a device change, losing the
/// session, turning the setting off or the resume window expiring all forget the pause.
/// </para>
/// Not thread-safe: call it from one place (the UI thread or a single serial queue).
/// </summary>
public sealed class EarMediaPolicy
{
    private const int UnknownCount = -1;

    private string? _deviceId;
    private int _lastInEar = UnknownCount;
    private DateTimeOffset? _lastEarAt;
    private (PodPlacement, PodPlacement)? _lastPlacement;
    private Claim? _claim;

    /// <summary>User setting. Turning it off forgets any pending resume.</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            if (!value) Forget();
        }
    }

    private bool _enabled;

    public EarPauseTrigger Trigger { get; set; } = EarPauseTrigger.AllRemoved;

    /// <summary>How long after Clasp's pause a resume is still allowed.</summary>
    public TimeSpan ResumeWindow { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How soon after Clasp decides to pause Windows must report that session as Paused for the
    /// pause to count as Clasp's. A later Paused report is treated as the user's own pause.
    /// </summary>
    public TimeSpan ConfirmWindow { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// A new ear report is acted on only if it is at most this old when evaluated. AirPods send
    /// ear reports only when placement changes, so this applies to the changed report, never to
    /// the earlier baseline: buds worn for hours and then removed still pause (review EF-IR-03).
    /// </summary>
    public TimeSpan MaxEarEventAge { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Clock slack allowed for a report that appears to come from the future.</summary>
    public TimeSpan FutureTolerance { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>True while a pause by Clasp may still be resumed (for UI/diagnostics).</summary>
    public bool HasPendingResume => _claim is { Confirmed: true };

    /// <summary>
    /// Evaluate a new device state. <paramref name="media"/> is the current system media session
    /// and <paramref name="callActive"/> whether a call is known to be in progress. Windows has no
    /// general call API, so callers pass what they can detect; see <see cref="IMediaSessionController.CallActive"/>.
    /// If the result is <see cref="EarMediaActionKind.Pause"/>, the caller must report the
    /// outcome through <see cref="PauseIssued"/> or <see cref="PauseFailed"/>.
    /// </summary>
    public EarMediaAction OnDeviceState(DeviceState state, MediaSnapshot media, bool callActive, DateTimeOffset now)
    {
        if (!_enabled) return EarMediaAction.NoneBecause("disabled");
        BeginDevice(state.DeviceId);

        var inEar = CountInEar(state);
        if (inEar == UnknownCount)
        {
            // Disconnect, broadcast-only data, transitional or missing placement: never act,
            // and never resume a pause across such a gap.
            Forget();
            ResetBaseline();
            return EarMediaAction.NoneBecause("ear placement unavailable");
        }

        // Freshness and order of the ear evidence (EF-IR-03). The event time is the newest of
        // the two sides' own observation times; other updates (battery, mode) never refresh it.
        var earAt = Max(state.Ears.Primary.ObservedAt!.Value, state.Ears.Secondary.ObservedAt!.Value);
        var placement = (state.Ears.Primary.Value!.Value, state.Ears.Secondary.Value!.Value);
        return Decide(inEar, placement, earAt, media, callActive, now);
    }

    /// <summary>
    /// Evaluate a stable ear reading derived from nearby broadcasts. Only
    /// <see cref="Ble.BroadcastEarSource"/> may produce <paramref name="reading"/>, and only after
    /// its gate passed; everything after that (freshness, order, call, pause/resume claims) is the
    /// same as for a live control session.
    /// </summary>
    public EarMediaAction OnBroadcastEar(string deviceId, Ble.BroadcastEarReading reading, MediaSnapshot media, bool callActive, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(reading);
        if (!_enabled) return EarMediaAction.NoneBecause("disabled");
        BeginDevice(deviceId);

        var inEar = (reading.Primary == PodPlacement.InEar ? 1 : 0) + (reading.Secondary == PodPlacement.InEar ? 1 : 0);
        return Decide(inEar, (reading.Primary, reading.Secondary), reading.ObservedAt, media, callActive, now);
    }

    /// <summary>
    /// Broadcast ear evidence stopped being usable (identity lost or ambiguous, stale, setting off,
    /// AirPods not connected). Same effect as unusable control-session data: nothing is resumed
    /// across the gap.
    /// </summary>
    public EarMediaAction OnBroadcastUnavailable(string reason)
    {
        if (!_enabled) return EarMediaAction.NoneBecause("disabled");
        Forget();
        ResetBaseline();
        return EarMediaAction.NoneBecause(reason);
    }

    private void BeginDevice(string deviceId)
    {
        if (_deviceId is not null && _deviceId != deviceId)
        {
            Forget();
            ResetBaseline();
        }
        _deviceId = deviceId;
    }

    private EarMediaAction Decide(int inEar, (PodPlacement, PodPlacement) placement, DateTimeOffset earAt,
        MediaSnapshot media, bool callActive, DateTimeOffset now)
    {
        if (earAt - now > FutureTolerance)
        {
            Forget();
            ResetBaseline();
            return EarMediaAction.NoneBecause("ear report is dated in the future");
        }

        var previous = _lastInEar;
        bool changed = _lastPlacement != placement;
        if (_lastEarAt is { } lastAt && earAt < lastAt)
        {
            // Older than evidence already used: a delayed or reordered report.
            if (changed)
            {
                Forget();
                ResetBaseline();
                return EarMediaAction.NoneBecause("ear report out of order");
            }
            previous = inEar; // the same placement again: nothing happened to the ears
        }
        else if (!changed)
        {
            // The same ear evidence re-sent with another update (battery, mode): not an ear event.
            previous = inEar;
        }
        else
        {
            _lastEarAt = earAt;
            _lastPlacement = placement;
            _lastInEar = inEar;
            // Every accepted ear change supersedes actions decided on earlier ear evidence: a
            // Resume still queued when the buds come out again must not play (EF-IR review F1).
            Epoch++;
            if (previous != UnknownCount && now - earAt > MaxEarEventAge)
            {
                // A delayed report: the change happened too long ago to act on now.
                Forget();
                ResetBaseline();
                return EarMediaAction.NoneBecause("ear report too old to act on");
            }
        }

        if (callActive)
        {
            Forget();
            return EarMediaAction.NoneBecause("call in progress");
        }

        // A pause Windows never confirmed in time was not Clasp's (the player ignored it).
        if (_claim is { Confirmed: false } pending && now - pending.PausedAt > ConfirmWindow) Invalidate();

        if (previous == UnknownCount || previous == inEar) return EarMediaAction.NoneBecause("no transition");

        if (inEar < previous)
        {
            var removed = Trigger == EarPauseTrigger.AnyRemoved || inEar == 0;
            if (!removed) return EarMediaAction.NoneBecause("a bud is still in an ear");
            if (media.SessionId is null || media.Playback != MediaPlayback.Playing)
                return EarMediaAction.NoneBecause("nothing playing");

            _claim = new Claim(media.SessionId, previous, now, Confirmed: false, Sent: false);
            return new EarMediaAction(EarMediaActionKind.Pause, media.SessionId, "removed from ear");
        }

        // inEar > previous: put back in. An unconfirmed pause is abandoned (and a queued pause
        // request must then not be sent; see IsPauseStillWanted).
        if (_claim is { Confirmed: false }) Invalidate();
        if (_claim is not { Confirmed: true } claim) return EarMediaAction.NoneBecause("Clasp did not pause anything");

        var restored = Trigger == EarPauseTrigger.AllRemoved ? inEar >= 1 : inEar >= claim.InEarBefore;
        if (!restored) return EarMediaAction.NoneBecause("not all buds are back");

        _claim = null;
        if (now - claim.PausedAt > ResumeWindow) return EarMediaAction.NoneBecause("resume window expired");
        if (media.SessionId != claim.SessionId) return EarMediaAction.NoneBecause("media session changed");
        if (media.Playback != MediaPlayback.Paused) return EarMediaAction.NoneBecause("session is no longer paused");

        return new EarMediaAction(EarMediaActionKind.Resume, claim.SessionId, "returned to ear");
    }

    /// <summary>
    /// Just before sending a queued pause: is it still wanted? False once the buds went back in,
    /// the session changed, or anything else abandoned the pause.
    /// </summary>
    public bool IsPauseStillWanted(string sessionId) => _claim is { Confirmed: false } c && c.SessionId == sessionId;

    /// <summary>
    /// The caller is about to send the pause for <paramref name="sessionId"/>. Only a Paused
    /// report after this can confirm the claim; one before it is the user's own pause.
    /// </summary>
    public void PauseSending(string sessionId)
    {
        if (_claim is { Confirmed: false } c && c.SessionId == sessionId) _claim = c with { Sent = true };
    }

    /// <summary>The caller asked Windows to pause <paramref name="sessionId"/>.</summary>
    public void PauseIssued(string sessionId)
    {
        // Confirmation comes from the next media change, not from the request succeeding.
        if (_claim is { } c && c.SessionId != sessionId) Invalidate();
        else if (_claim is { Sent: false } unsent) _claim = unsent with { Sent = true };
    }

    /// <summary>The pause request failed or was not attempted; nothing may be resumed.</summary>
    public void PauseFailed() => Invalidate();

    /// <summary>
    /// The pause Clasp sent for <paramref name="sessionId"/> was refused or failed. Its claim is
    /// dropped whatever has happened since (even if a Paused report looked like confirmation,
    /// which then was not Clasp's doing), so a later manual pause is never resumed (EF-IR recheck).
    /// </summary>
    public void SentPauseFailed(string sessionId)
    {
        if (_claim is { Sent: true } c && c.SessionId == sessionId) Invalidate();
    }

    /// <summary>
    /// The system media session changed. The first change to Paused on the claimed session
    /// confirms Clasp's pause if it arrives within <see cref="ConfirmWindow"/>; any other change
    /// means someone else acted, so the pause is forgotten and Clasp will not resume.
    /// </summary>
    public void OnMediaChanged(MediaSnapshot media, DateTimeOffset now)
    {
        if (_claim is not { } claim)
        {
            // No pause to track, but a Resume may already be queued: the user acting on the
            // session in the meantime (even play-then-pause) must cancel it.
            Epoch++;
            return;
        }

        if (!claim.Confirmed && !claim.Sent)
        {
            // Clasp has not sent its pause yet: any change, including a Paused report, is the
            // user's (or the player's) own doing, never confirmation (EF-IR review F2).
            Invalidate();
            return;
        }

        if (!claim.Confirmed && media.SessionId == claim.SessionId && media.Playback == MediaPlayback.Paused)
        {
            if (now - claim.PausedAt > ConfirmWindow)
            {
                Invalidate(); // too late to be Clasp's pause: the user paused it
                return;
            }
            _claim = claim with { Confirmed = true };
            return;
        }

        if (claim.Confirmed && media.SessionId == claim.SessionId && media.Playback == MediaPlayback.Paused)
            return; // repeated report of the same state (metadata/timeline updates)

        Invalidate();
    }

    private void ResetBaseline()
    {
        _lastInEar = UnknownCount;
        _lastEarAt = null;
        _lastPlacement = null;
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    /// <summary>Forget any pending resume (also used on sleep, engine restart or transport change).</summary>
    public void Forget() => Invalidate();

    /// <summary>
    /// Increases whenever earlier decisions stop being valid: Forget (sleep, transport change,
    /// setting off), a device change, unusable ear data, a call, the user acting on the media
    /// session, or a failed/abandoned pause. A caller that queued an action must drop it, and
    /// ignore its completion, if this changed after the decision (review EF-IR-02).
    /// </summary>
    public long Epoch { get; private set; }

    private void Invalidate()
    {
        _claim = null;
        Epoch++;
    }

    /// <summary>
    /// Buds in an ear, or <see cref="UnknownCount"/> when the state cannot be trusted for media
    /// decisions: not a live hardware control session, no ear capability, either side unknown,
    /// or either side reported as disconnected from the other.
    /// </summary>
    private static int CountInEar(DeviceState state)
    {
        if (state.Origin != DataOrigin.Hardware) return UnknownCount;
        if (!state.Supports(DeviceCapabilities.EarDetection)) return UnknownCount;
        if (state.Link.ProtocolSession.Value != true) return UnknownCount;

        var a = state.Ears.Primary;
        var b = state.Ears.Secondary;
        if (!a.HasValue || !b.HasValue) return UnknownCount;
        if (a.Value == PodPlacement.Disconnected || b.Value == PodPlacement.Disconnected) return UnknownCount;

        return (a.Value == PodPlacement.InEar ? 1 : 0) + (b.Value == PodPlacement.InEar ? 1 : 0);
    }

    private sealed record Claim(string SessionId, int InEarBefore, DateTimeOffset PausedAt, bool Confirmed, bool Sent);
}
