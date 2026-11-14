using System.Globalization;
using System.Text.RegularExpressions;

namespace AppleDeviceHub.Core.Connection;

/// <summary>
/// Reads a Bluetooth device address (48 bits) out of a Windows device identifier. Windows writes
/// the address into several id shapes, for example the paired-device (association endpoint) id
/// <c>Bluetooth#Bluetooth66:77:88:99:aa:bb-00:11:22:33:44:55</c> (the first address is this PC's
/// radio, the last one is the paired device) and the classic enumerator id
/// <c>BTHENUM\DEV_001122334455\...</c>. The result uses the same numeric convention as Win32
/// <c>BLUETOOTH_ADDRESS.ullLong</c> and WinRT <c>BluetoothDevice.BluetoothAddress</c>:
/// 00:11:22:33:44:55 is 0x001122334455.
/// </summary>
public static partial class BluetoothAddressParser
{
    [GeneratedRegex(@"(?<![0-9A-F])(?:[0-9A-F]{2}:){5}[0-9A-F]{2}(?![0-9A-F])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex ColonPattern();

    [GeneratedRegex(@"DEV_(?<mac>[0-9A-F]{12})(?![0-9A-F])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex EnumeratorPattern();

    /// <summary>The device address, or null when the id contains none (or only an all-zero one).</summary>
    public static ulong? TryParse(string? windowsDeviceId)
    {
        if (string.IsNullOrEmpty(windowsDeviceId)) return null;
        try
        {
            // The enumerator form is unambiguous: prefer it.
            var dev = EnumeratorPattern().Match(windowsDeviceId);
            if (dev.Success && Hex(dev.Groups["mac"].Value) is { } fromEnumerator) return fromEnumerator;

            // Otherwise the LAST address in the text is the paired device (the first, when
            // there are two, is the local radio).
            ulong? last = null;
            foreach (Match match in ColonPattern().Matches(windowsDeviceId))
            {
                var digits = match.Value.Replace(":", "", StringComparison.Ordinal);
                if (Hex(digits) is { } value) last = value;
            }
            return last;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    private static ulong? Hex(string twelveHexDigits)
    {
        if (twelveHexDigits.Length != 12) return null;
        return ulong.TryParse(twelveHexDigits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value) && value != 0
            ? value
            : null;
    }
}
