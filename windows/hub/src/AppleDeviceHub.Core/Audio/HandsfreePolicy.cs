namespace AppleDeviceHub.Core.Audio;

/// <summary>How Clasp manages the AirPods' Handsfree (call) Bluetooth service.</summary>
public enum HandsfreeMode
{
    /// <summary>Windows default: Clasp never changes the Handsfree service.</summary>
    AlwaysOn,

    /// <summary>Handsfree stays off: high-quality audio, AirPods microphone unavailable.</summary>
    AlwaysOff,

    /// <summary>Handsfree is off normally and on only while some app is using a microphone.</summary>
    Smart,
}

/// <summary>What is known about the Handsfree service right now. Unknown stays unknown.</summary>
public enum HandsfreeState
{
    Unknown,
    On,
    Off,
}

/// <summary>An action the caller should perform on the Handsfree service.</summary>
public enum HandsfreeAction
{
    None,
    Enable,
    Disable,
}

/// <summary>The outcome of an attempted change, reported back to <see cref="HandsfreePolicy"/>.</summary>
public enum HandsfreeOutcome
{
    Succeeded,
    AccessDenied,
    DeviceNotFound,
    Failed,
}

/// <summary>
/// A policy decision. <see cref="Reason"/> is plain language suitable for a status line.
/// <see cref="RecheckAt"/> is when the caller should evaluate again even if no input changes
/// (the end of a grace period); null when nothing time-based is pending.
/// </summary>
public sealed record HandsfreeDecision(HandsfreeAction Action, string Reason, DateTimeOffset? RecheckAt = null);

/// <summary>
/// Pure decision logic for Smart Handsfree. Time comes from the <c>now</c> parameter and the
/// policy performs no I/O. Call <see cref="Evaluate"/> whenever an input changes and at
/// <see cref="HandsfreeDecision.RecheckAt"/>; call <see cref="ReportOutcome"/> after acting.
/// Not thread-safe: use from one thread or lock externally.
/// </summary>
/// <remarks>
/// Rules: Smart enables immediately when microphone use starts and disables only after the
/// microphone has been idle for the grace period. Each desired change is attempted at most
/// once per transition (a failed attempt is not retried until the desired state flips or the
/// AirPods reconnect). After an access-denied result the policy stops acting for the session.
/// Nothing is ever attempted while the AirPods are disconnected. AlwaysOn never acts, except
/// for one Enable to undo a Disable that Clasp itself performed earlier.
/// </remarks>
public sealed class HandsfreePolicy
{
    /// <summary>Default time the microphone must stay idle before Handsfree is turned off again.</summary>
    public static readonly TimeSpan DefaultGracePeriod = TimeSpan.FromSeconds(20);

    private readonly TimeSpan _grace;
    private HandsfreeState _assumed = HandsfreeState.Unknown;
    private HandsfreeAction _attempted = HandsfreeAction.None;
    private bool _disabledByUs;
    private bool _micWasInUse;
    private DateTimeOffset? _idleSince;
    private bool _blocked;
    private string? _blockedReason;

    public HandsfreePolicy(TimeSpan? gracePeriod = null)
    {
        _grace = gracePeriod ?? DefaultGracePeriod;
        if (_grace < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(gracePeriod));
    }

    /// <summary>True once an access-denied result stopped all further attempts this session.</summary>
    public bool IsBlocked => _blocked;

    /// <summary>Plain-language reason the policy stopped acting, or null.</summary>
    public string? BlockedReason => _blockedReason;

    /// <summary>Clears an access-denied block (for example after the user explicitly retries).</summary>
    public void ResetBlock()
    {
        _blocked = false;
        _blockedReason = null;
        _attempted = HandsfreeAction.None;
    }

    /// <summary>Forgets state tied to one selected AirPods device or connection session.</summary>
    public void Reset()
    {
        _assumed = HandsfreeState.Unknown;
        _attempted = HandsfreeAction.None;
        _disabledByUs = false;
        _micWasInUse = false;
        _idleSince = null;
        _blocked = false;
        _blockedReason = null;
    }

    /// <summary>Decides what, if anything, to do now.</summary>
    public HandsfreeDecision Evaluate(
        HandsfreeMode mode, bool micInUse, bool airPodsConnected, HandsfreeState knownState, DateTimeOffset now)
    {
        TrackMic(micInUse, now);

        if (!airPodsConnected)
        {
            // A new connection gets a fresh single attempt.
            _attempted = HandsfreeAction.None;
            return new(HandsfreeAction.None, "AirPods are not connected.");
        }

        if (knownState != HandsfreeState.Unknown) _assumed = knownState;
        var effective = _assumed;

        if (mode == HandsfreeMode.AlwaysOn)
        {
            if (knownState == HandsfreeState.On) _disabledByUs = false;
            if (!_disabledByUs)
            {
                _attempted = HandsfreeAction.None;
                return new(HandsfreeAction.None, "Handsfree is left to Windows.");
            }
            return Choose(HandsfreeAction.Enable, effective, "Restoring the Windows default (Handsfree on).", null, hold: false);
        }

        if (mode == HandsfreeMode.AlwaysOff)
            return Choose(HandsfreeAction.Disable, effective, "Handsfree is set to always off.", null, hold: false);

        // Smart.
        if (micInUse)
            return Choose(HandsfreeAction.Enable, effective, "An app is using a microphone.", null, hold: false);

        if (_idleSince is { } since && now - since < _grace)
            return Choose(HandsfreeAction.Enable, effective, "Waiting briefly before turning Handsfree off.", since + _grace, hold: true);

        return Choose(HandsfreeAction.Disable, effective, "No app is using a microphone.", null, hold: false);
    }

    /// <summary>Records the result of acting on a decision so the policy tracks the service state.</summary>
    public void ReportOutcome(HandsfreeAction action, HandsfreeOutcome outcome)
    {
        if (action == HandsfreeAction.None) return;
        switch (outcome)
        {
            case HandsfreeOutcome.Succeeded:
                _assumed = action == HandsfreeAction.Enable ? HandsfreeState.On : HandsfreeState.Off;
                _disabledByUs = action == HandsfreeAction.Disable;
                break;
            case HandsfreeOutcome.AccessDenied:
                _blocked = true;
                _blockedReason = "Windows did not allow Clasp to change Handsfree without administrator rights.";
                break;
            default:
                // Not retried until the desired state flips or the AirPods reconnect.
                break;
        }
    }

    private void TrackMic(bool micInUse, DateTimeOffset now)
    {
        if (micInUse) _idleSince = null;
        else if (_micWasInUse) _idleSince = now;
        _micWasInUse = micInUse;
    }

    private HandsfreeDecision Choose(
        HandsfreeAction target, HandsfreeState effective, string reason, DateTimeOffset? recheck, bool hold)
    {
        if (_blocked) return new(HandsfreeAction.None, _blockedReason ?? "Handsfree changes are unavailable.");

        var targetState = target == HandsfreeAction.Enable ? HandsfreeState.On : HandsfreeState.Off;
        if (effective == targetState)
        {
            _attempted = HandsfreeAction.None;
            return new(HandsfreeAction.None, reason, recheck);
        }

        // During the grace hold, "keep on" never turns anything on by itself when the service
        // is known to be off (the enable already failed or was never wanted).
        if (hold && effective == HandsfreeState.Off)
            return new(HandsfreeAction.None, reason, recheck);

        if (_attempted == target)
            return new(HandsfreeAction.None, reason + " (already attempted)", recheck);

        _attempted = target;
        return new(target, reason, recheck);
    }
}
