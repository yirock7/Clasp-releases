using AppleDeviceHub.Core.Contracts;

namespace AppleDeviceHub.Core.Interop;

/// <summary>
/// Optional, additive companion to <see cref="IAirPodsTransport"/> for hardware transports
/// (local contract 1.1 is unchanged). Lets the UI say exactly why hardware is unavailable and
/// which source and session kind it is looking at. The demo transport does not implement it.
/// </summary>
public interface IHardwareTransportStatus
{
    /// <summary>
    /// Null while the selected device can be used; otherwise one readable sentence saying why
    /// not (bad launch arguments, engine not running, identity check failed, driver interface
    /// not found for this device, ...).
    /// </summary>
    string? UnavailableReason { get; }

    /// <summary>Where the data comes from, for example "Hardware: Clasp engine, observe session".</summary>
    string SourceText { get; }

    /// <summary>Why setting writes are disabled in this configuration (null when they may be offered).</summary>
    string? ReadOnlyReason { get; }

    /// <summary>
    /// Why a Conversation Awareness change cannot be offered right now (null when it may be). Only
    /// consulted while the state claims <see cref="DeviceCapabilities.ConversationAwareness"/>; a
    /// transport that never claims it can keep this default.
    /// </summary>
    string? ConversationAwarenessReadOnlyReason => null;

    /// <summary>
    /// Contract 1.2 broadcast observation: true while the transport is listening, false while it
    /// is not, null for transports that do not observe broadcasts.
    /// </summary>
    bool? ObservationActive => null;

    /// <summary>Raised (on any thread) when <see cref="UnavailableReason"/> changes.</summary>
    event EventHandler? StatusChanged;

    /// <summary>
    /// Session and connection events for the local diagnostics log. Messages name devices only by
    /// their opaque device_id, never by pairing name or address. Raised on any thread.
    /// </summary>
    event EventHandler<string>? Diagnostic;
}
