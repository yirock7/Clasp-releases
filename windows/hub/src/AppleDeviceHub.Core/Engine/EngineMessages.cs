using System.Text.Json;
using AppleDeviceHub.Core.Contracts;

namespace AppleDeviceHub.Core.Engine;

/// <summary>Any clasp-engine v1 message, in either direction.</summary>
public abstract record ClaspMessage
{
    /// <summary>The wire <c>type</c> value.</summary>
    public abstract string Type { get; }
}

// ---- Client to engine ------------------------------------------------------------------

/// <summary>A message this app sends. <see cref="ToUtf8Json"/> produces the wire JSON object.</summary>
public abstract record ClientMessage : ClaspMessage
{
    public byte[] ToUtf8Json()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", Type);
            WriteFields(writer);
            writer.WriteEndObject();
        }
        return buffer.ToArray();
    }

    protected abstract void WriteFields(Utf8JsonWriter writer);
}

/// <summary>A client message that expects exactly one reply matched by <see cref="RequestId"/>.</summary>
public abstract record ClientRequest(string RequestId) : ClientMessage;

public sealed record HelloMessage(string Protocol, int Major, int Minor, string Client) : ClientMessage
{
    public override string Type => "hello";

    protected override void WriteFields(Utf8JsonWriter writer)
    {
        writer.WriteString("protocol", Protocol);
        writer.WriteNumber("major", Major);
        writer.WriteNumber("minor", Minor);
        writer.WriteString("client", Client);
    }
}

public sealed record ListDevicesRequest(string RequestId) : ClientRequest(RequestId)
{
    public override string Type => "list_devices";

    protected override void WriteFields(Utf8JsonWriter writer) => writer.WriteString("request_id", RequestId);
}

public sealed record OpenSessionRequest(string RequestId, string DeviceId, EngineSessionMode Mode) : ClientRequest(RequestId)
{
    public override string Type => "open_session";

    protected override void WriteFields(Utf8JsonWriter writer)
    {
        writer.WriteString("request_id", RequestId);
        writer.WriteString("device_id", DeviceId);
        writer.WriteString("mode", EngineWire.SessionModeName(Mode));
    }
}

public sealed record CloseSessionRequest(string RequestId, long SessionId) : ClientRequest(RequestId)
{
    public override string Type => "close_session";

    protected override void WriteFields(Utf8JsonWriter writer)
    {
        writer.WriteString("request_id", RequestId);
        writer.WriteNumber("session_id", SessionId);
    }
}

public sealed record SetListeningModeRequest(string RequestId, long SessionId, ListeningMode Mode) : ClientRequest(RequestId)
{
    public override string Type => "set_listening_mode";

    protected override void WriteFields(Utf8JsonWriter writer)
    {
        writer.WriteString("request_id", RequestId);
        writer.WriteNumber("session_id", SessionId);
        writer.WriteString("mode", EngineWire.ListeningModeName(Mode));
    }
}

/// <summary>
/// Protocol 1.1: one Conversation Awareness write. Sent only when the engine negotiated 1.1 and
/// advertised <see cref="EngineWire.CapabilityConversationAwarenessWrite"/>.
/// </summary>
public sealed record SetConversationAwarenessRequest(string RequestId, long SessionId, bool Enabled) : ClientRequest(RequestId)
{
    public override string Type => "set_conversation_awareness";

    protected override void WriteFields(Utf8JsonWriter writer)
    {
        writer.WriteString("request_id", RequestId);
        writer.WriteNumber("session_id", SessionId);
        writer.WriteBoolean("enabled", Enabled);
    }
}

public sealed record CancelRequest(string RequestId, string TargetRequestId) : ClientRequest(RequestId)
{
    public override string Type => "cancel";

    protected override void WriteFields(Utf8JsonWriter writer)
    {
        writer.WriteString("request_id", RequestId);
        writer.WriteString("target_request_id", TargetRequestId);
    }
}

// ---- Engine to client ------------------------------------------------------------------

/// <summary>A message from the engine. <see cref="Seq"/> increases strictly within one engine epoch.</summary>
public abstract record EngineMessage(long Seq) : ClaspMessage
{
    /// <summary>The request this message answers, or null for unsolicited messages.</summary>
    public virtual string? ReplyTo => null;
}

public sealed record HelloAckMessage(
    long Seq, string Protocol, int Major, int Minor, string Engine, string EngineEpoch, IReadOnlyList<string> Capabilities)
    : EngineMessage(Seq)
{
    public override string Type => "hello_ack";
}

public enum DriverInterfaceStatus
{
    /// <summary>A value this client does not know (treated as not bound).</summary>
    Unknown = 0,
    Bound = 1,
    NotFound = 2,
    Ambiguous = 3,
    Unparsed = 4,
}

public sealed record EngineDevice(
    string DeviceId, string Name, bool AapService, DriverInterfaceStatus DriverInterface, bool? WindowsConnected);

public sealed record DevicesMessage(long Seq, string RequestId, IReadOnlyList<EngineDevice> Devices) : EngineMessage(Seq)
{
    public override string Type => "devices";

    public override string? ReplyTo => RequestId;
}

public enum EngineOutcome
{
    /// <summary>A value this client does not know (treated as a failure).</summary>
    Unknown = 0,
    Accepted = 1,
    Rejected = 2,
    Failed = 3,
    Unsupported = 4,
    TimedOut = 5,
    Cancelled = 6,
}

public sealed record ResultMessage(long Seq, string RequestId, EngineOutcome Outcome, string? Detail, long? SessionId)
    : EngineMessage(Seq)
{
    public override string Type => "result";

    public override string? ReplyTo => RequestId;
}

public enum EngineSessionState
{
    /// <summary>A value this client does not know (treated as not live).</summary>
    Unknown = 0,
    Opening = 1,
    Open = 2,
    Closed = 3,
    Lost = 4,
}

/// <summary>
/// <see cref="WritableSettings"/> (protocol 1.1) is null when the engine did not send the field
/// (a 1.0 engine); otherwise it holds the known setting names only
/// (<see cref="EngineWire.SettingListeningMode"/>, <see cref="EngineWire.SettingConversationAwareness"/>).
/// </summary>
public sealed record SessionMessage(
    long Seq, long SessionId, string DeviceId, EngineSessionState State, string? Reason, IReadOnlyList<ListeningMode> WritableModes,
    IReadOnlyList<string>? WritableSettings = null)
    : EngineMessage(Seq)
{
    public override string Type => "session";
}

/// <summary>
/// One battery component as the engine observed it. <c>Level == null</c>: absent or unavailable.
/// <c>Charging == null</c>: unknown, never false.
/// </summary>
public sealed record ObservedBattery(int? Level, bool? Charging);

/// <summary>Wire listening-mode values; <see cref="Unrecognised"/> is the engine's <c>"unknown"</c>.</summary>
public enum EngineListeningMode
{
    Unrecognised = 0,
    Off = 1,
    NoiseCancellation = 2,
    Transparency = 3,
    Adaptive = 4,
}

/// <summary>Protocol 1.1 Conversation Awareness values; <see cref="Unrecognised"/> is the engine's <c>"unknown"</c>.</summary>
public enum EngineConversationAwareness
{
    Unrecognised = 0,
    On = 1,
    Off = 2,
}

/// <summary>Protocol 1.1 ear-detection values for one bud; <see cref="Unrecognised"/> is <c>"unknown"</c> or any value this client does not know.</summary>
public enum EngineEarState
{
    Unrecognised = 0,
    InEar = 1,
    OutOfEar = 2,
    InCase = 3,
    Disconnected = 4,
}

/// <summary>Protocol 1.1 <c>ear</c> field: the AirPods' own primary/secondary roles, not left/right.</summary>
public sealed record ObservedEars(EngineEarState Primary, EngineEarState Secondary);

/// <summary>
/// The fields ONE inbound AAP packet carried. A null member was not in the packet and was
/// therefore not observed. <see cref="ConversationAwareness"/> and <see cref="Ears"/> are protocol 1.1.
/// </summary>
public sealed record ObservationFields(
    ObservedBattery? BatteryLeft, ObservedBattery? BatteryRight, ObservedBattery? BatteryCase, EngineListeningMode? ListeningMode,
    EngineConversationAwareness? ConversationAwareness = null, ObservedEars? Ears = null);

public sealed record ObservationMessage(long Seq, long SessionId, string DeviceId, DateTimeOffset ObservedAt, ObservationFields Fields)
    : EngineMessage(Seq)
{
    public override string Type => "observation";
}

public sealed record ErrorMessage(long Seq, string Code, string Detail, string? RequestId) : EngineMessage(Seq)
{
    public override string Type => "error";

    public override string? ReplyTo => RequestId;
}

/// <summary>Which kind of AAP session to open.</summary>
public enum EngineSessionMode
{
    /// <summary>Protocol setup writes only; no setting writes.</summary>
    Observe = 0,

    /// <summary>Also permits one listening-mode write per request.</summary>
    Control = 1,
}

/// <summary>String values shared by the codec and the transport.</summary>
public static class EngineWire
{
    public const string Protocol = "clasp-engine";
    public const int Major = 1;

    /// <summary>The minor version this client speaks (1.1). A 1.0 engine is still accepted; see <see cref="EngineFeatures"/>.</summary>
    public const int Minor = 1;

    public const string CapabilityObserveConversationAwareness = "observe_conversation_awareness";
    public const string CapabilityObserveEar = "observe_ear";
    public const string CapabilityConversationAwarenessWrite = "conversation_awareness_write";

    public const string SettingListeningMode = "listening_mode";
    public const string SettingConversationAwareness = "conversation_awareness";

    public static string SessionModeName(EngineSessionMode mode) => mode == EngineSessionMode.Control ? "control" : "observe";

    public static string ListeningModeName(ListeningMode mode) => mode switch
    {
        ListeningMode.Off => "off",
        ListeningMode.NoiseCancellation => "noise_cancellation",
        ListeningMode.Transparency => "transparency",
        ListeningMode.Adaptive => "adaptive",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    public static ListeningMode? ParseListeningMode(string value) => value switch
    {
        "off" => ListeningMode.Off,
        "noise_cancellation" => ListeningMode.NoiseCancellation,
        "transparency" => ListeningMode.Transparency,
        "adaptive" => ListeningMode.Adaptive,
        _ => null,
    };

    public static ListeningMode? ToContract(EngineListeningMode mode) => mode switch
    {
        EngineListeningMode.Off => ListeningMode.Off,
        EngineListeningMode.NoiseCancellation => ListeningMode.NoiseCancellation,
        EngineListeningMode.Transparency => ListeningMode.Transparency,
        EngineListeningMode.Adaptive => ListeningMode.Adaptive,
        _ => null,
    };

    /// <summary>On/off, or null for the engine's "unknown" (never treated as off).</summary>
    public static bool? ToContract(EngineConversationAwareness value) => value switch
    {
        EngineConversationAwareness.On => true,
        EngineConversationAwareness.Off => false,
        _ => null,
    };

    /// <summary>A resting placement, or null for "unknown".</summary>
    public static PodPlacement? ToContract(EngineEarState value) => value switch
    {
        EngineEarState.InEar => PodPlacement.InEar,
        EngineEarState.OutOfEar => PodPlacement.OutOfEar,
        EngineEarState.InCase => PodPlacement.InCase,
        EngineEarState.Disconnected => PodPlacement.Disconnected,
        _ => null,
    };
}

/// <summary>
/// What the connected engine negotiated in its <c>hello_ack</c>. A 1.1 feature is used only when
/// the ack reports minor 1 or later AND lists the capability; a 1.0 engine gets none of them.
/// </summary>
public sealed record EngineFeatures(bool ObserveConversationAwareness, bool ObserveEar, bool ConversationAwarenessWrite)
{
    public static EngineFeatures None { get; } = new(false, false, false);

    public static EngineFeatures From(HelloAckMessage ack)
    {
        ArgumentNullException.ThrowIfNull(ack);
        if (ack.Major != EngineWire.Major || ack.Minor < 1) return None;
        bool Has(string name) => ack.Capabilities.Contains(name, StringComparer.Ordinal);
        return new(
            Has(EngineWire.CapabilityObserveConversationAwareness),
            Has(EngineWire.CapabilityObserveEar),
            // Writing without being able to read back would never be confirmable.
            Has(EngineWire.CapabilityConversationAwarenessWrite) && Has(EngineWire.CapabilityObserveConversationAwareness));
    }
}
