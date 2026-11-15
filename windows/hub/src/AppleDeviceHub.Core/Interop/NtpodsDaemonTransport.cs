using AppleDeviceHub.Core.Presentation;
using AppleDeviceHub.Core.Contracts;
using AppleDeviceHub.Core.Engine;

namespace AppleDeviceHub.Core.Interop;

/// <summary>
/// The hardware transport: speaks clasp-engine protocol 1.1 (windows/protocol/clasp-engine-v1;
/// a 1.0 engine is still accepted and simply gets no 1.1 features)
/// to a separately started, identity-checked clasp-engine process. The class name is kept from
/// the phase-one plan; it no longer uses the upstream NTPods pipes.
/// <list type="bullet">
/// <item>Never starts the engine, never searches for it, never asks for elevation.</item>
/// <item><see cref="StartAsync"/> connects, verifies the server, handshakes and lists devices. It
/// does NOT open a session; <see cref="ConnectCommand"/> does (open_session with the configured
/// mode) and <see cref="DisconnectCommand"/> closes it (close_session). Windows audio is never
/// touched (<see cref="DeviceCapabilities.ControlSessionOnly"/>).</item>
/// <item>Battery, listening mode, Conversation Awareness and ear placement come only from engine
/// observations, per field, with the observation's own time. Commands never produce values; an
/// accepted write is only <see cref="CommandOutcome.Acknowledged"/>.</item>
/// <item>Protocol 1.1 values are used only when the engine negotiated minor 1 and advertised the
/// matching capability (<see cref="EngineFeatures"/>). <see cref="DeviceCapabilities.ConversationAwareness"/>
/// and <see cref="DeviceCapabilities.EarDetection"/> are claimed only once such a value was
/// actually observed in the current session. Adaptive strength is never offered.</item>
/// <item>Session end, connection loss and a new engine epoch clear every live observation.
/// Messages for any other session are dropped. Writes are sent once and never replayed.</item>
/// </list>
/// </summary>
public sealed class NtpodsDaemonTransport : IAirPodsTransport, IHardwareTransportStatus
{
    public const string EngineName = "Clasp engine";

    /// <summary>
    /// What every hardware state claims. ConversationAwareness and EarDetection are added per state
    /// only when a 1.1 engine advertised them and the session observed a value; AdaptiveStrength and
    /// CaseLidEvents are never claimed (no readback exists).
    /// </summary>
    public const DeviceCapabilities HardwareCapabilities =
        DeviceCapabilities.Battery | DeviceCapabilities.ListeningModes |
        DeviceCapabilities.ConnectControl | DeviceCapabilities.ControlSessionOnly;

    public const string NotInThisBuild = "Not available in this test build";

    public const string ObserveReadOnlyReason = "Observation session: changes disabled";

    public const string ModeNotReportedReason = "Listening mode has not been reported yet";

    public const string UncertainModeWriteReason = "The previous change is uncertain. End the session and start a new one before changing modes.";

    public const string AwarenessWriteUnsupportedReason = "This Clasp engine cannot change Conversation Awareness.";

    public const string AwarenessNotReportedReason = "Conversation Awareness has not been reported yet";

    public const string UncertainAwarenessWriteReason =
        "The previous Conversation Awareness change was not confirmed. End the session and start a new one before changing it again.";

    public const string AwarenessAwaitingConfirmationReason = "Waiting for the AirPods to report Conversation Awareness";

    public const string OtherWriteInFlightReason = "Another change is still being delivered.";

    public const string EngineRefusesAwarenessNowReason = "The engine does not allow a Conversation Awareness change in this session right now.";

    private readonly ClaspEngineOptions _options;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly HashSet<long> _retiredSessions = new();

    private EngineConnection? _connection;
    private string? _epoch;
    private long _epochLastSeq;

    private bool _listingKnown;
    private DateTimeOffset _listedAt;
    private EngineDevice? _device;

    private bool _sessionKnown;
    private DateTimeOffset _sessionAt;
    private EngineSessionState _sessionState;
    private long? _sessionId;
    private string? _openRequestId;
    private bool _sessionClosing;
    private ListeningMode[] _writable = Array.Empty<ListeningMode>();
    // A delivery acknowledgement is not a device reading. Hold this reservation until both
    // arrive; an uncertain completion retires only when the session ends.
    private string? _modeWriteRequestId;
    private ListeningMode _modeWriteRequested;
    private ModeWriteCompletionGate _modeWriteCompletion = new();
    // Conversation Awareness uses the same gate. The device reports CA once (after notifications
    // are requested), so an acknowledged write may never be read back: its reservation then holds,
    // as unconfirmed, until the session ends.
    private string? _awarenessWriteRequestId;
    private bool _awarenessWriteRequested;
    private ModeWriteCompletionGate _awarenessWriteCompletion = new();

    private EngineFeatures _features = EngineFeatures.None;
    private string[]? _writableSettings;

    private BatteryComponent _left;
    private BatteryComponent _right;
    private BatteryComponent _case;
    private Observation<ListeningMode> _mode;
    private Observation<bool> _awareness;
    private bool _awarenessObserved;
    private EarState _ears = EarState.Unknown;
    private bool _earsObserved;

    private long _sequence;
    private string? _connectionReason;
    private string? _lastPublishedReason;
    private DeviceState? _current;
    private bool _disposed;

    public NtpodsDaemonTransport(ClaspEngineOptions options, TimeProvider? time = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _time = time ?? TimeProvider.System;
        SourceText = $"Hardware: {EngineName}, {EngineWire.SessionModeName(options.SessionMode)} session";
    }

    public TransportInfo Info { get; } = new(EngineName, DataOrigin.Hardware, HubContract.Version,
        "clasp-engine protocol 1.1 (1.0 accepted) over a per-user local pipe; AirPods control session only, Windows audio untouched.");

    public string DeviceId => _options.DeviceId;

    public DeviceState? Current
    {
        get { lock (_gate) return _current; }
    }

    public string SourceText { get; }

    public string? ReadOnlyReason
    {
        get
        {
            lock (_gate)
            {
                if (_options.SessionMode == EngineSessionMode.Observe) return ObserveReadOnlyReason;
                if (_sessionClosing) return "The session is ending; mode changes are disabled.";
                if (_modeWriteCompletion.IsUncertain) return UncertainModeWriteReason;
                if (!_mode.HasValue) return ModeNotReportedReason;
                if (_modeWriteRequestId is not null) return "Waiting for device confirmation";
                return AwarenessWireInFlight ? OtherWriteInFlightReason : null;
            }
        }
    }

    /// <summary>
    /// Why a Conversation Awareness change cannot be offered right now, or null when it can: the
    /// engine supports the write, a control session is open, the device has reported a value, and
    /// no earlier change is outstanding or unconfirmed.
    /// </summary>
    public string? ConversationAwarenessReadOnlyReason
    {
        get
        {
            lock (_gate) return AwarenessRefusal(out _);
        }
    }

    /// <summary>What the connected engine negotiated (none before the first connection).</summary>
    public EngineFeatures Features
    {
        get { lock (_gate) return _features; }
    }

    public string? UnavailableReason
    {
        get { lock (_gate) return ComputeReason(); }
    }

    /// <summary>The engine epoch of the current (or last) connection.</summary>
    public string? EngineEpoch
    {
        get { lock (_gate) return _epoch; }
    }

    public event EventHandler<DeviceState>? StateChanged;

    public event EventHandler? StatusChanged;

    public event EventHandler<string>? Diagnostic;

    // ---- Start / connect -------------------------------------------------------------------

    /// <summary>
    /// Connect (or reconnect after a loss), verify the engine, handshake and list devices. Never
    /// throws for an unavailable engine: the reason is reported through <see cref="UnavailableReason"/>.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsDisposed) return;
        var invalid = _options.Validate();
        if (invalid is not null)
        {
            SetConnectionReason(invalid);
            return;
        }

        await _connectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EngineConnection? connection;
            lock (_gate) connection = _connection;
            if (connection is null)
            {
                connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
                if (connection is null) return;
            }
            await ListDevicesAsync(connection, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private async Task<EngineConnection?> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        string pipeName;
        try
        {
            pipeName = _options.PipeName ?? EngineConnection.DefaultPipeName();
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or InvalidOperationException)
        {
            SetConnectionReason(ex.Message);
            return null;
        }

        string? previousEpoch;
        long previousSeq;
        lock (_gate)
        {
            previousEpoch = _epoch;
            previousSeq = _epochLastSeq;
        }

        var settings = new EngineConnectionSettings(pipeName, _options.EnginePath, _options.EngineSha256)
        {
            ConnectTimeout = _options.ConnectTimeout,
            HandshakeTimeout = _options.HandshakeTimeout,
            Time = _time,
        };

        EngineConnection connection;
        try
        {
            connection = await EngineConnection.OpenAsync(settings, previousEpoch, previousSeq, OnMessage, OnClosed, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (EngineUnavailableException ex)
        {
            Log($"engine unavailable: {LogText.ExceptionKind(ex)}");
            SetConnectionReason(ex.Message);
            return null;
        }

        bool newEpoch;
        lock (_gate)
        {
            if (_disposed)
            {
                newEpoch = false;
            }
            else
            {
                newEpoch = _epoch is not null && !string.Equals(_epoch, connection.Epoch, StringComparison.Ordinal);
                if (newEpoch) ResetForNewEpoch();
                _epoch = connection.Epoch;
                _features = EngineFeatures.From(connection.Ack);
                _connection = connection;
                _connectionReason = null;
            }
        }
        if (IsDisposed)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            return null;
        }
        Log($"connected to {connection.Ack.Engine} (protocol {connection.Ack.Major}.{connection.Ack.Minor}, epoch {connection.Epoch})"
            + (newEpoch ? "; new engine epoch, earlier sessions and observations discarded" : ""));
        Publish();
        return connection;
    }

    private async Task ListDevicesAsync(EngineConnection connection, CancellationToken cancellationToken)
    {
        string? failure = null;
        try
        {
            var reply = await connection.RequestAsync(new ListDevicesRequest(NewRequestId()), _options.RequestTimeout, cancellationToken)
                .ConfigureAwait(false);
            if (reply is ErrorMessage e) failure = $"The engine could not list devices ({e.Code}: {e.Detail}).";
            else if (reply is not DevicesMessage) failure = "The engine answered list_devices with an unexpected message.";
        }
        catch (TimeoutException)
        {
            failure = "The engine did not list devices in time.";
        }
        catch (EngineConnectionClosedException)
        {
            return; // OnClosed already set the reason
        }

        if (failure is not null)
        {
            Log(failure);
            SetConnectionReason(failure);
            return;
        }

        string? deviceReason;
        lock (_gate) deviceReason = DeviceReason();
        Log(deviceReason is null
            ? $"device {_options.DeviceId}: driver interface bound, available for a session"
            : $"device {_options.DeviceId}: unavailable ({deviceReason})");
        Publish();
    }

    // ---- Commands --------------------------------------------------------------------------

    public async Task<CommandResult> SendAsync(DeviceCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (IsDisposed) return Result(command, CommandOutcome.TransportUnavailable, "The hardware transport is stopped.");
        try
        {
            return command switch
            {
                ConnectCommand => await OpenSessionAsync(command, cancellationToken).ConfigureAwait(false),
                DisconnectCommand => await CloseSessionAsync(command, cancellationToken).ConfigureAwait(false),
                SetListeningModeCommand m => await SetListeningModeAsync(m, cancellationToken).ConfigureAwait(false),
                SetConversationAwarenessCommand a => await SetConversationAwarenessAsync(a, cancellationToken).ConfigureAwait(false),
                SetAdaptiveStrengthCommand => Result(command, CommandOutcome.Unsupported, NotInThisBuild),
                _ => Result(command, CommandOutcome.Unsupported, "Not supported by the Clasp engine."),
            };
        }
        catch (EngineConnectionClosedException ex)
        {
            return Result(command, CommandOutcome.TransportUnavailable, $"The engine connection closed ({ex.Message}).");
        }
        catch (ObjectDisposedException)
        {
            return Result(command, CommandOutcome.TransportUnavailable, "The hardware transport is stopped.");
        }
    }

    private async Task<CommandResult> OpenSessionAsync(DeviceCommand command, CancellationToken cancellationToken)
    {
        EngineConnection? connection;
        string requestId = NewRequestId();
        lock (_gate)
        {
            connection = _connection;
            if (connection is null)
                return Result(command, CommandOutcome.TransportUnavailable, ComputeReason() ?? "Not connected to the Clasp engine.");
            if (_sessionId is not null && _sessionState is EngineSessionState.Open or EngineSessionState.Opening)
                return Result(command, CommandOutcome.Rejected, "A session is already open.");
            if (_openRequestId is not null)
                return Result(command, CommandOutcome.Rejected, "A session is already being opened.");
            if (DeviceReason() is { } reason)
                return Result(command, CommandOutcome.Failed, reason);
            _openRequestId = requestId;
        }

        var mode = EngineWire.SessionModeName(_options.SessionMode);
        Log($"open_session ({mode}) requested for {_options.DeviceId}");
        try
        {
            var reply = await connection.RequestAsync(
                new OpenSessionRequest(requestId, _options.DeviceId, _options.SessionMode), _options.OpenSessionTimeout, cancellationToken)
                .ConfigureAwait(false);
            var result = Map(command, reply);
            Log($"open_session for {_options.DeviceId}: {Describe(reply)}");
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // open_session is the one request the protocol can cancel.
            await connection.TrySendAsync(new CancelRequest(NewRequestId(), requestId)).ConfigureAwait(false);
            Log($"open_session for {_options.DeviceId} cancelled by Clasp");
            throw;
        }
        catch (TimeoutException)
        {
            await connection.TrySendAsync(new CancelRequest(NewRequestId(), requestId)).ConfigureAwait(false);
            Log($"open_session for {_options.DeviceId}: no answer in time; cancel sent");
            return Result(command, CommandOutcome.TimedOut, "The engine did not answer in time.");
        }
        finally
        {
            lock (_gate)
            {
                if (_openRequestId == requestId) _openRequestId = null;
            }
        }
    }

    private async Task<CommandResult> CloseSessionAsync(DeviceCommand command, CancellationToken cancellationToken)
    {
        EngineConnection? connection;
        long sessionId;
        lock (_gate)
        {
            connection = _connection;
            if (connection is null)
                return Result(command, CommandOutcome.TransportUnavailable, ComputeReason() ?? "Not connected to the Clasp engine.");
            if (_sessionId is not { } id) return Result(command, CommandOutcome.Rejected, "No session is open.");
            sessionId = id;
            _sessionClosing = true;
        }
        Publish();

        Log($"close_session requested for {_options.DeviceId} (session {sessionId})");
        try
        {
            var reply = await connection.RequestAsync(new CloseSessionRequest(NewRequestId(), sessionId), _options.RequestTimeout, cancellationToken)
                .ConfigureAwait(false);
            Log($"close_session for {_options.DeviceId}: {Describe(reply)}");
            return Map(command, reply);
        }
        catch (TimeoutException)
        {
            Log($"close_session for {_options.DeviceId}: no answer in time");
            return Result(command, CommandOutcome.TimedOut, "The engine did not answer in time.");
        }
    }

    private async Task<CommandResult> SetListeningModeAsync(SetListeningModeCommand command, CancellationToken cancellationToken)
    {
        EngineConnection? connection;
        long sessionId;
        string requestId = NewRequestId();
        lock (_gate)
        {
            connection = _connection;
            if (connection is null)
                return Result(command, CommandOutcome.TransportUnavailable, ComputeReason() ?? "Not connected to the Clasp engine.");
            if (_options.SessionMode != EngineSessionMode.Control)
                return Result(command, CommandOutcome.Unsupported, ObserveReadOnlyReason);
            if (_sessionId is not { } id || _sessionState != EngineSessionState.Open)
                return Result(command, CommandOutcome.Unsupported, "No control session is open.");
            if (_sessionClosing)
                return Result(command, CommandOutcome.Rejected, "The session is ending; mode changes are disabled.");
            if (_modeWriteCompletion.IsUncertain)
                return Result(command, CommandOutcome.Rejected, UncertainModeWriteReason);
            if (_modeWriteRequestId is not null)
                return Result(command, CommandOutcome.Rejected, "The previous mode change is still awaiting delivery or device confirmation.");
            if (AwarenessWireInFlight)
                return Result(command, CommandOutcome.Rejected, OtherWriteInFlightReason);
            if (!_mode.HasValue)
                return Result(command, CommandOutcome.Rejected, ModeNotReportedReason);
            if (!_writable.Contains(command.Mode))
                return Result(command, CommandOutcome.Unsupported, "This mode cannot be set in this session.");
            sessionId = id;
            _modeWriteRequestId = requestId;
            _modeWriteRequested = command.Mode;
            _modeWriteCompletion = new();
        }
        Publish();

        // One attempt. A timeout is uncertain (the write may still land): it is reported as such
        // and never retried. Later readings remain visible, but another write needs a new
        // session after any uncertain completion.
        try
        {
            var reply = await connection.RequestAsync(
                new SetListeningModeRequest(requestId, sessionId, command.Mode), _options.RequestTimeout, cancellationToken)
                .ConfigureAwait(false);
            Log($"set_listening_mode {EngineWire.ListeningModeName(command.Mode)} for {_options.DeviceId}: {Describe(reply)}");
            var result = Map(command, reply);
            CompleteModeWrite(requestId, result.Outcome);
            return result;
        }
        catch (TimeoutException)
        {
            Log($"set_listening_mode for {_options.DeviceId}: no answer in time (not retried)");
            CompleteModeWrite(requestId, CommandOutcome.TimedOut);
            return Result(command, CommandOutcome.TimedOut, "The engine did not answer in time.");
        }
        catch (OperationCanceledException)
        {
            // Cancellation stops our wait, not a device write already in progress.
            CompleteModeWrite(requestId, CommandOutcome.TimedOut);
            throw;
        }
    }

    private void CompleteModeWrite(string requestId, CommandOutcome outcome)
    {
        lock (_gate)
        {
            // A continuation from an ended session must not touch its replacement.
            if (_modeWriteRequestId != requestId) return;
            _modeWriteCompletion.Complete(outcome);
            if (_modeWriteCompletion.CanRelease) ClearModeWrite();
        }
        Publish();
    }

    private void ClearModeWrite()
    {
        _modeWriteRequestId = null;
        _modeWriteCompletion = new();
    }

    /// <summary>A mode write has been sent and its request wait has not ended yet.</summary>
    private bool ModeWireInFlight => _modeWriteRequestId is not null && !_modeWriteCompletion.HasCompleted;

    /// <summary>A Conversation Awareness write has been sent and its request wait has not ended yet.</summary>
    private bool AwarenessWireInFlight => _awarenessWriteRequestId is not null && !_awarenessWriteCompletion.HasCompleted;

    /// <summary>Null when a Conversation Awareness write may be sent now; otherwise the refusal.</summary>
    private string? AwarenessRefusal(out CommandOutcome outcome)
    {
        outcome = CommandOutcome.Unsupported;
        if (_connection is null) return ComputeReason() ?? "Not connected to the Clasp engine.";
        if (!_features.ConversationAwarenessWrite) return AwarenessWriteUnsupportedReason;
        if (_options.SessionMode != EngineSessionMode.Control) return ObserveReadOnlyReason;
        if (_sessionId is null || _sessionState != EngineSessionState.Open) return "No control session is open.";
        outcome = CommandOutcome.Rejected;
        if (_sessionClosing) return "The session is ending; changes are disabled.";
        if (_awarenessWriteCompletion.IsUncertain) return UncertainAwarenessWriteReason;
        if (_awarenessWriteRequestId is not null)
            return _awarenessWriteCompletion.HasCompleted ? AwarenessAwaitingConfirmationReason : OtherWriteInFlightReason;
        if (ModeWireInFlight) return OtherWriteInFlightReason;
        // Matches the engine, which keeps an uncertain mode write pending until the session ends.
        if (_modeWriteCompletion.IsUncertain) return UncertainModeWriteReason;
        if (!_awareness.HasValue) return AwarenessNotReportedReason;
        // 1.1 engines say what the session may write right now (re-sent when it changes).
        if (_writableSettings is { } settings && !settings.Contains(EngineWire.SettingConversationAwareness))
            return EngineRefusesAwarenessNowReason;
        return null;
    }

    private async Task<CommandResult> SetConversationAwarenessAsync(SetConversationAwarenessCommand command, CancellationToken cancellationToken)
    {
        EngineConnection? connection;
        long sessionId;
        string requestId = NewRequestId();
        lock (_gate)
        {
            connection = _connection;
            if (connection is null)
                return Result(command, CommandOutcome.TransportUnavailable, ComputeReason() ?? "Not connected to the Clasp engine.");
            if (AwarenessRefusal(out var refused) is { } reason) return Result(command, refused, reason);
            sessionId = _sessionId!.Value;
            _awarenessWriteRequestId = requestId;
            _awarenessWriteRequested = command.Enabled;
            _awarenessWriteCompletion = new();
        }
        Publish();

        // One attempt, never retried. Accepted is delivery only; the reservation is released only
        // by a matching readback after the acknowledgement, or by the end of the session.
        try
        {
            var reply = await connection.RequestAsync(
                new SetConversationAwarenessRequest(requestId, sessionId, command.Enabled), _options.RequestTimeout, cancellationToken)
                .ConfigureAwait(false);
            Log($"set_conversation_awareness {(command.Enabled ? "on" : "off")} for {_options.DeviceId}: {Describe(reply)}");
            var result = Map(command, reply);
            CompleteAwarenessWrite(requestId, result.Outcome);
            return result;
        }
        catch (TimeoutException)
        {
            Log($"set_conversation_awareness for {_options.DeviceId}: no answer in time (not retried)");
            CompleteAwarenessWrite(requestId, CommandOutcome.TimedOut);
            return Result(command, CommandOutcome.TimedOut, "The engine did not answer in time.");
        }
        catch (OperationCanceledException)
        {
            // Cancellation stops our wait, not a device write already in progress.
            CompleteAwarenessWrite(requestId, CommandOutcome.TimedOut);
            throw;
        }
        catch (EngineConnectionClosedException)
        {
            CompleteAwarenessWrite(requestId, CommandOutcome.TransportUnavailable);
            throw;
        }
    }

    private void CompleteAwarenessWrite(string requestId, CommandOutcome outcome)
    {
        lock (_gate)
        {
            // A continuation from an ended session must not touch its replacement.
            if (_awarenessWriteRequestId != requestId) return;
            _awarenessWriteCompletion.Complete(outcome);
            if (_awarenessWriteCompletion.CanRelease) ClearAwarenessWrite();
        }
        Publish();
    }

    private void ClearAwarenessWrite()
    {
        _awarenessWriteRequestId = null;
        _awarenessWriteCompletion = new();
    }

    private static CommandResult Map(DeviceCommand command, EngineMessage reply) => reply switch
    {
        ResultMessage { Outcome: EngineOutcome.Accepted } => Result(command, CommandOutcome.Acknowledged, null),
        ResultMessage { Outcome: EngineOutcome.Rejected } r => Result(command, CommandOutcome.Rejected, Readable(r.Detail) ?? "The engine rejected the request."),
        ResultMessage { Outcome: EngineOutcome.Failed } r => Result(command, CommandOutcome.Failed, Readable(r.Detail) ?? "The device operation failed."),
        ResultMessage { Outcome: EngineOutcome.Unsupported } r => Result(command, CommandOutcome.Unsupported, Readable(r.Detail) ?? "Not supported in this session."),
        ResultMessage { Outcome: EngineOutcome.TimedOut } r => Result(command, CommandOutcome.TimedOut, Readable(r.Detail) ?? "The device did not answer in time."),
        ResultMessage { Outcome: EngineOutcome.Cancelled } r => Result(command, CommandOutcome.Failed, Readable(r.Detail) ?? "Cancelled."),
        ResultMessage r => Result(command, CommandOutcome.Failed, Readable(r.Detail) ?? "The engine reported an unknown outcome."),
        // The engine answers a second open_session (or any request it cannot take now) with
        // error "busy" for that request_id and no result.
        ErrorMessage { Code: "busy" } e => Result(command, CommandOutcome.Rejected,
            string.IsNullOrWhiteSpace(e.Detail) ? "The engine is busy with another session." : $"The engine is busy: {e.Detail}"),
        ErrorMessage e => Result(command, CommandOutcome.Failed, $"Engine error {e.Code}: {e.Detail}"),
        _ => Result(command, CommandOutcome.Failed, "The engine answered with an unexpected message."),
    };

    private static string Describe(EngineMessage reply) => reply switch
    {
        ResultMessage r => r.Detail is null ? r.Outcome.ToString() : $"{r.Outcome} ({r.Detail})",
        ErrorMessage e => $"error {e.Code} ({e.Detail})",
        _ => reply.Type,
    };

    private static CommandResult Result(DeviceCommand command, CommandOutcome outcome, string? detail)
        => new(command.CommandId, outcome, DataOrigin.Hardware, detail);

    // ---- Engine messages (read loop thread, in seq order) ----------------------------------

    private void OnMessage(EngineConnection connection, EngineMessage message)
    {
        string? log = null;
        bool changed;
        bool recheck;
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(connection, _connection)) return;
            var before = _sessionState;
            changed = message switch
            {
                DevicesMessage d => ApplyDevices(d),
                ResultMessage r => ApplyResult(r),
                SessionMessage s => ApplySession(s, out log),
                ObservationMessage o => ApplyObservation(o),
                ErrorMessage { RequestId: null } e => LogOnly($"engine error {e.Code}: {e.Detail}", out log),
                _ => false,
            };
            // The Bluetooth link may change around a session: re-read the device list after
            // every session open, close and loss, so the link fact and its check time stay current.
            recheck = message is SessionMessage && _sessionState != before
                && _sessionState is EngineSessionState.Open or EngineSessionState.Closed or EngineSessionState.Lost;
        }
        if (log is not null) Log(log);
        if (changed) Publish();
        if (recheck) _ = Task.Run(() => RecheckDevicesAsync(connection));
    }

    /// <summary>A background list_devices; its reply updates the state through <see cref="OnMessage"/>.</summary>
    private async Task RecheckDevicesAsync(EngineConnection connection)
    {
        try
        {
            await connection.RequestAsync(new ListDevicesRequest(NewRequestId()), _options.RequestTimeout, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or EngineConnectionClosedException or OperationCanceledException or ObjectDisposedException)
        {
            // Best effort: the previous reading keeps its (older) check time.
        }
    }

    /// <summary>
    /// Engine details that start with a machine code (for example <c>not_connected:</c>) become
    /// a readable sentence; anything else is passed through.
    /// </summary>
    internal static string? Readable(string? detail)
    {
        const string NotConnected = "not_connected:";
        if (detail is null || !detail.StartsWith(NotConnected, StringComparison.Ordinal)) return detail;
        var rest = detail[NotConnected.Length..].Trim();
        if (rest.Length > 0) rest = char.ToUpperInvariant(rest[0]) + rest[1..];
        return rest.Length == 0
            ? "The AirPods are not connected to Windows"
            : $"The AirPods are not connected to Windows. {rest.TrimEnd('.')}";
    }

    private static bool LogOnly(string text, out string? log)
    {
        log = text;
        return false;
    }

    private bool ApplyDevices(DevicesMessage message)
    {
        _listingKnown = true;
        _listedAt = _time.GetUtcNow();
        _device = message.Devices.FirstOrDefault(d => string.Equals(d.DeviceId, _options.DeviceId, StringComparison.Ordinal));
        return true;
    }

    private bool ApplyResult(ResultMessage message)
    {
        if (message.RequestId == _modeWriteRequestId)
        {
            // Process the acknowledgement on the read loop before any later observation.
            // A late answer cannot release a reservation already made uncertain by timeout.
            _modeWriteCompletion.OnEngineReply(message.Outcome);
            if (_modeWriteCompletion.CanRelease) ClearModeWrite();
            return true;
        }
        if (message.RequestId == _awarenessWriteRequestId)
        {
            _awarenessWriteCompletion.OnEngineReply(message.Outcome);
            if (_awarenessWriteCompletion.CanRelease) ClearAwarenessWrite();
            return true;
        }
        // The accepted answer to our own open_session names the session to follow.
        if (message.RequestId != _openRequestId || message.Outcome != EngineOutcome.Accepted || message.SessionId is not { } id) return false;
        if (_retiredSessions.Contains(id) || (_sessionId is { } current && current != id)) return false;
        _sessionId = id;
        return false;
    }

    private bool ApplySession(SessionMessage message, out string? log)
    {
        log = null;
        if (!string.Equals(message.DeviceId, _options.DeviceId, StringComparison.Ordinal)) return false;
        if (_sessionId is null)
        {
            // Session messages can precede the open_session result; adopt only while our own
            // open request is outstanding, and never a session that has already ended.
            bool adoptable = _openRequestId is not null && !_retiredSessions.Contains(message.SessionId)
                && message.State is EngineSessionState.Opening or EngineSessionState.Open;
            if (!adoptable) return false;
            _sessionId = message.SessionId;
        }
        if (message.SessionId != _sessionId) return false; // another or an old session: ignored

        _sessionKnown = true;
        _sessionAt = _time.GetUtcNow();
        switch (message.State)
        {
            case EngineSessionState.Opening:
                _sessionState = EngineSessionState.Opening;
                _writable = Array.Empty<ListeningMode>();
                _writableSettings = null;
                log = $"session {message.SessionId} for {message.DeviceId}: opening";
                break;
            case EngineSessionState.Open:
                _sessionState = EngineSessionState.Open;
                _writableSettings = message.WritableSettings?.ToArray();
                // 1.1: an engine that lists writable settings without listening_mode allows no mode write.
                _writable = _writableSettings is { } settings && !settings.Contains(EngineWire.SettingListeningMode)
                    ? Array.Empty<ListeningMode>()
                    : message.WritableModes.ToArray();
                log = $"session {message.SessionId} for {message.DeviceId}: open ({EngineWire.SessionModeName(_options.SessionMode)}, "
                    + $"{_writable.Length} writable modes)";
                break;
            default:
                // closed, lost, or a state this client does not know: not live.
                var state = message.State == EngineSessionState.Closed ? EngineSessionState.Closed : EngineSessionState.Lost;
                EndSession(state);
                log = $"session {message.SessionId} for {message.DeviceId}: {state.ToString().ToLowerInvariant()}"
                    + (message.Reason is null ? "" : $" ({message.Reason})");
                break;
        }
        return true;
    }

    private bool ApplyObservation(ObservationMessage message)
    {
        if (_sessionId is not { } id || message.SessionId != id) return false;
        if (!string.Equals(message.DeviceId, _options.DeviceId, StringComparison.Ordinal)) return false;
        if (_sessionState is not (EngineSessionState.Open or EngineSessionState.Opening)) return false;

        var at = message.ObservedAt;
        var f = message.Fields;
        // A field absent from this packet was not observed: it keeps its value AND its time.
        if (f.BatteryLeft is { } l) _left = BatteryComponent.From(l.Level, l.Charging, at);
        if (f.BatteryRight is { } r) _right = BatteryComponent.From(r.Level, r.Charging, at);
        if (f.BatteryCase is { } c) _case = BatteryComponent.From(c.Level, c.Charging, at);
        if (f.ListeningMode is { } m)
        {
            _mode = EngineWire.ToContract(m) is { } mode ? Observation<ListeningMode>.At(mode, at) : Observation<ListeningMode>.Unknown;
            if (_modeWriteRequestId is not null && _mode.Value == _modeWriteRequested)
            {
                _modeWriteCompletion.ObserveMatchingReadback();
                if (_modeWriteCompletion.CanRelease) ClearModeWrite();
            }
        }
        // 1.1 fields count only when the engine advertised them.
        if (f.ConversationAwareness is { } ca && _features.ObserveConversationAwareness)
        {
            // "unknown" is a report without a value: shown as unknown, never as off.
            if (EngineWire.ToContract(ca) is bool on)
            {
                _awareness = Observation<bool>.At(on, at);
                _awarenessObserved = true;
                if (_awarenessWriteRequestId is not null && on == _awarenessWriteRequested)
                {
                    _awarenessWriteCompletion.ObserveMatchingReadback();
                    if (_awarenessWriteCompletion.CanRelease) ClearAwarenessWrite();
                }
            }
            else
            {
                _awareness = Observation<bool>.Unknown;
            }
        }
        if (f.Ears is { } ears && _features.ObserveEar)
        {
            var primary = EngineWire.ToContract(ears.Primary);
            var secondary = EngineWire.ToContract(ears.Secondary);
            _ears = new EarState(
                primary is { } p ? Observation<PodPlacement>.At(p, at) : Observation<PodPlacement>.Unknown,
                secondary is { } s ? Observation<PodPlacement>.At(s, at) : Observation<PodPlacement>.Unknown);
            if (primary is not null || secondary is not null) _earsObserved = true;
        }
        return true;
    }

    /// <summary>The session is over: nothing from it is live any more, and it can never come back.</summary>
    private void EndSession(EngineSessionState state)
    {
        if (_sessionId is { } id) _retiredSessions.Add(id);
        _sessionId = null;
        _sessionState = state;
        _sessionAt = _time.GetUtcNow();
        _writable = Array.Empty<ListeningMode>();
        _writableSettings = null;
        _sessionClosing = false;
        ClearModeWrite();
        ClearAwarenessWrite();
        ClearObservations();
    }

    private void ClearObservations()
    {
        _left = BatteryComponent.Unknown;
        _right = BatteryComponent.Unknown;
        _case = BatteryComponent.Unknown;
        _mode = Observation<ListeningMode>.Unknown;
        _awareness = Observation<bool>.Unknown;
        _awarenessObserved = false;
        _ears = EarState.Unknown;
        _earsObserved = false;
    }

    private void ResetForNewEpoch()
    {
        // A new engine process: every earlier session, sequence and observation is gone.
        if (_sessionKnown) EndSession(EngineSessionState.Lost);
        else ClearObservations();
        _sessionId = null;
        _retiredSessions.Clear(); // session ids are only unique within one epoch
        _openRequestId = null;
        _listingKnown = false;
        _device = null;
        _epochLastSeq = 0;
    }

    private void OnClosed(EngineConnection connection, string reason)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(connection, _connection)) return;
            _connection = null;
            _epochLastSeq = connection.LastSeq;
            if (_disposed) return;
            if (_sessionKnown || _sessionId is not null) EndSession(EngineSessionState.Lost);
            _features = EngineFeatures.None; // renegotiated on the next connection
            _listingKnown = false; // the device list is no longer current
            _connectionReason = $"The connection to the Clasp engine closed ({reason}). Restart Clasp to reconnect.";
        }
        Log($"engine connection closed: {reason}");
        Publish();
    }

    // ---- State -----------------------------------------------------------------------------

    private string? DeviceReason()
    {
        if (!_listingKnown) return null;
        if (_device is not { } d) return $"The engine does not list {_options.DeviceId}: it is not paired or does not publish the AirPods control service.";
        if (!d.AapService) return "This device does not publish the AirPods control service.";
        return d.DriverInterface switch
        {
            DriverInterfaceStatus.Bound => null,
            DriverInterfaceStatus.NotFound => "Driver interface not found for this device.",
            DriverInterfaceStatus.Ambiguous => "More than one driver interface matches this device; the engine refuses to choose.",
            DriverInterfaceStatus.Unparsed => "The driver interface path for this device could not be read.",
            _ => "The driver interface status for this device is not recognised.",
        };
    }

    private string? ComputeReason() => _connectionReason ?? DeviceReason();

    private void SetConnectionReason(string reason)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _connectionReason = reason;
        }
        Publish();
    }

    private void Publish()
    {
        DeviceState? state = null;
        bool reasonChanged;
        lock (_gate)
        {
            if (_disposed) return;
            var reason = ComputeReason();
            reasonChanged = !string.Equals(reason, _lastPublishedReason, StringComparison.Ordinal);
            _lastPublishedReason = reason;
            if (_listingKnown || _sessionKnown)
            {
                state = Build();
                _current = state;
            }
        }
        if (reasonChanged) StatusChanged?.Invoke(this, EventArgs.Empty);
        if (state is not null) StateChanged?.Invoke(this, state);
    }

    private DeviceState Build()
    {
        var now = _time.GetUtcNow();
        bool bound = _device is { AapService: true, DriverInterface: DriverInterfaceStatus.Bound };
        var link = new LinkState(
            Nearby: _listingKnown ? Observation<bool>.At(bound, _listedAt) : Observation<bool>.Unknown,
            Connecting: _sessionKnown ? Observation<bool>.At(_sessionState == EngineSessionState.Opening, _sessionAt) : Observation<bool>.Unknown,
            ProtocolSession: _sessionKnown ? Observation<bool>.At(_sessionState == EngineSessionState.Open, _sessionAt) : Observation<bool>.Unknown,
            AudioConnected: _listingKnown && _device?.WindowsConnected is bool audio ? Observation<bool>.At(audio, _listedAt) : Observation<bool>.Unknown);

        bool control = _sessionState == EngineSessionState.Open && _options.SessionMode == EngineSessionMode.Control
            && !_sessionClosing && _mode.HasValue && _modeWriteRequestId is null && !_modeWriteCompletion.IsUncertain
            && !AwarenessWireInFlight;
        var capabilities = HardwareCapabilities;
        if (_features.ObserveConversationAwareness && _awarenessObserved) capabilities |= DeviceCapabilities.ConversationAwareness;
        if (_features.ObserveEar && _earsObserved) capabilities |= DeviceCapabilities.EarDetection;
        return new DeviceState(
            DeviceId: _options.DeviceId,
            DisplayName: string.IsNullOrWhiteSpace(_device?.Name) ? "AirPods" : _device!.Name,
            ModelName: "",
            Origin: DataOrigin.Hardware,
            Sequence: ++_sequence,
            ObservedAt: now,
            Capabilities: capabilities,
            Link: link,
            Battery: new BatteryState(_left, _right, _case),
            Listening: new ListeningState(_mode, control ? _writable : Array.Empty<ListeningMode>()),
            ConversationAwareness: _awareness,
            AdaptiveStrength: Observation<int>.Unknown)
        {
            Ears = _ears,
        };
    }

    // ---- Helpers ---------------------------------------------------------------------------

    private bool IsDisposed
    {
        get { lock (_gate) return _disposed; }
    }

    private static string NewRequestId() => Guid.NewGuid().ToString("D");

    private void Log(string message)
    {
        try
        {
            Diagnostic?.Invoke(this, message);
        }
        catch (Exception)
        {
            // Diagnostics must never break the transport.
        }
    }

    public async ValueTask DisposeAsync()
    {
        EngineConnection? connection;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            connection = _connection;
            _connection = null;
        }
        if (connection is not null) await connection.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// Delivery, actual readback and the request wait's terminal outcome are independent events.
/// All access is serialized by the hardware transport's state lock.
/// </summary>
internal sealed class ModeWriteCompletionGate
{
    private bool _acknowledged;
    private bool _matchingReadback;
    private bool _terminalAcknowledged;
    private bool _terminalRejected;

    public bool IsUncertain { get; private set; }

    /// <summary>The request's own wait has ended (with any outcome).</summary>
    public bool HasCompleted => _terminalAcknowledged || _terminalRejected || IsUncertain;

    // The read loop can outrun the timeout/cancellation continuation. Retain the reservation
    // until Complete records which terminal result actually won that request's wait.
    public bool CanRelease => !IsUncertain
        && (_terminalRejected || (_terminalAcknowledged && _acknowledged && _matchingReadback));

    public void OnEngineReply(EngineOutcome outcome)
    {
        if (outcome == EngineOutcome.Accepted) _acknowledged = true;
        else if (outcome is not (EngineOutcome.Rejected or EngineOutcome.Unsupported)) IsUncertain = true;
    }

    public void ObserveMatchingReadback()
    {
        // A reading received before delivery is acknowledged may predate the actual write.
        if (_acknowledged) _matchingReadback = true;
    }

    public void Complete(CommandOutcome outcome)
    {
        if (outcome == CommandOutcome.Acknowledged) _terminalAcknowledged = true;
        else if (outcome is CommandOutcome.Rejected or CommandOutcome.Unsupported) _terminalRejected = true;
        else IsUncertain = true;
    }
}
