namespace AppleDeviceHub.Core.Ble;

/// <summary>
/// A short-lived, unconfirmed broadcast source for manual inspection. Id is random and local
/// to this observation; neither the Bluetooth address nor the encrypted payload is exposed.
/// Selecting one never confirms ownership and never creates a control or audio connection.
/// </summary>
public sealed record BroadcastSource(
    Guid Id, int Number, ushort ModelId, string ModelName, short? Rssi,
    int? LeftBattery, int? RightBattery, int? CaseBattery, DateTimeOffset LastHeardAt,
    bool? ThisPodInCase = null, bool? LidClosed = null, byte? LidCounter = null,
    IdentityConfidence Identity = IdentityConfidence.Unknown,
    BatteryLayout Layout = BatteryLayout.EarbudsWithCase);
