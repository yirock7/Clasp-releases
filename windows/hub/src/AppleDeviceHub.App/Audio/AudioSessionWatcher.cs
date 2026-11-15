using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace AppleDeviceHub.App.Audio;

/// <summary>
/// Reports process ids as they open audio sessions, using the documented
/// IAudioSessionManager2.RegisterSessionNotification (no process polling). Enumeration,
/// registration, unregistration and COM releases run on an initialized MTA thread. It watches every
/// active render endpoint, not only the default: an app already routed to another output opens its
/// session there. Call <see cref="Refresh"/> when endpoints change. Existing sessions are reported
/// once at start and at each refresh. <see cref="SessionStarted"/> may arrive on any thread.
/// </summary>
public sealed partial class AudioSessionWatcher : IDisposable
{
    private const int Render = 0;
    private const uint ActiveState = 1;
    private static readonly Guid SessionManager2Id = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
    private static readonly Guid MMDeviceEnumeratorId = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid MMDeviceEnumeratorIid = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private readonly object _gate = new();
    private readonly List<Watch> _watches = [];
    private bool _disposed;

    /// <summary>Raised with the owning process id of a new or already-existing audio session.</summary>
    public event Action<int>? SessionStarted;

    /// <summary>Starts (or restarts) watching all active render endpoints. Returns false if Windows refuses.</summary>
    public bool Refresh()
    {
        try { return MtaThread.Run(RefreshOnMta); }
        catch (Exception exception) when (exception is COMException or InvalidCastException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private bool RefreshOnMta()
    {
        lock (_gate)
        {
            if (_disposed) return false;
            StopWatching();
            IMMDeviceEnumerator? enumerator = null;
            IMMDeviceCollection? collection = null;
            try
            {
                Marshal.ThrowExceptionForHR(CoCreateInstance(MMDeviceEnumeratorId, IntPtr.Zero, 23, MMDeviceEnumeratorIid, out enumerator)); // CLSCTX_ALL
                if (enumerator.EnumAudioEndpoints(Render, ActiveState, out collection) < 0) return false;
                if (collection.GetCount(out var count) < 0) return false;
                for (uint index = 0; index < count; index++)
                {
                    IMMDevice? device = null;
                    try
                    {
                        if (collection.Item(index, out device) < 0) continue;
                        var watch = TryWatch(device);
                        if (watch is not null) _watches.Add(watch);
                    }
                    finally { Release(device); }
                }
                return true;
            }
            catch (Exception exception) when (exception is COMException or InvalidCastException)
            {
                return false;
            }
            finally
            {
                Release(collection);
                Release(enumerator);
            }
        }
    }

    public void Dispose()
    {
        try
        {
            MtaThread.Run(() =>
            {
                lock (_gate)
                {
                    if (_disposed) return true;
                    _disposed = true;
                    StopWatching();
                    return true;
                }
            });
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException or PlatformNotSupportedException)
        {
            // If COM could not initialize, do not try to release apartment-bound interfaces on the caller.
            lock (_gate)
            {
                _disposed = true;
                _watches.Clear();
            }
        }
        finally { SessionStarted = null; }
    }

    private Watch? TryWatch(IMMDevice device)
    {
        var iid = SessionManager2Id;
        if (device.Activate(ref iid, 23, IntPtr.Zero, out var activated) < 0) return null; // CLSCTX_ALL
        var manager = ComInterop.Wrap<IAudioSessionManager2>(activated);
        var callback = new Callback(this);
        try
        {
            if (manager.RegisterSessionNotification(callback) < 0) { Release(manager); return null; }
            ReportExisting(manager);
            return new(manager, callback);
        }
        catch
        {
            Release(manager);
            throw;
        }
    }

    private void ReportExisting(IAudioSessionManager2 manager)
    {
        IAudioSessionEnumerator? sessions = null;
        try
        {
            if (manager.GetSessionEnumerator(out sessions) < 0 || sessions.GetCount(out var count) < 0) return;
            for (var index = 0; index < count; index++)
            {
                IAudioSessionControl2? control = null;
                try
                {
                    if (sessions.GetSession(index, out control) < 0) continue;
                    Report(control);
                }
                finally { Release(control); }
            }
        }
        finally { Release(sessions); }
    }

    private void Report(IAudioSessionControl2 control)
    {
        // Process id 0 is the shared system-sounds session; there is nothing to route.
        if (control.GetProcessId(out var processId) < 0 || processId == 0) return;
        try { SessionStarted?.Invoke((int)processId); }
        catch { /* A subscriber cannot throw back into the audio engine. */ }
    }

    private void StopWatching()
    {
        foreach (var watch in _watches)
        {
            try { _ = watch.Manager.UnregisterSessionNotification(watch.Callback); }
            catch (Exception exception) when (exception is COMException or InvalidCastException) { /* device gone */ }
            Release(watch.Manager);
        }
        _watches.Clear();
    }

    private static void Release(object? value) => ComInterop.Release(value);

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid clsid, IntPtr outer, uint context, in Guid iid, [MarshalUsing(typeof(UniqueComInterfaceMarshaller<IMMDeviceEnumerator>))] out IMMDeviceEnumerator instance);

    private sealed record Watch(IAudioSessionManager2 Manager, Callback Callback);

    [GeneratedComClass]
    internal sealed partial class Callback(AudioSessionWatcher owner) : IAudioSessionNotification
    {
        public int OnSessionCreated(IAudioSessionControl2 newSession)
        {
            if (newSession is not null)
            {
                try { MtaThread.Run(() => { owner.Report(newSession); return true; }); }
                catch (Exception exception) when (exception is COMException or InvalidCastException or PlatformNotSupportedException)
                {
                    // The notification can be skipped if Windows cannot initialize the MTA worker.
                }
                finally { Release(newSession); }
            }
            return 0;
        }
    }

    [GeneratedComInterface, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    internal partial interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, uint stateMask, [MarshalUsing(typeof(UniqueComInterfaceMarshaller<IMMDeviceCollection>))] out IMMDeviceCollection devices);
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
    }

    [GeneratedComInterface, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
    internal partial interface IAudioSessionManager2
    {
        // IAudioSessionManager
        [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, uint streamFlags, out IntPtr control);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, uint streamFlags, out IntPtr volume);
        // IAudioSessionManager2
        [PreserveSig] int GetSessionEnumerator([MarshalUsing(typeof(UniqueComInterfaceMarshaller<IAudioSessionEnumerator>))] out IAudioSessionEnumerator enumerator);
        [PreserveSig] int RegisterSessionNotification(IAudioSessionNotification notification);
        [PreserveSig] int UnregisterSessionNotification(IAudioSessionNotification notification);
    }

    [GeneratedComInterface, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
    internal partial interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, [MarshalUsing(typeof(UniqueComInterfaceMarshaller<IAudioSessionControl2>))] out IAudioSessionControl2 session);
    }

    [GeneratedComInterface, Guid("641DD20B-4D41-49CC-ABA3-174B9477BB08")]
    internal partial interface IAudioSessionNotification
    {
        [PreserveSig] int OnSessionCreated([MarshalUsing(typeof(UniqueComInterfaceMarshaller<IAudioSessionControl2>))] IAudioSessionControl2 newSession);
    }

    // IAudioSessionControl members come first, then the IAudioSessionControl2 additions. Only
    // GetProcessId is ever called; the rest keep the native vtable order.
    [GeneratedComInterface, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
    internal partial interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);
        [PreserveSig] int GetGroupingParam(out Guid groupingId);
        [PreserveSig] int SetGroupingParam(ref Guid groupingId, ref Guid eventContext);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr events);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr events);
        [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetProcessId(out uint processId);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
    }
}
