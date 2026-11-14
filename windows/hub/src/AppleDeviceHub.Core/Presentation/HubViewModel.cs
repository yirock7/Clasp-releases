using AppleDeviceHub.Core.AirPods;
using AppleDeviceHub.Core.Ble;
using System.Collections.ObjectModel;
using AppleDeviceHub.Core.Contracts;
using AppleDeviceHub.Core.Interop;
using AppleDeviceHub.Core.State;

namespace AppleDeviceHub.Core.Presentation;

/// <summary>Visual category for a status chip. Meaning is always repeated in the chip's text.</summary>
public enum ChipKind
{
    Neutral = 0,
    Demo = 1,
    Success = 2,
    Pending = 3,
    Warning = 4,
    Error = 5,
}

/// <summary>Which earbuds are out of the case (in an ear or out of ear). Left and right are the physical sides.</summary>
public readonly record struct ArtworkBudsOut(bool Left, bool Right)
{
    public static ArtworkBudsOut None => default;

    public bool Any => Left || Right;
}

/// <summary>One listening-mode choice for the segmented control.</summary>
public sealed class ListeningModeOption
{
    public ListeningModeOption(ListeningMode mode, string label, string glyph)
    {
        Mode = mode;
        Label = label;
        Glyph = glyph;
    }

    public ListeningMode Mode { get; }

    public string Label { get; }

    /// <summary>Segoe Fluent Icons code point (provisional artwork).</summary>
    public string Glyph { get; }
}

/// <summary>
/// The single view model behind the tray flyout, case card and settings page. All public
/// members must be used on the UI thread; transport events are marshalled through the
/// <c>dispatch</c> delegate passed to the constructor.
/// </summary>
public sealed class HubViewModel : ObservableObject, IAsyncDisposable
{
    /// <summary>How long to wait for a device readback before showing "no confirmation".</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(4);

    private readonly IAirPodsTransport _transport;
    private readonly IHardwareTransportStatus? _status;
    private readonly Action<Action> _dispatch;
    private readonly TimeProvider _time;
    private readonly DeviceStateStore _store;
    private readonly CaseCardPolicy _cardPolicy;
    private readonly ITimer _timeoutTimer;

    private DeviceState? _state;
    private PendingRequest<ListeningMode>? _modeRequest;
    private PendingRequest<bool>? _awarenessRequest;
    private PendingRequest<int>? _strengthRequest;
    private int? _queuedStrength;
    private bool _strengthInFlight;
    private bool _hardwareModeWireInFlight;
    private bool _hardwareModeUncertain;
    // Conversation Awareness on hardware: the same separation of wire operation and displayed
    // request. The device reports CA once, so an unconfirmed write stays uncertain until the
    // session ends, even if a reading arrives later.
    private bool _hardwareAwarenessWireInFlight;
    private bool _hardwareAwarenessUncertain;
    private bool _disposed;
    private string? _unavailableReason;

    private string _deviceName = "AirPods";
    private string _modelName = "";
    private ConnectionSummary _connection = ConnectionSummary.Unknown;
    private string _connectionText = "Unknown";
    private ChipKind _connectionChipKind = ChipKind.Neutral;
    private ListeningMode? _actualMode;
    private ListeningMode? _displayedMode;
    private int _displayedModeIndex = -1;
    private int _actualModeIndex = -1;
    private bool _isModePending;
    private bool _hasModeError;
    private string _modeStatusText = "Mode unknown";
    private bool _areControlsAvailable;
    private bool? _conversationAwarenessOn;
    private bool _isConversationAwarenessKnown;
    private bool _isConversationAwarenessPending;
    private double? _adaptiveStrength;
    private string _awarenessAutomationName = "Conversation Awareness, not reported yet";
    private string _strengthAutomationName = "Adaptive strength, not reported yet";
    private bool _isAdaptiveStrengthKnown;
    private bool _isAdaptiveStrengthEnabled;
    private bool _isAdaptiveStrengthPending;
    private string? _lastErrorText;
    private bool _isConnectPending;
    private DateTimeOffset _connectIssuedAt;
    private Guid? _connectCommandId;          // the current attempt; results for other ids are ignored
    private DateTimeOffset? _engineConnectingSince;
    private bool _engineConnectingStalled;    // engine kept reporting Connecting past ConnectTimeout
    private bool _isDisconnectPending;
    private DateTimeOffset _disconnectIssuedAt;
    private Guid? _disconnectCommandId;
    // Bumped when the control session ends (Disconnect, session loss): continuations started in an
    // earlier generation are ignored, so old acknowledgements cannot settle or flush new work.
    private int _generation;

    /// <param name="transport">The deliberately chosen transport (phase one: demo only).</param>
    /// <param name="selectedDeviceId">The one device this UI follows; states for others are ignored.</param>
    /// <param name="dispatch">Runs an action on the UI thread.</param>
    /// <param name="cardPolicy">Decides when the case card appears automatically (null: never).</param>
    /// <param name="time">Clock for request timeouts; tests pass a fake one (null: system clock).</param>
    public HubViewModel(
        IAirPodsTransport transport,
        string selectedDeviceId,
        Action<Action> dispatch,
        CaseCardPolicy? cardPolicy = null,
        TimeProvider? time = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
        _time = time ?? TimeProvider.System;
        _store = new DeviceStateStore(selectedDeviceId);
        _cardPolicy = cardPolicy ?? new CaseCardPolicy();
        _timeoutTimer = _time.CreateTimer(_ => _dispatch(OnTimeoutTick), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        Transport = transport.Info;
        IsDemo = transport.Info.Origin == DataOrigin.Demo;
        Demo = transport as DemoAirPodsTransport;
        _status = transport as IHardwareTransportStatus;
        DataSourceText = IsDemo
            ? "Demo data. Nothing here comes from real AirPods."
            : _status?.SourceText ?? $"Data from {transport.Info.Name}.";
        _unavailableReason = _status?.UnavailableReason;
        if (_unavailableReason is not null) _connectionText = "Not available right now";

        Left = new BatteryItemViewModel("L", "Left earbud");
        Right = new BatteryItemViewModel("R", "Right earbud");
        Case = new BatteryItemViewModel("Case", "Charging case");
        Single = new BatteryItemViewModel("Battery", "Headphones battery");

        ModeOptions = new[]
        {
            new ListeningModeOption(ListeningMode.Off, "Off", ""),
            new ListeningModeOption(ListeningMode.Transparency, "Transparency", ""),
            new ListeningModeOption(ListeningMode.Adaptive, "Adaptive", ""),
            new ListeningModeOption(ListeningMode.NoiseCancellation, "Noise Cancellation", ""),
        };

        _transport.StateChanged += OnTransportStateChanged;
        if (_status is not null) _status.StatusChanged += OnTransportStatusChanged;
        if (_transport is BroadcastObservationTransport broadcasts) broadcasts.SourcesChanged += OnBroadcastSourcesChanged;
    }

    // ---- Identity and data honesty -----------------------------------------------------

    public TransportInfo Transport { get; }

    /// <summary>True when every value on screen is synthetic. Views must then show <see cref="DemoBadgeText"/>.</summary>
    public bool IsDemo { get; }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "XAML binds to it through the view-model instance.")]
    public string DemoBadgeText => "Demo";

    public string DataSourceText { get; }

    /// <summary>
    /// Why the hardware transport cannot be used right now (engine not running, identity check
    /// failed, driver interface not found, ...), or null. Never set for the demo.
    /// </summary>
    public string? UnavailableReason
    {
        get => _unavailableReason;
        private set
        {
            if (!Set(ref _unavailableReason, value)) return;
            Raise(nameof(HasUnavailableReason));
            RaiseConnectionDerived();
        }
    }

    public bool HasUnavailableReason => _unavailableReason is not null;

    /// <summary>
    /// Contract 1.1 <see cref="DeviceCapabilities.ControlSessionOnly"/>: Connect opens only the
    /// AirPods control session, so the UI says "Start session"/"End session" and reports the
    /// control session and the Bluetooth link as separate facts. A hardware transport that has not
    /// reported a state yet is treated the same way.
    /// </summary>
    public bool IsControlSessionOnly => _state?.Supports(DeviceCapabilities.ControlSessionOnly) ?? _status is not null;

    /// <summary>How old the engine's Bluetooth-link reading may be before it is shown as stale.</summary>
    public static readonly TimeSpan LinkCheckMaxAge = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Control-session mode only: the Bluetooth link fact the engine reported with the device list
    /// (carried in <see cref="LinkState.AudioConnected"/>), with the time it was checked, for example
    /// "Bluetooth link: connected (checked 14:05)", or "... - last checked 14:05" once older than
    /// <see cref="LinkCheckMaxAge"/>. It is the paired device's Bluetooth connection, not proof that
    /// Windows audio is playing through it. "" otherwise.
    /// </summary>
    public string BluetoothLinkText
    {
        get
        {
            if (!IsControlSessionOnly || _state is null) return "";
            var reading = _state.Link.AudioConnected;
            if (reading.Value is not bool connected || reading.ObservedAt is not { } checkedAt) return "Bluetooth: unknown";
            var word = connected ? "connected" : "not connected";
            var clock = FormatClock(checkedAt);
            return _time.GetUtcNow() - checkedAt > LinkCheckMaxAge
                ? $"Bluetooth: {word}, last checked {clock}"
                : $"Bluetooth: {word}, checked {clock}";
        }
    }

    /// <summary>Local wall-clock time of a reading, 24-hour "HH:mm".</summary>
    public static string FormatClock(DateTimeOffset at)
        => at.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Contract 1.1: the state says Connect/Disconnect are implemented. Without it neither is offered.</summary>
    public bool SupportsConnectControl => _state?.Supports(DeviceCapabilities.ConnectControl) == true;

    public CaseCardPolicy CardPolicy => _cardPolicy;

    public string DeviceName { get => _deviceName; private set => Set(ref _deviceName, value); }
    public string ModelName { get => _modelName; private set => Set(ref _modelName, value); }

    // ---- Connection --------------------------------------------------------------------

    public ConnectionSummary Connection
    {
        get => _connection;
        private set
        {
            if (Set(ref _connection, value)) RaiseConnectionDerived();
        }
    }

    public string ConnectionText { get => _connectionText; private set => Set(ref _connectionText, value); }
    public ChipKind ConnectionChipKind { get => _connectionChipKind; private set => Set(ref _connectionChipKind, value); }

    /// <summary>Longest a connect attempt may stay pending before the UI says it did not work.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// <see cref="ConnectTimeout"/> for a control-session transport. It must not be shorter than the
    /// transport's own open timeout (25 s, itself above the engine's 20 s), so the engine's precise
    /// answer arrives before the UI gives up.
    /// </summary>
    public static readonly TimeSpan SessionConnectTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The connect timeout that applies to the current transport.</summary>
    public TimeSpan EffectiveConnectTimeout => IsControlSessionOnly ? SessionConnectTimeout : ConnectTimeout;

    /// <summary>True from a Connect request until the device reports Connected, the request fails, or it times out.</summary>
    public bool IsConnectPending
    {
        get => _isConnectPending;
        private set
        {
            if (Set(ref _isConnectPending, value)) RaiseConnectionDerived();
        }
    }

    /// <summary>Longest a Disconnect may wait for the device to report the session closed.</summary>
    public static readonly TimeSpan DisconnectTimeout = TimeSpan.FromSeconds(10);

    /// <summary>True from a Disconnect request until the session is reported closed, it fails, or it times out.</summary>
    public bool IsDisconnectPending
    {
        get => _isDisconnectPending;
        private set
        {
            if (Set(ref _isDisconnectPending, value))
            {
                RaiseConnectionDerived();
                Raise(nameof(IsModeInputEnabled));
                Raise(nameof(IsConversationAwarenessInputEnabled));
            }
        }
    }

    public bool IsConnected => Connection == ConnectionSummary.Connected;

    /// <summary>
    /// This app's connect attempt is running, or the engine reports an attempt that has not yet
    /// run past <see cref="ConnectTimeout"/>. A stalled engine report does not lock the UI.
    /// </summary>
    public bool IsConnecting => !IsConnected
        && (IsConnectPending || (Connection == ConnectionSummary.Connecting && !_engineConnectingStalled));

    private bool IsConnectStalled => !IsConnected && !IsConnectPending
        && Connection == ConnectionSummary.Connecting && _engineConnectingStalled;

    /// <summary>The hero line in the flyout: what is true right now, in plain words.</summary>
    public string ConnectionHeadline => IsBroadcastObservation ? BroadcastHeadline
        : IsControlSessionOnly ? ControlSessionHeadline
        : IsConnected ? (IsDisconnectPending ? "Disconnecting…" : "Connected")
        : IsConnecting ? "Connecting…"
        : IsConnectStalled ? "Couldn't connect"
        : Connection switch
        {
            ConnectionSummary.Nearby => "Ready to connect",
            ConnectionSummary.NotDetected => "Not nearby",
            _ => "Looking for your AirPods…",
        };

    private string ControlSessionHeadline => IsConnected ? (IsDisconnectPending ? "Ending session…" : "Controls connected")
        : IsConnecting ? "Starting session…"
        : IsConnectStalled ? "Couldn't start session"
        : _state is null && HasUnavailableReason ? "Not available right now"
        : "Controls not connected";

    /// <summary>One short instruction under the headline, or "" when nothing needs doing.</summary>
    public string ConnectionHint => IsBroadcastObservation ? BroadcastHint
        : IsControlSessionOnly ? ControlSessionHint
        : IsConnected ? ""
        : IsConnecting ? "Keep the case open and close to your PC"
        : Connection == ConnectionSummary.Nearby ? $"Your {DeviceNoun} are nearby"
        : "Open the case near your PC";

    // Control-session mode: the unavailable reason has its own line, so the hint only explains the action.
    private string ControlSessionHint => IsConnected || HasUnavailableReason ? ""
        : IsConnecting ? "Turning on your AirPods controls…"
        : "Turns on your AirPods controls. Pairing and sound settings stay the same.";

    /// <summary>
    /// The flyout's one primary action: "Connect"/"Disconnect", or "Start session"/"End session"
    /// when only the control session is affected (never "Disconnect" there).
    /// </summary>
    public string ConnectActionText => IsBroadcastObservation ? (IsObserving ? "Stop listening" : "Start listening")
        : IsControlSessionOnly
        ? (IsConnected ? "End session" : "Start session")
        : IsConnected ? "Disconnect" : "Connect";

    /// <summary>
    /// Connect is offered once the buds are nearby (or to retry a stalled attempt); Disconnect
    /// while connected. Never while a connect or disconnect is outstanding.
    /// </summary>
    public bool IsConnectActionEnabled => IsBroadcastObservation
        ? SupportsConnectControl && !_observeRequestPending
        : SupportsConnectControl && !IsConnecting && !IsDisconnectPending
            && (IsConnected || Connection == ConnectionSummary.Nearby || IsConnectStalled);

    // ---- Broadcast observation (contract 1.2) -------------------------------------------

    /// <summary>
    /// The transport only listens to nearby Bluetooth LE broadcasts. The primary action is
    /// "Start/Stop observing" (sent as Connect/Disconnect), which completes when the transport
    /// acknowledges it: there is no connection to wait for, and nothing is written to the AirPods.
    /// </summary>
    public bool IsBroadcastObservation => _state?.Supports(DeviceCapabilities.BroadcastObservation) == true;

    /// <summary>True while a broadcast transport is listening.</summary>
    public bool IsObserving => IsBroadcastObservation && _status?.ObservationActive == true;

    public ObservableCollection<BroadcastSourceViewModel> BroadcastSources { get; } = new();

    private BroadcastSourceViewModel? _selectedBroadcastSource;
    public BroadcastSourceViewModel? SelectedBroadcastSource => _selectedBroadcastSource;
    public bool HasBroadcastSources => IsBroadcastObservation && BroadcastSources.Count > 0;

    /// <summary>
    /// Two different pairs are being heard and Clasp cannot tell them apart (the transport's Ambiguous phase),
    /// so the person has to choose. False outside broadcast observation and while not listening.
    /// </summary>
    public bool IsBroadcastAmbiguous => IsObserving
        && _transport is BroadcastObservationTransport broadcasts && broadcasts.Phase == BroadcastPhase.Ambiguous;

    /// <summary>How likely the nearby source being shown is the selected paired AirPods (never a proof). Unknown outside broadcast observation.</summary>
    public IdentityConfidence NearbyIdentityConfidence
        => _transport is BroadcastObservationTransport broadcasts ? broadcasts.IdentityConfidence : IdentityConfidence.Unknown;

    /// <summary>"Your AirPods (likely)" at Probable or better, otherwise "identity unconfirmed". The word "Confirmed" alone is never shown.</summary>
    public string NearbyIdentityText => NearbyIdentityConfidence.IsLikely() ? IdentityConfidenceText.LikelyFor(DeviceNoun) : IdentityConfidenceText.For(NearbyIdentityConfidence);
    public bool HasSelectedBroadcastSource => _selectedBroadcastSource is not null;
    public string SelectedBroadcastBatteryText => _selectedBroadcastSource?.BatteryText ?? "";
    public string SelectedBroadcastSignalText => _selectedBroadcastSource?.SignalText ?? "";

    /// <summary>Manual inspection only. No transport command, ownership claim or persistence.</summary>
    public void SelectBroadcastSource(Guid id)
    {
        if (_disposed) return;
        var selected = BroadcastSources.FirstOrDefault(source => source.Id == id);
        if (ReferenceEquals(selected, _selectedBroadcastSource)) return;
        _selectedBroadcastSource = selected;
        Raise(nameof(SelectedBroadcastSource));
        Raise(nameof(HasSelectedBroadcastSource));
        Raise(nameof(SelectedBroadcastBatteryText));
        Raise(nameof(SelectedBroadcastSignalText));
        RaiseConnectionDerived();
    }

    private void OnBroadcastSourcesChanged(object? sender, EventArgs e) => _dispatch(() =>
    {
        if (_disposed || _transport is not BroadcastObservationTransport broadcasts) return;
        var recent = broadcasts.Sources;
        foreach (var old in BroadcastSources.Where(item => !recent.Any(source => source.Id == item.Id)).ToArray())
            BroadcastSources.Remove(old);
        foreach (var source in recent)
        {
            var item = BroadcastSources.FirstOrDefault(candidate => candidate.Id == source.Id);
            if (item is null) BroadcastSources.Add(new BroadcastSourceViewModel(source));
            else item.Update(source);
        }
        // A quiet/rotated source is never replaced with another source automatically.
        if (_selectedBroadcastSource is { } selected && !BroadcastSources.Contains(selected))
            SelectBroadcastSource(Guid.Empty);
        Raise(nameof(HasBroadcastSources));
        Raise(nameof(SelectedBroadcastBatteryText));
        Raise(nameof(SelectedBroadcastSignalText));
        RaiseConnectionDerived();
        Raise(nameof(NearbyIdentityConfidence));
        Raise(nameof(NearbyIdentityText));
    });

    private bool _observeRequestPending;

    private string BroadcastHeadline => !IsObserving
            ? (HasUnavailableReason ? "Bluetooth isn't available" : "Not listening")
        : HasUnavailableReason ? "Bluetooth isn't available. Trying again…"
        : HasSelectedBroadcastSource ? (_selectedBroadcastSource!.Identity.IsLikely() ? IdentityConfidenceText.LikelyFor(DeviceNoun) : "Showing a nearby device (not confirmed)")
        : HasBroadcastSources ? "Pick a nearby device below"
        : Connection switch
        {
            ConnectionSummary.Nearby => NearbyIdentityConfidence.IsLikely() ? IdentityConfidenceText.LikelyFor(DeviceNoun) : $"Nearby {DeviceNoun} (unconfirmed)",
            ConnectionSummary.NotDetected => "Not heard recently",
            _ => "Listening for AirPods…",
        };

    /// <summary>Plain-language limits of broadcast observation; shown whenever it is the source.</summary>
    public const string BroadcastLimitsText =
        "Nearby estimate. These might not be your AirPods. To change settings, install the Clasp driver.";

    private string BroadcastHint => HasUnavailableReason && !IsObserving ? "" : BroadcastLimitsText;

    private async Task SetObservingAsync(bool start)
    {
        if (_disposed || _state is null || !SupportsConnectControl || _observeRequestPending) return;
        _observeRequestPending = true;
        LastErrorText = null;
        RaiseConnectionDerived();

        DeviceCommand command = start ? new ConnectCommand(Guid.NewGuid()) : new DisconnectCommand(Guid.NewGuid());
        var result = await SendSafeAsync(command).ConfigureAwait(false);
        _dispatch(() =>
        {
            if (_disposed) return;
            _observeRequestPending = false;
            if (!result.IsAcknowledged)
                LastErrorText = $"Couldn't {(start ? "start" : "stop")} observing: {result.Detail?.TrimEnd('.') ?? "no response"}.";
            RaiseConnectionDerived();
        });
    }

    private void RaiseConnectionDerived()
    {
        Raise(nameof(IsConnected));
        Raise(nameof(IsConnecting));
        Raise(nameof(ConnectionHeadline));
        Raise(nameof(ConnectionHint));
        Raise(nameof(ConnectActionText));
        Raise(nameof(IsConnectActionEnabled));
        Raise(nameof(IsControlSessionOnly));
        Raise(nameof(BluetoothLinkText));
        Raise(nameof(SupportsConnectControl));
        Raise(nameof(IsBroadcastObservation));
        Raise(nameof(IsObserving));
        Raise(nameof(HasBroadcastSources));
        Raise(nameof(IsBroadcastAmbiguous));
        Raise(nameof(NearbyIdentityConfidence));
        Raise(nameof(NearbyIdentityText));
    }

    // ---- Battery -----------------------------------------------------------------------

    public BatteryItemViewModel Left { get; }
    public BatteryItemViewModel Right { get; }
    public BatteryItemViewModel Case { get; }

    /// <summary>The one battery of a single-battery model (Beats headband headphones, AirPods Max). Fed from the reported left value.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720", Justification = "Reads as the layout name (SingleBattery) beside Left, Right and Case.")]
    public BatteryItemViewModel Single { get; }

    private BatteryLayout _shownLayout = BatteryLayout.EarbudsWithCase;
    private string _shownNoun = "AirPods";
    private ArtworkGroup _shownArtwork = ArtworkGroup.Generic;
    private ushort? _shownArtworkModelId;

    /// <summary>Which drawing the flyout shows; Stem earbuds with case while no model is known and the layout has buds; Generic for other unknown models.</summary>
    public ArtworkGroup ArtworkGroup => AppleModelCatalog.ArtworkOf(_state?.ModelId, BatteryLayout);

    /// <summary>The model id the per-model drawing is chosen from, or null when no model is known.</summary>
    public ushort? ArtworkModelId => _state?.ModelId;

    /// <summary>The selected transport device id; used with <see cref="ArtworkModelId"/> for per-device artwork choices.</summary>
    public string ArtworkDeviceId => _store.SelectedDeviceId;

    /// <summary>A true raw charging observation no older than the battery freshness window.</summary>
    public bool ArtworkIsCharging => HasFreshChargingObservation(_state, _time.GetUtcNow());

    /// <summary>
    /// At least one primary or secondary ear observation says InEar (the roles are not left and right). During a control session it
    /// holds for as long as the session is open; for a broadcast observation it must be no older than <see cref="ArtworkEarFreshness"/>.
    /// </summary>
    public bool ArtworkIsWorn => HasFreshInEarObservation(_state, _time.GetUtcNow());

    /// <summary>
    /// Which buds are out of the case right now (in an ear or out of ear, by a fresh observation), for the hero drawing; only for
    /// earbuds with a case battery layout. Per bud when the source says which side is which (a broadcast), otherwise only when both are out.
    /// </summary>
    public ArtworkBudsOut ArtworkBudsOut => FreshBudsOut(_state, _time.GetUtcNow());

    /// <summary>The next time a currently active charging or aged wear cue becomes stale, for a one-shot view refresh; null when none expires (a held session cue never does).</summary>
    public DateTimeOffset? ArtworkCueExpiresAt => NextArtworkCueExpiry(_state, _time.GetUtcNow());

    /// <summary>Five minutes, matching the freshness window used by the battery readouts.</summary>
    public static readonly TimeSpan ArtworkChargingFreshness = BatteryItemViewModel.StaleAfter;

    /// <summary>Broadcast ear placement is a snapshot; only fifteen seconds counts as fresh. A control session holds the cue instead.</summary>
    public static readonly TimeSpan ArtworkEarFreshness = TimeSpan.FromSeconds(15);

    // The Clasp control-protocol session: ears are cleared when it ends, so an InEar report stays true for as long as it is open.
    private static bool IsHeldEarSession(DeviceState state)
        => !state.Supports(DeviceCapabilities.BroadcastObservation) && state.Link.ProtocolSession.Value == true;

    internal static bool HasFreshChargingObservation(DeviceState? state, DateTimeOffset now)
    {
        if (state is null) return false;
        return IsFreshTrue(state.Battery.Left.Charging, now, ArtworkChargingFreshness)
            || IsFreshTrue(state.Battery.Right.Charging, now, ArtworkChargingFreshness)
            || IsFreshTrue(state.Battery.Case.Charging, now, ArtworkChargingFreshness);
    }

    internal static bool HasFreshInEarObservation(DeviceState? state, DateTimeOffset now)
    {
        if (state is null) return false;
        var held = IsHeldEarSession(state);
        return IsFreshInEar(state.Ears.Primary, now, held)
            || IsFreshInEar(state.Ears.Secondary, now, held);
    }

    /// <summary>
    /// A bud is out when its fresh placement is InEar or OutOfEar; InCase, Disconnected, unknown and stale are not. Freshness follows the wear
    /// cue: a broadcast observation counts for <see cref="ArtworkEarFreshness"/>, a control session holds it while it is open.
    /// When the state names the sides (<see cref="EarState.Left"/> / <see cref="EarState.Right"/>) each bud is judged alone. Otherwise
    /// primary and secondary are not left and right, so the buds count as out only when BOTH are out (then the side does not matter).
    /// </summary>
    internal static ArtworkBudsOut FreshBudsOut(DeviceState? state, DateTimeOffset now)
    {
        if (state is null || state.Layout != BatteryLayout.EarbudsWithCase) return ArtworkBudsOut.None;
        var held = IsHeldEarSession(state);
        var ears = state.Ears;
        if (ears.Left.Value is not null || ears.Right.Value is not null)
            return new ArtworkBudsOut(IsFreshOut(ears.Left, now, held), IsFreshOut(ears.Right, now, held));
        var both = IsFreshOut(ears.Primary, now, held) && IsFreshOut(ears.Secondary, now, held);
        return new ArtworkBudsOut(both, both);
    }

    private static bool IsFreshOut(Observation<PodPlacement> observation, DateTimeOffset now, bool held)
        => observation.Value is PodPlacement.InEar or PodPlacement.OutOfEar && observation.ObservedAt is { } at && at <= now
            && (held || observation.FreshnessAt(now, ArtworkEarFreshness) == Freshness.Fresh);

    private static bool IsFreshTrue(Observation<bool> observation, DateTimeOffset now, TimeSpan maxAge)
        => observation.Value == true && observation.ObservedAt is { } at && at <= now
            && observation.FreshnessAt(now, maxAge) == Freshness.Fresh;

    private static bool IsFreshInEar(Observation<PodPlacement> observation, DateTimeOffset now, bool held)
        => observation.Value == PodPlacement.InEar && observation.ObservedAt is { } at && at <= now
            && (held || observation.FreshnessAt(now, ArtworkEarFreshness) == Freshness.Fresh);

    internal static DateTimeOffset? NextArtworkCueExpiry(DeviceState? state, DateTimeOffset now)
    {
        if (state is null) return null;
        var heldEars = IsHeldEarSession(state);
        DateTimeOffset? earliest = null;
        Consider(state.Battery.Left.Charging, ArtworkChargingFreshness, state.Battery.Left.Charging.Value == true);
        Consider(state.Battery.Right.Charging, ArtworkChargingFreshness, state.Battery.Right.Charging.Value == true);
        Consider(state.Battery.Case.Charging, ArtworkChargingFreshness, state.Battery.Case.Charging.Value == true);
        Consider(state.Ears.Primary, ArtworkEarFreshness, !heldEars && state.Ears.Primary.Value == PodPlacement.InEar);
        Consider(state.Ears.Secondary, ArtworkEarFreshness, !heldEars && state.Ears.Secondary.Value == PodPlacement.InEar);
        // A bud that is out of the ear (not only in an ear) also stops counting as out after fifteen seconds.
        Consider(state.Ears.Primary, ArtworkEarFreshness, !heldEars && state.Ears.Primary.Value == PodPlacement.OutOfEar);
        Consider(state.Ears.Secondary, ArtworkEarFreshness, !heldEars && state.Ears.Secondary.Value == PodPlacement.OutOfEar);
        Consider(state.Ears.Left, ArtworkEarFreshness, !heldEars && state.Ears.Left.Value is PodPlacement.InEar or PodPlacement.OutOfEar);
        Consider(state.Ears.Right, ArtworkEarFreshness, !heldEars && state.Ears.Right.Value is PodPlacement.InEar or PodPlacement.OutOfEar);
        return earliest;

        void Consider<T>(Observation<T> observation, TimeSpan maxAge, bool cue) where T : struct
        {
            if (!cue || observation.FreshnessAt(now, maxAge) != Freshness.Fresh
                || observation.ObservedAt is not { } observedAt || observedAt > now) return;
            var expiry = observedAt + maxAge + TimeSpan.FromTicks(1);
            if (earliest is null || expiry < earliest) earliest = expiry;
        }
    }

    private bool _raisedArtworkCharging;
    private bool _raisedArtworkWorn;
    private ArtworkBudsOut _raisedArtworkBudsOut;
    private DateTimeOffset? _raisedArtworkExpiry;

    // Only on a real change: the flyout re-runs its hero sync for each of these.
    private void RaiseArtworkCues()
    {
        var now = _time.GetUtcNow();
        var charging = HasFreshChargingObservation(_state, now);
        var worn = HasFreshInEarObservation(_state, now);
        var expiry = NextArtworkCueExpiry(_state, now);
        var budsOut = FreshBudsOut(_state, now);
        if (budsOut != _raisedArtworkBudsOut)
        {
            _raisedArtworkBudsOut = budsOut;
            Raise(nameof(ArtworkBudsOut));
        }
        if (charging != _raisedArtworkCharging)
        {
            _raisedArtworkCharging = charging;
            Raise(nameof(ArtworkIsCharging));
        }
        if (worn != _raisedArtworkWorn)
        {
            _raisedArtworkWorn = worn;
            Raise(nameof(ArtworkIsWorn));
        }
        if (expiry != _raisedArtworkExpiry)
        {
            _raisedArtworkExpiry = expiry;
            Raise(nameof(ArtworkCueExpiresAt));
        }
    }

    /// <summary>Which batteries the device has. Earbuds with a case until a state says otherwise.</summary>
    public BatteryLayout BatteryLayout => _state?.Layout ?? BatteryLayout.EarbudsWithCase;

    public bool IsSingleBattery => BatteryLayout == BatteryLayout.SingleBattery;

    /// <summary>False for single-battery models and for earbuds whose case reports no battery.</summary>
    public bool HasCaseBattery => BatteryLayout == BatteryLayout.EarbudsWithCase;

    /// <summary>"AirPods", "Beats" or "headphones" for user-facing text; "AirPods" when no state exists yet.</summary>
    public string DeviceNoun => _state?.DeviceNoun ?? "AirPods";

    // ---- Listening mode ----------------------------------------------------------------

    /// <summary>Fixed display order; unsupported modes are disabled, not hidden.</summary>
    public IReadOnlyList<ListeningModeOption> ModeOptions { get; }

    /// <summary>The mode the device last reported, or null when unknown.</summary>
    public ListeningMode? ActualMode { get => _actualMode; private set => Set(ref _actualMode, value); }

    /// <summary>The requested mode while a request is pending, otherwise the actual mode.</summary>
    public ListeningMode? DisplayedMode
    {
        get => _displayedMode;
        private set
        {
            if (Set(ref _displayedMode, value)) Raise(nameof(IsAdaptiveModeDisplayed));
        }
    }

    /// <summary>Adaptive is the displayed mode, so its strength control belongs on screen.</summary>
    public bool IsAdaptiveModeDisplayed => DisplayedMode == ListeningMode.Adaptive;

    /// <summary>Index of <see cref="DisplayedMode"/> in <see cref="ModeOptions"/>, or -1.</summary>
    public int DisplayedModeIndex { get => _displayedModeIndex; private set => Set(ref _displayedModeIndex, value); }

    /// <summary>Index of <see cref="ActualMode"/> in <see cref="ModeOptions"/>, or -1.</summary>
    public int ActualModeIndex { get => _actualModeIndex; private set => Set(ref _actualModeIndex, value); }

    public bool IsModePending
    {
        get => _isModePending;
        private set
        {
            if (Set(ref _isModePending, value)) Raise(nameof(IsModeInputEnabled));
        }
    }
    public bool HasModeError { get => _hasModeError; private set => Set(ref _hasModeError, value); }

    /// <summary>One line describing the mode state, including "Demo" for simulated results.</summary>
    public string ModeStatusText { get => _modeStatusText; private set => Set(ref _modeStatusText, value); }

    /// <summary>Controls work only while a control session is up.</summary>
    public bool AreControlsAvailable
    {
        get => _areControlsAvailable;
        private set
        {
            if (Set(ref _areControlsAvailable, value)) Raise(nameof(IsModeInputEnabled));
        }
    }

    /// <summary>
    /// The listening-mode selector accepts input: controls are available and, for a control-session
    /// transport, no mode write is still waiting for its readback (the selector shows it as busy).
    /// </summary>
    public bool IsModeInputEnabled => AreControlsAvailable && (!IsControlSessionOnly
        || (!IsModePending && !IsDisconnectPending && !_hardwareModeWireInFlight && !_hardwareModeUncertain
            && ActualMode.HasValue && _state?.Listening.Supported.Count > 0));

    public bool IsModeSupported(ListeningMode mode) => _state?.Listening.Supported.Contains(mode) == true;

    /// <summary>
    /// Why the listening-mode selector is read-only while a session is up (for example
    /// "Observation session: changes disabled"), or "" when modes can be changed.
    /// </summary>
    public string ModeReadOnlyText => IsDemo || !AreControlsAvailable || _state is null ? ""
        : IsControlSessionOnly && _hardwareModeUncertain ? NtpodsDaemonTransport.UncertainModeWriteReason
        : IsControlSessionOnly && !ActualMode.HasValue ? NtpodsDaemonTransport.ModeNotReportedReason
        : _state.Listening.Supported.Count > 0 ? ""
        : _status?.ReadOnlyReason ?? "Listening mode can't be changed in this session";

    private const string NotInThisBuild = "Not available yet";

    /// <summary>The device state says Conversation Awareness can be reported and changed.</summary>
    public bool IsConversationAwarenessSupported => _state?.Supports(DeviceCapabilities.ConversationAwareness) == true;

    /// <summary>
    /// Caption under "Conversation Awareness": what it does, why it is unavailable or read-only, or
    /// (hardware) the unconfirmed request next to the value the AirPods last reported.
    /// </summary>
    public string ConversationAwarenessCaption
    {
        get
        {
            // Hardware: say why (e.g. "not reported yet" on a 1.1 engine, or an engine that cannot
            // change it) rather than implying the feature is missing from Clasp.
            if (_state is not null && !IsConversationAwarenessSupported)
                return IsControlSessionOnly && _status?.ConversationAwarenessReadOnlyReason is { Length: > 0 } why
                    ? why.TrimEnd('.')
                    : NotInThisBuild;
            if (IsControlSessionOnly && _hardwareAwarenessUncertain
                && _awarenessRequest is { Status: RequestStatus.TimedOut or RequestStatus.Failed } r)
                return $"You asked for {OnOff(r.Requested)}, but it isn't confirmed. Your AirPods last said {OnOff(_state?.ConversationAwareness.Value)}.";
            var readOnly = ConversationAwarenessReadOnlyText;
            return readOnly.Length > 0 ? readOnly : "Lowers media when you speak";
        }
    }

    private static string OnOff(bool? value) => value switch { true => "on", false => "off", null => "unknown" };

    /// <summary>
    /// The Conversation Awareness toggle accepts input: a reported value, controls available and,
    /// for a control-session transport, no change outstanding or unconfirmed and the transport
    /// able to write it now.
    /// </summary>
    public bool IsConversationAwarenessInputEnabled => AreControlsAvailable && IsConversationAwarenessSupported
        && ConversationAwarenessOn is bool
        && (!IsControlSessionOnly
            || (!IsConversationAwarenessPending && !IsDisconnectPending && !_hardwareAwarenessWireInFlight && !_hardwareAwarenessUncertain
                && _status?.ConversationAwarenessReadOnlyReason is null));

    /// <summary>
    /// Hardware only: why Conversation Awareness cannot be changed right now (for example
    /// "This Clasp engine cannot change Conversation Awareness."), or "".
    /// </summary>
    public string ConversationAwarenessReadOnlyText
    {
        get
        {
            if (IsDemo || !IsControlSessionOnly || !AreControlsAvailable || _state is null || !IsConversationAwarenessSupported) return "";
            if (_hardwareAwarenessUncertain) return NtpodsDaemonTransport.UncertainAwarenessWriteReason;
            if (IsConversationAwarenessPending || _hardwareAwarenessWireInFlight) return "";
            return _status?.ConversationAwarenessReadOnlyReason ?? "";
        }
    }

    // ---- Conversation Awareness --------------------------------------------------------

    /// <summary>
    /// Displayed value: the requested value while pending, otherwise the actual one.
    /// <c>null</c> whenever the device has not reported it: unknown is never stored as Off.
    /// </summary>
    public bool? ConversationAwarenessOn
    {
        get => _conversationAwarenessOn;
        private set
        {
            if (!Set(ref _conversationAwarenessOn, value)) return;
            IsConversationAwarenessKnown = value.HasValue;
            Raise(nameof(ConversationAwarenessStateText));
            Raise(nameof(ConversationAwarenessToggleValue));
        }
    }

    /// <summary>
    /// What the ToggleSwitch is bound to. It must be a plain bool: x:Bind skips a function
    /// binding whose argument is null, which would leave a stale On in the hidden control. The
    /// toggle is collapsed while unknown, and the view model ignores the Toggled echo this causes.
    /// </summary>
    public bool ConversationAwarenessToggleValue => ConversationAwarenessOn == true;

    /// <summary>True when <see cref="ConversationAwarenessOn"/> holds a reported (or pending) value.</summary>
    public bool IsConversationAwarenessKnown { get => _isConversationAwarenessKnown; private set => Set(ref _isConversationAwarenessKnown, value); }

    /// <summary>"On", "Off" or "Unknown": the only words used for this setting.</summary>
    public string ConversationAwarenessStateText => ConversationAwarenessOn switch
    {
        true => "On",
        false => "Off",
        null when _state is not null && !IsConversationAwarenessSupported => "Unavailable",
        null => "Unknown",
    };

    /// <summary>Narrator name for the control (or for the "Unknown" label that replaces it).</summary>
    public string ConversationAwarenessAutomationName { get => _awarenessAutomationName; private set => Set(ref _awarenessAutomationName, value); }
    public bool IsConversationAwarenessPending { get => _isConversationAwarenessPending; private set => Set(ref _isConversationAwarenessPending, value); }

    // ---- Adaptive strength -------------------------------------------------------------

    /// <summary>
    /// 0..100. Updates immediately while dragging; reverts if the device does not confirm.
    /// <c>null</c> whenever the device has not reported it: unknown is never stored as 0.
    /// </summary>
    public double? AdaptiveStrength
    {
        get => _adaptiveStrength;
        private set
        {
            if (!Set(ref _adaptiveStrength, value)) return;
            IsAdaptiveStrengthKnown = value.HasValue;
            Raise(nameof(AdaptiveStrengthText));
            Raise(nameof(AdaptiveStrengthSliderValue));
        }
    }

    /// <summary>What the Slider is bound to (see <see cref="ConversationAwarenessToggleValue"/>); collapsed and disabled while unknown.</summary>
    public double AdaptiveStrengthSliderValue => AdaptiveStrength ?? 0;

    /// <summary>True when <see cref="AdaptiveStrength"/> holds a reported (or pending) value.</summary>
    public bool IsAdaptiveStrengthKnown { get => _isAdaptiveStrengthKnown; private set => Set(ref _isAdaptiveStrengthKnown, value); }

    /// <summary>"62", or "—" when the device has not reported it (never a made-up 0).</summary>
    public string AdaptiveStrengthText => AdaptiveStrength is double v
        ? ((int)Math.Round(v)).ToString(System.Globalization.CultureInfo.CurrentCulture)
        : "—";

    /// <summary>Narrator name for the slider (or for the caption that replaces it).</summary>
    public string AdaptiveStrengthAutomationName { get => _strengthAutomationName; private set => Set(ref _strengthAutomationName, value); }

    /// <summary>Only adjustable in Adaptive mode with a live control session.</summary>
    public bool IsAdaptiveStrengthEnabled { get => _isAdaptiveStrengthEnabled; private set => Set(ref _isAdaptiveStrengthEnabled, value); }
    public bool IsAdaptiveStrengthPending { get => _isAdaptiveStrengthPending; private set => Set(ref _isAdaptiveStrengthPending, value); }

    /// <summary>Why the strength slider is unavailable, or "" when it is usable.</summary>
    public string AdaptiveStrengthCaption => _state is not null && !_state.Supports(DeviceCapabilities.AdaptiveStrength) ? NotInThisBuild
        : !IsAdaptiveStrengthKnown ? "Strength not reported yet"
        : IsAdaptiveStrengthEnabled ? ""
        : "Available in Adaptive mode";

    // ---- Errors ------------------------------------------------------------------------

    /// <summary>The most recent command problem, or null. Cleared by the next successful request.</summary>
    public string? LastErrorText
    {
        get => _lastErrorText;
        private set
        {
            if (Set(ref _lastErrorText, value)) Raise(nameof(HasError));
        }
    }

    public bool HasError => _lastErrorText is not null;

    /// <summary>Raised on the UI thread when <see cref="CardPolicy"/> decides the case card should appear.</summary>
    public event EventHandler? CaseCardRequested;

    // ---- Demo transport (kept for --transport=demo, CI and screenshots) ---------------

    public DemoAirPodsTransport? Demo { get; }

    // ---- Lifecycle ---------------------------------------------------------------------

    public Task StartAsync() => _transport.StartAsync();

    /// <summary>Recompute time-dependent text (stale battery ages). Call when a surface opens.</summary>
    public void Refresh() => UpdateAll(_time.GetUtcNow());

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _transport.StateChanged -= OnTransportStateChanged;
        if (_status is not null) _status.StatusChanged -= OnTransportStatusChanged;
        if (_transport is BroadcastObservationTransport broadcasts) broadcasts.SourcesChanged -= OnBroadcastSourcesChanged;
        _timeoutTimer.Dispose();
        await _transport.DisposeAsync().ConfigureAwait(false);
    }

    // ---- Commands ----------------------------------------------------------------------

    /// <summary>
    /// Ask the engine to connect. The flyout shows "Connecting…" until a state reports the
    /// connection (or the request fails, or <see cref="ConnectTimeout"/> passes): an
    /// acknowledgement alone is never shown as connected.
    /// </summary>
    public async Task ConnectAsync()
    {
        if (IsBroadcastObservation)
        {
            // Observation has no connection; the one action toggles listening.
            await SetObservingAsync(start: !IsObserving).ConfigureAwait(false);
            return;
        }
        if (_disposed || _state is null || !SupportsConnectControl || IsConnected || IsConnecting || IsDisconnectPending) return;

        var command = new ConnectCommand(Guid.NewGuid());
        LastErrorText = null;
        _connectIssuedAt = _time.GetUtcNow();
        _connectCommandId = command.CommandId;
        IsConnectPending = true;
        ArmTimeout();

        var result = await SendSafeAsync(command).ConfigureAwait(false);
        _dispatch(() =>
        {
            // Only the current attempt may settle: a late answer from a retired (timed-out)
            // attempt must not end a newer one.
            if (_disposed || _connectCommandId != command.CommandId || !IsConnectPending) return;
            if (!result.IsAcknowledged)
            {
                _connectCommandId = null;
                IsConnectPending = false;
                SetConnectError($"{(IsControlSessionOnly ? "Couldn't start session" : "Couldn't connect")}: {result.Detail?.TrimEnd('.') ?? "no response from your AirPods"}.");
                ArmTimeout();
            }
        });
    }

    /// <summary>
    /// Close the device session. Pending setting work is retired at once: nothing queued is sent
    /// afterwards and late answers are ignored. The pairing stays, so Connect brings it back.
    /// </summary>
    public async Task DisconnectAsync()
    {
        if (IsBroadcastObservation)
        {
            await SetObservingAsync(start: false).ConfigureAwait(false);
            return;
        }
        if (_disposed || _state is null || !SupportsConnectControl || !IsConnected || IsDisconnectPending) return;

        var command = new DisconnectCommand(Guid.NewGuid());
        LastErrorText = null;
        RetireSessionWork();
        _disconnectIssuedAt = _time.GetUtcNow();
        _disconnectCommandId = command.CommandId;
        IsDisconnectPending = true;
        ArmTimeout();

        var result = await SendSafeAsync(command).ConfigureAwait(false);
        _dispatch(() =>
        {
            if (_disposed || _disconnectCommandId != command.CommandId || !IsDisconnectPending) return;
            if (!result.IsAcknowledged)
            {
                _disconnectCommandId = null;
                IsDisconnectPending = false;
                LastErrorText = $"Couldn't disconnect: {result.Detail?.TrimEnd('.') ?? "no response from your AirPods"}.";
                ArmTimeout();
            }
        });
    }

    /// <summary>
    /// End all setting work tied to the current control session: pending mode, awareness and
    /// strength requests and any queued slider value. Bumping the generation makes continuations
    /// still in flight return without effect. Hardware uncertainty clears only after the session
    /// really ends; merely requesting End (which can fail) is not a session boundary.
    /// </summary>
    private void RetireSessionWork(bool sessionEnded = false)
    {
        if (IsControlSessionOnly && !sessionEnded && (_hardwareModeWireInFlight || _modeRequest?.IsPending == true))
            _hardwareModeUncertain = true;
        if (IsControlSessionOnly && !sessionEnded && (_hardwareAwarenessWireInFlight || _awarenessRequest?.IsPending == true))
            _hardwareAwarenessUncertain = true;
        _generation++;
        _modeRequest = null;
        _awarenessRequest = null;
        _strengthRequest = null;
        _queuedStrength = null;
        _strengthInFlight = false;
        if (sessionEnded)
        {
            _hardwareModeWireInFlight = false;
            _hardwareModeUncertain = false;
            _hardwareAwarenessWireInFlight = false;
            _hardwareAwarenessUncertain = false;
        }
        if (LastErrorText == TimeoutMessage) LastErrorText = null;
        UpdateMode();
        UpdateAwareness();
        UpdateStrength();
        ArmTimeout();
    }

    public async Task RequestModeAsync(ListeningMode mode)
    {
        if (_state is null || !IsModeInputEnabled || !IsModeSupported(mode)) return;
        // Hardware: keep the wire operation separate from the displayed request. Its earlier
        // confirmation timeout must not release an outstanding or uncertain write.
        if (IsControlSessionOnly && _modeRequest is { IsPending: true }) return;
        if (_modeRequest is { IsPending: true } pending && pending.Requested == mode) return;
        if (_modeRequest is not { IsPending: true } && _state.Listening.Actual.Value == mode) return;

        var id = Guid.NewGuid();
        if (IsControlSessionOnly) _hardwareModeWireInFlight = true;
        _modeRequest = RequestReconciler.Begin(id, mode, _state.Listening.Actual.Value, _time.GetUtcNow(), _modeRequest);
        LastErrorText = null;
        UpdateMode();
        ArmTimeout();

        int generation = _generation;
        var result = await SendSafeAsync(new SetListeningModeCommand(id, mode)).ConfigureAwait(false);
        _dispatch(() =>
        {
            if (_disposed || generation != _generation) return; // session ended meanwhile
            if (IsControlSessionOnly)
            {
                _hardwareModeWireInFlight = false;
                if (result.Outcome is CommandOutcome.TimedOut or CommandOutcome.Failed or CommandOutcome.TransportUnavailable)
                    _hardwareModeUncertain = true;
            }
            if (_modeRequest is { } r) _modeRequest = RequestReconciler.OnCommandResult(r, result);
            AfterResult(_modeRequest?.Status, _modeRequest?.Detail);
            UpdateMode();
        });
    }

    public async Task SetConversationAwarenessAsync(bool on)
    {
        if (_state is null || !AreControlsAvailable || !_state.Supports(DeviceCapabilities.ConversationAwareness)) return;
        // Nothing reported means nothing to change from: a hidden toggle still raises Toggled
        // when its binding moves, and that must not reach the earbuds.
        if (ConversationAwarenessOn is not bool current) return;
        if (on == current) return; // programmatic update echoing back from the view
        if (IsControlSessionOnly && !IsConversationAwarenessInputEnabled)
        {
            // Hardware: one change at a time, never over an unconfirmed one. Put the view back.
            Raise(nameof(ConversationAwarenessToggleValue));
            return;
        }

        var id = Guid.NewGuid();
        if (IsControlSessionOnly) _hardwareAwarenessWireInFlight = true;
        _awarenessRequest = RequestReconciler.Begin(id, on, _state.ConversationAwareness.Value, _time.GetUtcNow(), _awarenessRequest);
        LastErrorText = null;
        UpdateAwareness();
        ArmTimeout();

        int generation = _generation;
        var result = await SendSafeAsync(new SetConversationAwarenessCommand(id, on)).ConfigureAwait(false);
        _dispatch(() =>
        {
            if (_disposed || generation != _generation) return; // session ended meanwhile
            if (IsControlSessionOnly)
            {
                _hardwareAwarenessWireInFlight = false;
                if (result.Outcome is CommandOutcome.TimedOut or CommandOutcome.Failed or CommandOutcome.TransportUnavailable)
                    _hardwareAwarenessUncertain = true;
            }
            if (_awarenessRequest is { } r) _awarenessRequest = RequestReconciler.OnCommandResult(r, result);
            AfterResult(_awarenessRequest?.Status, _awarenessRequest?.Detail);
            UpdateAwareness();
        });
    }

    /// <summary>
    /// Slider input. The displayed value follows immediately; commands are coalesced so at most
    /// one is in flight and only the latest value is sent after it.
    /// </summary>
    public Task SetAdaptiveStrengthAsync(double value)
    {
        if (_state is null || !IsAdaptiveStrengthEnabled || AdaptiveStrength is not double current) return Task.CompletedTask;
        int percent = (int)Math.Round(Math.Clamp(value, 0, 100));
        if (percent == (int)Math.Round(current)) return Task.CompletedTask;

        AdaptiveStrength = percent;
        if (_strengthInFlight)
        {
            _queuedStrength = percent;
            return Task.CompletedTask;
        }
        return SendStrengthAsync(percent);
    }

    private async Task SendStrengthAsync(int percent)
    {
        _strengthInFlight = true;
        var id = Guid.NewGuid();
        _strengthRequest = RequestReconciler.Begin(id, percent, _state?.AdaptiveStrength.Value, _time.GetUtcNow(), _strengthRequest);
        LastErrorText = null;
        UpdateStrength();
        ArmTimeout();

        int generation = _generation;
        var result = await SendSafeAsync(new SetAdaptiveStrengthCommand(id, percent)).ConfigureAwait(false);
        _dispatch(() =>
        {
            // Session ended meanwhile: RetireSessionWork already cleared the flight and the queue.
            if (_disposed || generation != _generation) return;
            _strengthInFlight = false;
            if (_strengthRequest is { } r) _strengthRequest = RequestReconciler.OnCommandResult(r, result);
            AfterResult(_strengthRequest?.Status, _strengthRequest?.Detail);
            DropQueuedStrengthIfChainEnded();
            // ...and only if the slider is still usable (strength reported, Adaptive mode, session up).
            if (_queuedStrength is int next && IsAdaptiveStrengthEnabled && AdaptiveStrength is double)
            {
                _queuedStrength = null;
                _ = SendStrengthAsync(next);
                return;
            }
            _queuedStrength = null;
            UpdateStrength();
        });
    }

    private async Task<CommandResult> SendSafeAsync(DeviceCommand command)
    {
        try
        {
            return await _transport.SendAsync(command).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new CommandResult(command.CommandId, CommandOutcome.TransportUnavailable, Transport.Origin, "The connection to your AirPods stopped");
        }
        catch (Exception ex)
        {
            return new CommandResult(command.CommandId, CommandOutcome.Failed, Transport.Origin, ex.Message);
        }
    }

    /// <summary>Surface a failure and let the one-shot demo flag reset in the view.</summary>
    private void AfterResult(RequestStatus? status, string? detail)
    {
        if (status is RequestStatus.Failed) LastErrorText = detail ?? "That didn't work";
    }

    // ---- State application -------------------------------------------------------------

    private DeviceState? _latestState;
    private int _statePending;

    // Latest-state coalescing: a burst of states queues at most one UI callback, which applies the newest.
    // Same Interlocked "queued" flag App.OnNearbyEvaluationNeeded uses (App.xaml.cs, _nearbyEvaluationQueued).
    private void OnTransportStateChanged(object? sender, DeviceState state)
    {
        Volatile.Write(ref _latestState, state);
        if (Interlocked.Exchange(ref _statePending, 1) != 0) return;
        _dispatch(() =>
        {
            // Reset first so a state arriving while we apply queues a fresh callback.
            Interlocked.Exchange(ref _statePending, 0);
            if (Volatile.Read(ref _latestState) is { } latest) Apply(latest);
        });
    }

    private void OnTransportStatusChanged(object? sender, EventArgs e) => _dispatch(() =>
    {
        if (_disposed || _status is null) return;
        UnavailableReason = _status.UnavailableReason;
        if (_state is null) ConnectionText = HasUnavailableReason ? "Not available right now" : "Unknown";
        RaiseConnectionDerived();
    });

    private void Apply(DeviceState next)
    {
        if (_disposed || !_store.TryApply(next, out var previous)) return;
        bool hadSession = _state?.Link.ProtocolSession.Value == true;
        _state = next;
        // The control session ended (dropped, out of range, engine closed it): its pending setting
        // work can never be confirmed in a later session, so retire it now.
        if (hadSession && next.Link.ProtocolSession.Value != true) RetireSessionWork(sessionEnded: true);

        bool wasTimedOut = AnyTimedOut();
        if (_modeRequest is { } m) _modeRequest = RequestReconciler.OnActual(m, next.Listening.Actual.Value);
        if (_awarenessRequest is { } a) _awarenessRequest = RequestReconciler.OnActual(a, next.ConversationAwareness.Value);
        if (_strengthRequest is { } s) _strengthRequest = RequestReconciler.OnActual(s, next.AdaptiveStrength.Value);
        DropQueuedStrengthIfChainEnded();
        // A late readback resolved the timeout: drop the now-false timeout message.
        if (wasTimedOut && !AnyTimedOut() && LastErrorText == TimeoutMessage) LastErrorText = null;
        ArmTimeout(); // re-arm (or disarm) for whatever is still pending after this readback

        var now = _time.GetUtcNow();
        UpdateAll(now);
        ArmTimeout(); // UpdateAll may have started an engine-"connecting" deadline
        // Observing starts/stops without changing the connection summary; refresh the action.
        if (next.Supports(DeviceCapabilities.BroadcastObservation)) RaiseConnectionDerived();

        if (_cardPolicy.ShouldPresent(previous, next, now))
        {
            _cardPolicy.MarkPresented(now);
            CaseCardRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private const string TimeoutMessage = "Your AirPods didn't respond";

    private const string ConnectTimeoutMessage = "Couldn't connect. Keep the case open near your PC and try again.";

    private const string SessionTimeoutMessage = "Couldn't start session. Your AirPods didn't respond in time. Try again.";

    private string ConnectTimeoutText => IsControlSessionOnly ? SessionTimeoutMessage : ConnectTimeoutMessage;

    /// <summary>The last connect failure shown; cleared once the device does connect.</summary>
    private string? _connectErrorText;

    private void SetConnectError(string text)
    {
        _connectErrorText = text;
        LastErrorText = text;
    }

    private const string DisconnectTimeoutMessage = "Couldn't disconnect. Your AirPods didn't respond. Try again.";

    private void OnTimeoutTick()
    {
        if (_disposed) return;
        var now = _time.GetUtcNow();
        bool newlyTimedOut = false;
        if (_modeRequest is { } m)
        {
            _modeRequest = Tick(m, ref newlyTimedOut);
            if (IsControlSessionOnly && m.IsPending && _modeRequest.Status == RequestStatus.TimedOut)
                _hardwareModeUncertain = true;
        }
        if (_awarenessRequest is { } a)
        {
            _awarenessRequest = Tick(a, ref newlyTimedOut);
            // No readback in time: the device may never report it again, so the write stays uncertain.
            if (IsControlSessionOnly && a.IsPending && _awarenessRequest.Status == RequestStatus.TimedOut)
                _hardwareAwarenessUncertain = true;
        }
        if (_strengthRequest is { } s) _strengthRequest = Tick(s, ref newlyTimedOut);
        DropQueuedStrengthIfChainEnded();
        // Only a request that timed out on THIS tick reports; an old timed-out request must not
        // bring the message back after later commands succeed.
        if (newlyTimedOut) LastErrorText = TimeoutMessage;
        if (IsConnectPending && now - _connectIssuedAt >= EffectiveConnectTimeout)
        {
            _connectCommandId = null; // retire the attempt: its late answer is ignored
            IsConnectPending = false;
            SetConnectError(ConnectTimeoutText);
        }
        if (_engineConnectingSince is { } since && !_engineConnectingStalled && now - since >= EffectiveConnectTimeout)
        {
            // The engine has reported "connecting" for too long: stop showing progress, allow a retry.
            _engineConnectingStalled = true;
            RaiseConnectionDerived();
            SetConnectError(ConnectTimeoutText);
        }
        if (IsDisconnectPending && now - _disconnectIssuedAt >= DisconnectTimeout)
        {
            _disconnectCommandId = null;
            IsDisconnectPending = false;
            LastErrorText = DisconnectTimeoutMessage;
        }
        UpdateAll(now);
        ArmTimeout();

        PendingRequest<T> Tick<T>(PendingRequest<T> request, ref bool timedOut) where T : struct
        {
            var next = RequestReconciler.OnTick(request, now, RequestTimeout);
            if (request.IsPending && next.Status == RequestStatus.TimedOut) timedOut = true;
            return next;
        }
    }

    /// <summary>
    /// A strength value queued behind an in-flight command belongs to the user's current chain of
    /// changes. The chain stays live only while the in-flight request is Pending, or Confirmed with
    /// the device still reporting that value. Once it times out, is superseded or fails, or the
    /// device moves after confirming, the queued value is dropped for good, so a later transition
    /// ("Confirmed after the timeout", a late acknowledgement) cannot send it. The device's own
    /// change wins (ARCHITECTURE.md).
    /// </summary>
    private void DropQueuedStrengthIfChainEnded()
    {
        if (_queuedStrength is null || _strengthRequest is not { } r) return;
        bool live = r.Status == RequestStatus.Pending
            || (r.Status == RequestStatus.Confirmed && _state?.AdaptiveStrength.Value == r.Requested);
        if (!live) _queuedStrength = null;
    }

    private bool AnyTimedOut()
        => _modeRequest?.Status == RequestStatus.TimedOut
        || _awarenessRequest?.Status == RequestStatus.TimedOut
        || _strengthRequest?.Status == RequestStatus.TimedOut;

    /// <summary>One-shot timer for the earliest pending deadline. No polling while idle.</summary>
    private void ArmTimeout()
    {
        // Each pending item contributes its own deadline; the timer fires for the earliest.
        DateTimeOffset? earliest = null;
        foreach (var issued in new[] { PendingSince(_modeRequest), PendingSince(_awarenessRequest), PendingSince(_strengthRequest) })
        {
            if (issued is { } t && (earliest is null || t + RequestTimeout < earliest)) earliest = t + RequestTimeout;
        }
        if (IsConnectPending && (earliest is null || _connectIssuedAt + EffectiveConnectTimeout < earliest))
            earliest = _connectIssuedAt + EffectiveConnectTimeout;
        if (_engineConnectingSince is { } since && !_engineConnectingStalled
            && (earliest is null || since + EffectiveConnectTimeout < earliest))
            earliest = since + EffectiveConnectTimeout;
        if (IsDisconnectPending && (earliest is null || _disconnectIssuedAt + DisconnectTimeout < earliest))
            earliest = _disconnectIssuedAt + DisconnectTimeout;
        if (earliest is not { } deadline)
        {
            _timeoutTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            return;
        }
        var due = deadline - _time.GetUtcNow();
        _timeoutTimer.Change(due < TimeSpan.Zero ? TimeSpan.Zero : due, Timeout.InfiniteTimeSpan);
    }

    private static DateTimeOffset? PendingSince<T>(PendingRequest<T>? request) where T : struct
        => request is { IsPending: true } r ? r.IssuedAt : null;

    private void UpdateAll(DateTimeOffset now)
    {
        if (_state is { } s)
        {
            DeviceName = s.DisplayName;
            ModelName = s.ModelName;
            var connection = Summarize(s);
            // Track how long the engine has said "connecting"; a new episode starts fresh.
            if (connection == ConnectionSummary.Connecting) _engineConnectingSince ??= now;
            else { _engineConnectingSince = null; _engineConnectingStalled = false; }
            Connection = connection;
            if (Connection == ConnectionSummary.Connected && _connectErrorText is not null)
            {
                // The session did come up after all: the earlier "couldn't connect" is no longer true.
                if (LastErrorText == _connectErrorText) LastErrorText = null;
                _connectErrorText = null;
            }
            if (IsConnectPending && Connection == ConnectionSummary.Connected)
            {
                _connectCommandId = null;
                IsConnectPending = false;
            }
            if (IsDisconnectPending && Connection != ConnectionSummary.Connected)
            {
                _disconnectCommandId = null;
                IsDisconnectPending = false;
            }
            RaiseConnectionDerived();
            ConnectionText = IsControlSessionOnly ? Connection switch
            {
                ConnectionSummary.Connected => "Controls connected",
                ConnectionSummary.Connecting => "Starting session…",
                ConnectionSummary.Nearby => "Ready",
                ConnectionSummary.NotDetected => "Not available",
                _ => "Unknown",
            }
            : Connection switch
            {
                ConnectionSummary.Connected => "Connected",
                ConnectionSummary.Connecting => "Connecting…",
                ConnectionSummary.Nearby => "Nearby",
                ConnectionSummary.NotDetected => "Not nearby",
                _ => "Unknown",
            };
            ConnectionChipKind = Connection switch
            {
                ConnectionSummary.Connected => ChipKind.Success,
                ConnectionSummary.Connecting => ChipKind.Pending,
                ConnectionSummary.NotDetected => ChipKind.Warning,
                _ => ChipKind.Neutral,
            };
            AreControlsAvailable = s.Link.ProtocolSession.Value == true;
            var tenSteps = s.Origin == DataOrigin.Broadcast;
            Left.Update(s.Battery.Left, now, IsDemo, tenSteps);
            Right.Update(s.Battery.Right, now, IsDemo, tenSteps);
            Case.Update(s.Battery.Case, now, IsDemo, tenSteps);
            // A single-battery model reports its one level in the left slot; Left stays fed so history, tray and alerts are untouched.
            Single.Update(s.Layout == BatteryLayout.SingleBattery ? s.Battery.Left : BatteryComponent.Unknown, now, IsDemo, tenSteps);
            // Only on a real change: the flyout rebuilds its rings on these.
            if (_shownLayout != s.Layout)
            {
                _shownLayout = s.Layout;
                Raise(nameof(BatteryLayout));
                Raise(nameof(IsSingleBattery));
                Raise(nameof(HasCaseBattery));
            }
            var artwork = AppleModelCatalog.ArtworkOf(s.ModelId, s.Layout);
            if (_shownArtwork != artwork)
            {
                _shownArtwork = artwork;
                Raise(nameof(ArtworkGroup));
            }
            if (_shownArtworkModelId != s.ModelId)
            {
                _shownArtworkModelId = s.ModelId;
                Raise(nameof(ArtworkModelId));
            }
            if (_shownNoun != s.DeviceNoun)
            {
                _shownNoun = s.DeviceNoun;
                Raise(nameof(DeviceNoun)); // the hint and headline are re-raised with the connection-derived properties
            }
        }
        else if (HasUnavailableReason)
        {
            ConnectionText = "Not available right now";
        }
        UpdateMode();
        UpdateAwareness();
        UpdateStrength();
        Raise(nameof(ModeReadOnlyText));
        Raise(nameof(IsConversationAwarenessSupported));
        Raise(nameof(ConversationAwarenessCaption));
        Raise(nameof(ConversationAwarenessStateText));
        RaiseArtworkCues();
    }

    /// <summary>
    /// The connection summary. With <see cref="DeviceCapabilities.ControlSessionOnly"/>, "connected"
    /// means the control session is open; Windows audio alone never counts (it is shown separately).
    /// </summary>
    private static ConnectionSummary Summarize(DeviceState state)
    {
        if (!state.Supports(DeviceCapabilities.ControlSessionOnly)) return state.Link.Summarize();
        var link = state.Link;
        if (link.ProtocolSession.Value == true) return ConnectionSummary.Connected;
        if (link.Connecting.Value == true) return ConnectionSummary.Connecting;
        if (link.Nearby.Value == true) return ConnectionSummary.Nearby;
        if (link.Nearby.HasValue || link.ProtocolSession.HasValue) return ConnectionSummary.NotDetected;
        return ConnectionSummary.Unknown;
    }

    private void UpdateMode()
    {
        var actual = _state?.Listening.Actual.Value;
        var request = _modeRequest;
        bool pending = request?.IsPending == true;

        ActualMode = actual;
        DisplayedMode = pending ? request!.Requested : actual;
        ActualModeIndex = actual is { } a ? IndexOf(ModeOptions, a) : -1;
        DisplayedModeIndex = DisplayedMode is { } d ? IndexOf(ModeOptions, d) : -1;
        IsModePending = pending;
        HasModeError = request?.Status is RequestStatus.Failed or RequestStatus.TimedOut;
        Raise(nameof(IsModeInputEnabled));
        Raise(nameof(ModeReadOnlyText));

        string text = request switch
        {
            { IsPending: true } r => $"Switching to {Label(r.Requested)}…",
            { Status: RequestStatus.Failed or RequestStatus.TimedOut } r =>
                $"Couldn't switch to {Label(r.Requested)}: {r.Detail?.TrimEnd('.')}. Still {Label(actual)}.",
            { Status: RequestStatus.Superseded } => $"{Label(actual)} (changed on the device)",
            _ => actual is null ? "Mode unknown" : Label(actual),
        };
        ModeStatusText = IsDemo ? $"{text} · Demo" : text;
        UpdateStrength();
    }

    private void UpdateAwareness()
    {
        var actual = _state?.ConversationAwareness.Value;
        var request = _awarenessRequest;
        bool pending = request?.IsPending == true;
        IsConversationAwarenessPending = pending;
        // Unknown stays unknown even while a request is pending: there is no reported value
        // to show, and the control is not offered.
        ConversationAwarenessOn = actual is null ? null : pending ? request!.Requested : actual;
        ConversationAwarenessAutomationName = _state is not null && !IsConversationAwarenessSupported && !IsConversationAwarenessKnown
            ? "Conversation Awareness, not available yet"
            : !IsConversationAwarenessKnown ? "Conversation Awareness, not reported yet"
            : pending ? "Conversation Awareness, waiting for confirmation"
            : "Conversation Awareness";
        Raise(nameof(IsConversationAwarenessInputEnabled));
        Raise(nameof(ConversationAwarenessReadOnlyText));
        Raise(nameof(ConversationAwarenessCaption));
    }

    private void UpdateStrength()
    {
        var actual = _state?.AdaptiveStrength.Value;
        var request = _strengthRequest;
        bool pending = request?.IsPending == true || _strengthInFlight;
        IsAdaptiveStrengthPending = pending;
        // Unknown strength stays disabled: a slider resting at 0 would look like a reported value.
        IsAdaptiveStrengthEnabled = AreControlsAvailable
            && actual.HasValue
            && _state?.Supports(DeviceCapabilities.AdaptiveStrength) == true
            && DisplayedMode == ListeningMode.Adaptive;
        // Unknown is shown as unknown; once reported again during a pending request, show the
        // value being requested (the latest queued one if any), as before the gap.
        if (actual is null) AdaptiveStrength = null;
        else if (!pending) AdaptiveStrength = actual;
        else if (AdaptiveStrength is null) AdaptiveStrength = _queuedStrength ?? request?.Requested ?? actual;
        AdaptiveStrengthAutomationName = _state is not null && !_state.Supports(DeviceCapabilities.AdaptiveStrength)
            ? "Adaptive strength, not available yet"
            : !IsAdaptiveStrengthKnown ? "Adaptive strength, not reported yet"
            : pending ? "Adaptive strength, waiting for confirmation"
            : "Adaptive strength";
        Raise(nameof(AdaptiveStrengthCaption));
    }

    private string Label(ListeningMode? mode)
    {
        if (mode is not { } m) return "unknown";
        foreach (var option in ModeOptions)
        {
            if (option.Mode == m) return option.Label;
        }
        return m.ToString();
    }

    private static int IndexOf(IReadOnlyList<ListeningModeOption> options, ListeningMode mode)
    {
        for (int i = 0; i < options.Count; i++)
        {
            if (options[i].Mode == mode) return i;
        }
        return -1;
    }

    private static int IndexOf(IReadOnlyList<ListeningMode> modes, ListeningMode mode)
    {
        for (int i = 0; i < modes.Count; i++)
        {
            if (modes[i] == mode) return i;
        }
        return -1;
    }
}
