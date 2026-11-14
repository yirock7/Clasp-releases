using AppleDeviceHub.Core.Ble;

namespace AppleDeviceHub.Core.Tests.Ble;

/// <summary>
/// Synthetic proximity payloads built from the UPSTREAM-ASSUMED layout (LibrePods 4589176,
/// linux/ble/blemanager.cpp). No byte here was captured from real AirPods.
/// </summary>
internal static class Payload
{
    public const ushort ProTwoUsbC = 0x2420;

    /// <summary>AirPods Pro 3 in wire order (observed on the owner's pair; Windows shows it as PID 2027).</summary>
    public const ushort ProThree = 0x2720;

    /// <summary>An id deliberately absent from the allow-list (stands in for a future, unlisted model).</summary>
    public const ushort Unlisted = 0x3F20;

    public static byte[] Build(
        ushort model = ProTwoUsbC, byte status = 0x22, byte pods = 0x58, byte flagsAndCase = 0x47,
        byte lid = 0x0B, byte color = 0x01, byte connection = 0x05, byte paired = 0x01, bool encrypted = true)
    {
        var body = new List<byte> { paired, (byte)(model >> 8), (byte)(model & 0xFF), status, pods, flagsAndCase, lid, color, connection };
        if (encrypted) body.AddRange(Enumerable.Range(0xA0, 16).Select(i => (byte)i));
        return [ProximityMessage.ProximityPairingType, (byte)body.Count, .. body];
    }

    public static AdvertisementSighting Sighting(byte[] data, ulong address = 0xAABBCCDDEEFF, string? name = null, ushort company = ProximityMessage.AppleCompanyId)
        => new(address, DateTimeOffset.UnixEpoch, -60, name, company, data);
}

/// <summary>Scripted advertisement source. Never touches a radio.</summary>
internal sealed class FakeAdvertisementSource : IAdvertisementSource
{
    public int StartCount { get; private set; }
    public int StopCount { get; private set; }
    public bool Disposed { get; private set; }

    /// <summary>What the next StartAsync answers (default Running).</summary>
    public Func<CancellationToken, Task<AdvertisementSourceStatus>>? OnStart { get; set; }

    public AdvertisementSourceStatus Status { get; private set; } = AdvertisementSourceStatus.Stopped;

    public event EventHandler<AdvertisementSighting>? Received;
    public event EventHandler<AdvertisementSourceStatus>? StatusChanged;

    public async Task<AdvertisementSourceStatus> StartAsync(CancellationToken cancellationToken = default)
    {
        StartCount++;
        Status = OnStart is { } start
            ? await start(cancellationToken)
            : new AdvertisementSourceStatus(AdvertisementSourceState.Running);
        return Status;
    }

    public Task StopAsync()
    {
        StopCount++;
        Status = AdvertisementSourceStatus.Stopped;
        return Task.CompletedTask;
    }

    public void Dispose() => Disposed = true;

    /// <summary>Raise a sighting whether or not the source is "running" (the transport must filter).</summary>
    public void Emit(AdvertisementSighting sighting) => Received?.Invoke(this, sighting);

    public void RaiseStatus(AdvertisementSourceStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(this, status);
    }
}
