using AppleDeviceHub.Core.Ble;

namespace AppleDeviceHub.Core.Contracts;

/// <summary>Battery level and charging flag for one part (left bud, right bud, or case).</summary>
public readonly record struct BatteryComponent(Observation<int> Level, Observation<bool> Charging)
{
    public static BatteryComponent Unknown => default;

    /// <summary>
    /// Build a component from raw transport values. Levels outside 0..100 (including NTPods'
    /// 0xFF "absent" sentinel) become Unknown. A charging flag is only kept when the level is
    /// known, because "not charging" means nothing for a part that is not reporting.
    /// </summary>
    public static BatteryComponent From(int? percent, bool? charging, DateTimeOffset observedAt)
    {
        if (percent is not (>= 0 and <= 100)) return Unknown;
        var charge = charging is bool c ? Observation<bool>.At(c, observedAt) : Observation<bool>.Unknown;
        return new BatteryComponent(Observation<int>.At(percent.Value, observedAt), charge);
    }
}

public sealed record BatteryState(BatteryComponent Left, BatteryComponent Right, BatteryComponent Case)
{
    public static BatteryState Unknown { get; } = new(default, default, default);
}

/// <summary>Short connection label derived from <see cref="LinkState"/>.</summary>
public enum ConnectionSummary
{
    Unknown = 0,
    NotDetected = 1,
    Nearby = 2,
    Connecting = 3,
    Connected = 4,
}

/// <summary>
/// Separate link facts that are easy to conflate (BUILD-PLAN section 7): a BLE
/// advertisement attributed to the selected device, an in-progress connection attempt, an
/// AirPods control-protocol session (needs the AAP driver), and a working Windows audio
/// connection. Any of them may be unknown.
/// </summary>
public sealed record LinkState(
    Observation<bool> Nearby,
    Observation<bool> Connecting,
    Observation<bool> ProtocolSession,
    Observation<bool> AudioConnected)
{
    public static LinkState Unknown { get; } = new(default, default, default, default);

    public ConnectionSummary Summarize()
    {
        if (ProtocolSession.Value == true || AudioConnected.Value == true) return ConnectionSummary.Connected;
        if (Connecting.Value == true) return ConnectionSummary.Connecting;
        if (Nearby.Value == true) return ConnectionSummary.Nearby;
        if (Nearby.HasValue || ProtocolSession.HasValue || AudioConnected.HasValue) return ConnectionSummary.NotDetected;
        return ConnectionSummary.Unknown;
    }
}

/// <summary>Listening modes. Numeric values match NTPods' <c>anc</c> byte (1..4).</summary>
public enum ListeningMode
{
    Off = 1,
    NoiseCancellation = 2,
    Transparency = 3,
    Adaptive = 4,
}

/// <summary>The listening mode the device reported, plus the modes it supports.</summary>
public sealed record ListeningState(Observation<ListeningMode> Actual, IReadOnlyList<ListeningMode> Supported)
{
    public static ListeningState Unknown { get; } = new(default, Array.Empty<ListeningMode>());
}

/// <summary>
/// What a device (or the demo) says it supports. A capability is only claimed for hardware
/// after it has been validated on that model and firmware.
/// </summary>
[Flags]
public enum DeviceCapabilities
{
    None = 0,
    Battery = 1 << 0,
    ListeningModes = 1 << 1,
    ConversationAwareness = 1 << 2,
    AdaptiveStrength = 1 << 3,
    EarDetection = 1 << 4,

    /// <summary>Case-lid open/close events. Never claimed for hardware until validated.</summary>
    CaseLidEvents = 1 << 5,

    /// <summary>
    /// Contract 1.1: the transport implements <see cref="ConnectCommand"/> and
    /// <see cref="DisconnectCommand"/>. Without it the UI offers neither, and a transport that
    /// receives them anyway must answer <see cref="CommandOutcome.Unsupported"/>.
    /// </summary>
    ConnectControl = 1 << 6,

    /// <summary>
    /// Contract 1.1: Connect/Disconnect open and close only the AirPods control session (AAP).
    /// Windows audio is not touched, so the UI must not call the action "Disconnect" or
    /// imply that audio was released; the control-session and Windows-audio facts are shown
    /// separately.
    /// </summary>
    ControlSessionOnly = 1 << 7,

    /// <summary>
    /// Contract 1.2: values come from listening to Bluetooth LE broadcasts, not from a session
    /// with the device. Connect/Disconnect (when <see cref="ConnectControl"/> is also set) start
    /// and stop listening; nothing is written to the device. Values are approximate and the
    /// sender is not confirmed to be the user's AirPods (see <see cref="DataOrigin.Broadcast"/>).
    /// </summary>
    BroadcastObservation = 1 << 8,
}

/// <summary>Where one bud is, as the AirPods report it (contract 1.2).</summary>
public enum PodPlacement
{
    InEar = 1,
    OutOfEar = 2,
    InCase = 3,

    /// <summary>The bud has dropped off the AirPods' own link (not a position).</summary>
    Disconnected = 4,
}

/// <summary>
/// Contract 1.2: ear placement of the AirPods' current primary and secondary bud. These are the
/// AirPods' own roles, which change; they are not left/right. An unreported side is unknown.
/// </summary>
public sealed record EarState(Observation<PodPlacement> Primary, Observation<PodPlacement> Secondary)
{
    public static EarState Unknown { get; } = new(default, default);

    /// <summary>
    /// The same two buds by physical side, when the source can say which is which (a broadcast does, through its primary-is-left
    /// bit; a control session reports only primary and secondary). Unknown when the side mapping is not available.
    /// </summary>
    public Observation<PodPlacement> Left { get; init; }

    /// <inheritdoc cref="Left"/>
    public Observation<PodPlacement> Right { get; init; }

    /// <summary>True when at least one reported side is in an ear. Unknown sides never count.</summary>
    public bool AnyInEar => Primary.Value == PodPlacement.InEar || Secondary.Value == PodPlacement.InEar;
}

/// <summary>
/// Immutable snapshot of the ONE device the user selected. <see cref="Sequence"/> increases
/// strictly per transport instance, so consumers can drop duplicates and out-of-order updates.
/// </summary>
public sealed record DeviceState(
    string DeviceId,
    string DisplayName,
    string ModelName,
    DataOrigin Origin,
    long Sequence,
    DateTimeOffset ObservedAt,
    DeviceCapabilities Capabilities,
    LinkState Link,
    BatteryState Battery,
    ListeningState Listening,
    Observation<bool> ConversationAwareness,
    Observation<int> AdaptiveStrength)
{
    public bool IsDemo => Origin == DataOrigin.Demo;

    /// <summary>Contract 1.2: ear placement, with <see cref="DeviceCapabilities.EarDetection"/>. Unknown by default.</summary>
    public EarState Ears { get; init; } = EarState.Unknown;

    /// <summary>
    /// Contract 1.3: which batteries this device has (two buds and a case, two buds with no case battery, or one battery).
    /// Defaults to earbuds with a case, so 1.2 transports need no change.
    /// </summary>
    public BatteryLayout Layout { get; init; } = BatteryLayout.EarbudsWithCase;

    /// <summary>
    /// Contract 1.3: the word for this device in user-facing text: "AirPods", "Beats" or "headphones".
    /// Defaults to "AirPods", so 1.2 transports keep their current wording.
    /// </summary>
    public string DeviceNoun { get; init; } = "AirPods";

    /// <summary>
    /// Contract 1.4: the wire model id the device is known or selected to be, used to pick artwork. Null when no
    /// model is known (for example the hardware engine today). Defaults to null, so 1.3 transports need no change.
    /// </summary>
    public ushort? ModelId { get; init; }

    public bool Supports(DeviceCapabilities capability) => (Capabilities & capability) == capability;
}
