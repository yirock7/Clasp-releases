using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace AppleDeviceHub.App.Audio;

/// <summary>Lifecycle of <see cref="KeepAliveRenderer"/>.</summary>
public enum KeepAliveRendererState
{
    Stopped,
    Starting,
    Running,
    /// <summary>The stream could not start or the device went away. The renderer is stopped.</summary>
    Failed,
}

/// <summary>
/// Plays digital silence to one Windows render endpoint so the Bluetooth audio link stays awake
/// and new sounds are not clipped. Uses a WASAPI shared-mode, event-driven stream on a private
/// background thread; every buffer is submitted with the SILENT flag, so no samples are written
/// and CPU use is a few wake-ups per second. Shared mode never blocks exclusive-mode apps, and
/// the stream is created hidden from the volume mixer and is not a media session (no SMTC).
/// </summary>
/// <remarks>
/// Start, Stop and Dispose are safe to call from any thread. <see cref="StateChanged"/> is raised
/// on the worker thread (or the caller's thread for Stopped): subscribers must marshal to the UI
/// thread. A removed or invalidated device (AUDCLNT_E_DEVICE_INVALIDATED and any other native
/// failure) stops the stream and reports Failed with a message; it never throws out of the worker.
/// This is believed to match MagicPods' silence-playback approach, which is not confirmed.
/// </remarks>
public sealed partial class KeepAliveRenderer : IDisposable
{
    private const int Shared = 0;
    private const uint EventCallback = 0x00040000;       // AUDCLNT_STREAMFLAGS_EVENTCALLBACK
    private const uint ExpireWhenUnowned = 0x10000000;   // AUDCLNT_SESSIONFLAGS_EXPIREWHENUNOWNED
    private const uint DisplayHide = 0x20000000;         // AUDCLNT_SESSIONFLAGS_DISPLAY_HIDE
    private const uint BufferSilent = 0x2;               // AUDCLNT_BUFFERFLAGS_SILENT
    private const uint ClsctxAll = 23;
    private static readonly Guid AudioClientIid = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    private static readonly Guid RenderClientIid = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
    private static readonly Guid MMDeviceEnumeratorId = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid MMDeviceEnumeratorIid = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private readonly object _gate = new();
    private Worker? _worker;
    private bool _disposed;

    /// <summary>Raised when the state changes; the string is a short plain-language detail (may be empty).</summary>
    public event Action<KeepAliveRendererState, string>? StateChanged;

    public KeepAliveRendererState State { get; private set; } = KeepAliveRendererState.Stopped;

    /// <summary>The endpoint currently targeted, or null when stopped.</summary>
    public string? EndpointId { get; private set; }

    /// <summary>Starts silence on <paramref name="endpointId"/> (restarts if a different endpoint is running).</summary>
    public void Start(string endpointId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId);
        Worker? old;
        Worker worker;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_worker is { IsAlive: true } && string.Equals(EndpointId, endpointId, StringComparison.Ordinal)) return;
            old = _worker;
            worker = new Worker(this, endpointId);
            _worker = worker;
            EndpointId = endpointId;
            State = KeepAliveRendererState.Starting;
        }
        old?.StopAndJoin();
        Raise(KeepAliveRendererState.Starting, "");
        worker.Begin();
    }

    /// <summary>Stops the stream and waits briefly for the worker to release the device.</summary>
    public void Stop()
    {
        Worker? worker;
        lock (_gate)
        {
            worker = _worker;
            _worker = null;
            EndpointId = null;
            if (State == KeepAliveRendererState.Stopped) { worker?.StopAndJoin(); return; }
            State = KeepAliveRendererState.Stopped;
        }
        worker?.StopAndJoin();
        Raise(KeepAliveRendererState.Stopped, "");
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        Stop();
    }

    private void Raise(KeepAliveRendererState state, string detail)
    {
        try { StateChanged?.Invoke(state, detail); }
        catch { /* A subscriber fault must never break the audio thread. */ }
    }

    // Called by the worker. Ignored when the worker was superseded or stopped.
    private void WorkerReported(Worker worker, KeepAliveRendererState state, string detail)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_worker, worker)) return;
            State = state;
            if (state == KeepAliveRendererState.Failed)
            {
                _worker = null;
                EndpointId = null;
            }
        }
        Raise(state, detail);
    }

    private sealed class AudioCallException(int hr) : Exception($"Audio call failed (0x{hr:X8}).")
    {
        public int Hr { get; } = hr;
    }

    // The stop event is read after the thread ends (IsAlive), so it is left to the finalizer.
#pragma warning disable CA1001
    private sealed class Worker(KeepAliveRenderer owner, string endpointId)
    {
        private readonly ManualResetEventSlim _stop = new(false);
        private Thread? _thread;

        public bool IsAlive => _thread is { IsAlive: true } && !_stop.IsSet;

        public void Begin()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "Clasp keep-alive audio" };
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();
        }

        public void StopAndJoin()
        {
            _stop.Set();
            var thread = _thread;
            if (thread is not null && thread != Thread.CurrentThread) thread.Join(TimeSpan.FromSeconds(3));
        }

        private void Run()
        {
            IMMDeviceEnumerator? enumerator = null;
            IMMDevice? device = null;
            IAudioClient? client = null;
            IAudioRenderClient? render = null;
            IntPtr format = IntPtr.Zero;
            var started = false;
            using var bufferReady = new AutoResetEvent(false);
            try
            {
                Check(CoCreateInstance(MMDeviceEnumeratorId, IntPtr.Zero, ClsctxAll, MMDeviceEnumeratorIid, out enumerator));
                Check(enumerator.GetDevice(endpointId, out device));
                var audioIid = AudioClientIid;
                Check(device.Activate(ref audioIid, ClsctxAll, IntPtr.Zero, out var activated));
                client = ComInterop.Wrap<IAudioClient>(activated);
                Check(client.GetMixFormat(out format));
                // Zero duration/period lets Windows pick the shared-mode engine period.
                Check(client.Initialize(Shared, EventCallback | ExpireWhenUnowned | DisplayHide, 0, 0, format, IntPtr.Zero));
                Check(client.SetEventHandle(bufferReady.SafeWaitHandle.DangerousGetHandle()));
                Check(client.GetBufferSize(out var frames));
                var renderIid = RenderClientIid;
                Check(client.GetService(ref renderIid, out var renderObject));
                render = ComInterop.Wrap<IAudioRenderClient>(renderObject);
                SubmitSilence(client, render, frames);
                Check(client.Start());
                started = true;
                owner.WorkerReported(this, KeepAliveRendererState.Running, "");

                var handles = new WaitHandle[] { _stop.WaitHandle, bufferReady };
                while (WaitHandle.WaitAny(handles, TimeSpan.FromSeconds(2)) != 0)
                    SubmitSilence(client, render, frames);
            }
            catch (Exception ex) when (!_stop.IsSet)
            {
                owner.WorkerReported(this, KeepAliveRendererState.Failed, Describe(ex));
            }
            catch
            {
                // Stopped on purpose; teardown errors are irrelevant.
            }
            finally
            {
                if (started) try { client?.Stop(); } catch { }
                if (format != IntPtr.Zero) Marshal.FreeCoTaskMem(format);
                Release(render); Release(client); Release(device); Release(enumerator);
                _stop.Set();
            }
        }

        // Marking every frame SILENT tells the engine to treat the buffer as zeros without
        // the app touching it.
        private static void SubmitSilence(IAudioClient client, IAudioRenderClient render, uint bufferFrames)
        {
            Check(client.GetCurrentPadding(out var padding));
            var available = bufferFrames - padding;
            if (available == 0) return;
            Check(render.GetBuffer(available, out _));
            Check(render.ReleaseBuffer(available, BufferSilent));
        }

        private static string Describe(Exception ex) => ex is AudioCallException { Hr: unchecked((int)0x88890004) }
            ? "The sound output changed."
            : "Low audio latency stopped: " + ex.Message;

        private static void Check(int hr)
        {
            if (hr < 0) throw new AudioCallException(hr);
        }

        private static void Release(object? com)
        {
            try { ComInterop.Release(com); } catch { }
        }
    }

#pragma warning restore CA1001
    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid clsid, IntPtr outer, uint context, in Guid iid, [MarshalUsing(typeof(UniqueComInterfaceMarshaller<IMMDeviceEnumerator>))] out IMMDeviceEnumerator instance);


    [GeneratedComInterface, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    internal partial interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, uint stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, [MarshalUsing(typeof(UniqueComInterfaceMarshaller<IMMDevice>))] out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalUsing(typeof(UniqueComInterfaceMarshaller<IMMDevice>))] out IMMDevice device);
    }

    [GeneratedComInterface, Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    internal partial interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint context, IntPtr activation, out IntPtr result);
    }

    [GeneratedComInterface, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2")]
    internal partial interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr audioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService(ref Guid iid, out IntPtr service);
    }

    [GeneratedComInterface, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2")]
    internal partial interface IAudioRenderClient
    {
        [PreserveSig] int GetBuffer(uint frames, out IntPtr data);
        [PreserveSig] int ReleaseBuffer(uint frames, uint flags);
    }
}
