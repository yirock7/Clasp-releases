using System.Runtime.InteropServices;
using AppleDeviceHub.Core.Audio;
using AppleDeviceHub.Core.Resources;

namespace AppleDeviceHub.App.Audio;

/// <summary>
/// Per-application output through Windows' undocumented AudioPolicyConfig activation factory
/// (the interface behind Settings > Sound > Volume mixer). Experimental: Windows may change or
/// remove it. Every public member returns a result; nothing here throws to the caller.
/// </summary>
/// <remarks>
/// The factory is activated with RoGetActivationFactory("Windows.Media.Internal.AudioPolicyConfig").
/// Two interface ids are known for the same method layout: ab3d4648-e242-459f-b02f-541c70306324
/// (Windows 10 21H2 and later, Windows 11) and 2a59116d-6c4f-45e0-a74f-707e3fef9258 (older Windows
/// 10). Both are tried, newest first; E_NOINTERFACE simply moves to the next. The layout is described
/// by the open-source EarTrumpet project (interface shape only; no code is copied). Instead of a
/// COM interop declaration the vtable is called directly: after IUnknown and IInspectable (six slots)
/// come 19 unused methods, then Set (slot 25), Get (26) and ClearAll (27, never called here). IsSupported
/// activates the factory and performs one read-only Get for this process; if that does not succeed the
/// layout is not trusted and no write is ever attempted. Call from a COM-initialized thread (the UI thread).
/// </remarks>
public sealed class PerAppRouting : IPerAppRouting
{
    private const int DataFlowRender = 0;
    private const int RoleConsole = 0;
    private const int RoleMultimedia = 1;
    private const int SetSlot = 25;
    private const int GetSlot = 26;
    private const int ReleaseSlot = 2;
    private static readonly Guid[] KnownInterfaceIds =
    [
        new("ab3d4648-e242-459f-b02f-541c70306324"), // Windows 10 21H2 and later
        new("2a59116d-6c4f-45e0-a74f-707e3fef9258"), // earlier Windows 10
    ];

    private readonly object _gate = new();
    private IntPtr _factory;
    private SetFn? _set;
    private GetFn? _get;
    private bool _probed;
    private string _probeDetail = Strings.WinPerApp_Support_NotCheckedYet;
    private bool _disposed;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetFn(IntPtr self, uint processId, int flow, int role, IntPtr deviceId);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetFn(IntPtr self, uint processId, int flow, int role, out IntPtr deviceId);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint ReleaseFn(IntPtr self);

    /// <summary>True when the Windows interface activated and passed the read-only probe. Changes nothing.</summary>
    public bool IsSupported
    {
        get { lock (_gate) { return EnsureProbed(); } }
    }

    /// <summary>Why the feature is (not) available, for diagnostics and the settings page.</summary>
    public string SupportDetail
    {
        get { lock (_gate) { EnsureProbed(); return _probeDetail; } }
    }

    /// <summary>Routes the process to the endpoint for the console and multimedia roles.</summary>
    public PerAppRoutingResult SetAppOutput(int processId, string endpointId)
    {
        var persisted = PerAppEndpointId.ToPersisted(endpointId);
        if (processId <= 0 || persisted is null)
            return new(PerAppRoutingStatus.InvalidArgument, 0, "A process and an output are both required; nothing was changed.");
        return Write(processId, persisted, "Routed", endpointId);
    }

    /// <summary>Resets the process to the Windows default output (empty id).</summary>
    public PerAppRoutingResult ClearAppOutput(int processId)
    {
        if (processId <= 0)
            return new(PerAppRoutingStatus.InvalidArgument, 0, "A process is required; nothing was changed.");
        return Write(processId, null, "Reset", null);
    }

    /// <summary>Reads the app's chosen output. Succeeds with a null EndpointId when it follows the default.</summary>
    public PerAppRoutingResult GetAppOutput(int processId)
    {
        if (processId <= 0)
            return new(PerAppRoutingStatus.InvalidArgument, 0, "A process is required.");
        lock (_gate)
        {
            if (!EnsureProbed()) return new(PerAppRoutingStatus.Unsupported, 0, _probeDetail);
            return ReadLocked(processId);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            ReleaseFactory();
        }
    }

    private PerAppRoutingResult Write(int processId, string? persisted, string verb, string? endpointId)
    {
        lock (_gate)
        {
            if (!EnsureProbed() || _set is null) return new(PerAppRoutingStatus.Unsupported, 0, _probeDetail);
            var hstring = IntPtr.Zero; // a null HSTRING is the empty string, which means "default"
            try
            {
                if (persisted is not null)
                {
                    var created = WindowsCreateString(persisted, (uint)persisted.Length, out hstring);
                    if (created < 0) return new(PerAppRoutingStatus.Failed, created, $"Windows could not prepare the output name (0x{created:X8}).");
                }
                foreach (var role in new[] { RoleMultimedia, RoleConsole })
                {
                    var hr = _set(_factory, (uint)processId, DataFlowRender, role, hstring);
                    if (hr < 0)
                        return new(PerAppRoutingStatus.Failed, hr, $"Windows refused the per-app output change (0x{hr:X8}); the app may have exited.");
                }
                var verify = ReadLocked(processId);
                var expected = persisted is null ? "" : PerAppEndpointId.FromPersisted(endpointId);
                if (!verify.Succeeded)
                    return new(PerAppRoutingStatus.Failed, verify.HResult, "Windows accepted the change but it could not be read back.");
                if (!string.Equals(verify.EndpointId ?? "", expected, StringComparison.OrdinalIgnoreCase))
                    return new(PerAppRoutingStatus.Failed, 0, "Windows accepted the change but readback did not confirm it.");
                return new(PerAppRoutingStatus.Success, 0, $"{verb} process {processId}.", verify.EndpointId);
            }
            catch (Exception exception) when (exception is COMException or InvalidCastException or MarshalDirectiveException or SEHException)
            {
                return new(PerAppRoutingStatus.Failed, exception.HResult, $"Per-app output failed (0x{exception.HResult:X8}): {exception.Message}");
            }
            finally { if (hstring != IntPtr.Zero) _ = WindowsDeleteString(hstring); }
        }
    }

    private PerAppRoutingResult ReadLocked(int processId)
    {
        if (_get is null) return new(PerAppRoutingStatus.Unsupported, 0, _probeDetail);
        var hstring = IntPtr.Zero;
        try
        {
            var hr = _get(_factory, (uint)processId, DataFlowRender, RoleMultimedia, out hstring);
            if (hr < 0) return new(PerAppRoutingStatus.Failed, hr, $"Windows could not read the app's output (0x{hr:X8}).");
            string? text = null;
            if (hstring != IntPtr.Zero)
            {
                var raw = WindowsGetStringRawBuffer(hstring, out var length);
                if (raw != IntPtr.Zero && length > 0) text = Marshal.PtrToStringUni(raw, (int)length);
            }
            var id = PerAppEndpointId.FromPersisted(text);
            return new(PerAppRoutingStatus.Success, 0, id.Length == 0 ? "Uses the Windows default." : "Uses a chosen output.", id.Length == 0 ? null : id);
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException or MarshalDirectiveException or SEHException)
        {
            return new(PerAppRoutingStatus.Failed, exception.HResult, $"Per-app output read failed (0x{exception.HResult:X8}): {exception.Message}");
        }
        finally { if (hstring != IntPtr.Zero) _ = WindowsDeleteString(hstring); }
    }

    private bool EnsureProbed()
    {
        if (_disposed) return false;
        if (_probed) return _factory != IntPtr.Zero;
        _probed = true;
        IntPtr classId = IntPtr.Zero;
        try
        {
            const string className = "Windows.Media.Internal.AudioPolicyConfig";
            var created = WindowsCreateString(className, (uint)className.Length, out classId);
            if (created < 0) { _probeDetail = Strings.WinPerApp_Support_NotAvailable; return false; }

            var lastHr = 0;
            foreach (var known in KnownInterfaceIds)
            {
                var iid = known;
                lastHr = RoGetActivationFactory(classId, ref iid, out var factory);
                if (lastHr >= 0 && factory != IntPtr.Zero) { _factory = factory; break; }
            }
            if (_factory == IntPtr.Zero)
            {
                _probeDetail = Strings.WinPerApp_Support_WindowsVersionUnsupported;
                return false;
            }

            var vtable = Marshal.ReadIntPtr(_factory);
            _set = Marshal.GetDelegateForFunctionPointer<SetFn>(Marshal.ReadIntPtr(vtable, SetSlot * IntPtr.Size));
            _get = Marshal.GetDelegateForFunctionPointer<GetFn>(Marshal.ReadIntPtr(vtable, GetSlot * IntPtr.Size));

            // Layout safety check: one read-only call for this very process must succeed.
            var probe = ReadLocked(Environment.ProcessId);
            if (!probe.Succeeded)
            {
                _probeDetail = Strings.WinPerApp_Support_SafetyCheckFailed;
                ReleaseFactory();
                return false;
            }
            _probeDetail = Strings.WinPerApp_Support_Available;
            return true;
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException or MarshalDirectiveException or SEHException or DllNotFoundException or EntryPointNotFoundException)
        {
            _probeDetail = Strings.WinPerApp_Support_NotAvailable;
            ReleaseFactory();
            return false;
        }
        finally { if (classId != IntPtr.Zero) _ = WindowsDeleteString(classId); }
    }

    private void ReleaseFactory()
    {
        var factory = _factory;
        _factory = IntPtr.Zero;
        _set = null;
        _get = null;
        if (factory == IntPtr.Zero) return;
        try
        {
            var vtable = Marshal.ReadIntPtr(factory);
            Marshal.GetDelegateForFunctionPointer<ReleaseFn>(Marshal.ReadIntPtr(vtable, ReleaseSlot * IntPtr.Size))(factory);
        }
        catch (Exception exception) when (exception is AccessViolationException or SEHException or MarshalDirectiveException) { /* leak rather than crash */ }
    }

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

    [DllImport("combase.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string source, uint length, out IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern IntPtr WindowsGetStringRawBuffer(IntPtr hstring, out uint length);
}
