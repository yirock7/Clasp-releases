namespace AppleDeviceHub.Core.Contracts;

/// <summary>A request from the UI to the device engine. Each carries its own id for result matching.</summary>
public abstract record DeviceCommand(Guid CommandId);

public sealed record SetListeningModeCommand(Guid CommandId, ListeningMode Mode) : DeviceCommand(CommandId);

public sealed record SetConversationAwarenessCommand(Guid CommandId, bool Enabled) : DeviceCommand(CommandId);

/// <summary>Adaptive Audio strength, 0..100.</summary>
public sealed record SetAdaptiveStrengthCommand(Guid CommandId, int Percent) : DeviceCommand(CommandId);

/// <summary>
/// Bring the selected (already paired) device's audio and control session up. Acknowledged
/// means the engine started trying; success is only shown once a later state reports it.
/// </summary>
public sealed record ConnectCommand(Guid CommandId) : DeviceCommand(CommandId);

/// <summary>Release the device's audio and control session; the pairing is kept.</summary>
public sealed record DisconnectCommand(Guid CommandId) : DeviceCommand(CommandId);

public enum CommandOutcome
{
    /// <summary>
    /// The engine accepted the command for delivery. NOT proof that the device changed:
    /// the UI waits for a readback in a later <see cref="DeviceState"/> before confirming.
    /// </summary>
    Acknowledged = 0,

    /// <summary>Invalid for this device or state (for example, an unsupported mode).</summary>
    Rejected = 1,

    /// <summary>Delivery failed.</summary>
    Failed = 2,

    TimedOut = 3,
    Unsupported = 4,
    TransportUnavailable = 5,
}

public sealed record CommandResult(Guid CommandId, CommandOutcome Outcome, DataOrigin Origin, string? Detail = null)
{
    public bool IsAcknowledged => Outcome == CommandOutcome.Acknowledged;
}

/// <summary>Identifies a transport implementation and where its data comes from.</summary>
public sealed record TransportInfo(string Name, DataOrigin Origin, string ContractVersion, string Description);

/// <summary>
/// The boundary between the desktop UI and an AirPods engine. Phase one ships only the demo
/// implementation; a future NTPods-daemon adapter implements the same interface.
/// </summary>
public interface IAirPodsTransport : IAsyncDisposable
{
    TransportInfo Info { get; }

    /// <summary>Latest state, or null before the first report.</summary>
    DeviceState? Current { get; }

    /// <summary>
    /// Raised for every new state. May be raised on any thread. <see cref="DeviceState.Sequence"/>
    /// increases strictly within one transport instance.
    /// </summary>
    event EventHandler<DeviceState>? StateChanged;

    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Send one command and return the engine's answer. One attempt only: implementations must
    /// not retry silently when the outcome is uncertain.
    /// </summary>
    Task<CommandResult> SendAsync(DeviceCommand command, CancellationToken cancellationToken = default);
}
