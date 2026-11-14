namespace AppleDeviceHub.Core.Connection;

/// <summary>
/// A paired Bluetooth identity and the render endpoints Windows associates with its exact
/// ContainerId. Device names are display text only and never establish identity.
/// <paramref name="ModelId"/> is the Apple model id parsed from the Windows device id
/// (<see cref="AppleBluetoothIdentity"/>), null when Windows exposes none.
/// <paramref name="WindowsBatteryPercent"/> is Windows' Handsfree battery reading, null when
/// absent (normal: it is missing whenever Handsfree is disabled) and never a guess.
/// <paramref name="HasHandsfreeService"/> says whether Windows lists a Handsfree service node for
/// the device: false explains a missing battery, null means Windows' node list was not read.
/// </summary>
public sealed record ConnectionDevice(
    string Id,
    string Name,
    string ContainerId,
    bool HasAvailableAudioEndpoint,
    bool IsConnected,
    bool IsCurrentOutput,
    bool CanReconnect,
    string? Detail,
    ushort? ModelId = null,
    int? WindowsBatteryPercent = null,
    bool? HasHandsfreeService = null)
{
    // Picker items show this text; it is also the name screen readers announce for the item.
    public override string ToString() => Name;
}

/// <summary>A Windows render endpoint associated with a Bluetooth device by ContainerId.</summary>
public sealed record ConnectionAudioEndpoint(
    string Id,
    string Name,
    string ContainerId,
    bool IsAvailable,
    bool IsCurrentOutput,
    bool SupportsReconnect,
    string? Detail = null);

/// <summary>
/// A complete metadata snapshot. An incomplete snapshot must not be treated as proof that a
/// paired device or endpoint disappeared.
/// </summary>
public sealed record ConnectionSnapshot(
    bool IsComplete,
    IReadOnlyList<ConnectionDevice> Devices,
    IReadOnlyList<ConnectionAudioEndpoint> AudioEndpoints,
    string? DetailText)
{
    public static ConnectionSnapshot Incomplete(string detailText)
        => new(false, Array.Empty<ConnectionDevice>(), Array.Empty<ConnectionAudioEndpoint>(), detailText);
}

/// <summary>The outcome of asking the Windows Bluetooth audio driver for a reconnect attempt.</summary>
public enum ConnectionAttemptKind
{
    /// <summary>Windows accepted an attempt; a later active-endpoint readback is still required.</summary>
    Attempted = 0,
    Unsupported = 1,
    Busy = 2,
    TimedOut = 3,
    Failed = 4,
}

/// <summary>
/// A native request outcome. <see cref="ConnectionAttemptKind.Attempted"/> means only that Windows accepted an
/// attempt, never that a Bluetooth audio connection was established.
/// </summary>
public sealed record ConnectionAttemptResult(ConnectionAttemptKind Kind, string Detail);

/// <summary>
/// Read-only paired-device and Windows audio metadata plus the documented one-shot audio-driver
/// reconnect and disconnect requests. Implementations never pair, scan, open GATT/RFCOMM, select an
/// output, or change Bluetooth profiles. Native work must stay off the UI thread.
/// </summary>
public interface IAirPodsConnectionController
{
    event EventHandler? Changed;

    /// <summary>True while a metadata or native request operation is still executing.</summary>
    bool IsBusy { get; }

    /// <summary>Reads paired identities and audio endpoint state without connecting to a device.</summary>
    Task<ConnectionSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Makes one bounded reconnect request for this exact paired identity and expected container.
    /// A successful result is only an attempt; callers must verify a later active endpoint.
    /// </summary>
    Task<ConnectionAttemptResult> ReconnectAsync(
        string deviceId,
        string expectedContainerId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Makes one bounded disconnect request (the same Windows audio-driver interface as reconnect)
    /// for this exact paired identity. The Bluetooth pairing is kept. A successful result is only an
    /// attempt; callers must verify that the endpoint became inactive.
    /// </summary>
    Task<ConnectionAttemptResult> DisconnectAsync(
        string deviceId,
        string expectedContainerId,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new ConnectionAttemptResult(ConnectionAttemptKind.Unsupported, "Windows can't disconnect this device."));
}

/// <summary>Container identifiers which cannot safely establish a device-to-endpoint association.</summary>
public static class ConnectionIdentity
{
    private static readonly Guid MachineContainerId = new("00000000-0000-0000-FFFF-FFFFFFFFFFFF");

    public static bool IsUsableContainerId(string? value)
        => Guid.TryParse(value, out var parsed) && parsed != Guid.Empty && parsed != MachineContainerId;

    public static string? NormalizeContainerId(string? value)
        => Guid.TryParse(value, out var parsed) && parsed != Guid.Empty && parsed != MachineContainerId
            ? parsed.ToString("D")
            : null;
}

/// <summary>
/// Recognises a paired device whose audio endpoint is marked not present. This usually means the
/// device nodes are gone, but it can also mean Bluetooth is off or the adapter is missing. Probing
/// such an endpoint fails with "no such device interface".
/// </summary>
public static class ConnectionDriverState
{
    /// <summary>Plain-language guidance shown instead of the raw Windows error code.</summary>
    public const string MissingDriverDetail = "Windows can't find this device's drivers. Check that Bluetooth is on. If it is, remove the device in Bluetooth settings and pair it again, or choose another device in Clasp's Settings.";

    private const uint EndpointNotPresent = 0x4; // DEVICE_STATE_NOTPRESENT; UNPLUGGED (0x8) is a different state.
    private const int MissingInterfaceHResult = unchecked((int)0xE0000225); // ERROR_NO_SUCH_DEVICE_INTERFACE

    /// <summary>True when the endpoint state has the NOTPRESENT bit set, whatever other bits are set.</summary>
    public static bool IsEndpointNotPresent(uint endpointState) => (endpointState & EndpointNotPresent) != 0;

    /// <summary>True for the "no such device interface" HRESULT raised when probing a missing device.</summary>
    public static bool IsMissingInterface(int hresult) => hresult == MissingInterfaceHResult;
}
