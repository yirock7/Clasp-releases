using System.Collections.ObjectModel;
using System.ComponentModel;
using AppleDeviceHub.Core.Automation;
using AppleDeviceHub.Core.Ble;
using AppleDeviceHub.Core.Connection;
using AppleDeviceHub.Core.Contracts;
using AppleDeviceHub.Core.Resources;
using System.Globalization;

namespace AppleDeviceHub.Core.Presentation;

/// <summary>
/// Coordinates explicit selection and one-shot Windows audio reconnect attempts. A native
/// request never sets Connected by itself; only a complete snapshot with an active endpoint
/// associated by the selected device's exact ContainerId confirms that state.
/// </summary>
public sealed class CompanionConnectionViewModel : ObservableObject, IDisposable
{
    public static readonly TimeSpan MetadataRefreshInterval = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan MetadataRefreshTimeout = TimeSpan.FromSeconds(5);
    /// <summary>How long auto-connect waits for the endpoint to turn active before giving up (one request only).</summary>
    public static readonly TimeSpan ConnectionConfirmationWindow = TimeSpan.FromSeconds(8);
    /// <summary>Total wait for a user-requested connect, counted from the first accepted request.</summary>
    public static readonly TimeSpan UserConnectTotalWindow = TimeSpan.FromSeconds(30);
    /// <summary>A user-requested connect sends one more request if still not connected this long after the first.</summary>
    public static readonly TimeSpan UserConnectRetryAfter = TimeSpan.FromSeconds(8);
    /// <summary>How long a user click waits for Windows to finish a running read (or answer Busy) before giving up.</summary>
    public static readonly TimeSpan UserConnectBusyWaitLimit = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan UserConnectBusyPollInterval = TimeSpan.FromMilliseconds(100);
    public static readonly TimeSpan ConnectionConfirmationInterval = TimeSpan.FromMilliseconds(400);
    public static readonly TimeSpan AutoConnectCooldown = TimeSpan.FromSeconds(30);

    private readonly IAirPodsConnectionController _controller;
    private readonly HubSettings _settings;
    private readonly Action<Action> _dispatch;
    private readonly Func<IdentityConfidence>? _autoConnectIdentity;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _lifetimeToken;
    private int _operationGeneration;
    private readonly ITimer _refreshTimer;
    private ConnectionSnapshot _snapshot = new(
        true, Array.Empty<ConnectionDevice>(), Array.Empty<ConnectionAudioEndpoint>(), null);
    private string? _selectedDeviceId;
    private string? _lastAnnouncedDeviceId;
    private bool _isConnecting;
    private bool _isDisconnecting;
    private bool _selectionClearedByUser;
    private CancellationTokenSource? _activeConnectCancellation;
    private bool _isNearby;
    private bool _rearmRequired;
    private bool _disposed;
    private int _nearbyEpisode;
    private int _attemptedEpisode = -1;
    private DateTimeOffset? _absenceStartedAt;
    private DateTimeOffset? _lastAutoAttemptAt;
    private string? _operationDetail;
    private readonly HashSet<string> _switchedAwayFromIds = new(StringComparer.Ordinal);
    private string? _snapshotError;
    private readonly Action<string>? _log;
    private readonly ConnectionAttemptTracker _origins = new();

    public CompanionConnectionViewModel(
        IAirPodsConnectionController controller,
        HubSettings settings,
        Action<Action> dispatch,
        TimeProvider? timeProvider = null,
        Func<IdentityConfidence>? autoConnectIdentity = null,
        Action<string>? log = null)
    {
        _log = log;
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
        _autoConnectIdentity = autoConnectIdentity;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _lifetimeToken = _lifetime.Token;
        _selectedDeviceId = string.IsNullOrWhiteSpace(settings.AirPodsDeviceId)
            ? null
            : settings.AirPodsDeviceId;
        _lastAnnouncedDeviceId = _selectedDeviceId;

        _controller.Changed += OnControllerChanged;
        _settings.PropertyChanged += OnSettingsChanged;
        _refreshTimer = _timeProvider.CreateTimer(
            OnRefreshTimer,
            null,
            MetadataRefreshInterval,
            MetadataRefreshInterval);
    }

    public ObservableCollection<ConnectionDevice> Devices { get; } = new();

    /// <summary>
    /// The last complete snapshot as the view model understood it: devices already matched to their
    /// endpoints by exact ContainerId, and the audio endpoints with normalized container ids.
    /// </summary>
    public ConnectionSnapshot Snapshot => _snapshot;

    /// <summary>
    /// Raised on the dispatcher thread after every complete snapshot has been applied (including
    /// the periodic refresh), once the selection and connection state are up to date. Incomplete
    /// snapshots never raise it: they are not proof of a disconnect.
    /// </summary>
    public event EventHandler<ConnectionSnapshot>? SnapshotApplied;

    /// <summary>
    /// Raised when the selected device id actually changes (selected, cleared, migrated or
    /// auto-selected). Unlike <c>PropertyChanged</c> for <see cref="SelectedDeviceId"/>, which is
    /// re-raised on every refresh so bound lists stay in sync, this does not fire for a refresh
    /// that keeps the same device.
    /// </summary>
    public event EventHandler? SelectedDeviceChanged;

    /// <summary>
    /// Raised on the dispatcher thread when the user (not auto-connect) asks to connect, before the
    /// request reaches Windows. The host treats it as a request to move audio to the AirPods.
    /// </summary>
    public event EventHandler? UserConnectRequested;

    /// <summary>
    /// Raised on the dispatcher thread when Clasp's own "connect automatically when nearby" starts a
    /// reconnect, before the request reaches Windows. The host marks that connect as an unverified
    /// nearby signal, so nothing but the reconnect follows it.
    /// </summary>
    public event EventHandler? AutoConnectStarted;

    /// <summary>
    /// Raised on the dispatcher thread when an auto-connect attempt is over (connected, failed, timed
    /// out), so the host can forget its mark. Not raised if the selection changed meanwhile; the host
    /// clears the mark on a selection change itself.
    /// </summary>
    public event EventHandler? AutoConnectEnded;

    /// <summary>Raised on the dispatcher thread once a user disconnect is confirmed by a fresh snapshot.</summary>
    public event EventHandler? UserDisconnectConfirmed;

    /// <summary>
    /// The selected paired device ID. Setting this property selects only a current device with
    /// a valid ContainerId. Clearing is explicit through
    /// <see cref="ClearSelection"/> so a temporary empty ComboBox does not erase saved identity.
    /// </summary>
    public string? SelectedDeviceId
    {
        get => _selectedDeviceId;
        set
        {
            if (_disposed || string.IsNullOrWhiteSpace(value)) return;
            var selected = _snapshot.Devices.FirstOrDefault(device =>
                string.Equals(device.Id, value, StringComparison.Ordinal));
            if (selected is null || !ConnectionIdentity.IsUsableContainerId(selected.ContainerId))
                return;
            if (string.Equals(_selectedDeviceId, selected.Id, StringComparison.Ordinal)) return;

            _operationGeneration++;
            CancelActiveConnect();
            _selectedDeviceId = selected.Id;
            _settings.AirPodsDeviceId = selected.Id;
            _operationDetail = null;
            SetConnecting(false);
            RaiseSelectionProperties();
            TryAutoConnectCurrentEpisode();
        }
    }
    public string? SelectedDeviceName => SelectedDevice?.Name;

    public bool HasSelectedDevice => SelectedDevice is not null;

    /// <summary>True only when a fresh Windows endpoint snapshot reports this device's endpoint active.</summary>
    public bool IsConnected => SelectedDevice?.IsConnected == true;

    /// <summary>True when Windows uses one of this device's endpoints as a default render output.</summary>
    public bool IsCurrentOutput => SelectedDevice?.IsCurrentOutput == true;

    public bool IsConnecting => _isConnecting;

    public bool IsDisconnecting => _isDisconnecting;

    /// <summary>
    /// A user click can start a connect. This deliberately ignores the controller's momentary busy
    /// flag (a metadata read runs every time the flyout opens and on a timer, and the flag has no
    /// change notification): a click during a read waits for it instead of being dropped.
    /// </summary>
    public bool CanConnect => !_disposed
        && !_isConnecting
        && !_isDisconnecting
        && SelectedDevice is { IsConnected: false, CanReconnect: true };

    /// <summary>Auto-connect only starts when Windows is idle; it never queues behind a read.</summary>
    private bool CanConnectNow => CanConnect && !_controller.IsBusy;

    /// <summary>The selected device is connected and its audio driver supports the one-shot requests.</summary>
    public bool CanDisconnect => !_disposed
        && !_isConnecting
        && !_isDisconnecting
        && !_controller.IsBusy
        && SelectedDevice is { IsConnected: true, CanReconnect: true };

    /// <summary>
    /// Windows' own battery reading for the selected AirPods (one combined percent over the Bluetooth
    /// Handsfree link), or null when they are not connected to this PC or Windows reports none.
    /// </summary>
    public int? WindowsBatteryPercent
        => SelectedDevice is { IsConnected: true, WindowsBatteryPercent: >= 0 and <= 100 and var percent } ? percent : null;

    /// <summary>
    /// The word for the selected device in text: "AirPods", "Beats" or "headphones" by its model id.
    /// "AirPods" when nothing is selected or the id is unknown, which is the original wording.
    /// </summary>
    public string SelectedDeviceNoun
        => SelectedDevice?.ModelId is ushort model ? AppleModelCatalog.FamilyNounOf(model) : "AirPods";

    /// <summary>Windows' reading as a gauge (labelled with the device noun, "AirPods" by default), for the battery ring; unknown when there is none.</summary>
    public BatteryItemViewModel WindowsBattery { get; } = new("AirPods", string.Format(CultureInfo.CurrentCulture, Strings.Companion_Battery_ReportedByWindows, "AirPods"));

    /// <summary>"Battery 80%" while Windows reports a reading, otherwise empty.</summary>
    public string WindowsBatteryText => WindowsBatteryPercent is int percent
        ? string.Format(CultureInfo.CurrentCulture, Strings.Companion_Battery_Text, percent.ToString(CultureInfo.CurrentCulture))
        : "";

    /// <summary>
    /// Why there is no battery line, when Clasp knows: the selected AirPods are connected but Windows
    /// lists no Handsfree service for them, and Windows reports battery only through that service.
    /// Empty otherwise, including when Windows' node list could not be read.
    /// </summary>
    public string WindowsBatteryHint
        => WindowsBatteryPercent is null && SelectedDevice is { IsConnected: true, HasHandsfreeService: false }
            ? string.Format(CultureInfo.CurrentCulture, Strings.Companion_Battery_HandsfreeOffHint, SelectedDeviceNoun)
            : "";

    /// <summary>
    /// True when Windows lists no paired AirPods with an audio endpoint, so the user still has to
    /// pair them in Windows before Clasp can connect.
    /// </summary>
    public bool NeedsPairing => _snapshot.IsComplete && !_snapshot.Devices.Any(IsLikelySupportedDevice);

    public string StatusText
    {
        get
        {
            if (_isConnecting) return Strings.Companion_Status_Connecting;
            if (_isDisconnecting) return Strings.Companion_Status_Disconnecting;
            var selected = SelectedDevice;
            if (selected is null)
            {
                if (_selectedDeviceId is not null) return Strings.Companion_Status_DeviceUnavailable;
                return _snapshot.Devices.Count == 0 ? Strings.Companion_Status_NoPairedDevices : Strings.Companion_Status_ChooseDevice;
            }

            if (selected.IsConnected)
                return selected.IsCurrentOutput ? Strings.Companion_Status_ConnectedAsOutput : Strings.Companion_Status_Connected;
            if (!selected.HasAvailableAudioEndpoint)
                return Strings.Companion_Status_NotReadyForAudio;
            if (selected.CanReconnect) return Strings.Companion_Status_ReadyToConnect;
            return Strings.Companion_Status_CannotConnect;
        }
    }

    public string DetailText
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_operationDetail)) return _operationDetail;
            if (!string.IsNullOrWhiteSpace(_snapshotError)) return _snapshotError;
            return SelectedDevice?.Detail ?? _snapshot.DetailText ?? "";
        }
    }

    /// <summary>Refresh paired identities and Windows audio endpoint metadata asynchronously.</summary>
    public async Task RefreshAsync()
    {
        if (_disposed || !await _refreshGate.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeToken);
            Task<ConnectionSnapshot> read;
            try
            {
                read = _controller.ReadSnapshotAsync(timeout.Token);
                var snapshot = await read
                    .WaitAsync(MetadataRefreshTimeout, _lifetimeToken)
                    .ConfigureAwait(false);
                if (_disposed) return;

                await DispatchAsync(() => ApplySnapshot(snapshot)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                timeout.Cancel();
                await DispatchAsync(() =>
                {
                    _snapshotError = Strings.Companion_Detail_RefreshSlow;
                    Raise(nameof(DetailText));
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetimeToken.IsCancellationRequested)
            {
                // The owner closed the view while a metadata read was still pending.
            }
            catch (OperationCanceledException) when (!_lifetimeToken.IsCancellationRequested)
            {
                await DispatchAsync(() =>
                {
                    _snapshotError = Strings.Companion_Detail_RefreshCanceled;
                    Raise(nameof(DetailText));
                }).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                await DispatchAsync(() =>
                {
                    _snapshotError = Strings.Companion_Detail_RefreshFailed; Log($"Refresh failed ({exception.GetType().Name}, 0x{exception.HResult:X8}).");
                    Raise(nameof(DetailText));
                }).ConfigureAwait(false);
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <summary>
    /// Make one Windows reconnect attempt for the selected paired device because the user asked.
    /// </summary>
    public Task ConnectAsync() => StartConnect(userRequested: true, ConnectionOrigin.User);

    /// <summary>
    /// Same as <see cref="ConnectAsync()"/> for an explicit request that did not come from a click
    /// (a Clasp hotkey, or an external request the user allowed). The origin belongs to this attempt only.
    /// </summary>
    public Task ConnectAsync(ConnectionOrigin origin) => StartConnect(userRequested: true, origin);

    /// <summary>
    /// The origin of the connect attempt that is producing (or has just produced) this device's connection,
    /// or <see cref="ConnectionOrigin.None"/> when no Clasp attempt is responsible. Never time-based leftovers:
    /// it is cleared by every attempt outcome that did not connect.
    /// </summary>
    public ConnectionOrigin CurrentConnectionOrigin
        => _origins.Current(_selectedDeviceId, _timeProvider.GetUtcNow());

    /// <summary>The host has planned the connection's actions: the origin is spent.</summary>
    public void ConsumeConnectionOrigin() => _origins.Consume();

    /// <summary>Forget any origin (sleep/resume, a repair, a confirmed disconnect).</summary>
    public void ClearConnectionOrigin() => _origins.Clear();

    private Task StartConnect(bool userRequested, ConnectionOrigin origin = ConnectionOrigin.NearbyAuto)
    {
        if (!(userRequested ? CanConnect : CanConnectNow) || SelectedDevice is not { } selected)
        {
            if (userRequested) Log($"Connect: click ignored ({DescribeConnectBlocker()}).");
            return Task.CompletedTask;
        }
        if (userRequested)
        {
            Log("Connect: click accepted.");
            try { UserConnectRequested?.Invoke(this, EventArgs.Empty); }
            catch (Exception) { /* a subscriber failure must not block the connect */ }
        }
        else
        {
            try { AutoConnectStarted?.Invoke(this, EventArgs.Empty); }
            catch (Exception) { /* a subscriber failure must not block the connect */ }
        }
        var deviceId = selected.Id;
        var containerId = selected.ContainerId;
        // An automatic attempt is always NearbyAuto (unless an explicit attempt still owns the connection).
        var attemptId = _origins.Begin(userRequested ? origin : ConnectionOrigin.NearbyAuto, deviceId, _timeProvider.GetUtcNow());
        var generation = ++_operationGeneration;
        var attemptGeneration = _nearbyEpisode;
        var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeToken);
        _activeConnectCancellation = operationCancellation;
        if (_isNearby) _attemptedEpisode = _nearbyEpisode;

        SetConnecting(true);
        _operationDetail = Strings.Companion_Detail_AskingToConnect;
        Raise(nameof(DetailText));
        return ConnectCoreAsync(deviceId, containerId, generation, attemptGeneration, operationCancellation, userRequested, attemptId);
    }

    private string DescribeConnectBlocker()
    {
        if (_disposed) return "closed";
        if (_isConnecting) return "already connecting";
        if (_isDisconnecting) return "disconnecting";
        return SelectedDevice switch
        {
            null => "no device selected",
            { IsConnected: true } => "already connected",
            _ => "no reconnect path",
        };
    }

    private void Log(string message)
    {
        try { _log?.Invoke(message); }
        catch (Exception) { /* diagnostics must never break the connect */ }
    }

    private long ElapsedMs(DateTimeOffset since)
        => (long)(_timeProvider.GetUtcNow() - since).TotalMilliseconds;

    /// <summary>
    /// Sends the user's reconnect request. If Windows is mid-read (or answers Busy) the click waits,
    /// bounded by <see cref="UserConnectBusyWaitLimit"/>, instead of vanishing. Requests never overlap:
    /// this only sends once the controller reports idle, and the caller awaits each result.
    /// </summary>
    private async Task<ConnectionAttemptResult> SendUserRequestAsync(
        string deviceId,
        string containerId,
        int generation,
        CancellationTokenSource operationCancellation,
        DateTimeOffset startedAt)
    {
        var token = operationCancellation.Token;
        var limit = _timeProvider.GetUtcNow() + UserConnectBusyWaitLimit;
        var waiting = false;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            // Every pass (and so every request) re-checks, on the UI thread, that this connect is
            // still current: same generation, not disposed, the device still selected.
            await EnsureCurrentAsync(generation, deviceId, operationCancellation).ConfigureAwait(false);
            if (_controller.IsBusy)
            {
                if (_timeProvider.GetUtcNow() >= limit) return BusyGiveUp(startedAt);
            }
            else
            {
                var result = await _controller.ReconnectAsync(deviceId, containerId, token).ConfigureAwait(false);
                Log($"Connect: request 1 result {result.Kind} after {ElapsedMs(startedAt)} ms.");
                if (result.Kind != ConnectionAttemptKind.Busy) return result;
                if (_timeProvider.GetUtcNow() >= limit) return BusyGiveUp(startedAt);
            }

            if (!waiting)
            {
                waiting = true;
                Log("Connect: waiting for Windows to finish its current operation.");
                await DispatchOrCancelAsync(generation, deviceId, operationCancellation, () =>
                {
                    _operationDetail = Strings.Companion_Detail_WindowsBusyWaiting;
                    Raise(nameof(DetailText));
                }).ConfigureAwait(false);
            }
            await Task.Delay(UserConnectBusyPollInterval, _timeProvider, token).ConfigureAwait(false);
        }
    }

    private ConnectionAttemptResult BusyGiveUp(DateTimeOffset startedAt)
    {
        Log($"Connect: gave up, Windows stayed busy for {ElapsedMs(startedAt)} ms.");
        return new(ConnectionAttemptKind.Busy, Strings.Companion_Detail_WindowsBusyGaveUp);
    }

    /// <summary>Throws OperationCanceledException (and cancels the operation) unless this connect is still current.</summary>
    private async Task EnsureCurrentAsync(
        int generation,
        string deviceId,
        CancellationTokenSource operationCancellation)
    {
        operationCancellation.Token.ThrowIfCancellationRequested();
        await DispatchOrCancelAsync(generation, deviceId, operationCancellation, static () => { })
            .ConfigureAwait(false);
    }

    /// <summary>Runs on the UI thread if this connect is still current; otherwise cancels it.</summary>
    private async Task DispatchOrCancelAsync(
        int generation,
        string deviceId,
        CancellationTokenSource operationCancellation,
        Action action)
    {
        if (!await DispatchForOperationAsync(generation, deviceId, action).ConfigureAwait(false))
        {
            try { operationCancellation.Cancel(); } catch (ObjectDisposedException) { }
            operationCancellation.Token.ThrowIfCancellationRequested();
        }
    }

    private async Task ConnectCoreAsync(
        string deviceId,
        string containerId,
        int generation,
        int attemptGeneration,
        CancellationTokenSource operationCancellation,
        bool userRequested,
        long attemptId)
    {
        var operationToken = operationCancellation.Token;
        var startedAt = _timeProvider.GetUtcNow();
        var producedConnection = false;
        try
        {
            ConnectionAttemptResult result;
            if (userRequested)
            {
                result = await SendUserRequestAsync(deviceId, containerId, generation, operationCancellation, startedAt)
                    .ConfigureAwait(false);
            }
            else
            {
                result = await _controller.ReconnectAsync(deviceId, containerId, operationToken)
                    .ConfigureAwait(false);
                Log($"Connect: auto-connect request result {result.Kind} after {ElapsedMs(startedAt)} ms.");
            }
            if (!await DispatchForOperationAsync(generation, deviceId, () =>
            {
                if (result.Kind != ConnectionAttemptKind.Attempted)
                {
                    _operationDetail = result.Detail;
                    SetConnecting(false);
                    RaiseStatusProperties();
                }
                else
                {
                    // A successful KS response means the driver attempted to connect, not that
                    // the Bluetooth device actually connected.
                    _operationDetail = Strings.Companion_Detail_WaitingForAudio;
                    Raise(nameof(DetailText));
                }
            }).ConfigureAwait(false))
                return;

            if (result.Kind != ConnectionAttemptKind.Attempted) return;

            // The wait is measured from the first accepted request. A user-requested connect waits
            // longer and sends exactly one more request partway through; auto-connect does neither.
            var confirmationStart = _timeProvider.GetUtcNow();
            var deadline = confirmationStart + (userRequested ? UserConnectTotalWindow : ConnectionConfirmationWindow);
            var retryAt = confirmationStart + UserConnectRetryAfter;
            var retried = false;
            var skipLogged = false;
            while (_timeProvider.GetUtcNow() < deadline && !operationToken.IsCancellationRequested)
            {
                await RefreshAsync().ConfigureAwait(false);

                // The observable collection belongs to the UI thread; read connection state only
                // inside the same generation-fenced dispatcher callback that updates UI state.
                var connected = false;
                var stillTarget = false;
                if (!await DispatchForOperationAsync(generation, deviceId, () =>
                {
                    connected = IsConnected;
                    stillTarget = SelectedDevice is { IsConnected: false, CanReconnect: true };
                }).ConfigureAwait(false))
                    return;

                if (connected)
                {
                    Log($"Connect: connected after {ElapsedMs(startedAt)} ms.");
                    producedConnection = true;
                    await DispatchForOperationAsync(generation, deviceId, () =>
                    {
                        _operationDetail = null;
                        SetConnecting(false);
                        RaiseStatusProperties();
                    }).ConfigureAwait(false);
                    return;
                }

                if (userRequested && !retried && _timeProvider.GetUtcNow() >= retryAt)
                {
                    if (!stillTarget)
                    {
                        if (!skipLogged)
                        {
                            skipLogged = true;
                            Log("Connect: retry skipped, the device is no longer a valid reconnect target.");
                        }
                    }
                    else
                    {
                        await EnsureCurrentAsync(generation, deviceId, operationCancellation).ConfigureAwait(false);
                        Log($"Connect: retry sent, not connected after {ElapsedMs(startedAt)} ms.");
                        var retry = await _controller.ReconnectAsync(deviceId, containerId, operationToken)
                            .ConfigureAwait(false);
                        Log($"Connect: request 2 result {retry.Kind} after {ElapsedMs(startedAt)} ms.");
                        switch (retry.Kind)
                        {
                            case ConnectionAttemptKind.Attempted:
                            case ConnectionAttemptKind.TimedOut:
                                // TimedOut means a native call is still running, so Windows has the
                                // request; keep waiting and never send a third.
                                retried = true;
                                break;
                            case ConnectionAttemptKind.Busy:
                                // The request never reached the driver (a read was running), so it
                                // does not count: try again on the next poll, inside the same window.
                                break;
                            default:
                                // Unsupported or Failed: Windows refuses outright. Stop and say why.
                                await DispatchForOperationAsync(generation, deviceId, () =>
                                {
                                    _operationDetail = retry.Detail;
                                    SetConnecting(false);
                                    RaiseStatusProperties();
                                }).ConfigureAwait(false);
                                return;
                        }
                    }
                }

                await Task.Delay(ConnectionConfirmationInterval, _timeProvider, operationToken)
                    .ConfigureAwait(false);
            }

            if (operationToken.IsCancellationRequested) return;
            Log($"Connect: timed out after {ElapsedMs(startedAt)} ms.");
            await DispatchForOperationAsync(generation, deviceId, () =>
            {
                _operationDetail = userRequested
                    ? Strings.Companion_Detail_ConnectTimedOutUser
                    : Strings.Companion_Detail_ConnectTimedOutAuto;
                SetConnecting(false);
                if (_nearbyEpisode == attemptGeneration)
                {
                    // A transport loss and an intentional iPhone switch look the same to Windows.
                    // Require a new nearby episode after a full absence cooldown before retrying.
                    _rearmRequired = true;
                    if (_isNearby) _absenceStartedAt = null;
                    else _absenceStartedAt ??= _timeProvider.GetUtcNow();
                }
                RaiseStatusProperties();
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (operationToken.IsCancellationRequested)
        {
            // Selection changes and view shutdown cancel requests that have not reached Windows.
            // A driver call already issued cannot be canceled by the application.
        }
        catch (Exception exception)
        {
            await DispatchForOperationAsync(generation, deviceId, () =>
            {
                _operationDetail = Strings.Companion_Detail_ConnectFailed; Log($"Connect: failed ({exception.GetType().Name}, 0x{exception.HResult:X8}).");
                SetConnecting(false);
                RaiseStatusProperties();
            }).ConfigureAwait(false);
        }
        finally
        {
            // Every way out of the attempt ends its origin: only a confirmed connection keeps it, and only
            // for the policy that is about to plan this connection's actions. Done here, not inside the
            // dispatcher callback, so a cancelled or superseded attempt cannot skip it.
            _origins.Complete(attemptId, producedConnection, _timeProvider.GetUtcNow());
            await DispatchForOperationAsync(generation, deviceId, () =>
            {
                SetConnecting(false);
                RaiseStatusProperties();
                if (ReferenceEquals(_activeConnectCancellation, operationCancellation))
                    _activeConnectCancellation = null;
                if (!userRequested)
                {
                    try { AutoConnectEnded?.Invoke(this, EventArgs.Empty); }
                    catch (Exception) { /* a subscriber failure must not break cleanup */ }
                }
            }).ConfigureAwait(false);
            operationCancellation.Dispose();
        }
    }

    /// <summary>
    /// Ask Windows to disconnect the selected AirPods' audio (the pairing is kept), then wait for a
    /// fresh snapshot to confirm. Auto-connect will not undo it while the AirPods stay nearby: the
    /// connected-to-disconnected transition re-arms it only after a full absence.
    /// </summary>
    public Task DisconnectAsync()
    {
        if (!CanDisconnect || SelectedDevice is not { } selected) return Task.CompletedTask;
        var generation = ++_operationGeneration;
        CancelActiveConnect();
        SetDisconnecting(true);
        _operationDetail = Strings.Companion_Detail_AskingToDisconnect;
        Raise(nameof(DetailText));
        return DisconnectCoreAsync(selected.Id, selected.ContainerId, generation);
    }

    private async Task DisconnectCoreAsync(string deviceId, string containerId, int generation)
    {
        try
        {
            var result = await _controller.DisconnectAsync(deviceId, containerId, _lifetimeToken).ConfigureAwait(false);
            if (result.Kind != ConnectionAttemptKind.Attempted)
            {
                await DispatchForOperationAsync(generation, deviceId, () => _operationDetail = result.Detail).ConfigureAwait(false);
                return;
            }

            var deadline = _timeProvider.GetUtcNow() + ConnectionConfirmationWindow;
            while (_timeProvider.GetUtcNow() < deadline && !_lifetimeToken.IsCancellationRequested)
            {
                await RefreshAsync().ConfigureAwait(false);
                var connected = true;
                if (!await DispatchForOperationAsync(generation, deviceId, () => connected = IsConnected).ConfigureAwait(false))
                    return;
                if (!connected)
                {
                    await DispatchForOperationAsync(generation, deviceId, () =>
                    {
                        _operationDetail = null;
                        try { UserDisconnectConfirmed?.Invoke(this, EventArgs.Empty); }
                        catch (Exception) { /* a subscriber failure must not break the view model */ }
                    }).ConfigureAwait(false);
                    return;
                }
                await Task.Delay(ConnectionConfirmationInterval, _timeProvider, _lifetimeToken).ConfigureAwait(false);
            }

            await DispatchForOperationAsync(generation, deviceId, () =>
                _operationDetail = Strings.Companion_Detail_DisconnectStillActive)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetimeToken.IsCancellationRequested)
        {
            // The view closed while the request was pending.
        }
        catch (Exception)
        {
            await DispatchForOperationAsync(generation, deviceId, () =>
                _operationDetail = Strings.Companion_Detail_DisconnectFailed)
                .ConfigureAwait(false);
        }
        finally
        {
            await DispatchAsync(() =>
            {
                SetDisconnecting(false);
                RaiseStatusProperties();
            }).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reports presence from a fresh, uniquely attributed nearby observation. A reconnect is
    /// attempted at most once per false-to-true episode. After a connected device disappears,
    /// a fresh episode is required and the source must have been absent for the cooldown.
    /// </summary>
    public void NotifyNearbyEpisode(bool isPresent)
    {
        if (_disposed) return;
        try
        {
            _dispatch(() =>
            {
                if (_disposed) return;
                var now = _timeProvider.GetUtcNow();
                if (!isPresent)
                {
                    if (_isNearby) _absenceStartedAt = now;
                    else if (_rearmRequired && _absenceStartedAt is null) _absenceStartedAt = now;
                    _isNearby = false;
                    Raise(nameof(DetailText));
                    return;
                }

                if (!_isNearby)
                {
                    _nearbyEpisode++;
                    if (_rearmRequired)
                    {
                        var absentLongEnough = _absenceStartedAt is DateTimeOffset absentAt
                            && now - absentAt >= AutoConnectCooldown;
                        _rearmRequired = !absentLongEnough;
                    }
                    _isNearby = true;
                }
                _absenceStartedAt = null;
                TryAutoConnectCurrentEpisode();
                Raise(nameof(DetailText));
            });
        }
        catch (Exception)
        {
            // Dispatcher shutdown must not escape from a BLE observation callback.
        }
    }
    public void ClearSelection()
    {
        if (_disposed) return;
        _operationGeneration++;
        // Same as a selection change: a connect still waiting on Windows must never send for a
        // device the user just deselected.
        CancelActiveConnect();
        _selectedDeviceId = null;
        _selectionClearedByUser = true;
        _settings.AirPodsDeviceId = "";
        _operationDetail = null;
        SetConnecting(false);
        RaiseSelectionProperties();
    }
    /// <summary>
    /// The selected paired device from the last complete snapshot (including its Apple model id
    /// and optional Windows battery), or null when none is selected or it is not listed.
    /// </summary>
    public ConnectionDevice? SelectedDevice
        => _selectedDeviceId is null
            ? null
            : _snapshot.Devices.FirstOrDefault(device => string.Equals(device.Id, _selectedDeviceId, StringComparison.Ordinal));

    private void ApplySnapshot(ConnectionSnapshot snapshot)
    {
        if (_disposed) return;
        if (!snapshot.IsComplete)
        {
            _snapshotError = snapshot.DetailText ?? Strings.Companion_Detail_SnapshotUnreadable;
            Raise(nameof(DetailText));
            return;
        }

        var oldSelectedConnected = IsConnected;
        var endpoints = (snapshot.AudioEndpoints ?? Array.Empty<ConnectionAudioEndpoint>())
            .Where(endpoint => endpoint is not null
                && !string.IsNullOrWhiteSpace(endpoint.Id)
                && ConnectionIdentity.IsUsableContainerId(endpoint.ContainerId))
            .Select(endpoint => endpoint with
            {
                ContainerId = ConnectionIdentity.NormalizeContainerId(endpoint.ContainerId)!,
            })
            .ToArray();
        var validDevices = (snapshot.Devices ?? Array.Empty<ConnectionDevice>())
            .Where(device => device is not null
                && !string.IsNullOrWhiteSpace(device.Id)
                && ConnectionIdentity.IsUsableContainerId(device.ContainerId))
            .ToArray();
        var identityCountsByContainer = validDevices
            .GroupBy(device => ConnectionIdentity.NormalizeContainerId(device.ContainerId)!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key,
                group => group.Select(device => device.Id).Distinct(StringComparer.Ordinal).Count(),
                StringComparer.OrdinalIgnoreCase);
        var safeDevices = new List<ConnectionDevice>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var device in validDevices)
        {
            if (!seenIds.Add(device.Id)) continue;

            var containerId = ConnectionIdentity.NormalizeContainerId(device.ContainerId)!;
            var endpointCandidates = endpoints.Where(endpoint =>
                string.Equals(endpoint.ContainerId, containerId, StringComparison.OrdinalIgnoreCase)).ToArray();
            var ambiguousContainer = identityCountsByContainer[containerId] > 1;
            var matches = ambiguousContainer ? Array.Empty<ConnectionAudioEndpoint>() : endpointCandidates;
            var connected = matches.Any(endpoint => endpoint.IsAvailable);
            var currentOutput = matches.Any(endpoint => endpoint.IsCurrentOutput);
            var canReconnect = matches.Any(endpoint => endpoint.SupportsReconnect);
            var hasEndpoint = endpointCandidates.Length > 0;
            var detail = ambiguousContainer
                ? Strings.Companion_Detail_AmbiguousAudio
                : hasEndpoint
                    ? device.Detail
                    : Strings.Companion_Detail_NoAudioOutput;
            safeDevices.Add(device with
            {
                Name = string.IsNullOrWhiteSpace(device.Name) ? Strings.Companion_Device_UnnamedBluetooth : device.Name,
                ContainerId = containerId,
                HasAvailableAudioEndpoint = hasEndpoint,
                IsConnected = connected,
                IsCurrentOutput = currentOutput,
                CanReconnect = canReconnect,
                Detail = detail,
            });
        }
        _snapshot = snapshot with
        {
            Devices = safeDevices
                .OrderBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(device => device.Id, StringComparer.Ordinal)
                .ToArray(),
            AudioEndpoints = endpoints,
        };
        Devices.Clear();
        foreach (var device in _snapshot.Devices) Devices.Add(device);
        _snapshotError = null;

        MigrateLegacyTargetIfUnambiguous();
        AutoSelectSoleAirPods();
        var switched = SwitchFromDriverlessSelection();
        var newSelectedConnected = IsConnected;
        if (newSelectedConnected && !switched) _operationDetail = null;
        if (oldSelectedConnected && !newSelectedConnected)
        {
            _rearmRequired = true;
            _absenceStartedAt = _isNearby ? null : _timeProvider.GetUtcNow();
        }
        RaiseSelectionProperties();
        TryAutoConnectCurrentEpisode();
        try { SnapshotApplied?.Invoke(this, _snapshot); }
        catch (Exception)
        {
            // A subscriber failure must not break the connection view model.
        }
    }

    private void MigrateLegacyTargetIfUnambiguous()
    {
        if (!string.IsNullOrWhiteSpace(_settings.AirPodsDeviceId)) return;
        if (!ConnectionIdentity.IsUsableContainerId(_settings.AudioTargetContainerId)
            || string.IsNullOrWhiteSpace(_settings.AudioTargetEndpointId))
            return;

        var savedContainer = ConnectionIdentity.NormalizeContainerId(_settings.AudioTargetContainerId)!;
        var savedEndpointMatches = _snapshot.AudioEndpoints.Any(endpoint =>
            string.Equals(endpoint.Id, _settings.AudioTargetEndpointId, StringComparison.Ordinal)
            && string.Equals(endpoint.ContainerId, savedContainer, StringComparison.OrdinalIgnoreCase));
        if (!savedEndpointMatches) return;

        var matches = _snapshot.Devices.Where(device =>
            string.Equals(device.ContainerId, savedContainer, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1) return;

        _selectedDeviceId = matches[0].Id;
        _settings.AirPodsDeviceId = matches[0].Id;
    }

    /// <summary>
    /// With nothing selected, picks the paired AirPods when exactly one paired device with a Windows
    /// audio endpoint is AirPods, so Connect works without a trip to Settings. A listed AirPods
    /// model id decides first; "AirPods" in the name is the fallback. Beats are picked only when no
    /// AirPods candidate exists at all and exactly one listed Beats model has an endpoint, so an
    /// owner of both keeps AirPods and two AirPods plus a Beats picks nothing. Other Apple audio
    /// devices are never picked, and an explicit Clear in Settings is respected for the session.
    /// </summary>
    private void AutoSelectSoleAirPods()
    {
        if (_selectedDeviceId is not null || _selectionClearedByUser
            || !string.IsNullOrWhiteSpace(_settings.AirPodsDeviceId))
            return;
        var byModel = _snapshot.Devices.Where(device => device.HasAvailableAudioEndpoint && IsListedAirPodsModel(device)).ToArray();
        var candidates = byModel.Length > 0 ? byModel : _snapshot.Devices.Where(IsLikelyAirPods).ToArray();
        if (candidates.Length == 0)
            candidates = _snapshot.Devices.Where(device => device.HasAvailableAudioEndpoint && IsListedBeatsModel(device)).ToArray();
        if (candidates.Length != 1) return;
        _selectedDeviceId = candidates[0].Id;
        _settings.AirPodsDeviceId = candidates[0].Id;
    }

    /// <summary>
    /// If the selected device is not connected and Windows reports its drivers are gone, it can
    /// never connect, so Clasp moves the selection to a working replacement and says so. It only
    /// switches when exactly one replacement exists: a working AirPods (by model id or name) first,
    /// and only when there is none, a listed Beats model; it needs an audio endpoint, reconnect
    /// support and a usable ContainerId. It never switches mid connect or disconnect, and never
    /// switches away from the same device twice in a session, so choosing the old pair again in
    /// Settings is respected.
    /// </summary>
    private bool SwitchFromDriverlessSelection()
    {
        if (_isConnecting || _isDisconnecting) return false;
        var old = SelectedDevice;
        if (old is null || old.IsConnected
            || !string.Equals(old.Detail, ConnectionDriverState.MissingDriverDetail, StringComparison.Ordinal)
            || _switchedAwayFromIds.Contains(old.Id))
            return false;

        var others = _snapshot.Devices.Where(device =>
            !string.Equals(device.Id, old.Id, StringComparison.Ordinal)
            && device.HasAvailableAudioEndpoint
            && device.CanReconnect
            && ConnectionIdentity.IsUsableContainerId(device.ContainerId)).ToArray();
        var candidates = others.Where(IsLikelyAirPods).ToArray();
        if (candidates.Length == 0)
            candidates = others.Where(IsListedBeatsModel).ToArray();
        if (candidates.Length != 1) return false;

        var replacement = candidates[0];
        _switchedAwayFromIds.Add(old.Id);
        _selectedDeviceId = replacement.Id;
        _settings.AirPodsDeviceId = replacement.Id;
        _operationDetail = string.Format(CultureInfo.CurrentCulture, Strings.Companion_Detail_SwitchedFromDriverless, replacement.Name, old.Name);
        return true;
    }

    private static bool IsListedAirPodsModel(ConnectionDevice device)
        => device.ModelId is ushort model && AppleModelCatalog.Find(model)?.Family == ModelFamily.AirPods;

    private static bool IsListedBeatsModel(ConnectionDevice device)
        => device.ModelId is ushort model && AppleModelCatalog.Find(model)?.Family == ModelFamily.Beats;

    /// <summary>A paired device with a Windows audio endpoint that is AirPods by model id or by name.</summary>
    public static bool IsLikelyAirPods(ConnectionDevice device)
        => device.HasAvailableAudioEndpoint
            && (IsListedAirPodsModel(device) || device.Name.Contains("AirPods", StringComparison.OrdinalIgnoreCase));

    /// <summary>A device Clasp can work with: <see cref="IsLikelyAirPods"/>, or a listed Beats model with an audio endpoint.</summary>
    public static bool IsLikelySupportedDevice(ConnectionDevice device)
        => IsLikelyAirPods(device) || (device.HasAvailableAudioEndpoint && IsListedBeatsModel(device));

    private void TryAutoConnectCurrentEpisode()
    {
        if (_disposed || !_isNearby || _rearmRequired
            || !_settings.ObserveNearbyAirPods || !_settings.AutoConnectAirPods
            || !HasIndependentNearbyIdentity()
            || _attemptedEpisode == _nearbyEpisode
            || IsConnected || !CanConnectNow)
            return;

        var now = _timeProvider.GetUtcNow();
        if (_lastAutoAttemptAt is DateTimeOffset previous && now - previous < AutoConnectCooldown) return;
        _attemptedEpisode = _nearbyEpisode;
        _lastAutoAttemptAt = now;
        _ = StartConnect(userRequested: false);
    }

    private bool HasIndependentNearbyIdentity()
    {
        try { return _autoConnectIdentity?.Invoke().HasIndependentClue() == true; }
        catch (Exception) { return false; }
    }

    private void OnControllerChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        try
        {
            _dispatch(() =>
            {
                if (_disposed) return;
                RaiseStatusProperties();
                _ = RefreshAsync();
            });
        }
        catch (Exception)
        {
            // Native notification handlers must not throw back into Windows.
        }
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed) return;
        if (e.PropertyName is nameof(HubSettings.AutoConnectAirPods)
            or nameof(HubSettings.ObserveNearbyAirPods))
        {
            try { _dispatch(() => { if (!_disposed) TryAutoConnectCurrentEpisode(); }); }
            catch (Exception) { /* settings UI may be closing */ }
        }
    }

    private void OnRefreshTimer(object? state)
    {
        if (_disposed) return;
        try { _dispatch(() => { if (!_disposed) _ = RefreshAsync(); }); }
        catch (Exception) { /* the owner window is closing */ }
    }

    private Task DispatchAsync(Action action)
    {
        if (_disposed) return Task.CompletedTask;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _dispatch(() =>
            {
                if (_disposed)
                {
                    completion.TrySetResult();
                    return;
                }
                try
                {
                    action();
                    completion.TrySetResult();
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            });
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
        return completion.Task;
    }

    private void CancelActiveConnect()
    {
        // A cancelled attempt (selection change, deselect, disconnect, shutdown) leaves no origin behind.
        _origins.Clear();
        var cancellation = _activeConnectCancellation;
        _activeConnectCancellation = null;
        if (cancellation is null) return;
        try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
    }

    private Task<bool> DispatchForOperationAsync(int generation, string deviceId, Action action)
    {
        if (_disposed) return Task.FromResult(false);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _dispatch(() =>
            {
                // Fence inside the dispatcher callback. A selection change can happen after
                // the await queues this action but before the UI thread executes it.
                if (_disposed
                    || generation != _operationGeneration
                    || !string.Equals(_selectedDeviceId, deviceId, StringComparison.Ordinal))
                {
                    completion.TrySetResult(false);
                    return;
                }

                try
                {
                    action();
                    completion.TrySetResult(true);
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            });
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }

        return completion.Task;
    }
    private void RaiseSelectionProperties()
    {
        Raise(nameof(SelectedDeviceId));
        Raise(nameof(SelectedDeviceName));
        Raise(nameof(HasSelectedDevice));
        RaiseStatusProperties();

        if (!string.Equals(_selectedDeviceId, _lastAnnouncedDeviceId, StringComparison.Ordinal))
        {
            _lastAnnouncedDeviceId = _selectedDeviceId;
            try { SelectedDeviceChanged?.Invoke(this, EventArgs.Empty); }
            catch (Exception)
            {
                // A subscriber failure must not break the connection view model.
            }
        }
    }

    private void RaiseStatusProperties()
    {
        Raise(nameof(IsConnected));
        Raise(nameof(IsCurrentOutput));
        Raise(nameof(IsConnecting));
        Raise(nameof(IsDisconnecting));
        Raise(nameof(CanConnect));
        Raise(nameof(CanDisconnect));
        Raise(nameof(NeedsPairing));
        Raise(nameof(SelectedDeviceNoun));
        Raise(nameof(WindowsBatteryPercent));
        Raise(nameof(WindowsBatteryText));
        Raise(nameof(WindowsBatteryHint));
        var now = DateTimeOffset.UtcNow;
        var noun = SelectedDeviceNoun;
        WindowsBattery.SetLabels(noun, string.Format(CultureInfo.CurrentCulture, Strings.Companion_Battery_ReportedByWindows, noun));
        WindowsBattery.Update(BatteryComponent.From(WindowsBatteryPercent, null, now), now, isDemo: false);
        Raise(nameof(StatusText));
        Raise(nameof(DetailText));
    }

    private void SetConnecting(bool value)
    {
        if (!Set(ref _isConnecting, value)) return;
        Raise(nameof(CanConnect));
        Raise(nameof(CanDisconnect));
        Raise(nameof(StatusText));
    }

    private void SetDisconnecting(bool value)
    {
        if (!Set(ref _isDisconnecting, value)) return;
        Raise(nameof(CanConnect));
        Raise(nameof(CanDisconnect));
        Raise(nameof(StatusText));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _operationGeneration++;
        _controller.Changed -= OnControllerChanged;
        _settings.PropertyChanged -= OnSettingsChanged;
        _refreshTimer.Dispose();
        CancelActiveConnect();
        _lifetime.Cancel();
        // Keep the CTS and semaphore alive until pending async methods have observed cancellation
        // and released the refresh gate. They are small managed objects and require no cleanup.
        _isConnecting = false;
        _isDisconnecting = false;
        Devices.Clear();
        _snapshot = ConnectionSnapshot.Incomplete("The connection view has closed.");
    }
}
