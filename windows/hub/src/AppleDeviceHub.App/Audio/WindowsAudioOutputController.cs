using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Diagnostics;
using AppleDeviceHub.Core.Audio;

namespace AppleDeviceHub.App.Audio;

/// <summary>
/// Enumerates Windows render endpoints without Bluetooth/AAP or microphone access.
/// Construct, Read, TryRestore and Dispose on the owning UI thread. Changed is posted
/// to a pool thread: subscribers must dispatch it before accessing the controller.
/// </summary>
/// <remarks>
/// Reading and notifications use the documented MMDevice API. Windows exposes no
/// supported desktop setter here; restoration uses the undocumented IPolicyConfig
/// ABI (CLSID 870af99c, IID f8679f50), which may change or be unavailable. Failures
/// are returned, never silently treated as successful. This is a role-specific
/// default-output preference, not a per-application routing or stream migration API.
/// Explicit output selection uses the same setter for all three render roles; volume
/// reads/writes use the documented IAudioEndpointVolume endpoint interface.
/// Guards are best effort: Windows has no compare-and-set default-endpoint API, so
/// a concurrent user/OS change in the final native-call interval cannot be atomic.
/// Native restore behavior still requires an explicitly authorized hardware check.
/// </remarks>
public sealed partial class WindowsAudioOutputController : IAudioOutputController
{
    private const uint AllDeviceStates = 0xF;
    private const uint Active = 1;
    private const int Render = 0;
    private const int NotFound = unchecked((int)0x80070490);
    private const int ErrorNoSuchDeviceInstance = unchecked((int)0xE000020B); // ERROR_NO_SUCH_DEVINST
    private const int SpapiNoSuchDeviceInstance = unchecked((int)0x800F020B); // SPAPI_E_NO_SUCH_DEVINST
    private const string EndpointVolumeInterfaceId = "5CDF2C82-841E-4546-9722-0CF74078229A";
    private static readonly Guid ContainerPropertySet = new("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C");
    private static readonly Guid SystemContainerId = new("00000000-0000-0000-ffff-ffffffffffff");
    private readonly int _ownerThread = Environment.CurrentManagedThreadId;
    private readonly IMMDeviceEnumerator _enumerator;
    private readonly Notifications _notifications;
    private readonly AudioEndpointChangeTracker _changeTracker = new(Enum.GetValues<AudioOutputRole>().Length);
    private readonly AudioNotificationSettleWindow _settleWindow = new();
    private readonly object _settleGate = new();
    private readonly object _subscriberGate = new();
    private readonly Timer _settleTimer;
    private int _disposed;
    private int _subscriberWorkQueued;

    public event EventHandler? Changed;

    public WindowsAudioOutputController()
    {
        Check(CoCreateInstance(MMDeviceEnumeratorId, IntPtr.Zero, ClsctxAll, MMDeviceEnumeratorIid, out _enumerator));
        _notifications = new Notifications(this);
        _settleTimer = new(OnSettleTimer, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        try { Check(_enumerator.RegisterEndpointNotificationCallback(_notifications)); }
        catch
        {
            _settleTimer.Dispose();
            Release(_enumerator);
            throw;
        }
    }

    public AudioOutputSnapshot Read()
    {
        CheckAccess();
        IMMDeviceCollection? collection = null;
        try
        {
            Check(_enumerator.EnumAudioEndpoints(Render, AllDeviceStates, out collection));
            Check(collection.GetCount(out var count));
            var endpoints = new List<AudioOutputEndpoint>();
            for (uint index = 0; index < count; index++)
            {
                IMMDevice? device = null;
                try
                {
                    // An endpoint disappearing during enumeration fails the whole
                    // snapshot. A partial list must not be mistaken for disconnection.
                    Check(collection.Item(index, out device));
                    Check(device.GetId(out var id));
                    Check(device.GetState(out var state));
                    endpoints.Add(ReadEndpoint(device, id, (state & Active) != 0));
                }
                finally { Release(device); }
            }
            var defaults = new Dictionary<AudioOutputRole, string?>();
            foreach (var role in Enum.GetValues<AudioOutputRole>()) defaults[role] = ReadDefault(role);
            return new(endpoints.AsReadOnly(), defaults);
        }
        finally { Release(collection); }
    }

    public AudioOutputWriteResult TryRestore(AudioOutputRestoreRequest request)
    {
        CheckAccess();
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(request.Role) || string.IsNullOrWhiteSpace(request.RestoreEndpointId)
            || request.TargetEndpointIds is null || request.TargetEndpointIds.Count == 0
            || request.TargetEndpointIds.Any(string.IsNullOrWhiteSpace))
            return new(false, "Couldn't switch back. Nothing was changed.");

        IPolicyConfig? policy = null;
        try
        {
            if (request.IsCurrent is not null && !request.IsCurrent())
                return new(false, "That request expired. Nothing was changed.");
            // Activate before the guards: activation can be slow, and must not
            // lengthen the interval between validating state and the write.
            Check(CoCreateInstance(PolicyConfigClientId, IntPtr.Zero, ClsctxAll, PolicyConfigIid, out policy));
            var changes = _changeTracker.Capture((int)request.Role);
            var fresh = Read();
            if (!fresh.Defaults.TryGetValue(request.Role, out var current)
                || !string.Equals(current, request.ExpectedCurrentEndpointId, StringComparison.Ordinal))
                return new(false, "The output changed, so your newer choice was kept.");

            var targetIds = new HashSet<string>(request.TargetEndpointIds, StringComparer.OrdinalIgnoreCase);
            var targetContainer = NormalizeContainerId(request.TargetContainerId);
            bool IsTarget(AudioOutputEndpoint endpoint) => targetIds.Contains(endpoint.Id)
                || (targetContainer is not null && endpoint.ContainerId is not null
                    && string.Equals(endpoint.ContainerId, targetContainer, StringComparison.OrdinalIgnoreCase));
            if (fresh.Endpoints.Any(endpoint => endpoint.IsAvailable && IsTarget(endpoint)))
                return new(false, "Your AirPods are still connected. Nothing was changed.");
            var destination = fresh.Endpoints.FirstOrDefault(endpoint =>
                string.Equals(endpoint.Id, request.RestoreEndpointId, StringComparison.Ordinal));
            if (destination is null || !destination.IsAvailable || IsTarget(destination))
                return new(false, "The previous output isn't available.");
            if (string.Equals(current, destination.Id, StringComparison.Ordinal))
                return new(false, "Sound is already going to the previous output.");

            // Read a second complete snapshot immediately before writing. The endpoint-change
            // tracker catches state/property notifications for only this destination and the
            // known selected-device profiles, while additions/removals/container changes and
            // this role's default remain broad invalidators. A different role's default and an
            // unrelated microphone/property notification do not cancel this restoration.
            var relevantEndpointIds = new HashSet<string>(targetIds, StringComparer.OrdinalIgnoreCase)
            {
                request.RestoreEndpointId,
            };
            foreach (var endpoint in fresh.Endpoints.Where(IsTarget)) relevantEndpointIds.Add(endpoint.Id);
            var final = Read();
            if (!final.Defaults.TryGetValue(request.Role, out var finalCurrent)
                || !string.Equals(finalCurrent, current, StringComparison.Ordinal))
                return new(false, "The output changed, so your newer choice was kept.");
            if (final.Endpoints.Any(endpoint => endpoint.IsAvailable && IsTarget(endpoint)))
                return new(false, "Your AirPods are still connected. Nothing was changed.");
            var finalDestination = final.Endpoints.FirstOrDefault(endpoint =>
                string.Equals(endpoint.Id, request.RestoreEndpointId, StringComparison.Ordinal));
            if (finalDestination is null || !finalDestination.IsAvailable || IsTarget(finalDestination))
                return new(false, "The previous output isn't available. Nothing was changed.");

            if (!string.Equals(ReadDefault(request.Role), finalCurrent, StringComparison.Ordinal)
                || !_changeTracker.IsUnchangedSince(changes, relevantEndpointIds))
                return new(false, "Your audio devices changed. Nothing was changed.");
            if (request.IsCurrent is not null && !request.IsCurrent())
                return new(false, "That request expired. Nothing was changed.");
            Check(policy.SetDefaultEndpoint(finalDestination.Id, (int)request.Role));
            if (!string.Equals(ReadDefault(request.Role), finalDestination.Id, StringComparison.Ordinal))
                return new(false, "Windows didn't confirm the switch.");
            return new(true, $"Switched back to {finalDestination.Name}.");
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException)
        {
            return new(false, $"Windows audio restoration failed (0x{exception.HResult:X8}).");
        }
        finally { Release(policy); }
    }

    /// <summary>Explicit user action: make this active endpoint the default for all Windows output roles.</summary>
    public AudioOutputWriteResult TrySetDefaultOutput(string endpointId)
    {
        CheckAccess();
        if (string.IsNullOrWhiteSpace(endpointId))
            return new(false, "Choose an output first.");

        IPolicyConfig? policy = null;
        try
        {
            var endpoint = Read().Endpoints.FirstOrDefault(item =>
                string.Equals(item.Id, endpointId, StringComparison.OrdinalIgnoreCase));
            if (endpoint is null || !endpoint.IsAvailable)
                return new(false, "That output is disconnected, so nothing was changed.");

            Check(CoCreateInstance(PolicyConfigClientId, IntPtr.Zero, ClsctxAll, PolicyConfigIid, out policy));
            foreach (var role in Enum.GetValues<AudioOutputRole>())
            {
                var fresh = Read();
                endpoint = fresh.Endpoints.FirstOrDefault(item =>
                    string.Equals(item.Id, endpointId, StringComparison.OrdinalIgnoreCase));
                if (endpoint is null || !endpoint.IsAvailable)
                    return new(false, "The output disconnected partway. Some settings may already have changed.");
                if (string.Equals(fresh.Defaults.GetValueOrDefault(role), endpoint.Id, StringComparison.Ordinal)) continue;

                Check(policy.SetDefaultEndpoint(endpoint.Id, (int)role));
                if (!string.Equals(ReadDefault(role), endpoint.Id, StringComparison.Ordinal))
                    return new(false, "Windows didn't confirm the change. Some settings may already have changed.");
            }
            return new(true, $"{endpoint.Name} is now your sound output.");
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException)
        {
            AppleDeviceHub.App.Shell.LocalLog.Write($"Windows could not change default output (0x{exception.HResult:X8}, {AppleDeviceHub.Core.Presentation.LogText.OpaqueRef(endpointId)}).");
            return new(false, "Couldn't switch the sound output.");
        }
        finally { Release(policy); }
    }

    /// <summary>Reads the selected endpoint's master volume. Returns null when it is unavailable.</summary>
    public float? ReadVolume(string endpointId)
    {
        CheckAccess();
        if (string.IsNullOrWhiteSpace(endpointId)) return null;

        IMMDevice? device = null;
        IAudioEndpointVolume? volume = null;
        try
        {
            var endpoint = ReadEndpointById(endpointId);
            if (endpoint is not { IsAvailable: true }) return null;
            volume = ActivateVolume(endpoint.Id, out device);
            Check(volume.GetMasterVolumeLevelScalar(out var scalar));
            return Math.Clamp(scalar, 0f, 1f);
        }
        catch (COMException exception) when (IsMissingDeviceInstanceFailure(exception.HResult) || exception.HResult == NotFound)
        {
            return null;
        }
        finally
        {
            Release(volume);
            Release(device);
        }
    }

    /// <summary>Explicit user action: set the active endpoint's master volume from 0 to 1.</summary>
    public AudioOutputWriteResult TrySetVolume(string endpointId, float scalar)
    {
        CheckAccess();
        if (string.IsNullOrWhiteSpace(endpointId) || !float.IsFinite(scalar) || scalar < 0f || scalar > 1f)
            return new(false, "Choose a volume from 0% to 100%.");

        IMMDevice? device = null;
        IAudioEndpointVolume? volume = null;
        try
        {
            var endpoint = ReadEndpointById(endpointId);
            if (endpoint is not { IsAvailable: true })
                return new(false, "That output is disconnected, so its volume wasn't changed.");
            volume = ActivateVolume(endpoint.Id, out device);
            var eventContext = Guid.Empty;
            Check(volume.SetMasterVolumeLevelScalar(scalar, ref eventContext));
            Check(volume.GetMasterVolumeLevelScalar(out var readback));
            if (Math.Abs(readback - scalar) > 0.01f)
                return new(false, $"Volume changed, but Windows reports about {Math.Round(readback * 100)}%.");
            return new(true, $"{endpoint.Name} volume is now {Math.Round(readback * 100)}%.");
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException)
        {
            AppleDeviceHub.App.Shell.LocalLog.Write($"Windows could not change output volume (0x{exception.HResult:X8}, {AppleDeviceHub.Core.Presentation.LogText.OpaqueRef(endpointId)}).");
            return new(false, "Couldn't change the volume.");
        }
        finally
        {
            Release(volume);
            Release(device);
        }
    }

    private IAudioEndpointVolume ActivateVolume(string endpointId, out IMMDevice device)
    {
        Check(_enumerator.GetDevice(endpointId, out device));
        try
        {
            var iid = new Guid(EndpointVolumeInterfaceId);
            Check(device.Activate(ref iid, 23, IntPtr.Zero, out var activated)); // CLSCTX_ALL
            return ComInterop.Wrap<IAudioEndpointVolume>(activated);
        }
        catch
        {
            Release(device);
            device = null!;
            throw;
        }
    }

    private AudioOutputEndpoint? ReadEndpointById(string endpointId)
    {
        IMMDevice? device = null;
        try
        {
            var result = _enumerator.GetDevice(endpointId, out device);
            if (result == NotFound || IsMissingDeviceInstanceFailure(result)) return null;
            Check(result);
            Check(device.GetId(out var id));
            Check(device.GetState(out var state));
            return ReadEndpoint(device, id, (state & Active) != 0);
        }
        catch (COMException exception) when (exception.HResult == NotFound || IsMissingDeviceInstanceFailure(exception.HResult))
        {
            return null;
        }
        finally { Release(device); }
    }

    private static AudioOutputEndpoint ReadEndpoint(IMMDevice device, string id, bool available)
    {
        IPropertyStore? store = null;
        try
        {
            Check(device.OpenPropertyStore(0, out store)); // STGM_READ
            var name = ReadString(store, new(new("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14));
            var container = ReadContainer(store);
            return new(id, string.IsNullOrWhiteSpace(name) ? "Unnamed output" : name, available, container);
        }
        catch (COMException exception) when (!available && IsMissingDeviceInstanceFailure(exception.HResult))
        {
            // A phantom inactive endpoint can remain in MMDevice enumeration after
            // its device instance has disappeared. Preserve its exact ID and state;
            // only the unavailable metadata falls back to a generic name/no group.
            return new(id, "Unnamed output", false, null);
        }
        finally { Release(store); }
    }

    private static bool IsMissingDeviceInstanceFailure(int hresult) =>
        hresult == ErrorNoSuchDeviceInstance || hresult == SpapiNoSuchDeviceInstance;

    private static string? ReadString(IPropertyStore store, PropertyKey key)
    {
        PropVariant value = default;
        try
        {
            Check(store.GetValue(ref key, out value));
            return value.Type == 31 && value.Pointer != IntPtr.Zero ? Marshal.PtrToStringUni(value.Pointer) : null;
        }
        finally { Check(PropVariantClear(ref value)); }
    }

    private static string? ReadContainer(IPropertyStore store)
    {
        var key = new PropertyKey(ContainerPropertySet, 2);
        PropVariant value = default;
        try
        {
            Check(store.GetValue(ref key, out value));
            // Missing properties normally return VT_EMPTY. Never invent a group
            // from the endpoint name or parse the opaque endpoint identifier.
            Guid container;
            if (value.Type == 72 && value.Pointer != IntPtr.Zero) // VT_CLSID
                container = Marshal.PtrToStructure<Guid>(value.Pointer);
            else if (value.Type == 31 && value.Pointer != IntPtr.Zero
                && Guid.TryParse(Marshal.PtrToStringUni(value.Pointer), out var parsed)) container = parsed;
            else return null;
            return IsUsableContainer(container) ? container.ToString("D") : null;
        }
        finally { Check(PropVariantClear(ref value)); }
    }

    private string? ReadDefault(AudioOutputRole role)
    {
        IMMDevice? device = null;
        try
        {
            var result = _enumerator.GetDefaultAudioEndpoint(Render, (int)role, out device);
            if (result == NotFound) return null;
            Check(result);
            Check(device.GetId(out var id));
            return id;
        }
        finally { Release(device); }
    }

    private static bool IsUsableContainer(Guid container) => container != Guid.Empty && container != SystemContainerId;

    private static string? NormalizeContainerId(string? containerId) =>
        Guid.TryParse(containerId, out var container) && IsUsableContainer(container) ? container.ToString("D") : null;

    private static bool IsContainerProperty(PropertyKey key) => key.PropertyId == 2 && key.FormatId == ContainerPropertySet;

    private void NotifyEndpointChanged(string endpointId, bool changesTopology = false)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        _changeTracker.RecordEndpointChange(endpointId, changesTopology);
        ScheduleSettledChange();
    }

    private void NotifyDefaultChanged(int role)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        _changeTracker.RecordDefaultChange(role);
        ScheduleSettledChange();
    }

    private void ScheduleSettledChange()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var now = Stopwatch.GetTimestamp();
        lock (_settleGate)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            if (!_settleWindow.Record(now)) return;
            var delay = _settleWindow.GetRemainingDelay(now, Stopwatch.Frequency) ?? Timeout.InfiniteTimeSpan;
            _settleTimer.Change(delay, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnSettleTimer(object? state)
    {
        lock (_settleGate)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            var now = Stopwatch.GetTimestamp();
            if (!_settleWindow.TryComplete(now, Stopwatch.Frequency, out var delay))
            {
                if (delay != Timeout.InfiniteTimeSpan)
                    _settleTimer.Change(delay, Timeout.InfiniteTimeSpan);
                return;
            }
        }

        QueueSettledChange();
    }

    private void QueueSettledChange()
    {
        if (Interlocked.Exchange(ref _subscriberWorkQueued, 1) != 0) return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            Interlocked.Exchange(ref _subscriberWorkQueued, 0);
            lock (_subscriberGate)
            {
                if (Volatile.Read(ref _disposed) != 0) return;
                var subscribers = Changed;
                if (subscribers is null) return;
                foreach (EventHandler subscriber in subscribers.GetInvocationList())
                {
                    if (Volatile.Read(ref _disposed) != 0) return;
                    try { subscriber(this, EventArgs.Empty); }
                    catch { /* A consumer cannot throw back across a native notification. */ }
                }
            }
        });
    }

    public void Dispose()
    {
        if (Environment.CurrentManagedThreadId != _ownerThread)
            throw new InvalidOperationException("Dispose audio outputs on the owning UI thread.");
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Changed = null;
        lock (_settleGate)
        {
            _settleWindow.Dispose();
            _settleTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _settleTimer.Dispose();
        }
        try { Check(_enumerator.UnregisterEndpointNotificationCallback(_notifications)); }
        finally { Release(_enumerator); }
        // Wait for a subscriber already running on the pool. Once Dispose returns, no
        // queued or in-flight Changed handler can run against this controller.
        lock (_subscriberGate) { }
        GC.KeepAlive(_notifications);
    }

    private void CheckAccess()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Environment.CurrentManagedThreadId != _ownerThread)
            throw new InvalidOperationException("Read and restore audio outputs on the owning UI thread.");
    }

    private static void Check(int result) { if (result < 0) Marshal.ThrowExceptionForHR(result); }
    private static void Release(object? value) => ComInterop.Release(value);

    private const uint ClsctxAll = 23;
    private static readonly Guid MMDeviceEnumeratorId = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid MMDeviceEnumeratorIid = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid PolicyConfigClientId = new("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9");
    private static readonly Guid PolicyConfigIid = new("F8679F50-850A-41CF-9C72-430F290290C8");

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid clsid, IntPtr outer, uint context, in Guid iid, [MarshalUsing(typeof(UniqueComInterfaceMarshaller<IMMDeviceEnumerator>))] out IMMDeviceEnumerator instance);

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid clsid, IntPtr outer, uint context, in Guid iid, [MarshalUsing(typeof(UniqueComInterfaceMarshaller<IPolicyConfig>))] out IPolicyConfig instance);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int PropVariantClear(ref PropVariant value);

    [StructLayout(LayoutKind.Sequential)]
    public struct PropertyKey(Guid formatId, uint propertyId)
    {
        public Guid FormatId = formatId;
        public uint PropertyId = propertyId;
    }

    // The app is x64. PROPVARIANT's union is 16 bytes on x64 (24 total).
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    internal struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public IntPtr Pointer;
    }


    [GeneratedComInterface, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    internal partial interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, uint stateMask, [MarshalUsing(typeof(UniqueComInterfaceMarshaller<IMMDeviceCollection>))] out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, [MarshalUsing(typeof(UniqueComInterfaceMarshaller<IMMDevice>))] out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalUsing(typeof(UniqueComInterfaceMarshaller<IMMDevice>))] out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient callback);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient callback);
    }

    [GeneratedComInterface, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    internal partial interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, [MarshalUsing(typeof(UniqueComInterfaceMarshaller<IMMDevice>))] out IMMDevice device);
    }

    [GeneratedComInterface, Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    internal partial interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint context, IntPtr activation, out IntPtr result);
        [PreserveSig] int OpenPropertyStore(uint access, [MarshalUsing(typeof(UniqueComInterfaceMarshaller<IPropertyStore>))] out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }

    [GeneratedComInterface, Guid(EndpointVolumeInterfaceId)]
    internal partial interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
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
        [PreserveSig] int GetVolumeStepInfo(out uint step, out uint stepCount);
        [PreserveSig] int VolumeStepUp(ref Guid eventContext);
        [PreserveSig] int VolumeStepDown(ref Guid eventContext);
        [PreserveSig] int QueryHardwareSupport(out uint hardwareSupportMask);
        [PreserveSig] int GetVolumeRange(out float minimumDb, out float maximumDb, out float incrementDb);
    }

    [GeneratedComInterface, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    internal partial interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [GeneratedComInterface, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0")]
    public partial interface IMMNotificationClient
    {
        [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string id, uint state);
        [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string? id);
        [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string id, PropertyKey key);
    }

    [GeneratedComClass]
    internal sealed partial class Notifications(WindowsAudioOutputController owner) : IMMNotificationClient
    {
        public int OnDeviceStateChanged(string id, uint state) { owner.NotifyEndpointChanged(id); return 0; }
        public int OnDeviceAdded(string id) { owner.NotifyEndpointChanged(id, changesTopology: true); return 0; }
        public int OnDeviceRemoved(string id) { owner.NotifyEndpointChanged(id, changesTopology: true); return 0; }
        public int OnDefaultDeviceChanged(int flow, int role, string? id) { if (flow == Render) owner.NotifyDefaultChanged(role); return 0; }
        public int OnPropertyValueChanged(string id, PropertyKey key)
        {
            owner.NotifyEndpointChanged(id, changesTopology: IsContainerProperty(key));
            return 0;
        }
    }


    // Complete method order through SetDefaultEndpoint is required for the native
    // vtable. Earlier methods are never called and their data buffers stay opaque.
    [GeneratedComInterface, Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
    internal partial interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string id, out IntPtr format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, int defaultFormat, out IntPtr format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, int defaultPeriod, IntPtr period, IntPtr minimumPeriod);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr period);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string id, int visible);
    }
}
