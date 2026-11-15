using System.Globalization;
using AppleDeviceHub.Core.Ble;
using AppleDeviceHub.Core.Resources;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Radios;
using Windows.Storage.Streams;

namespace AppleDeviceHub.App.Ble;

/// <summary>
/// Passive Bluetooth LE listener for Apple manufacturer data (company 0x004C), built on
/// <see cref="BluetoothLEAdvertisementWatcher"/>. Passive scanning only: no scan requests are
/// sent, and nothing here pairs, connects, opens GATT or writes to any device. The watcher is
/// created and started only from <see cref="StartAsync"/> (the user's Start-observing action);
/// radio-off, missing adapter, policy and access-denied conditions become an
/// <see cref="AdvertisementSourceState.Unavailable"/> status with a reason, never an escaping exception.
/// Also linked into tools/BleObservationProbe, so it must not depend on WinUI.
/// </summary>
public sealed class WinRtAdvertisementSource : IAdvertisementSource
{
    private readonly Lock _gate = new();
    private BluetoothLEAdvertisementWatcher? _watcher;
    private AdvertisementSourceStatus _status = AdvertisementSourceStatus.Stopped;
    private bool _disposed;
    private long _stopEpoch;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly StopSignal _stopSignal = new();
    private readonly Func<Task<string?>> _checkRadio;

    public WinRtAdvertisementSource() : this(CheckRadioAsync) { }

    /// <summary>Test seam: replaces the Windows radio check.</summary>
    internal WinRtAdvertisementSource(Func<Task<string?>> checkRadio) => _checkRadio = checkRadio;

    public AdvertisementSourceStatus Status { get { lock (_gate) return _status; } }

    public event EventHandler<AdvertisementSighting>? Received;

    public event EventHandler<AdvertisementSourceStatus>? StatusChanged;

    /// <summary>
    /// A fault swallowed in the WinRT callback (exception type and HRESULT only), at most one per
    /// <see cref="FaultReportInterval"/> so a bad stream cannot flood a log. Raised on any thread.
    /// </summary>
    public event EventHandler<string>? Diagnostic;

    private static readonly TimeSpan FaultReportInterval = TimeSpan.FromSeconds(30);
    private long _lastFaultReportTicks;
    private int _suppressedFaults;

    public async Task<AdvertisementSourceStatus> StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long epoch;
        lock (_gate)
        {
            if (_disposed) return Closed;
            if (_watcher is not null) return _status;
            // A Stop issued after this point (even while this start waits for the radio check
            // or behind another start) cancels this start: see StopAsync.
            epoch = _stopEpoch;
        }

        // One start at a time, so two overlapping starts can never create two watchers.
        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Task stopSignal;
            lock (_gate)
            {
                if (_disposed) return Closed;
                if (_stopEpoch != epoch) return AdvertisementSourceStatus.Stopped;
                if (_watcher is not null) return _status;
                // Taken under the same lock as the epoch check, so a Stop after it always wakes this wait.
                stopSignal = _stopSignal.Current;
            }

            // A Stop or Dispose ends the radio check at once instead of waiting for Windows to answer.
            // CheckRadioAsync catches everything, so an abandoned check cannot fault unobserved.
            var (completed, problem) = await StopSignal.RunUnlessSignalledAsync(_checkRadio, stopSignal).ConfigureAwait(false);
            if (!completed)
            {
                lock (_gate) return _disposed ? Closed : AdvertisementSourceStatus.Stopped;
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (problem is not null) return SetStatusIfCurrent(epoch, new AdvertisementSourceStatus(AdvertisementSourceState.Unavailable, problem));

            BluetoothLEAdvertisementWatcher? watcher = null;
            try
            {
                watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Passive };
                // Platform-side filter: Apple manufacturer data only (any payload). The parser
                // re-checks company id and message type, so a looser platform filter is harmless.
                watcher.AdvertisementFilter.Advertisement.ManufacturerData.Add(
                    new BluetoothLEManufacturerData(ProximityMessage.AppleCompanyId, new DataWriter().DetachBuffer()));
                watcher.Received += OnReceived;
                watcher.Stopped += OnStopped;
                lock (_gate)
                {
                    if (_disposed) return Closed;
                    if (_stopEpoch != epoch) return AdvertisementSourceStatus.Stopped; // stopped during the radio check
                    _watcher = watcher;
                }
                watcher.Start();
                bool detachedMeanwhile;
                lock (_gate) detachedMeanwhile = !ReferenceEquals(_watcher, watcher);
                if (detachedMeanwhile)
                {
                    // A Stop/Dispose detached this watcher between attach and Start(): its Stop()
                    // ran before Start(), so stop it again rather than leave the radio scanning.
                    StopQuietly(watcher);
                    return AdvertisementSourceStatus.Stopped;
                }
                if (watcher.Status == BluetoothLEAdvertisementWatcherStatus.Aborted)
                {
                    Detach();
                    return SetStatusIfCurrent(epoch, new AdvertisementSourceStatus(AdvertisementSourceState.Unavailable, Strings.WinBle_Listening_WindowsRefused));
                }
                return SetStatusIfCurrent(epoch, new AdvertisementSourceStatus(AdvertisementSourceState.Running));
            }
            catch (UnauthorizedAccessException)
            {
                DetachOrStop(watcher);
                return SetStatusIfCurrent(epoch, new AdvertisementSourceStatus(AdvertisementSourceState.Unavailable,
                    Strings.WinBle_Access_Denied));
            }
            catch (Exception ex)
            {
                DetachOrStop(watcher);
                return SetStatusIfCurrent(epoch, new AdvertisementSourceStatus(AdvertisementSourceState.Unavailable,
                    string.Format(CultureInfo.CurrentCulture, Strings.WinBle_Listening_CouldNotStart, ex.GetType().Name, ex.HResult)));
            }
        }
        finally
        {
            _startGate.Release();
        }
    }

    /// <summary>
    /// Stop listening. Also cancels any start still in progress (it returns Stopped instead of
    /// creating a watcher), so a Stop can never be overtaken by an older Start.
    /// </summary>
    public Task StopAsync()
    {
        Action wake;
        lock (_gate)
        {
            _stopEpoch++;
            wake = _stopSignal.Replace();
        }
        wake();
        bool wasRunning = Detach();
        if (wasRunning) SetStatus(AdvertisementSourceStatus.Stopped);
        return Task.CompletedTask;
    }

    private static AdvertisementSourceStatus Closed =>
        new(AdvertisementSourceState.Unavailable, Strings.WinBle_Listener_Closed);

    /// <summary>Release <paramref name="watcher"/> whether or not it was attached.</summary>
    private void DetachOrStop(BluetoothLEAdvertisementWatcher? watcher)
    {
        bool attached;
        lock (_gate) attached = watcher is not null && ReferenceEquals(_watcher, watcher);
        if (attached) Detach();
        else if (watcher is not null) StopQuietly(watcher);
    }

    private void StopQuietly(BluetoothLEAdvertisementWatcher watcher)
    {
        watcher.Received -= OnReceived;
        watcher.Stopped -= OnStopped;
        try { watcher.Stop(); }
        catch (Exception) { /* never started, already stopped or radio gone */ }
    }

    public void Dispose()
    {
        Action wake;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _stopEpoch++;
            wake = _stopSignal.Replace();
        }
        wake();
        Detach();
        Received = null;
        StatusChanged = null;
        Diagnostic = null;
    }

    /// <summary>Returns a reason when listening cannot work, or null.</summary>
    private static async Task<string?> CheckRadioAsync()
    {
        try
        {
            var adapter = await BluetoothAdapter.GetDefaultAsync();
            if (adapter is null) return Strings.WinBle_Adapter_NotFound;
            if (!adapter.IsLowEnergySupported) return Strings.WinBle_Adapter_NoLowEnergy;
            if (!adapter.IsCentralRoleSupported) return Strings.WinBle_Adapter_NoCentralRole;
            var radio = await adapter.GetRadioAsync();
            if (radio is null) return Strings.WinBle_Radio_CouldNotRead;
            return radio.State switch
            {
                RadioState.On => null,
                RadioState.Off => Strings.WinBle_Radio_Off,
                RadioState.Disabled => Strings.WinBle_Radio_Disabled,
                _ => Strings.WinBle_Radio_StateUnknown,
            };
        }
        catch (UnauthorizedAccessException)
        {
            return Strings.WinBle_Access_Denied;
        }
        catch (Exception ex)
        {
            return string.Format(CultureInfo.CurrentCulture, Strings.WinBle_Adapter_CheckFailed, ex.GetType().Name, ex.HResult);
        }
    }

    private void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        try
        {
            var handler = Received;
            if (handler is null) return;
            lock (_gate)
            {
                if (_disposed || !ReferenceEquals(sender, _watcher)) return;
            }
            var name = args.Advertisement.LocalName;
            foreach (var section in args.Advertisement.ManufacturerData)
            {
                if (section.CompanyId != ProximityMessage.AppleCompanyId) continue;
                var bytes = new byte[section.Data.Length];
                using (var reader = DataReader.FromBuffer(section.Data)) reader.ReadBytes(bytes);
                handler(this, new AdvertisementSighting(
                    args.BluetoothAddress, args.Timestamp, args.RawSignalStrengthInDBm,
                    string.IsNullOrEmpty(name) ? null : name, section.CompanyId, bytes));
            }
        }
        catch (Exception ex)
        {
            // A malformed advertisement or subscriber fault must never escape into the WinRT callback.
            ReportFault(ex);
        }
    }

    private void ReportFault(Exception ex)
    {
        try
        {
            long now = Environment.TickCount64;
            long last = Interlocked.Read(ref _lastFaultReportTicks);
            if (last != 0 && now - last < FaultReportInterval.TotalMilliseconds)
            {
                Interlocked.Increment(ref _suppressedFaults);
                return;
            }
            Interlocked.Exchange(ref _lastFaultReportTicks, now);
            int suppressed = Interlocked.Exchange(ref _suppressedFaults, 0);
            Diagnostic?.Invoke(this, $"Advertisement callback failed ({ex.GetType().Name}, 0x{ex.HResult:X8})"
                + (suppressed > 0 ? $"; {suppressed} more since the last report." : "."));
        }
        catch (Exception)
        {
            // Diagnostics must never break the callback.
        }
    }

    private void OnStopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args)
    {
        try
        {
            lock (_gate)
            {
                // Our own Stop() detaches first, so only unexpected stops get here.
                if (_disposed || !ReferenceEquals(sender, _watcher)) return;
            }
            Detach();
            string? reason = args.Error switch
            {
                BluetoothError.Success => null,
                BluetoothError.RadioNotAvailable => Strings.WinBle_Stopped_RadioNotAvailable,
                BluetoothError.DisabledByPolicy => Strings.WinBle_Stopped_DisabledByPolicy,
                BluetoothError.DisabledByUser => Strings.WinBle_Stopped_DisabledByUser,
                BluetoothError.NotSupported => Strings.WinBle_Stopped_NotSupported,
                BluetoothError.ResourceInUse => Strings.WinBle_Stopped_RadioBusy,
                BluetoothError.ConsentRequired => Strings.WinBle_Stopped_ConsentRequired,
                _ => string.Format(CultureInfo.CurrentCulture, Strings.WinBle_Stopped_WithError, args.Error),
            };
            SetStatus(reason is null
                ? AdvertisementSourceStatus.Stopped
                : new AdvertisementSourceStatus(AdvertisementSourceState.Unavailable, reason));
        }
        catch (Exception)
        {
            // Never escape into the WinRT callback.
        }
    }

    /// <summary>Stop and release the watcher. Returns true when one was running.</summary>
    private bool Detach()
    {
        BluetoothLEAdvertisementWatcher? watcher;
        lock (_gate)
        {
            watcher = _watcher;
            _watcher = null;
        }
        if (watcher is null) return false;
        watcher.Received -= OnReceived;
        watcher.Stopped -= OnStopped;
        try { watcher.Stop(); }
        catch (Exception) { /* already stopped or radio gone */ }
        return true;
    }

    /// <summary>
    /// Publish a status from inside <see cref="StartAsync"/> only if that start is still current.
    /// A start retired by Stop/Dispose returns its result quietly: its event would otherwise be
    /// read as the loss of a newer observation (EF-IR review F3).
    /// </summary>
    private AdvertisementSourceStatus SetStatusIfCurrent(long epoch, AdvertisementSourceStatus status)
    {
        lock (_gate)
        {
            if (_disposed || _stopEpoch != epoch) return status;
        }
        return SetStatus(status);
    }

    private AdvertisementSourceStatus SetStatus(AdvertisementSourceStatus status)
    {
        bool changed;
        lock (_gate)
        {
            changed = _status != status;
            _status = status;
        }
        if (changed)
        {
            try { StatusChanged?.Invoke(this, status); }
            catch (Exception) { /* subscriber faults stay local */ }
        }
        return status;
    }
}
