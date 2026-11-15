using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace AppleDeviceHub.App.Media;

/// <summary>
/// Win32 and Core Audio declarations for <see cref="StemActionHook"/>. Kept separate from the shell
/// layer's NativeMethods so this feature can be removed without touching shared files.
/// </summary>
internal static partial class StemActionInterop
{
    public const int WH_KEYBOARD_LL = 13;
    public const int HC_ACTION = 0;
    public const uint WM_KEYDOWN = 0x0100;
    public const uint WM_KEYUP = 0x0101;
    public const uint WM_SYSKEYDOWN = 0x0104;
    public const uint WM_SYSKEYUP = 0x0105;
    public const uint WM_QUIT = 0x0012;
    public const uint PM_NOREMOVE = 0x0000;

    public const uint LLKHF_LOWER_IL_INJECTED = 0x02;
    public const uint LLKHF_INJECTED = 0x10;

    public const ushort VK_MEDIA_NEXT_TRACK = 0xB0;
    public const ushort VK_MEDIA_PREV_TRACK = 0xB1;
    public const ushort VK_MEDIA_PLAY_PAUSE = 0xB3;

    public const uint INPUT_KEYBOARD = 1;
    public const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    public const uint KEYEVENTF_KEYUP = 0x0002;

    public delegate nint LowLevelKeyboardProc(int nCode, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct KbdLlHookStruct
    {
        public uint VkCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Msg
    {
        public nint Hwnd;
        public uint Message;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int PtX;
        public int PtY;
    }

    // INPUT on x64 is 40 bytes: type (4) + padding (4) + a 32-byte union whose largest member is MOUSEINPUT.
    // Only the keyboard member is used; the explicit size keeps the struct the size SendInput expects.
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    public struct Input
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public ushort VirtualKey;
        [FieldOffset(10)] public ushort ScanCode;
        [FieldOffset(12)] public uint Flags;
        [FieldOffset(16)] public uint Time;
        [FieldOffset(24)] public nuint ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint SetWindowsHookExW(int idHook, LowLevelKeyboardProc proc, nint module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("user32.dll")]
    public static extern nint CallNextHookEx(nint hook, int nCode, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PeekMessageW(out Msg message, nint hwnd, uint min, uint max, uint remove);

    [DllImport("user32.dll")]
    public static extern int GetMessageW(out Msg message, nint hwnd, uint min, uint max);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostThreadMessageW(uint threadId, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint count, Input[] inputs, int size);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern nint GetModuleHandleW(string? name);

    // ---- Core Audio: default microphone mute -------------------------------------------

    private const int Capture = 1;
    private const int Console = 0;
    private const int Communications = 2;
    private static readonly Guid EndpointVolumeIid = new("5CDF2C82-841E-4546-9722-0CF74078229A");
    private static readonly Guid MMDeviceEnumeratorId = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid MMDeviceEnumeratorIid = new("A95664D2-9614-4F35-A746-DE8DB63617E6");

    /// <summary>
    /// Toggles mute on the default microphone. The communications endpoint (what call apps use) decides
    /// the new state; the console endpoint is set to match when it is a different device. Returns the
    /// new muted state, or null when no microphone or no mute control is available.
    /// </summary>
    public static bool? ToggleDefaultMicrophoneMute()
    {
        IMMDeviceEnumerator? enumerator = null;
        try
        {
            Marshal.ThrowExceptionForHR(CoCreateInstance(MMDeviceEnumeratorId, 0, 23 /* CLSCTX_ALL */, MMDeviceEnumeratorIid, out enumerator));
            var primary = OpenVolume(enumerator, Communications, out var primaryId)
                ?? OpenVolume(enumerator, Console, out primaryId);
            if (primary is null) return null;
            try
            {
                if (primary.GetMute(out var muted) != 0) return null;
                var target = !muted;
                var context = Guid.Empty;
                if (primary.SetMute(target, ref context) != 0) return null;

                var secondary = OpenVolume(enumerator, Console, out var secondaryId);
                if (secondary is not null)
                {
                    try
                    {
                        if (!string.Equals(primaryId, secondaryId, StringComparison.OrdinalIgnoreCase))
                            secondary.SetMute(target, ref context);
                    }
                    finally { ComInterop.Release(secondary); }
                }
                return target;
            }
            finally { ComInterop.Release(primary); }
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException or InvalidOperationException)
        {
            return null;
        }
        finally
        {
            ComInterop.Release(enumerator);
        }
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, [MarshalUsing(typeof(UniqueComInterfaceMarshaller<IMMDeviceEnumerator>))] out IMMDeviceEnumerator instance);

    private static IAudioEndpointVolume? OpenVolume(IMMDeviceEnumerator enumerator, int role, out string? id)
    {
        id = null;
        if (enumerator.GetDefaultAudioEndpoint(Capture, role, out var device) != 0 || device is null) return null;
        try
        {
            device.GetId(out id);
            var iid = EndpointVolumeIid;
            if (device.Activate(ref iid, 23 /* CLSCTX_ALL */, 0, out var volume) != 0) return null;
            try { return ComInterop.Wrap<IAudioEndpointVolume>(volume); }
            catch (InvalidCastException) { return null; }
        }
        finally { ComInterop.Release(device); }
    }


    [GeneratedComInterface, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    internal partial interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, uint stateMask, out nint devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, [MarshalUsing(typeof(UniqueComInterfaceMarshaller<IMMDevice>))] out IMMDevice device);
    }

    [GeneratedComInterface, Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    internal partial interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint context, nint activation, out IntPtr result);
        [PreserveSig] int OpenPropertyStore(uint access, out nint store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }

    // Full vtable order is required even though only GetMute/SetMute are called.
    [GeneratedComInterface, Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    internal partial interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(nint notify);
        [PreserveSig] int UnregisterControlChangeNotify(nint notify);
        [PreserveSig] int GetChannelCount(out uint channelCount);
        [PreserveSig] int SetMasterVolumeLevel(float level, ref Guid eventContext);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
        [PreserveSig] int GetMasterVolumeLevel(out float level);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float level, ref Guid eventContext);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid eventContext);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float level);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
