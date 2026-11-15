using AppleDeviceHub.Core.Contracts;
using AppleDeviceHub.Core.Interop;

namespace AppleDeviceHub.Core.Engine;

/// <summary>
/// Stands in when hardware was explicitly requested but cannot even be attempted (missing or
/// invalid launch arguments, unknown transport). It reports no device state and refuses every
/// command, so nothing on screen can be mistaken for real or demo data.
/// </summary>
public sealed class UnavailableHardwareTransport : IAirPodsTransport, IHardwareTransportStatus
{
    public UnavailableHardwareTransport(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        UnavailableReason = reason;
    }

    public TransportInfo Info { get; } = new(NtpodsDaemonTransport.EngineName, DataOrigin.Hardware, HubContract.Version,
        "Hardware was requested but is not available; no device data is shown.");

    public DeviceState? Current => null;

    public string? UnavailableReason { get; }

    public string SourceText => "Hardware: Clasp engine (not connected)";

    public string? ReadOnlyReason => null;

    public event EventHandler<DeviceState>? StateChanged { add { } remove { } }

    public event EventHandler? StatusChanged { add { } remove { } }

    public event EventHandler<string>? Diagnostic { add { } remove { } }

    public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<CommandResult> SendAsync(DeviceCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return Task.FromResult(new CommandResult(command.CommandId, CommandOutcome.TransportUnavailable, DataOrigin.Hardware, UnavailableReason));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
