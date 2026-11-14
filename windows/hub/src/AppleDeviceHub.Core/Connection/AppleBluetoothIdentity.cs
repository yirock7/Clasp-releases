using System.Globalization;
using System.Text.RegularExpressions;

namespace AppleDeviceHub.Core.Connection;

/// <summary>
/// Reads the Apple model id out of a Windows Bluetooth device or instance id. Apple devices carry
/// <c>VID&amp;0001004C_PID&amp;XXXX</c> (Bluetooth SIG vendor source, Apple vendor id).
/// <para>
/// Two byte orders exist for the same id. Windows writes the PID byte-swapped relative to the
/// nearby broadcast: AirPods Pro 3 is <c>PID&amp;2027</c> in Windows and the bytes <c>27 20</c> on the
/// wire. The app-wide ("canonical") model id is the WIRE form (0x2720), the one
/// <see cref="Ble.ProximityMessage.ModelId"/> and <see cref="Ble.AppleModelCatalog"/> use. This class is
/// the only place that converts, so nothing else ever sees the Windows PID order.
/// </para>
/// </summary>
public static partial class AppleBluetoothIdentity
{
    [GeneratedRegex(@"VID&0001004C_PID&(?<pid>[0-9A-F]{4})(?![0-9A-F])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex ModelPattern();

    /// <summary>
    /// The canonical (wire-order) model id, or null when the text has no Apple vendor/product marker.
    /// <c>PID&amp;2027</c> in the Windows id becomes 0x2720.
    /// </summary>
    public static ushort? TryParseModelId(string? windowsDeviceId)
    {
        if (string.IsNullOrEmpty(windowsDeviceId)) return null;
        try
        {
            var match = ModelPattern().Match(windowsDeviceId);
            return match.Success
                && ushort.TryParse(match.Groups["pid"].ValueSpan, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var model)
                ? PidToWireModelId(model)
                : null;
        }
        catch (RegexMatchTimeoutException) { return null; }
    }

    /// <summary>Windows PID order to the canonical broadcast order (swap the two bytes). Its own inverse.</summary>
    public static ushort PidToWireModelId(ushort windowsPid) => (ushort)((windowsPid << 8) | (windowsPid >> 8));
}
