using AppleDeviceHub.Core.Ble;

namespace BleObservationProbe.Analysis;

/// <summary>One type-length-value entry inside Apple manufacturer data.</summary>
public readonly record struct AppleTlvEntry(byte Type, int Offset, int DeclaredLength, bool Truncated);

/// <summary>
/// Walks Apple manufacturer data (company id stripped) as a sequence of [type][length][body]
/// entries, and explains why the proximity parser declined a payload. Pure; never throws.
/// </summary>
public static class AppleTlv
{
    public static IReadOnlyList<AppleTlvEntry> Walk(ReadOnlySpan<byte> data)
    {
        var entries = new List<AppleTlvEntry>();
        int i = 0;
        while (i < data.Length)
        {
            byte type = data[i];
            if (i + 1 >= data.Length)
            {
                entries.Add(new AppleTlvEntry(type, i, 0, Truncated: true));
                break;
            }
            int length = data[i + 1];
            bool truncated = i + 2 + length > data.Length;
            entries.Add(new AppleTlvEntry(type, i, length, truncated));
            if (truncated) break;
            i += 2 + length;
        }
        return entries;
    }

    public static string Hex(byte b) => "0x" + b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Why <see cref="ProximityMessageParser"/> returned null for this payload (call only then).
    /// </summary>
    public static string ExplainParseFailure(ReadOnlySpan<byte> data, IReadOnlyList<AppleTlvEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (data.Length == 0) return "empty manufacturer data";
        if (!entries.Any(e => e.Type == ProximityMessage.ProximityPairingType))
            return "not a proximity message (types: " + string.Join(", ", entries.Select(e => Hex(e.Type))) + ")";
        if (data[0] != ProximityMessage.ProximityPairingType)
            return "proximity message is not the first entry; the parser only decodes it at offset 0";
        if (data.Length < 5) return $"too short ({data.Length} bytes)";
        if (data[1] + 2 > data.Length) return $"truncated (declares {data[1]} body bytes, {data.Length - 2} heard)";
        if (data[2] == 0x00) return "pairing-mode message (different layout, not decoded)";
        return "parser returned no result";
    }
}
