namespace AppleDeviceHub.Core.Ble;

/// <summary>
/// One received BLE advertisement carrying manufacturer data. Only what a passive listener can
/// hear: no connection, pairing or GATT is involved in producing it.
/// </summary>
/// <param name="Address">The (usually random, rotating) BLE address. Never shown in full.</param>
/// <param name="ReceivedAt">When the platform reported it.</param>
/// <param name="Rssi">Signal strength in dBm, when known.</param>
/// <param name="LocalName">The advertised name, if any. Never sufficient to identify a device.</param>
/// <param name="CompanyId">Manufacturer company id of <paramref name="ManufacturerData"/>.</param>
/// <param name="ManufacturerData">Manufacturer data with the company id stripped.</param>
public sealed record AdvertisementSighting(
    ulong Address,
    DateTimeOffset ReceivedAt,
    short? Rssi,
    string? LocalName,
    ushort CompanyId,
    IReadOnlyList<byte> ManufacturerData);

public enum AdvertisementSourceState
{
    Stopped = 0,
    Running = 1,

    /// <summary>The radio is off, missing, disabled by policy, or access was denied. See the reason.</summary>
    Unavailable = 2,
}

public sealed record AdvertisementSourceStatus(AdvertisementSourceState State, string? Reason = null)
{
    public static AdvertisementSourceStatus Stopped { get; } = new(AdvertisementSourceState.Stopped);
}

/// <summary>
/// A passive BLE advertisement listener. Implementations must not scan until
/// <see cref="StartAsync"/> is called, must never connect, pair or write, and must report radio
/// problems through <see cref="Status"/> instead of throwing.
/// <para>
/// Lifecycle contract (EF-IR-01 review): <see cref="StopAsync"/> and Dispose cancel any start
/// still in progress (it then returns Stopped and creates no listener), and only the current
/// start or running listener may raise <c>StatusChanged</c>. A start that a Stop has already
/// retired must not raise it, because the consumer treats a non-Running event as the loss of
/// whatever observation is current. The transport also defends against a source that reports
/// Running late, but it cannot tell a retired start's event from a genuine loss.
/// </para>
/// </summary>
public interface IAdvertisementSource : IDisposable
{
    AdvertisementSourceStatus Status { get; }

    /// <summary>Raised on any thread while running.</summary>
    event EventHandler<AdvertisementSighting>? Received;

    /// <summary>Raised on any thread when <see cref="Status"/> changes.</summary>
    event EventHandler<AdvertisementSourceStatus>? StatusChanged;

    /// <summary>Start listening. Returns the resulting status (Running or Unavailable with a reason).</summary>
    Task<AdvertisementSourceStatus> StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Stop listening. Idempotent.</summary>
    Task StopAsync();
}
