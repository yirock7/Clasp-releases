using System.Diagnostics;
using System.Runtime.InteropServices;
using AppleDeviceHub.Core.Audio;
using Microsoft.Win32;

namespace AppleDeviceHub.App.Audio;

/// <summary>Event data: short identifiers of apps currently using a microphone (memory only).</summary>
public sealed class MicrophoneUsageChangedEventArgs(IReadOnlySet<string> appsInUse) : EventArgs
{
    /// <summary>Package family names, or executable file names only (never full paths).</summary>
    public IReadOnlySet<string> AppsInUse { get; } = appsInUse;
}

/// <summary>
/// Detects "some app is using a microphone" from the Windows privacy consent store
/// (HKCU\...\CapabilityAccessManager\ConsentStore\microphone). An app is in use when
/// LastUsedTimeStart is non-zero and LastUsedTimeStop is zero. Uses RegNotifyChangeKeyValue
/// on the whole subtree with a slow safety re-read; there is no fast polling loop.
/// Clasp's own process is excluded. Identifiers are kept in memory and never logged.
/// </summary>
/// <remarks>
/// Changed is raised on a background thread, only when the set of apps actually changes.
/// An app that crashed mid-capture can leave a stale in-use record; that is Windows' data and
/// is reported as is.
/// </remarks>
public sealed class MicrophoneUsageWatcher : IDisposable
{
    private const string ConsentPath =
        @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";
    private const string NonPackagedName = "NonPackaged";
    private static readonly TimeSpan SafetyReread = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(250);

    private readonly string _ownExecutable;
    private readonly ManualResetEvent _stop = new(false);
    private readonly object _gate = new();
    private MicrophoneUsageSnapshot _snapshot = MicrophoneUsageSnapshot.Unknown;
    private Thread? _thread;
    private bool _disposed;

    public MicrophoneUsageWatcher()
    {
        _ownExecutable = Path.GetFileName(Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "");
    }

    /// <summary>Raised on a background thread when the set of in-use apps changes.</summary>
    public event EventHandler<MicrophoneUsageChangedEventArgs>? Changed;

    /// <summary>Raised when microphone-use reads become available or unknown.</summary>
    public event EventHandler? ReadinessChanged;

    /// <summary>True when the most recent microphone-use read completed successfully.</summary>
    public bool IsReady { get { lock (_gate) return _snapshot.IsReady; } }

    /// <summary>One consistent, immutable result from the most recent microphone-use read.</summary>
    public MicrophoneUsageSnapshot CurrentSnapshot { get { lock (_gate) return _snapshot; } }

    /// <summary>The set of apps from the most recent successful microphone-use read.</summary>
    public IReadOnlySet<string> AppsInUse
    {
        get { lock (_gate) return _snapshot.AppsInUse; }
    }

    /// <summary>True when a successful read found another app using a microphone.</summary>
    public bool IsMicrophoneInUse { get { lock (_gate) return _snapshot.IsMicrophoneInUse; } }

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_thread is not null) return;
            _thread = new Thread(Run) { IsBackground = true, Name = "Clasp mic watcher" };
            _thread.Start();
        }
    }

    public void Dispose()
    {
        Thread? thread;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            thread = _thread;
        }
        _stop.Set();
        thread?.Join(TimeSpan.FromSeconds(2));
        _stop.Dispose();
    }

    private void Run()
    {
        using var changed = new AutoResetEvent(false);
        var handles = new WaitHandle[] { _stop, changed };
        RegistryKey? key = null;
        var armed = false;
        try
        {
            while (true)
            {
                key ??= Registry.CurrentUser.OpenSubKey(ConsentPath, writable: false);
                if (key is not null && !armed) armed = Arm(key, changed);
                if (!armed) { key?.Dispose(); key = null; }

                ApplyRead(ReadInUse());

                var index = WaitHandle.WaitAny(handles, SafetyReread);
                if (index == 0) return;
                if (index == 1)
                {
                    armed = false; // a notification is one-shot and must be re-armed
                    if (_stop.WaitOne(Debounce)) return; // collapse bursts of writes
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A failed first read is unknown, not proof that the microphone is idle.
            ApplyRead(null);
        }
        finally { key?.Dispose(); }
    }

    private static bool Arm(RegistryKey key, WaitHandle signal)
    {
        const uint notifyName = 0x1, notifyLastSet = 0x4, threadAgnostic = 0x10000000;
        var rc = RegNotifyChangeKeyValue(
            key.Handle, true, notifyName | notifyLastSet | threadAgnostic, signal.SafeWaitHandle, true);
        return rc == 0;
    }

    private void ApplyRead(HashSet<string>? latest)
    {
        EventHandler<MicrophoneUsageChangedEventArgs>? changedHandler = null;
        EventHandler? readinessHandler = null;
        IReadOnlySet<string> appsInUse;
        lock (_gate)
        {
            var previous = _snapshot;
            var current = previous.AfterRead(latest);
            if (!previous.AppsInUse.SetEquals(current.AppsInUse)) changedHandler = Changed;
            if (previous.IsReady != current.IsReady) readinessHandler = ReadinessChanged;
            _snapshot = current;
            appsInUse = current.AppsInUse;
        }
        if (changedHandler is not null && latest is not null)
            changedHandler.Invoke(this, new MicrophoneUsageChangedEventArgs(appsInUse));
        readinessHandler?.Invoke(this, EventArgs.Empty);
    }

    private HashSet<string>? ReadInUse()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(ConsentPath, writable: false);
            if (root is null) return result;
            foreach (var name in root.GetSubKeyNames())
            {
                if (string.Equals(name, NonPackagedName, StringComparison.OrdinalIgnoreCase))
                {
                    using var nonPackaged = root.OpenSubKey(name, writable: false);
                    if (nonPackaged is null) continue;
                    foreach (var exe in nonPackaged.GetSubKeyNames())
                    {
                        // Keys encode the path with '#'; keep only the file name for privacy.
                        var fileName = exe[(exe.LastIndexOf('#') + 1)..];
                        if (string.Equals(fileName, _ownExecutable, StringComparison.OrdinalIgnoreCase)) continue;
                        if (IsInUse(nonPackaged, exe)) result.Add(fileName);
                    }
                }
                else if (IsInUse(root, name))
                {
                    result.Add(name);
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            // Unknown is not evidence that the microphone is idle. Keep Smart mode from acting
            // until a later safety re-read succeeds.
            return null;
        }
        return result;
    }

    private static bool IsInUse(RegistryKey parent, string subKey)
    {
        using var app = parent.OpenSubKey(subKey, writable: false);
        if (app is null) return false;
        return app.GetValue("LastUsedTimeStart") is long start && start != 0
            && app.GetValue("LastUsedTimeStop") is long stop && stop == 0;
    }

    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern int RegNotifyChangeKeyValue(
        Microsoft.Win32.SafeHandles.SafeRegistryHandle key,
        [MarshalAs(UnmanagedType.Bool)] bool watchSubtree,
        uint filter,
        Microsoft.Win32.SafeHandles.SafeWaitHandle eventHandle,
        [MarshalAs(UnmanagedType.Bool)] bool asynchronous);
}
