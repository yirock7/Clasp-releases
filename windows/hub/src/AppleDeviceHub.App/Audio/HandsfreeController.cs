using System.Runtime.InteropServices;
using AppleDeviceHub.Core.Audio;

namespace AppleDeviceHub.App.Audio;

/// <summary>The result of one attempt to change the Handsfree service.</summary>
/// <param name="Outcome">Categorised result; AccessDenied lets the UI fall back to manual choices.</param>
/// <param name="Win32Error">The raw Win32 code (0 on success), for diagnostics only.</param>
public sealed record HandsfreeResult(HandsfreeOutcome Outcome, int Win32Error);

/// <summary>
/// Enables or disables the Handsfree (HFP, UUID 0000111E) Bluetooth service of a paired
/// device with <c>BluetoothSetServiceState</c> (bthprops.cpl). Windows removes or reinstalls the
/// device's Handsfree driver node, so the headset briefly renegotiates its audio profiles.
/// </summary>
/// <remarks>
/// Microsoft's documentation does not state whether administrator rights are required; because
/// the call installs and removes a driver, an access-denied result is expected on standard
/// accounts and is reported as <see cref="HandsfreeOutcome.AccessDenied"/>, never thrown.
/// The call blocks, so <see cref="SetEnabledAsync"/> runs it on a pool thread. Changing a
/// service state is a system change: callers must only invoke it from the explicit setting.
/// </remarks>
public sealed class HandsfreeController
{
    /// <summary>The Bluetooth Handsfree service class (HFP, 0x111E).</summary>
    public static readonly Guid HandsfreeServiceId = new("0000111E-0000-1000-8000-00805F9B34FB");

    /// <summary>Enables or disables Handsfree for the paired device with this Bluetooth address.</summary>
    public static Task<HandsfreeResult> SetEnabledAsync(ulong bluetoothAddress, bool enabled, CancellationToken cancellationToken = default)
        => Task.Run(() => SetEnabled(bluetoothAddress, enabled), cancellationToken);

    private static HandsfreeResult SetEnabled(ulong address, bool enabled)
    {
        try
        {
            var radios = HandsfreeInterop.OpenRadios();
            if (radios.Count == 0) return new(HandsfreeOutcome.Failed, HandsfreeInterop.ErrorNoMoreItems);
            try
            {
                var lastError = HandsfreeInterop.ErrorNotFound;
                foreach (var radio in radios)
                {
                    var info = HandsfreeInterop.NewDeviceInfo(address);
                    var lookup = HandsfreeInterop.BluetoothGetDeviceInfo(radio, ref info);
                    if (lookup != 0) { lastError = (int)lookup; continue; }

                    var serviceId = HandsfreeServiceId;
                    var code = (int)HandsfreeInterop.BluetoothSetServiceState(
                        radio, ref info, ref serviceId, enabled ? HandsfreeInterop.ServiceEnable : HandsfreeInterop.ServiceDisable);
                    return Map(code);
                }
                return Map(lastError, deviceLookup: true);
            }
            finally { HandsfreeInterop.CloseAll(radios); }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException)
        {
            return new(HandsfreeOutcome.Failed, Marshal.GetLastWin32Error());
        }
    }

    /// <summary>Maps a Win32 code to an outcome. Exposed for review; pure and side-effect free.</summary>
    internal static HandsfreeResult Map(int code, bool deviceLookup = false)
    {
        // "Already in the requested state" is reported as E_INVALIDARG; the end state is what we wanted.
        // Flags are always valid constants here, so plain 87 cannot mean bad flags.
        if (!deviceLookup && code is 0 or 87 or unchecked((int)0x80070057))
            return new(HandsfreeOutcome.Succeeded, 0);
        return code switch
        {
            5 or unchecked((int)0x80070005) => new(HandsfreeOutcome.AccessDenied, code),
            1168 or 1167 or 1169 or 1060 => new(HandsfreeOutcome.DeviceNotFound, code),
            _ when deviceLookup => new(HandsfreeOutcome.DeviceNotFound, code),
            _ => new(HandsfreeOutcome.Failed, code),
        };
    }
}

/// <summary>Private bthprops.cpl declarations for <see cref="HandsfreeController"/>.</summary>
internal static class HandsfreeInterop
{
    internal const uint ServiceDisable = 0;
    internal const uint ServiceEnable = 1;
    internal const int ErrorNotFound = 1168;
    internal const int ErrorNoMoreItems = 259;

    [StructLayout(LayoutKind.Sequential)]
    internal struct SystemTime
    {
        public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct BluetoothDeviceInfo
    {
        public uint Size;
        public ulong Address;
        public uint ClassOfDevice;
        public int Connected;
        public int Remembered;
        public int Authenticated;
        public SystemTime LastSeen;
        public SystemTime LastUsed;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)]
        public string Name;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FindRadioParams
    {
        public uint Size;
    }

    internal static BluetoothDeviceInfo NewDeviceInfo(ulong address) => new()
    {
        Size = (uint)Marshal.SizeOf<BluetoothDeviceInfo>(),
        Address = address,
        Name = "",
    };

    /// <summary>Opens a handle to every local Bluetooth radio. Close with <see cref="CloseAll"/>.</summary>
    internal static List<IntPtr> OpenRadios()
    {
        var radios = new List<IntPtr>();
        var parameters = new FindRadioParams { Size = (uint)Marshal.SizeOf<FindRadioParams>() };
        var find = BluetoothFindFirstRadio(ref parameters, out var radio);
        if (find == IntPtr.Zero) return radios;
        try
        {
            do radios.Add(radio);
            while (BluetoothFindNextRadio(find, out radio));
        }
        finally { BluetoothFindRadioClose(find); }
        return radios;
    }

    internal static void CloseAll(List<IntPtr> radios)
    {
        foreach (var radio in radios) CloseHandle(radio);
    }

    [DllImport("bthprops.cpl", ExactSpelling = true, SetLastError = true)]
    internal static extern IntPtr BluetoothFindFirstRadio(ref FindRadioParams parameters, out IntPtr radio);

    [DllImport("bthprops.cpl", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool BluetoothFindNextRadio(IntPtr find, out IntPtr radio);

    [DllImport("bthprops.cpl", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool BluetoothFindRadioClose(IntPtr find);

    [DllImport("bthprops.cpl", ExactSpelling = true)]
    internal static extern uint BluetoothGetDeviceInfo(IntPtr radio, ref BluetoothDeviceInfo info);

    [DllImport("bthprops.cpl", ExactSpelling = true)]
    internal static extern uint BluetoothSetServiceState(
        IntPtr radio, ref BluetoothDeviceInfo info, ref Guid service, uint flags);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
