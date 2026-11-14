using System.Globalization;
using System.Text.Json.Nodes;
using AppleDeviceHub.Core.Ble;

namespace BleObservationProbe.Analysis;

/// <summary>
/// One Apple advertisement as recorded by the probe. Holds no raw BLE address and no device name:
/// only a per-run address tag and whether a name was present.
/// </summary>
public sealed record SightingObservation(
    long Sequence,
    DateTimeOffset Utc,
    double SecondsSinceStart,
    string AddressTag,
    short? Rssi,
    ushort CompanyId,
    byte[] ManufacturerData,
    bool NamePresent,
    IReadOnlyList<AppleTlvEntry> Entries,
    ProximityMessage? Proximity,
    string? ParseFailure,
    string? Attribution = null)
{
    public static SightingObservation Create(
        long sequence, DateTimeOffset utc, double secondsSinceStart, string addressTag, short? rssi,
        ushort companyId, byte[] manufacturerData, bool namePresent)
    {
        ArgumentNullException.ThrowIfNull(manufacturerData);
        var entries = AppleTlv.Walk(manufacturerData);
        var parsed = ProximityMessageParser.Parse(companyId, manufacturerData);
        string? failure = parsed is null
            ? (companyId != ProximityMessage.AppleCompanyId
                ? $"not Apple company id (0x{companyId:X4})"
                : AppleTlv.ExplainParseFailure(manufacturerData, entries))
            : null;
        return new SightingObservation(sequence, utc, secondsSinceStart, addressTag, rssi, companyId,
            manufacturerData, namePresent, entries, parsed, failure);
    }

    /// <summary>Raw byte at an index of the proximity body (bytes after type and length), or null.</summary>
    public static byte? RawAt(ProximityMessage m, int index)
    {
        ArgumentNullException.ThrowIfNull(m);
        return index < m.Raw.Length ? m.Raw[index] : null;
    }

    /// <summary>One JSON object for sightings.jsonl.</summary>
    public JsonObject ToJson()
    {
        var o = new JsonObject
        {
            ["seq"] = Sequence,
            ["utc"] = Utc.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
            ["t"] = Math.Round(SecondsSinceStart, 3),
            ["addressTag"] = AddressTag,
            ["rssi"] = Rssi,
            ["companyId"] = $"0x{CompanyId:X4}",
            ["manufacturerDataHex"] = Convert.ToHexStringLower(ManufacturerData),
            ["namePresent"] = NamePresent,
            ["messageTypes"] = new JsonArray(Entries.Select(e => (JsonNode?)JsonValue.Create(AppleTlv.Hex(e.Type) + (e.Truncated ? "(truncated)" : ""))).ToArray()),
            ["proximityParsed"] = Proximity is not null,
            ["parseFailure"] = ParseFailure,
            ["attribution"] = Attribution,
            ["identity"] = "unconfirmed",
        };
        if (Proximity is ProximityMessage m) o["proximity"] = ProximityJson(m);
        return o;
    }

    private static JsonObject ProximityJson(ProximityMessage m)
    {
        byte? caseByte = RawAt(m, 5);
        byte? lidByte = RawAt(m, 6);
        return new JsonObject
        {
            ["pairedFlag"] = HexOrNull(m.PairedFlag),
            ["modelId"] = m.ModelId is ushort id ? $"0x{id:X4}" : null,
            ["modelName"] = m.ModelId is ushort id2 ? AppleModelCatalog.DisplayNameOf(id2) : null,
            ["status"] = HexOrNull(m.Status),
            ["primaryIsLeft"] = m.PrimaryIsLeft,
            ["podsByte"] = HexOrNull(m.PodBatteryByte),
            ["podsHighNibble"] = m.PodBatteryByte is byte p ? p >> 4 : null,
            ["podsLowNibble"] = m.PodBatteryByte is byte p2 ? p2 & 0x0F : null,
            ["leftBatteryPercent"] = m.LeftBatteryPercent,
            ["rightBatteryPercent"] = m.RightBatteryPercent,
            ["caseNibble"] = caseByte is byte c ? c & 0x0F : null,
            ["caseBatteryPercent"] = m.CaseBatteryPercent,
            ["chargingFlags"] = m.ChargingFlags is byte f ? (int)f : null,
            ["leftCharging"] = m.LeftCharging,
            ["rightCharging"] = m.RightCharging,
            ["caseCharging"] = m.CaseCharging,
            ["thisPodInCase"] = m.ThisPodInCase,
            ["onePodInCase"] = m.OnePodInCase,
            ["bothPodsInCase"] = m.BothPodsInCase,
            ["leftInEar"] = m.LeftInEar,
            ["rightInEar"] = m.RightInEar,
            ["lidByte"] = HexOrNull(lidByte),
            ["lidCounter"] = m.LidCounter is byte lc ? (int)lc : null,
            ["lidClosed"] = m.LidClosed,
            ["colorCode"] = HexOrNull(m.ColorCode),
            ["connectionStateCode"] = HexOrNull(m.ConnectionStateCode),
            ["encryptedTailPresent"] = m.EncryptedPayload is not null,
        };
    }

    private static string? HexOrNull(byte? b) => b is byte x ? AppleTlv.Hex(x) : null;
}
