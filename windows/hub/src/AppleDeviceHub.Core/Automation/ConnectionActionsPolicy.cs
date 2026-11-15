using AppleDeviceHub.Core.Connection;

namespace AppleDeviceHub.Core.Automation;

/// <summary>One step Clasp performs after the selected AirPods connect or disconnect.</summary>
public enum ConnectionActionKind
{
    /// <summary>Make <see cref="ConnectionAction.EndpointId"/> the Windows default output.</summary>
    SwitchOutput = 0,

    /// <summary>Set the master volume of <see cref="ConnectionAction.EndpointId"/>.</summary>
    SetVolume = 1,

    /// <summary>
    /// Ask the current Windows media player to play. <see cref="ConnectionAction.EndpointId"/> is the output
    /// the AirPods were made (or already were) the default on; the runner skips Play if it no longer is.
    /// </summary>
    Play = 2,

    /// <summary>Ask the current Windows media player to pause.</summary>
    Pause = 3,
}

/// <summary>Where a connect request came from. Decides what the connection may do afterwards.</summary>
public enum ConnectionOrigin
{
    /// <summary>No Clasp request: Windows or the AirPods connected on their own.</summary>
    None = 0,

    /// <summary>The user clicked Connect inside Clasp.</summary>
    User = 1,

    /// <summary>The user pressed one of Clasp's own global shortcuts.</summary>
    Hotkey = 2,

    /// <summary>A clasp:// link, pipe or command line asked, and the user said Allow in Clasp's prompt.</summary>
    ExternalApproved = 3,

    /// <summary>
    /// Clasp's "connect automatically when nearby" reconnect. The nearby signal can be spoofed, so this
    /// origin only reconnects: it never plays or changes volume, and moves the sound only if the user's own
    /// "switch output on connect" setting is on.
    /// </summary>
    NearbyAuto = 4,
}

/// <summary>
/// One ordered action. <see cref="Delay"/> is how long the runner should wait before performing it
/// (for Play: a short pause so Windows finishes routing audio to the AirPods first).
/// </summary>
public sealed record ConnectionAction(
    ConnectionActionKind Kind,
    string? EndpointId = null,
    int? VolumePercent = null,
    TimeSpan Delay = default);

/// <summary>
/// The user's four connection settings. <see cref="DefaultVolumeOnConnect"/> is a percent from 0
/// to 100, or null to leave the volume alone; any other value is ignored.
/// </summary>
public sealed record ConnectionActionSettings(
    bool AutoPlayOnConnect,
    bool AutoPauseOnDisconnect,
    int? DefaultVolumeOnConnect,
    bool SwitchOutputToAirPodsOnConnect)
{
    /// <summary>Everything off, which is the shipped default.</summary>
    public static ConnectionActionSettings AllOff { get; } = new(false, false, null, false);
}

/// <summary>
/// What Clasp can see of the selected AirPods right now. Build it from a complete snapshot with
/// <see cref="From"/>; an incomplete snapshot or a missing device yields null and must be skipped
/// (it is never proof of a disconnect).
/// </summary>
/// <param name="Connected">Windows reports the paired device as connected.</param>
/// <param name="AudioEndpointId">The device's own render endpoint, or null when Windows exposes none.</param>
/// <param name="EndpointAvailable">That endpoint is currently available.</param>
/// <param name="EndpointIsDefault">That endpoint is already the Windows default output.</param>
public sealed record ConnectionObservation(
    bool Connected,
    string? AudioEndpointId,
    bool EndpointAvailable,
    bool EndpointIsDefault)
{
    /// <summary>True when an available endpoint is known, so output actions can target it.</summary>
    public bool HasUsableEndpoint => EndpointAvailable && !string.IsNullOrWhiteSpace(AudioEndpointId);

    /// <summary>
    /// Reads the selected device from a snapshot. Endpoints are matched by the exact ContainerId,
    /// never by name. Returns null for an incomplete snapshot or an unknown device.
    /// </summary>
    public static ConnectionObservation? From(ConnectionSnapshot snapshot, string deviceId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!snapshot.IsComplete || string.IsNullOrWhiteSpace(deviceId)) return null;
        var device = snapshot.Devices.FirstOrDefault(item =>
            string.Equals(item.Id, deviceId, StringComparison.OrdinalIgnoreCase));
        if (device is null) return null;

        var container = ConnectionIdentity.NormalizeContainerId(device.ContainerId);
        ConnectionAudioEndpoint? endpoint = null;
        if (container is not null)
        {
            var matches = snapshot.AudioEndpoints.Where(item =>
                    string.Equals(ConnectionIdentity.NormalizeContainerId(item.ContainerId), container, StringComparison.OrdinalIgnoreCase)
                    && item.IsAvailable)
                .ToList();
            endpoint = matches.FirstOrDefault(item => item.IsCurrentOutput) ?? matches.FirstOrDefault();
        }

        return endpoint is null
            ? new(device.IsConnected, null, false, false)
            : new(device.IsConnected, endpoint.Id, true, endpoint.IsCurrentOutput);
    }
}

/// <summary>Facts about the rest of the app at the moment of an observation.</summary>
/// <param name="IsAppStart">
/// True for the very first observation after launch (or after the selected device changed). It
/// only sets the baseline, so already-connected AirPods never trigger actions.
/// </param>
/// <param name="MediaPlaying">Windows currently reports a playing media session.</param>
/// <param name="RestorationInProgress">
/// An audio restoration (<c>AudioOutputRestorationPolicy</c>) request is being applied or pending.
/// </param>
/// <param name="LastManualOutputChoiceAt">When the user last picked an output by hand, or null.</param>
/// <param name="Origin">
/// Where the connect that is in progress came from. A user click, one of Clasp's hotkeys or an
/// external request the user allowed is a request to move audio to the AirPods, newer than any earlier
/// output choice, so the output switch runs whatever the setting and the automatic-switch safeguards
/// (flap, manual choice, restoration) say. <see cref="ConnectionOrigin.NearbyAuto"/> plans at most the output switch.
/// </param>
public sealed record ConnectionActionContext(
    bool IsAppStart,
    bool MediaPlaying,
    bool RestorationInProgress = false,
    DateTimeOffset? LastManualOutputChoiceAt = null,
    ConnectionOrigin Origin = ConnectionOrigin.None)
{
    /// <summary>True when the person using this PC asked for this connect (not an unverified signal).</summary>
    public bool UserRequestedConnect => Origin is ConnectionOrigin.User or ConnectionOrigin.Hotkey or ConnectionOrigin.ExternalApproved;
}

/// <summary>
/// The result of one evaluation. <see cref="Actions"/> is already in execution order and may be
/// empty. <see cref="Reason"/> is plain language for a diagnostics line. <see cref="RecheckAt"/>,
/// when set, is when the caller must call <see cref="ConnectionActionsPolicy.Evaluate"/> again
/// even if nothing changed (the policy is waiting for the AirPods endpoint to appear).
/// </summary>
public sealed record ConnectionActionPlan(
    IReadOnlyList<ConnectionAction> Actions,
    string Reason,
    DateTimeOffset? RecheckAt = null)
{
    internal static ConnectionActionPlan None(string reason, DateTimeOffset? recheckAt = null)
        => new(Array.Empty<ConnectionAction>(), reason, recheckAt);
}

/// <summary>
/// Pure decision logic for connection-triggered actions (MagicPods-style auto play, auto pause,
/// default volume and switch output). Time comes from the <c>now</c> argument; the policy does no
/// I/O and is not thread-safe (call it from one thread or lock externally).
/// </summary>
/// <remarks>
/// Rules:
/// <list type="bullet">
/// <item>The first observation (or <see cref="ConnectionActionContext.IsAppStart"/>) is only a
/// baseline: AirPods that are already connected at launch never trigger anything.</item>
/// <item>Actions fire only on a real transition of the selected device, once per transition.</item>
/// <item>Disconnect: Pause is returned immediately, but only if media is playing and the setting
/// is on. Windows often pauses by itself first; then nothing is playing and nothing is sent.</item>
/// <item>Connect: Windows may report the device connected before its audio endpoint exists, so the
/// policy waits up to <see cref="DefaultEndpointWaitTimeout"/> for an available endpoint
/// (<see cref="ConnectionActionPlan.RecheckAt"/>). If none appears, nothing is done: playing
/// would go to the wrong speakers. Once known, the order is SwitchOutput, SetVolume, Play.</item>
/// <item>Play is only returned when the AirPods are, or have just been made, the default output,
/// and nothing is already playing. It carries a delay hint so audio routes first.</item>
/// <item>Debounce: a reconnect within the flap window (3 s) of a disconnect is a flap. The
/// connect actions are suppressed so a brief drop never produces Pause then Play. The Pause that
/// followed the disconnect was already returned, since leaving music on the PC speakers is worse.</item>
/// <item>Restore-previous-output wins: while a restoration is in progress, or within the manual
/// choice window (30 s) of a manual output choice, SwitchOutput is never returned. Without the
/// switch the AirPods are not the default output, so Play is skipped too. Volume still applies
/// because it targets the AirPods endpoint and does not move audio.</item>
/// <item>A nearby auto-connect (<see cref="ConnectionOrigin.NearbyAuto"/>) plans at most SwitchOutput,
/// and only when the user turned on "switch output on connect" (it targets the user's own selected
/// AirPods). Volume, Play and anything else are never planned, because the nearby signal cannot be verified.</item>
/// <item>An explicit Connect (<see cref="ConnectionActionContext.UserRequestedConnect"/>) always
/// switches output to the AirPods and is never treated as a flap: the safeguards above exist for
/// automatic connections, not for a request the user just made.</item>
/// </list>
/// </remarks>
public sealed class ConnectionActionsPolicy
{
    /// <summary>A reconnect this soon after a disconnect is treated as a flap.</summary>
    public static readonly TimeSpan DefaultFlapWindow = TimeSpan.FromSeconds(3);

    /// <summary>How long a connect waits for the AirPods audio endpoint before giving up.</summary>
    public static readonly TimeSpan DefaultEndpointWaitTimeout = TimeSpan.FromSeconds(15);

    /// <summary>A manual output choice newer than this blocks SwitchOutput.</summary>
    public static readonly TimeSpan DefaultManualChoiceWindow = TimeSpan.FromSeconds(30);

    /// <summary>Delay hint attached to Play so Windows finishes routing audio first.</summary>
    public static readonly TimeSpan DefaultPlayDelay = TimeSpan.FromMilliseconds(1500);

    private readonly TimeSpan _flapWindow;
    private readonly TimeSpan _endpointWait;
    private readonly TimeSpan _manualChoiceWindow;
    private readonly TimeSpan _playDelay;
    private bool _hasBaseline;
    private bool _connected;
    private DateTimeOffset? _lastDisconnectAt;
    private DateTimeOffset? _pendingConnectDeadline;

    public ConnectionActionsPolicy(
        TimeSpan? flapWindow = null,
        TimeSpan? endpointWaitTimeout = null,
        TimeSpan? manualChoiceWindow = null,
        TimeSpan? playDelay = null)
    {
        _flapWindow = flapWindow ?? DefaultFlapWindow;
        _endpointWait = endpointWaitTimeout ?? DefaultEndpointWaitTimeout;
        _manualChoiceWindow = manualChoiceWindow ?? DefaultManualChoiceWindow;
        _playDelay = playDelay ?? DefaultPlayDelay;
        if (_flapWindow < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(flapWindow));
        if (_endpointWait < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(endpointWaitTimeout));
        if (_manualChoiceWindow < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(manualChoiceWindow));
        if (_playDelay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(playDelay));
    }

    /// <summary>Forgets all state; the next observation becomes a new baseline (device changed, resume).</summary>
    public void Reset()
    {
        _hasBaseline = false;
        _connected = false;
        _lastDisconnectAt = null;
        _pendingConnectDeadline = null;
    }

    /// <summary>
    /// Call whenever the selected device's connection snapshot changes, and at
    /// <see cref="ConnectionActionPlan.RecheckAt"/>. Returns the actions to run now, in order.
    /// </summary>
    public ConnectionActionPlan Evaluate(
        ConnectionObservation observation,
        ConnectionActionSettings settings,
        ConnectionActionContext context,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(context);

        if (!_hasBaseline || context.IsAppStart)
        {
            _hasBaseline = true;
            _connected = observation.Connected;
            _pendingConnectDeadline = null;
            return ConnectionActionPlan.None("First observation is only a baseline; no action.");
        }

        if (observation.Connected != _connected)
        {
            _connected = observation.Connected;
            if (observation.Connected)
            {
                if (!context.UserRequestedConnect && _lastDisconnectAt is { } lost && now - lost < _flapWindow)
                {
                    _pendingConnectDeadline = null;
                    return ConnectionActionPlan.None("Reconnect right after a disconnect; treated as a flap, no action.");
                }
                _pendingConnectDeadline = now + _endpointWait;
            }
            else
            {
                _lastDisconnectAt = now;
                _pendingConnectDeadline = null;
                return PlanDisconnect(settings, context);
            }
        }

        if (_connected && _pendingConnectDeadline is { } deadline)
            return PlanConnect(observation, settings, context, now, deadline);

        return ConnectionActionPlan.None("No connection change.");
    }

    private static ConnectionActionPlan PlanDisconnect(ConnectionActionSettings settings, ConnectionActionContext context)
    {
        if (!settings.AutoPauseOnDisconnect) return ConnectionActionPlan.None("Disconnected; auto pause is off.");
        if (!context.MediaPlaying) return ConnectionActionPlan.None("Disconnected; nothing was playing, so no Pause.");
        return new([new ConnectionAction(ConnectionActionKind.Pause)], "Disconnected while playing; pausing.");
    }

    private ConnectionActionPlan PlanConnect(
        ConnectionObservation observation,
        ConnectionActionSettings settings,
        ConnectionActionContext context,
        DateTimeOffset now,
        DateTimeOffset deadline)
    {
        if (!observation.HasUsableEndpoint)
        {
            if (now >= deadline)
            {
                _pendingConnectDeadline = null;
                return ConnectionActionPlan.None("Connected, but Windows exposed no AirPods audio output in time; no action.");
            }
            return ConnectionActionPlan.None("Connected; waiting for the AirPods audio output.", deadline);
        }

        _pendingConnectDeadline = null; // exactly once per connect
        var endpoint = observation.AudioEndpointId!;
        var actions = new List<ConnectionAction>();
        bool isDefault = observation.EndpointIsDefault;

        // A nearby auto-connect may only do what the user's own "switch output" setting says, and that
        // always targets the user's selected paired device. Nothing else follows an unverified signal.
        bool nearbyAuto = context.Origin == ConnectionOrigin.NearbyAuto;
        string? note = null;
        if (context.UserRequestedConnect && !isDefault)
        {
            actions.Add(new(ConnectionActionKind.SwitchOutput, endpoint));
            isDefault = true;
        }
        else if (settings.SwitchOutputToAirPodsOnConnect && !isDefault)
        {
            if (context.RestorationInProgress)
                note = "output switch skipped: a restore is in progress";
            else if (context.LastManualOutputChoiceAt is { } chosen && now - chosen < _manualChoiceWindow)
                note = "output switch skipped: you chose an output recently";
            else
            {
                actions.Add(new(ConnectionActionKind.SwitchOutput, endpoint));
                isDefault = true;
            }
        }

        if (nearbyAuto)
            return new(actions, "Connected by nearby auto-connect; only the output switch setting applies, no volume or play.");

        if (settings.DefaultVolumeOnConnect is >= 0 and <= 100 and var percent)
            actions.Add(new(ConnectionActionKind.SetVolume, endpoint, percent));

        if (settings.AutoPlayOnConnect)
        {
            if (context.MediaPlaying) note ??= "play skipped: media is already playing";
            else if (!isDefault) note ??= "play skipped: AirPods are not the default output";
            else actions.Add(new(ConnectionActionKind.Play, endpoint, Delay: _playDelay));
        }

        return new(actions, note is null ? "Connected; running connection actions." : $"Connected; {note}.");
    }
}
